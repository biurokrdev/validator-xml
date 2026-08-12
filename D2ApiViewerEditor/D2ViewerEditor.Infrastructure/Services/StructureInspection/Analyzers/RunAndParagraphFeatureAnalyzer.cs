using System.Globalization;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class RunAndParagraphFeatureAnalyzer : IStructureAnalyzer
{
    private static readonly HashSet<string> ParagraphNonFormattingChildren = new(StringComparer.Ordinal)
    {
        "pStyle", "numPr", "sectPr", "pPrChange", "rPr"
    };

    private static readonly HashSet<string> RunNonFormattingChildren = new(StringComparer.Ordinal)
    {
        "rStyle", "rPrChange"
    };

    public void Analyze(StructureAnalysisContext context)
    {
        foreach (var node in context.WordprocessingNodes("pPr")
                     .Where(node => node.Node.Parent?.Name.LocalName == "p"))
        {
            AnalyzeParagraphProperties(node);
        }

        foreach (var node in context.WordprocessingNodes("rPr")
                     .Where(node => node.Node.Parent?.Name.LocalName == "r"))
        {
            AnalyzeRunProperties(node);
        }
    }

    private static void AnalyzeParagraphProperties(IndexedNode node)
    {
        var element = node.Element;

        if (HasFormattingChildren(node.Node, ParagraphNonFormattingChildren))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DirectFormattingPresent,
                StructureIssueSeverity.Info,
                "Formatowanie bezpośrednie akapitu",
                "Akapit niesie własne właściwości poza stylem. Redundancję względem stylów ocenia osobno analizator formatowania efektywnego."));
        }

        var indentation = OoxmlXml.Child(node.Node, "ind");

        foreach (var attributeName in new[] { "left", "start", "right", "end", "firstLine", "hanging" })
        {
            var value = OoxmlXml.AttributeLong(indentation, attributeName);

            if (value < 0)
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.NegativeIndentation,
                    StructureIssueSeverity.Warning,
                    "Ujemne wcięcie akapitu",
                    $"w:ind/@{attributeName}={value} — treść wychodzi poza margines i może być inaczej łamana niż w Wordzie."));
            }
        }

        var spacing = OoxmlXml.Child(node.Node, "spacing");
        var lineRule = OoxmlXml.Attribute(spacing, "lineRule");

        if (lineRule is "exact" or "atLeast")
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.ExactLineSpacing,
                StructureIssueSeverity.Info,
                "Sztywna interlinia",
                $"w:spacing/@lineRule={lineRule} — wysokość wiersza jest wymuszona, niezależnie od metryk czcionki."));
        }

        var tabStops = OoxmlXml.Children(OoxmlXml.Child(node.Node, "tabs"), "tab").Count();

        if (tabStops > 0)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.CustomTabStops,
                StructureIssueSeverity.Info,
                "Własne tabulatory",
                $"Akapit definiuje {tabStops} własnych pozycji tabulacji — pozycjonowanie treści zależy od nich, a nie od domyślnego kroku."));
        }
    }

    private static void AnalyzeRunProperties(IndexedNode node)
    {
        var element = node.Element;

        if (HasFormattingChildren(node.Node, RunNonFormattingChildren))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DirectFormattingPresent,
                StructureIssueSeverity.Info,
                "Formatowanie bezpośrednie runu",
                "Run niesie własne właściwości poza stylem znakowym."));
        }

        if (OoxmlXml.IsToggleEnabled(OoxmlXml.Child(node.Node, "vanish")))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.HiddenText,
                StructureIssueSeverity.Warning,
                "Tekst ukryty",
                "w:vanish — tekst jest ukryty w widoku Worda, ale nadal istnieje w dokumencie."));
        }

        var scaling = OoxmlXml.Val(OoxmlXml.Child(node.Node, "w"));

        if (scaling is not null && scaling != "100")
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.CharacterScaling,
                StructureIssueSeverity.Info,
                "Skalowanie znaków",
                $"w:w/@val={scaling}% — szerokość znaków jest przeskalowana względem czcionki."));
        }

        var characterSpacing = OoxmlXml.Val(OoxmlXml.Child(node.Node, "spacing"));

        if (characterSpacing is not null &&
            long.TryParse(characterSpacing, NumberStyles.Integer, CultureInfo.InvariantCulture, out var spacingValue) &&
            spacingValue != 0)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.CharacterSpacing,
                StructureIssueSeverity.Info,
                "Rozstrzelenie znaków",
                $"w:spacing/@val={characterSpacing} (dwudziesta część punktu) — odstęp między znakami jest zmieniony."));
        }

        if (OoxmlXml.Child(node.Node, "rtl") is not null || OoxmlXml.Child(node.Node, "bidi") is not null)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.BidirectionalText,
                StructureIssueSeverity.Warning,
                "Tekst dwukierunkowy",
                "Run deklaruje kierunek RTL/bidi — kolejność znaków w edytorze może różnić się od Worda."));
        }
    }

    private static bool HasFormattingChildren(XElement properties, IReadOnlySet<string> excluded) =>
        properties.Elements().Any(child => !excluded.Contains(child.Name.LocalName));
}
