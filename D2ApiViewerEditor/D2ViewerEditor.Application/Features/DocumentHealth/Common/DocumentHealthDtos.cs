namespace D2ViewerEditor.Application.Features.DocumentHealth.Common;

/// <summary>
/// Raport „Kondycja dokumentu" — jedna odpowiedź, bez stanu po stronie serwera. Enumy są
/// przekazywane jako nazwy (stabilny kontrakt niezależny od kolejności wartości), żeby GUI
/// i logi mogły rozpoznawać werdykty po tekście.
/// </summary>
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
    IReadOnlyList<ImplementationCoverageItemDto> Coverage,
    string CoverageSummary,
    DateTimeOffset AnalyzedAtUtc,
    long DurationMs);

/// <summary>
/// Ustalenie z oceną wpływu na Worda i konwersję oraz podpowiedzią naprawy. <c>AppSupport</c>/<c>AppNote</c>
/// opisują, jak z tym problemem radzi sobie nasza aplikacja (reader/edytor/writer).
/// </summary>
public record HealthFindingDto(
    string Code,
    string Severity,
    string Stage,
    string Title,
    string Description,
    string? Location,
    string WordImpact,
    string PdfImpact,
    string? Remedy,
    string AppSupport,
    string? AppNote);

/// <summary>
/// Jedna konstrukcja DOCX obecna w dokumencie zestawiona z obsługą przez nasz pipeline:
/// zadeklarowane poziomy per komponent, wynik round-tripu i wskaźnik do kodu.
/// </summary>
public record ImplementationCoverageItemDto(
    string FeatureKey,
    string Label,
    int SourceCount,
    string? SampleLocation,
    string Reader,
    string Editor,
    string Writer,
    int? RoundTripCount,
    string RoundTrip,
    string Status,
    string Note,
    string? CodePointer);

/// <summary>Wynik próby przetworzenia dokumentu przez realny komponent.</summary>
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
