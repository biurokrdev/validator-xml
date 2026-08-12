using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class SchemaIssueMapper
{
    public IReadOnlyList<SchemaValidationIssue> Map(
        IReadOnlyList<SchemaValidationIssue> issues,
        IReadOnlyList<InspectedElement> elements,
        bool annotateElements)
    {
        var byPart = elements
            .GroupBy(element => element.PartPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var mapped = new List<SchemaValidationIssue>(issues.Count);

        foreach (var issue in issues)
        {
            var element = FindBestElement(issue, byPart);

            if (element is not null && annotateElements)
            {
                element.Issues.Add(new StructureIssue(
                    $"{StructureIssueCodes.SchemaValidationPrefix}{issue.Code}",
                    StructureIssueSeverity.Error,
                    "Walidacja schematu Open XML",
                    issue.Description));
            }

            mapped.Add(issue with { ElementId = element?.Id });
        }

        return mapped;
    }

    private static InspectedElement? FindBestElement(
        SchemaValidationIssue issue,
        IReadOnlyDictionary<string, InspectedElement[]> byPart)
    {
        if (string.IsNullOrWhiteSpace(issue.PartPath) || !byPart.TryGetValue(issue.PartPath, out var candidates))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(issue.NodeName))
        {
            return candidates.FirstOrDefault(element => element.Depth == 0);
        }

        var byNodeName = candidates
            .Where(element => element.LocalName.Equals(issue.NodeName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (byNodeName.Length <= 1 || string.IsNullOrWhiteSpace(issue.Path))
        {
            return byNodeName.FirstOrDefault();
        }

        return byNodeName
            .OrderByDescending(element => PathSimilarity(element.DisplayPath, issue.Path))
            .First();
    }

    private static int PathSimilarity(string displayPath, string validationPath)
    {
        var score = 0;

        foreach (var token in displayPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var localName = token.Split(':').Last().Split('[').First();

            if (validationPath.Contains(localName, StringComparison.OrdinalIgnoreCase))
            {
                score++;
            }
        }

        return score;
    }
}
