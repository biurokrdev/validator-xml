using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class SectionLayoutAnalyzer : IStructureAnalyzer
{
    public void Analyze(StructureAnalysisContext context)
    {
        var sectionNumber = 0;

        foreach (var node in context.WordprocessingNodes("sectPr")
                     .Where(node => node.Element.PartPath.Equals(context.MainDocumentPartPath, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(node => node.Element.Order))
        {
            AnalyzeSection(node, ++sectionNumber);
        }
    }

    private static void AnalyzeSection(IndexedNode node, int sectionNumber)
    {
        var element = node.Element;
        var source = node.Node;

        element.Properties.Add(new StructureProperty("Numer sekcji", sectionNumber.ToString(), PropertySources.DocumentStructure));
        element.Properties.Add(new StructureProperty(
            "Typ podziału sekcji",
            OoxmlXml.Val(OoxmlXml.Child(source, "type")) ?? "nextPage (domyślnie)",
            "w:type"));

        AddPageSize(element, node);
        AddMargins(element, node, sectionNumber);
        AddColumns(element, node, sectionNumber);

        AddDescribedChild(element, node, "pgNumType", "Numeracja stron");
        AddDescribedChild(element, node, "lnNumType", "Numeracja wierszy");
        AddDescribedChild(element, node, "vAlign", "Wyrównanie pionowe");
        AddDescribedChild(element, node, "textDirection", "Kierunek tekstu");
        AddDescribedChild(element, node, "docGrid", "Siatka dokumentu");
        AddDescribedChild(element, node, "footnotePr", "Ustawienia przypisów dolnych");
        AddDescribedChild(element, node, "endnotePr", "Ustawienia przypisów końcowych");

        if (OoxmlXml.Child(source, "pgBorders") is { } pageBorders)
        {
            element.Properties.Add(new StructureProperty("Obramowania strony", OoxmlXml.DescribeAttributes(pageBorders), "w:pgBorders"));
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.SectionPageBorders,
                StructureIssueSeverity.Info,
                "Obramowania strony",
                $"Sekcja {sectionNumber} definiuje obramowania strony."));
        }
    }

    private static void AddPageSize(InspectedElement element, IndexedNode node)
    {
        var pageSize = OoxmlXml.Child(node.Node, "pgSz");

        if (pageSize is null)
        {
            return;
        }

        var width = OoxmlXml.AttributeLong(pageSize, "w");
        var height = OoxmlXml.AttributeLong(pageSize, "h");
        var orientation = OoxmlXml.Attribute(pageSize, "orient")
                          ?? (width > height ? "landscape (wywnioskowana)" : "portrait (wywnioskowana)");

        element.Properties.Add(new StructureProperty(
            "Rozmiar strony",
            $"szer.={OoxmlXml.FormatTwips(width)}; wys.={OoxmlXml.FormatTwips(height)}",
            "w:pgSz"));
        element.Properties.Add(new StructureProperty("Orientacja", orientation, "w:pgSz"));
    }

    private static void AddMargins(InspectedElement element, IndexedNode node, int sectionNumber)
    {
        var margins = OoxmlXml.Child(node.Node, "pgMar");

        if (margins is null)
        {
            return;
        }

        element.Properties.Add(new StructureProperty("Marginesy strony", OoxmlXml.DescribeTwipsAttributes(margins), "w:pgMar"));

        foreach (var name in new[] { "top", "right", "bottom", "left", "header", "footer", "gutter" })
        {
            var value = OoxmlXml.AttributeLong(margins, name);

            if (value < 0)
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.SectionNegativeMargin,
                    StructureIssueSeverity.Warning,
                    "Ujemny margines sekcji",
                    $"Sekcja {sectionNumber} ma ujemny margines '{name}' ({value} twipów)."));
            }
        }
    }

    private static void AddColumns(InspectedElement element, IndexedNode node, int sectionNumber)
    {
        var columns = OoxmlXml.Child(node.Node, "cols");

        if (columns is null)
        {
            return;
        }

        var count = OoxmlXml.Attribute(columns, "num") ?? "1";
        var explicitColumns = OoxmlXml.Children(columns, "col").Count();

        element.Properties.Add(new StructureProperty(
            "Kolumny",
            $"num={count}; equalWidth={OoxmlXml.Attribute(columns, "equalWidth") ?? "true (domyślnie)"}; " +
            $"space={OoxmlXml.FormatTwips(OoxmlXml.AttributeLong(columns, "space"))}; jawnych kolumn={explicitColumns}",
            "w:cols"));

        if (int.TryParse(count, out var columnCount) && columnCount > 1)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.SectionMultiColumn,
                StructureIssueSeverity.Info,
                "Sekcja wielokolumnowa",
                $"Sekcja {sectionNumber} używa {columnCount} kolumn tekstu."));
        }
    }

    private static void AddDescribedChild(InspectedElement element, IndexedNode node, string localName, string label)
    {
        if (OoxmlXml.Child(node.Node, localName) is { } child)
        {
            element.Properties.Add(new StructureProperty(label, OoxmlXml.DescribeAttributes(child), $"w:{localName}"));
        }
    }
}
