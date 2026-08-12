namespace D2ViewerEditor.Domain.Models;

public enum StructureIssueSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed record StructureIssue(
    string Code,
    StructureIssueSeverity Severity,
    string Title,
    string Description);

public sealed record StructureAttribute(
    string Name,
    string LocalName,
    string NamespaceUri,
    string RawValue,
    string? InterpretedValue);

public sealed record StructureProperty(
    string Name,
    string? Value,
    string Source,
    string? SourceReference = null,
    bool IsRedundant = false);

public enum StructureRelationshipStatus
{
    Resolved = 0,
    External = 1,
    TargetMissing = 2,
    NotDeclared = 3
}

public sealed record StructureRelationship(
    string SourcePart,
    string RelationshipPartPath,
    string Id,
    string Type,
    string Target,
    string TargetMode,
    string? ResolvedTarget,
    StructureRelationshipStatus Status);

public sealed record EditorCompatibilityInfo(
    string Feature,
    string Level,
    string? Notes);

public sealed record SchemaValidationIssue(
    string Code,
    string Severity,
    string Description,
    string? PartPath,
    string? NodeName,
    string? Path,
    string? ElementId,
    string TargetVersion);

public sealed record InspectedPackageEntry(
    string Path,
    long UncompressedSize,
    long CompressedSize,
    string? ContentType,
    bool IsXml);

public sealed record InspectedPackagePart(
    string Path,
    string? ContentType,
    long UncompressedSize,
    long CompressedSize,
    int ElementCount,
    bool IsIndexed);

public sealed record HeaderFooterBinding(
    string Kind,
    string Type,
    string Source,
    int? SourceSectionNumber,
    bool IsActive,
    string? ReferenceElementId,
    string? RelationshipId,
    string? RelationshipType,
    string? TargetMode,
    string? Target,
    string? PartPath,
    string? PartRootElementId,
    bool PartExists,
    IReadOnlyList<StructureIssue> Issues);

public sealed record DocumentSectionInfo(
    int Number,
    string SectionPropertiesElementId,
    string DisplayPath,
    bool FirstPageDifferent,
    bool EvenAndOddHeaders,
    IReadOnlyList<HeaderFooterBinding> HeaderFooterBindings,
    IReadOnlyList<StructureIssue> Issues);

public sealed class InspectedElement
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string PartPath { get; init; }
    public required int Depth { get; init; }
    public required int Order { get; init; }
    public required IReadOnlyList<int> NodePath { get; init; }
    public required string DisplayPath { get; init; }
    public required string XmlName { get; init; }
    public required string LocalName { get; init; }
    public required string NamespaceUri { get; init; }
    public required string Category { get; init; }
    public required string DisplayName { get; init; }
    public string? Preview { get; init; }
    public required bool HasChildren { get; init; }
    public List<StructureAttribute> Attributes { get; } = [];
    public List<StructureProperty> Properties { get; } = [];
    public List<StructureRelationship> Relationships { get; } = [];
    public List<StructureIssue> Issues { get; } = [];
    public List<EditorCompatibilityInfo> EditorCompatibility { get; } = [];

    public string SearchText { get; set; } = string.Empty;

    public StructureIssueSeverity? HighestSeverity =>
        Issues.Count == 0 ? null : Issues.Max(issue => issue.Severity);
}

public sealed class DocumentStructureAnalysis
{
    public required string FileName { get; init; }
    public required long FileSizeInBytes { get; init; }

    public required string MainDocumentPartPath { get; init; }

    public required IReadOnlyList<InspectedElement> Elements { get; init; }
    public required IReadOnlyDictionary<string, InspectedElement> ElementsById { get; init; }
    public required IReadOnlyList<InspectedPackagePart> Parts { get; init; }
    public required IReadOnlyList<InspectedPackageEntry> Entries { get; init; }
    public required IReadOnlyList<DocumentSectionInfo> Sections { get; init; }
    public required IReadOnlyList<StructureIssue> PackageIssues { get; init; }
    public required IReadOnlyList<SchemaValidationIssue> SchemaIssues { get; init; }

    public required int SchemaIssueCount { get; init; }

    public required bool ElementsTruncated { get; init; }
}

public sealed record DocumentStructureInspection(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DocumentStructureAnalysis Analysis,
    byte[] DocumentBytes);
