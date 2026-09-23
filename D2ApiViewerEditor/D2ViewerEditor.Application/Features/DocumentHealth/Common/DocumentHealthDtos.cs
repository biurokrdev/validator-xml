namespace D2ViewerEditor.Application.Features.DocumentHealth.Common;

public record DocumentHealthReportDto(
    string FileName,
    long FileSizeInBytes,
    string DetectedFormat,
    string? MainDocumentPartPath,
    string Verdict,
    string VerdictSummary,
    string WordOpen,
    string WordOpenSummary,
    string PdfConversion,
    string PdfConversionSummary,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    bool FindingsTruncated,
    IReadOnlyList<HealthFindingDto> Findings,
    IReadOnlyList<ConversionProbeDto> Probes,
    DocumentHealthStatisticsDto Statistics,
    DateTimeOffset AnalyzedAtUtc,
    long DurationMs);

public record HealthFindingDto(
    string Code,
    string Severity,
    string Stage,
    string Title,
    string Description,
    string? Location,
    string WordImpact,
    string PdfImpact,
    string? Remedy);

public record ConversionProbeDto(
    string Id,
    string Name,
    string Description,
    string Status,
    long DurationMs,
    string? Message,
    string? Details);

public record DocumentHealthStatisticsDto(
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
