using DocxPipeline.Internal;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DocxPipeline.Workers;

/// <summary>
/// Usuwa formanty zawartości o podanym tagu w całym dokumencie. Bez warunku usuwa zawsze (jak <c>DocXContentControlRemover</c>);
/// z <see cref="RemoveWhen"/> usuwa tylko, gdy wartość w modelu jest „prawdziwa” (<see cref="Negate"/> odwraca), w przeciwnym razie zostawia.
/// <see cref="Unwrap"/>: formant, który zostaje, traci ramkę (bez warunku: tylko zdjęcie ramki, zawartość zostaje).
/// Klucz nazywa się <c>remove-when</c>, bo <c>when</c> jest zarezerwowane dla kroku potoku (warunek wykonania kroku).
/// </summary>
[DocxWorker("content-control-remover", Description = "Usuwa formanty o tagu, opcjonalnie pod warunkiem z modelu; może tylko zdjąć ramkę (unwrap).")]
public sealed class ContentControlRemoverWorker : IDocxWorker
{
    /// <summary>Tag formantu (albo tytuł); <c>*</c> jako symbol wieloznaczny.</summary>
    [JsonProperty("tag-name")]
    public string TagName { get; set; } = string.Empty;

    /// <summary>JSONPath w modelu; formant jest usuwany, gdy wartość jest „prawdziwa”. Brak = usuń bezwarunkowo.</summary>
    [JsonProperty("remove-when")]
    public string? RemoveWhen { get; set; }

    /// <summary>Odwraca warunek: usuń, gdy wartość jest „fałszywa”.</summary>
    [JsonProperty("negate")]
    public bool Negate { get; set; }

    /// <summary>Gdy formant zostaje, zdejmij jego ramkę; bez <c>remove-when</c> oznacza: nie usuwaj, tylko zdejmij ramkę.</summary>
    [JsonProperty("unwrap")]
    public bool Unwrap { get; set; }

    /// <inheritdoc />
    public void Run(DocxWorkerContext context)
    {
        if (string.IsNullOrWhiteSpace(TagName)) throw new InvalidOperationException("Brak 'tag-name'.");

        var remove = string.IsNullOrWhiteSpace(RemoveWhen) ? !Unwrap : ModelValues.IsTruthy(context.Resolve(RemoveWhen)) != Negate;
        var found = ContentControls.Find(context.Roots(), TagName);
        if (found.Count == 0) { context.Warn($"Formant '{TagName}': nie znaleziono w dokumencie."); return; }
        context.Logger.LogInformation("Formant '{Tag}': {Action}.", TagName, remove ? "usuwany" : Unwrap ? "zostaje bez ramki" : "zostaje");

        foreach (var sdt in found.Where(ContentControls.IsAttached))
        {
            if (remove) ContentControls.Remove(sdt);
            else if (Unwrap) ContentControls.Unwrap(sdt);
        }
    }
}
