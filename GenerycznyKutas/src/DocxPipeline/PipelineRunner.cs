using System.Diagnostics;
using DocumentFormat.OpenXml.Packaging;
using DocxPipeline.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace DocxPipeline;

/// <summary>Wykonuje kroki potoku sekwencyjnie na jednym otwartym dokumencie; każdy krok to worker utworzony z rejestru na podstawie nazwy i konfiguracji.</summary>
public sealed class PipelineRunner(WorkerRegistry registry, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Rejestr, z którego tworzone są workery.</summary>
    public WorkerRegistry Registry { get; } = registry;

    /// <summary>Wykonuje potok na otwartym dokumencie (zapis należy do wołającego).</summary>
    public PipelineResult Run(WordprocessingDocument document, JToken model, PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(pipeline);

        var context = new DocxWorkerContext(document, model, _logger);
        var results = new List<StepResult>();
        _logger.LogInformation("Potok '{Pipeline}': {Count} kroków.", pipeline.Name ?? "(bez nazwy)", pipeline.Steps.Count);

        foreach (var (step, index) in pipeline.Steps.Select((s, i) => (s, i + 1)))
        {
            var result = RunStep(step, index, context);
            results.Add(result);
            if (result.Status == StepStatus.Failed && step.OnError == StepErrorPolicy.Stop)
            {
                _logger.LogError("Potok przerwany na kroku {Index} ({Worker}).", index, step.Worker);
                break;
            }
        }

        return new PipelineResult(results);
    }

    /// <summary>Kopiuje szablon do pliku wynikowego, wykonuje potok i zapisuje.</summary>
    public PipelineResult RunFile(string templatePath, string outputPath, JToken model, PipelineDefinition pipeline)
    {
        File.Copy(templatePath, outputPath, overwrite: true);
        using var document = WordprocessingDocument.Open(outputPath, isEditable: true);
        var result = Run(document, model, pipeline);
        document.Save();
        return result;
    }

    private StepResult RunStep(PipelineStep step, int index, DocxWorkerContext context)
    {
        context.ClearWarnings();
        var watch = Stopwatch.StartNew();
        StepResult Done(StepStatus status, string? error = null) =>
            new(index, step.Worker, status, watch.Elapsed, error, context.Warnings.ToList());

        if (!step.Enabled)
            return Done(StepStatus.Skipped, "krok wyłączony (enabled = false)");
        if (!string.IsNullOrWhiteSpace(step.When) && !ModelValues.IsTruthy(context.Resolve(step.When)))
            return Done(StepStatus.Skipped, $"warunek 'when' = {step.When} niespełniony");
        if (!string.IsNullOrWhiteSpace(step.Unless) && ModelValues.IsTruthy(context.Resolve(step.Unless)))
            return Done(StepStatus.Skipped, $"warunek 'unless' = {step.Unless} spełniony");

        try
        {
            var worker = Registry.Create(step.Worker, step.ConfigurationObject());
            _logger.LogInformation("Krok {Index}: {Worker} ({Type})", index, step.Worker, worker.GetType().Name);
            worker.Run(context);
            return Done(StepStatus.Done);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Krok {Index} ({Worker}) zakończył się błędem: {Message}", index, step.Worker, ex.Message);
            return Done(StepStatus.Failed, ex.Message);
        }
    }
}

/// <summary>Status kroku.</summary>
public enum StepStatus
{
    /// <summary>Wykonany.</summary>
    Done,

    /// <summary>Pominięty (wyłączony albo warunek niespełniony).</summary>
    Skipped,

    /// <summary>Wyjątek workera albo nieznany worker.</summary>
    Failed,
}

/// <summary>Wynik jednego kroku.</summary>
public sealed record StepResult(int Index, string Worker, StepStatus Status, TimeSpan Duration, string? Message, IReadOnlyList<string> Warnings);

/// <summary>Wynik potoku.</summary>
public sealed class PipelineResult(IReadOnlyList<StepResult> steps)
{
    /// <summary>Kroki w kolejności wykonania (po przerwaniu potoku lista jest krótsza niż definicja).</summary>
    public IReadOnlyList<StepResult> Steps { get; } = steps;

    /// <summary>Czy żaden krok nie zakończył się błędem.</summary>
    public bool Succeeded => Steps.All(s => s.Status != StepStatus.Failed);

    /// <summary>Wszystkie ostrzeżenia z prefiksem kroku.</summary>
    public IEnumerable<string> AllWarnings => Steps.SelectMany(s => s.Warnings.Select(w => $"[{s.Index}. {s.Worker}] {w}"));
}
