using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Application.Features.DocumentHealth.Common;

public static class DocumentHealthMapper
{
    public static DocumentHealthReportDto ToDto(DocumentHealthReport report) => new(
        report.FileName,
        report.FileSizeInBytes,
        report.DetectedFormat,
        report.MainDocumentPartPath,
        report.Verdict.ToString(),
        report.VerdictSummary,
        report.WordOpen.ToString(),
        report.WordOpenSummary,
        report.PdfConversion.ToString(),
        report.PdfConversionSummary,
        report.ErrorCount,
        report.WarningCount,
        report.InfoCount,
        report.FindingsTruncated,
        report.Findings.Select(ToDto).ToArray(),
        report.Probes.Select(ToDto).ToArray(),
        ToDto(report.Statistics),
        report.AnalyzedAtUtc,
        report.DurationMs);

    public static HealthFindingDto ToDto(HealthFinding finding) => new(
        finding.Code,
        finding.Severity.ToString(),
        finding.Stage.ToString(),
        finding.Title,
        finding.Description,
        finding.Location,
        finding.WordImpact.ToString(),
        finding.PdfImpact.ToString(),
        finding.Remedy);

    public static ConversionProbeDto ToDto(ConversionProbeResult probe) => new(
        probe.Id,
        probe.Name,
        probe.Description,
        probe.Status.ToString(),
        probe.DurationMs,
        probe.Message,
        probe.Details);

    public static DocumentHealthStatisticsDto ToDto(DocumentHealthStatistics statistics) => new(
        statistics.PackageEntries,
        statistics.XmlParts,
        statistics.ImageParts,
        statistics.ImageBytes,
        statistics.Elements,
        statistics.Paragraphs,
        statistics.Tables,
        statistics.MaxTableNesting,
        statistics.Drawings,
        statistics.Fields,
        statistics.Sections,
        statistics.Footnotes,
        statistics.Endnotes,
        statistics.Comments,
        statistics.TrackedRevisions,
        statistics.ContentControls,
        statistics.AltChunks,
        statistics.EmbeddedFonts);
}
