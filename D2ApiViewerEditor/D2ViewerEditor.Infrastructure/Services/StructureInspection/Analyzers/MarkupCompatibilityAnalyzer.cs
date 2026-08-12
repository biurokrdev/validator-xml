using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class MarkupCompatibilityAnalyzer : IStructureAnalyzer
{
    private static readonly string[] CompatibilityAttributes =
    [
        "Ignorable", "MustUnderstand", "ProcessContent", "PreserveElements", "PreserveAttributes"
    ];

    public void Analyze(StructureAnalysisContext context)
    {
        foreach (var node in context.Nodes.Where(node =>
                     node.Element.NamespaceUri == OoxmlNamespaces.MarkupCompatibility &&
                     node.Element.LocalName == "AlternateContent"))
        {
            AnalyzeAlternateContent(context, node);
        }

        foreach (var node in context.Nodes.Where(node => node.Element.Depth == 0))
        {
            AnalyzeRootAttributes(node);
        }

        foreach (var node in context.Nodes.Where(node => node.Element.Category == ElementCategories.UnknownNamespace))
        {
            node.Element.Issues.Add(new StructureIssue(
                StructureIssueCodes.UnknownNamespace,
                StructureIssueSeverity.Info,
                "Nieznane rozszerzenie OOXML",
                $"Element należy do namespace '{node.Element.NamespaceUri}', którego konwerter nie interpretuje. Treść pozostaje widoczna w surowym XML."));
        }
    }

    private static void AnalyzeAlternateContent(StructureAnalysisContext context, IndexedNode node)
    {
        var element = node.Element;
        var choices = node.Node.Elements()
            .Where(child => child.Name.NamespaceName == OoxmlNamespaces.MarkupCompatibility && child.Name.LocalName == "Choice")
            .ToArray();
        var fallback = node.Node.Elements().FirstOrDefault(child =>
            child.Name.NamespaceName == OoxmlNamespaces.MarkupCompatibility && child.Name.LocalName == "Fallback");

        element.Properties.Add(new StructureProperty("Liczba gałęzi Choice", choices.Length.ToString(), "mc:AlternateContent"));
        element.Properties.Add(new StructureProperty("Gałąź Fallback", fallback is not null ? "tak" : "nie", "mc:AlternateContent"));
        element.Issues.Add(new StructureIssue(
            StructureIssueCodes.AlternateContent,
            StructureIssueSeverity.Warning,
            "Treść alternatywna",
            "mc:AlternateContent — Word i nasz edytor mogą wybrać różne gałęzie Choice/Fallback tej samej treści."));

        var selectedChoice = -1;

        for (var index = 0; index < choices.Length; index++)
        {
            var requires = OoxmlXml.Attribute(choices[index], "Requires");
            var namespaces = ResolveRequiredNamespaces(choices[index], requires);
            var isKnown = namespaces.Count > 0 && namespaces.All(IsKnownNamespace);

            element.Properties.Add(new StructureProperty(
                $"Choice {index + 1}",
                $"Requires={requires ?? "(brak)"}; namespaces={string.Join(", ", namespaces)}; rozpoznany={(isKnown ? "tak" : "nie")}",
                "mc:Choice"));

            if (selectedChoice < 0 && isKnown)
            {
                selectedChoice = index;
            }

            if (string.IsNullOrWhiteSpace(requires))
            {
                context.FindElement(choices[index])?.Issues.Add(new StructureIssue(
                    StructureIssueCodes.ChoiceRequiresMissing,
                    StructureIssueSeverity.Error,
                    "Brak atrybutu Requires",
                    "mc:Choice nie deklaruje listy prefiksów w atrybucie Requires."));
            }
        }

        element.Properties.Add(new StructureProperty(
            "Gałąź wybrana przez walidator",
            selectedChoice >= 0 ? $"Choice {selectedChoice + 1}" : fallback is not null ? "Fallback" : "brak",
            "Markup Compatibility",
            "na podstawie namespace rozpoznawanych przez walidator; inny konsument może wybrać inaczej"));

        if (fallback is null)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.AlternateContentNoFallback,
                StructureIssueSeverity.Warning,
                "Brak gałęzi Fallback",
                "Konsument nierozumiejący żadnej gałęzi Choice nie ma czym odtworzyć tej treści."));
        }
    }

    private static void AnalyzeRootAttributes(IndexedNode node)
    {
        foreach (var attributeName in CompatibilityAttributes)
        {
            var value = node.Node.Attributes().FirstOrDefault(attribute =>
                attribute.Name.NamespaceName == OoxmlNamespaces.MarkupCompatibility &&
                attribute.Name.LocalName == attributeName)?.Value;

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var resolved = value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => ResolveToken(node.Node, token))
                .ToArray();

            node.Element.Properties.Add(new StructureProperty(
                $"mc:{attributeName}",
                string.Join("; ", resolved),
                "Markup Compatibility"));

            if (resolved.Any(item => item.EndsWith("(nierozwiązany)", StringComparison.Ordinal)))
            {
                node.Element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.CompatibilityPrefixUnresolved,
                    StructureIssueSeverity.Warning,
                    "Nierozwiązany prefiks kompatybilności",
                    $"mc:{attributeName} zawiera prefiks, którego nie da się rozwiązać w deklaracjach namespace korzenia części."));
            }
        }
    }

    private static string ResolveToken(XElement source, string token)
    {
        var separator = token.IndexOf(':');
        var prefix = separator < 0 ? token : token[..separator];
        var namespaceUri = source.GetNamespaceOfPrefix(prefix)?.NamespaceName;

        return $"{token}={namespaceUri ?? "(nierozwiązany)"}";
    }

    private static IReadOnlyList<string> ResolveRequiredNamespaces(XElement choice, string? requires)
    {
        if (string.IsNullOrWhiteSpace(requires))
        {
            return [];
        }

        return requires
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(prefix => choice.GetNamespaceOfPrefix(prefix)?.NamespaceName ?? $"(nierozwiązany:{prefix})")
            .ToArray();
    }

    private static bool IsKnownNamespace(string namespaceUri) =>
        OoxmlNamespaces.IsWordprocessing(namespaceUri) ||
        OoxmlNamespaces.IsWordprocessingDrawing(namespaceUri) ||
        OoxmlNamespaces.IsDrawingMain(namespaceUri) ||
        OoxmlNamespaces.IsVml(namespaceUri) ||
        namespaceUri == OoxmlNamespaces.MarkupCompatibility ||
        namespaceUri.Contains("schemas.microsoft.com/office", StringComparison.OrdinalIgnoreCase);
}
