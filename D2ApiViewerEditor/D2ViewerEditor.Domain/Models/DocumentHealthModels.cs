namespace D2ViewerEditor.Domain.Models;

public enum HealthStage
{
    File = 0,

    Package = 1,

    Xml = 2,

    Structure = 3,

    Conversion = 4,

    Application = 5
}

public enum AppSupportLevel
{
    Unknown = 0,

    Full = 1,

    Partial = 2,

    PassThrough = 3,

    Unsupported = 4
}

public enum RoundTripOutcome
{
    NotVerified = 0,
    Preserved = 1,
    Reduced = 2,
    Lost = 3
}

public enum CoverageStatus
{
    Supported = 0,
    Partial = 1,
    PassThrough = 2,
    Unsupported = 3,

    UnexpectedLoss = 4,

    Unverified = 5
}

public sealed record ImplementationCoverageItem(
    string FeatureKey,
    string Label,
    int SourceCount,
    string? SampleLocation,
    AppSupportLevel Reader,
    AppSupportLevel Editor,
    AppSupportLevel Writer,
    int? RoundTripCount,
    RoundTripOutcome RoundTrip,
    CoverageStatus Status,
    string Note,
    string? CodePointer);

public enum WordOpenImpact
{
    None = 0,

    Repair = 1,

    CannotOpen = 2
}

public enum PdfConversionImpact
{
    None = 0,

    Possible = 1,

    Likely = 2,

    Blocking = 3
}

public sealed record HealthFinding(
    string Code,
    StructureIssueSeverity Severity,
    HealthStage Stage,
    string Title,
    string Description,
    string? Location,
    WordOpenImpact WordImpact,
    PdfConversionImpact PdfImpact,
    string? Remedy,
    AppSupportLevel AppSupport = AppSupportLevel.Unknown,
    string? AppNote = null);

public enum ProbeStatus
{
    Passed = 0,
    Warning = 1,
    Failed = 2,
    Skipped = 3
}

public sealed record ConversionProbeResult(
    string Id,
    string Name,
    string Description,
    ProbeStatus Status,
    long DurationMs,
    string? Message,
    string? Details);

public sealed record DocumentHealthStatistics(
    int PackageEntries,
    int XmlParts,
    int ImageParts,
    long ImageBytes,
    int Elements,
    int Paragraphs,
    int Tables,
    int MaxTableNesting,
    int Drawings,
    int Fields,
    int Sections,
    int Footnotes,
    int Endnotes,
    int Comments,
    int TrackedRevisions,
    int ContentControls,
    int AltChunks,
    int EmbeddedFonts);

public enum HealthVerdict
{
    Healthy = 0,
    Warnings = 1,
    NeedsRepair = 2,
    Corrupt = 3
}

public enum WordOpenVerdict
{
    Ok = 0,
    Repair = 1,
    CannotOpen = 2
}

public enum PdfConversionVerdict
{
    Ok = 0,
    AtRisk = 1,
    Likely = 2,
    Blocked = 3
}

public sealed class DocumentHealthReport
{
    public required string FileName { get; init; }
    public required long FileSizeInBytes { get; init; }

    public required string DetectedFormat { get; init; }

    public string? MainDocumentPartPath { get; init; }

    public required HealthVerdict Verdict { get; init; }
    public required string VerdictSummary { get; init; }
    public required WordOpenVerdict WordOpen { get; init; }
    public required string WordOpenSummary { get; init; }
    public required PdfConversionVerdict PdfConversion { get; init; }
    public required string PdfConversionSummary { get; init; }

    public required IReadOnlyList<HealthFinding> Findings { get; init; }

    public required bool FindingsTruncated { get; init; }

    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
    public required int InfoCount { get; init; }

    public required IReadOnlyList<ConversionProbeResult> Probes { get; init; }
    public required DocumentHealthStatistics Statistics { get; init; }

    public required IReadOnlyList<ImplementationCoverageItem> Coverage { get; init; }

    public required string CoverageSummary { get; init; }

    public required DateTimeOffset AnalyzedAtUtc { get; init; }
    public required long DurationMs { get; init; }
}
