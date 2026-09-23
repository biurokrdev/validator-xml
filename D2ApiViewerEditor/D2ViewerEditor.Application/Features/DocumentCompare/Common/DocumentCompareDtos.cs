namespace D2ViewerEditor.Application.Features.DocumentCompare.Common;

public record DocumentComparisonReportDto(
    ComparedFileDto Left,
    ComparedFileDto Right,
    bool IgnoreRevisionIds,
    bool IgnoreDocumentProperties,
    bool Identical,
    int TotalDifferences,
    bool Truncated,
    int IgnoredAttributeCount,
    IReadOnlyDictionary<string, int> CountsByKind,
    IReadOnlyDictionary<string, int> CountsByCategory,
    IReadOnlyList<ComparedPartDto> Parts,
    IReadOnlyList<DocumentDifferenceDto> Differences,
    DateTimeOffset ComparedAtUtc,
    long DurationMs);

public record ComparedFileDto(string FileName, long SizeInBytes, string DetectedFormat, int PartCount, IReadOnlyList<string> Notes);

public record ComparedPartDto(
    string Path,
    string Status,
    bool IsXml,
    int DifferenceCount,
    long? LeftSize,
    long? RightSize,
    int? LeftElementCount,
    int? RightElementCount,
    string? Note);

public record DocumentDifferenceDto(
    string Kind,
    string Category,
    string PartPath,
    string? LeftPath,
    string? RightPath,
    int? LeftLine,
    int? RightLine,
    string? Name,
    string? LeftValue,
    string? RightValue,
    string? LeftExcerpt,
    string? RightExcerpt,
    bool ExcerptTruncated,
    string? LeftContext,
    string? RightContext);
