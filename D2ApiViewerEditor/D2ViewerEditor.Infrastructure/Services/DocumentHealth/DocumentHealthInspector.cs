using System.Diagnostics;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public sealed class DocumentHealthInspector : IDocumentHealthInspector
{
    private readonly FileContainerCheck _fileCheck;
    private readonly PackageHealthCheck _packageCheck;
    private readonly XmlPartsHealthCheck _xmlCheck;
    private readonly WordStructureHealthCheck _structureCheck;
    private readonly ConversionProbeRunner _probes;
    private readonly DocumentHealthOptions _options;
    private readonly ILogger<DocumentHealthInspector> _logger;

    public DocumentHealthInspector(
        FileContainerCheck fileCheck,
        PackageHealthCheck packageCheck,
        XmlPartsHealthCheck xmlCheck,
        WordStructureHealthCheck structureCheck,
        ConversionProbeRunner probes,
        IOptions<DocumentHealthOptions> options,
        ILogger<DocumentHealthInspector> logger)
    {
        _fileCheck = fileCheck;
        _packageCheck = packageCheck;
        _xmlCheck = xmlCheck;
        _structureCheck = structureCheck;
        _probes = probes;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DocumentHealthReport> InspectAsync(
        byte[] documentBytes,
        string fileName,
        bool includeConversionProbes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);

        var stopwatch = Stopwatch.StartNew();
        var findings = new HealthFindingCollector(_options.MaxFindings, _options.MaxFindingsPerCode);
        var detectedFormat = "unknown";
        LenientPackage? package = null;
        HealthPackageContext? context = null;
        IReadOnlyDictionary<string, XDocument> parsed = new Dictionary<string, XDocument>();
        DocumentHealthStatistics? statistics = null;

        Guard(HealthStage.File, findings, () => package = _fileCheck.Run(documentBytes, findings, out detectedFormat));

        if (package is not null)
        {
            Guard(HealthStage.Package, findings, () =>
            {
                context = _packageCheck.Run(package, findings, cancellationToken, out var format);
                detectedFormat = format;
            });
        }

        if (context is not null)
        {
            Guard(HealthStage.Xml, findings, () => parsed = _xmlCheck.Run(context, findings, cancellationToken));
            Guard(HealthStage.Structure, findings, () => statistics = _structureCheck.Run(context, parsed, findings, cancellationToken));
        }

        IReadOnlyList<ConversionProbeResult> probes = [];

        try
        {
            probes = await _probes.RunAsync(documentBytes, fileName, findings, includeConversionProbes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Etap prób konwersji diagnostyki dokumentu nie ukończył pracy.");
            findings.Add(DocumentHealthCodes.StageFailed, StructureIssueSeverity.Warning, HealthStage.Conversion,
                "Etap prób konwersji nie ukończył pracy",
                $"{exception.GetType().Name}: {exception.Message}. Wyniki prób są niekompletne; pozostałe etapy pozostają ważne.");
        }

        findings.AnnotateRepetitions();
        var verdicts = HealthVerdictBuilder.Build(findings, probes, detectedFormat);
        stopwatch.Stop();

        return new DocumentHealthReport
        {
            FileName = fileName,
            FileSizeInBytes = documentBytes.LongLength,
            DetectedFormat = detectedFormat,
            MainDocumentPartPath = context?.MainDocumentPartPath,
            Verdict = verdicts.Verdict,
            VerdictSummary = verdicts.VerdictSummary,
            WordOpen = verdicts.WordOpen,
            WordOpenSummary = verdicts.WordOpenSummary,
            PdfConversion = verdicts.PdfConversion,
            PdfConversionSummary = verdicts.PdfConversionSummary,
            Findings = findings.Findings.OrderBy(finding => finding.Stage).ThenByDescending(finding => finding.Severity).ToArray(),
            FindingsTruncated = findings.Truncated,
            ErrorCount = findings.ErrorCount,
            WarningCount = findings.WarningCount,
            InfoCount = findings.InfoCount,
            Probes = probes,
            Statistics = statistics ?? EmptyStatistics(package),
            AnalyzedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private void Guard(HealthStage stage, HealthFindingCollector findings, Action action)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Etap {Stage} diagnostyki dokumentu nie ukończył pracy.", stage);
            findings.Add(DocumentHealthCodes.StageFailed, StructureIssueSeverity.Warning, stage,
                $"Etap „{Describe(stage)}” nie ukończył pracy",
                $"{exception.GetType().Name}: {exception.Message}. Ustalenia tego etapu są niekompletne — sam fakt, że analizator się wywrócił, sugeruje nietypową strukturę pliku.");
        }
    }

    private static string Describe(HealthStage stage) => stage switch
    {
        HealthStage.File => "Plik",
        HealthStage.Package => "Pakiet OPC",
        HealthStage.Xml => "XML",
        HealthStage.Structure => "Struktura",
        _ => "Próby konwersji"
    };

    private static DocumentHealthStatistics EmptyStatistics(LenientPackage? package) => new(
        package?.Entries.Count ?? 0,
        package?.Entries.Count(entry => entry.IsXml) ?? 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
