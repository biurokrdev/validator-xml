using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class RevisionAnalyzer : IStructureAnalyzer
{
    private static readonly string[] RevisionContainers =
    [
        "ins", "del", "moveFrom", "moveTo",
        "pPrChange", "rPrChange", "tblPrChange", "tblGridChange", "trPrChange", "tcPrChange",
        "sectPrChange", "numberingChange"
    ];

    private static readonly (string Start, string End)[] RangePairs =
    [
        ("moveFromRangeStart", "moveFromRangeEnd"),
        ("moveToRangeStart", "moveToRangeEnd"),
        ("commentRangeStart", "commentRangeEnd")
    ];

    public void Analyze(StructureAnalysisContext context)
    {
        foreach (var node in context.WordprocessingNodes(RevisionContainers))
        {
            AddRevisionMetadata(node.Element);
        }

        foreach (var partNodes in context.Nodes.GroupBy(node => node.Element.PartPath, StringComparer.OrdinalIgnoreCase))
        {
            ValidateRanges(partNodes.Select(node => node.Element).ToArray());
        }
    }

    private static void AddRevisionMetadata(InspectedElement revision)
    {
        foreach (var (attributeName, label) in new[] { ("id", "identyfikator"), ("author", "autor"), ("date", "data") })
        {
            var value = revision.Attributes.FirstOrDefault(attribute => attribute.LocalName == attributeName)?.RawValue;

            if (!string.IsNullOrWhiteSpace(value))
            {
                revision.Properties.Add(new StructureProperty($"Zmiana — {label}", value, $"w:{revision.LocalName}"));
            }
        }

        var isPropertyChange = revision.LocalName.EndsWith("PrChange", StringComparison.Ordinal) ||
                               revision.LocalName is "tblGridChange" or "numberingChange";

        revision.Properties.Add(new StructureProperty(
            "Rodzaj zmiany",
            isPropertyChange ? "Zmiana właściwości/formatowania" : "Zmiana treści",
            $"w:{revision.LocalName}"));

        revision.Issues.Add(new StructureIssue(
            StructureIssueCodes.TrackedRevision,
            StructureIssueSeverity.Warning,
            "Śledzona zmiana",
            $"Element w:{revision.LocalName} niesie nieprzyjętą zmianę redakcyjną. Zdecyduj, czy edytor pokazuje wersję bieżącą, oryginalną czy widok recenzji."));
    }

    private static void ValidateRanges(IReadOnlyList<InspectedElement> partElements)
    {
        foreach (var (startName, endName) in RangePairs)
        {
            var starts = GroupById(partElements, startName);
            var ends = GroupById(partElements, endName);

            ReportUnmatched(starts, ends, StructureIssueCodes.RevisionRangeEndMissing,
                "Brak końca zakresu", startName, endName);
            ReportUnmatched(ends, starts, StructureIssueCodes.RevisionRangeStartMissing,
                "Brak początku zakresu", endName, startName);
        }
    }

    private static Dictionary<string, List<InspectedElement>> GroupById(
        IReadOnlyList<InspectedElement> elements,
        string localName) =>
        elements
            .Where(element => OoxmlNamespaces.IsWordprocessing(element.NamespaceUri) && element.LocalName == localName)
            .GroupBy(GetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    private static void ReportUnmatched(
        IReadOnlyDictionary<string, List<InspectedElement>> source,
        IReadOnlyDictionary<string, List<InspectedElement>> counterpart,
        string code,
        string title,
        string sourceName,
        string counterpartName)
    {
        foreach (var group in source.Where(group => group.Key.Length == 0 || !counterpart.ContainsKey(group.Key)))
        {
            foreach (var element in group.Value)
            {
                element.Issues.Add(new StructureIssue(
                    code,
                    StructureIssueSeverity.Warning,
                    title,
                    $"w:{sourceName} o id='{group.Key}' nie ma pasującego w:{counterpartName}."));
            }
        }
    }

    private static string GetId(InspectedElement element) =>
        element.Attributes.FirstOrDefault(attribute => attribute.LocalName == "id")?.RawValue ?? string.Empty;
}
