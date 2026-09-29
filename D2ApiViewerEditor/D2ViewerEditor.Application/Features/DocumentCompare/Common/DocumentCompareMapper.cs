using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Application.Features.DocumentCompare.Common;

public static class DocumentCompareMapper
{
    public static DocumentComparisonReportDto ToDto(DocumentComparisonReport report) => new(
        ToDto(report.Left),
        ToDto(report.Right),
        report.IgnoreRevisionIds,
        report.IgnoreDocumentProperties,
        report.Identical,
        report.TotalDifferences,
        report.Truncated,
        report.IgnoredAttributeCount,
        report.CountsByKind,
        report.CountsByCategory,
        report.CountsByCause,
        report.CountsByImpact,
        report.Parts.Select(ToDto).ToArray(),
        report.Differences.Select(ToDto).ToArray(),
        report.ComparedAtUtc,
        report.DurationMs);

    public static ComparedFileDto ToDto(ComparedFile file) =>
        new(file.FileName, file.SizeInBytes, file.DetectedFormat, file.PartCount, file.Notes);

    public static ComparedPartDto ToDto(ComparedPart part) => new(
        part.Path,
        part.Status.ToString(),
        part.IsXml,
        part.DifferenceCount,
        part.LeftSize,
        part.RightSize,
        part.LeftElementCount,
        part.RightElementCount,
        part.Note);

    public static DocumentDifferenceDto ToDto(DocumentDifference difference) => new(
        difference.Kind.ToString(),
        difference.Category,
        difference.PartPath,
        difference.LeftPath,
        difference.RightPath,
        difference.LeftLine,
        difference.RightLine,
        difference.Name,
        difference.LeftValue,
        difference.RightValue,
        difference.LeftExcerpt,
        difference.RightExcerpt,
        difference.ExcerptTruncated,
        difference.LeftContext,
        difference.RightContext,
        difference.Analysis is null ? null : ToDto(difference.Analysis));

    public static DifferenceAnalysisDto ToDto(DifferenceAnalysis analysis) => new(
        analysis.Cause.ToString(),
        analysis.Impact.ToString(),
        analysis.Explanation,
        analysis.FeatureKey,
        analysis.CodePointer);
}
