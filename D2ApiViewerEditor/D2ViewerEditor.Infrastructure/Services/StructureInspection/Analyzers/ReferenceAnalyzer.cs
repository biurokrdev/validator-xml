using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class ReferenceAnalyzer : IStructureAnalyzer
{
    private static readonly (string Reference, string Target, string Label)[] ReferenceKinds =
    [
        ("footnoteReference", "footnote", "Przypis dolny"),
        ("endnoteReference", "endnote", "Przypis końcowy"),
        ("commentReference", "comment", "Komentarz")
    ];

    public void Analyze(StructureAnalysisContext context)
    {
        foreach (var (referenceName, targetName, label) in ReferenceKinds)
        {
            var targets = BuildTargetIndex(context, targetName, label);
            ResolveReferences(context, referenceName, targetName, label, targets);
        }
    }

    private static Dictionary<string, InspectedElement> BuildTargetIndex(
        StructureAnalysisContext context,
        string targetLocalName,
        string label)
    {
        var index = new Dictionary<string, InspectedElement>(StringComparer.Ordinal);

        foreach (var node in context.WordprocessingNodes(targetLocalName))
        {
            var id = GetId(node.Element);

            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (!index.TryAdd(id, node.Element))
            {
                node.Element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.ReferenceIdDuplicate,
                    StructureIssueSeverity.Error,
                    $"{label}: zduplikowany identyfikator",
                    $"Identyfikator '{id}' występuje w więcej niż jednym elemencie w:{targetLocalName}."));
            }
        }

        return index;
    }

    private static void ResolveReferences(
        StructureAnalysisContext context,
        string referenceLocalName,
        string targetLocalName,
        string label,
        IReadOnlyDictionary<string, InspectedElement> targets)
    {
        foreach (var node in context.WordprocessingNodes(referenceLocalName))
        {
            var element = node.Element;
            var id = GetId(element);

            element.Properties.Add(new StructureProperty($"{label}: identyfikator", id, $"w:{referenceLocalName}"));

            if (string.IsNullOrWhiteSpace(id))
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.ReferenceIdMissing,
                    StructureIssueSeverity.Error,
                    $"{label}: brak identyfikatora",
                    $"Element w:{referenceLocalName} nie ma atrybutu w:id."));
                continue;
            }

            if (!targets.TryGetValue(id, out var target))
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.ReferenceTargetNotFound,
                    StructureIssueSeverity.Error,
                    $"{label}: brak celu odwołania",
                    $"Identyfikator '{id}' nie ma pasującego elementu w:{targetLocalName} w pakiecie."));
                continue;
            }

            element.Properties.Add(new StructureProperty(
                $"{label}: cel",
                target.DisplayPath,
                PropertySources.ResolvedReference,
                target.PartPath));
            target.Properties.Add(new StructureProperty(
                "Odwołanie z",
                element.DisplayPath,
                PropertySources.ResolvedReference,
                element.PartPath));
        }
    }

    private static string? GetId(InspectedElement element) =>
        element.Attributes.FirstOrDefault(attribute => attribute.LocalName == "id")?.RawValue;
}
