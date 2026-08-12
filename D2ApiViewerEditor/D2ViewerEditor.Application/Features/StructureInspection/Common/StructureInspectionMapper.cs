using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Application.Features.StructureInspection.Common;

public static class StructureInspectionMapper
{
    public const string NoSeverity = "None";

    public static StructureElementDto ToListItem(InspectedElement element) => new(
        element.Id,
        element.ParentId,
        element.Depth,
        element.PartPath,
        element.XmlName,
        element.Category,
        element.DisplayName,
        element.Preview,
        element.SearchText,
        ToSeverityName(element.HighestSeverity),
        element.Issues.Count,
        element.HasChildren);

    public static StructureElementDetailsDto ToDetails(InspectedElement element) => new(
        element.Id,
        element.ParentId,
        element.Depth,
        element.PartPath,
        element.DisplayPath,
        element.XmlName,
        element.LocalName,
        element.NamespaceUri,
        element.Category,
        element.DisplayName,
        element.Preview,
        element.Attributes,
        element.Properties,
        element.Relationships,
        element.Issues,
        element.EditorCompatibility);

    public static SchemaIssueDto ToDto(SchemaValidationIssue issue) => new(
        issue.Code,
        issue.Severity,
        issue.Description,
        issue.PartPath,
        issue.NodeName,
        issue.Path,
        issue.ElementId,
        issue.TargetVersion);

    public static StructurePartDto ToDto(InspectedPackagePart part) => new(
        part.Path,
        part.ContentType,
        part.UncompressedSize,
        part.CompressedSize,
        part.ElementCount);

    public static StructurePackageEntryDto ToDto(InspectedPackageEntry entry) => new(
        entry.Path,
        entry.UncompressedSize,
        entry.CompressedSize,
        entry.ContentType,
        entry.IsXml);

    public static DocumentSectionDto ToDto(DocumentSectionInfo section) => new(
        section.Number,
        section.SectionPropertiesElementId,
        section.DisplayPath,
        section.FirstPageDifferent,
        section.EvenAndOddHeaders,
        section.HeaderFooterBindings,
        section.Issues);

    public static string ToSeverityName(StructureIssueSeverity? severity) => severity?.ToString() ?? NoSeverity;

}
