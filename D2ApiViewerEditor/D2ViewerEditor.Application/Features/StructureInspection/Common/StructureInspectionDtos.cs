using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Application.Features.StructureInspection.Common;

public record StructureInspectionSummaryDto(
    Guid InspectionId,
    string FileName,
    long FileSizeInBytes,
    string MainDocumentPartPath,
    DateTimeOffset ExpiresAtUtc,
    int ElementCount,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    int SchemaIssueCount,
    int PackageIssueCount,
    int SectionCount,
    bool ElementsTruncated,
    IReadOnlyList<StructurePartDto> Parts,
    IReadOnlyList<string> Categories);

public record StructurePartDto(
    string Path,
    string? ContentType,
    long UncompressedSize,
    long CompressedSize,
    int ElementCount);

public record StructureElementDto(
    string Id,
    string? ParentId,
    int Depth,
    string PartPath,
    string XmlName,
    string Category,
    string DisplayName,
    string? Preview,
    string SearchText,
    string Severity,
    int IssueCount,
    bool HasChildren);

public record StructureElementDetailsDto(
    string Id,
    string? ParentId,
    int Depth,
    string PartPath,
    string DisplayPath,
    string XmlName,
    string LocalName,
    string NamespaceUri,
    string Category,
    string DisplayName,
    string? Preview,
    IReadOnlyList<StructureAttribute> Attributes,
    IReadOnlyList<StructureProperty> Properties,
    IReadOnlyList<StructureRelationship> Relationships,
    IReadOnlyList<StructureIssue> Issues,
    IReadOnlyList<EditorCompatibilityInfo> EditorCompatibility);

public record StructureElementXmlDto(
    string ElementId,
    string PartPath,
    string DisplayPath,
    string Xml,
    int? SourceLine);

public record StructurePartXmlDto(
    string PartPath,
    string Xml,
    string? HighlightElementId,
    int? HighlightLine);

public record SchemaIssueDto(
    string Code,
    string Severity,
    string Description,
    string? PartPath,
    string? NodeName,
    string? Path,
    string? ElementId,
    string TargetVersion);

public record SchemaIssuesDto(
    string TargetVersion,
    int TotalCount,
    IReadOnlyList<SchemaIssueDto> Issues);

public record PackageDiagnosticsDto(
    string MainDocumentPartPath,
    IReadOnlyList<StructureIssue> Issues,
    IReadOnlyList<StructurePackageEntryDto> Entries,
    IReadOnlyList<string> SupportedSchemaTargets);

public record StructurePackageEntryDto(
    string Path,
    long UncompressedSize,
    long CompressedSize,
    string? ContentType,
    bool IsXml);

public record DocumentSectionDto(
    int Number,
    string SectionPropertiesElementId,
    string DisplayPath,
    bool FirstPageDifferent,
    bool EvenAndOddHeaders,
    IReadOnlyList<HeaderFooterBinding> HeaderFooterBindings,
    IReadOnlyList<StructureIssue> Issues);
