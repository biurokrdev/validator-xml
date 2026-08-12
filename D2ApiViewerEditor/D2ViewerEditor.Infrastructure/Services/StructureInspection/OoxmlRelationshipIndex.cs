using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class OoxmlRelationshipIndex
{
    private readonly Dictionary<string, StructureRelationship> _relationships;

    public OoxmlRelationshipIndex(Dictionary<string, StructureRelationship> relationships)
    {
        _relationships = relationships;
    }

    public IEnumerable<StructureRelationship> All => _relationships.Values;

    public static string CreateKey(string sourcePart, string relationshipId) => $"{sourcePart}|{relationshipId}";

    public StructureRelationship? Find(string sourcePart, string relationshipId) =>
        _relationships.GetValueOrDefault(CreateKey(sourcePart, relationshipId));

    public string? FindTargetByType(string sourcePart, string typeSuffix) =>
        _relationships.Values
            .Where(relationship =>
                relationship.SourcePart.Equals(sourcePart, StringComparison.OrdinalIgnoreCase) &&
                OoxmlNamespaces.IsRelationshipType(relationship.Type, typeSuffix) &&
                relationship.ResolvedTarget is not null)
            .Select(relationship => relationship.ResolvedTarget)
            .FirstOrDefault();

    public string? FindSourceByTarget(string targetPath, string typeSuffix) =>
        _relationships.Values
            .Where(relationship =>
                OoxmlNamespaces.IsRelationshipType(relationship.Type, typeSuffix) &&
                relationship.ResolvedTarget?.Equals(targetPath, StringComparison.OrdinalIgnoreCase) == true)
            .Select(relationship => relationship.SourcePart)
            .FirstOrDefault();

    internal void MarkTargetMissing(StructureRelationship relationship)
    {
        _relationships[CreateKey(relationship.SourcePart, relationship.Id)] =
            relationship with { Status = StructureRelationshipStatus.TargetMissing };
    }
}
