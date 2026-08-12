using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class TableAnalyzer : IStructureAnalyzer
{
    public void Analyze(StructureAnalysisContext context)
    {
        foreach (var node in context.WordprocessingNodes("tbl"))
        {
            AnalyzeTable(context, node);
        }
    }

    private static void AnalyzeTable(StructureAnalysisContext context, IndexedNode node)
    {
        var table = node.Element;
        var tableProperties = OoxmlXml.Child(node.Node, "tblPr");
        var grid = OoxmlXml.Child(node.Node, "tblGrid");
        var rows = OoxmlXml.Children(node.Node, "tr").ToArray();
        var gridColumns = OoxmlXml.Children(grid, "gridCol").ToArray();

        table.Properties.Add(new StructureProperty("Wiersze", rows.Length.ToString(), "w:tbl"));
        table.Properties.Add(new StructureProperty("Kolumny siatki", gridColumns.Length.ToString(), "w:tblGrid"));
        table.Properties.Add(new StructureProperty("Szerokości siatki", DescribeGridWidths(gridColumns), "w:tblGrid"));
        table.Properties.Add(new StructureProperty("Szerokość tabeli", DescribeWidth(OoxmlXml.Child(tableProperties, "tblW")), "w:tblW"));
        table.Properties.Add(new StructureProperty("Układ tabeli", OoxmlXml.Attribute(OoxmlXml.Child(tableProperties, "tblLayout"), "type") ?? "autofit", "w:tblLayout"));
        table.Properties.Add(new StructureProperty("Wcięcie tabeli", OoxmlXml.DescribeAttributes(OoxmlXml.Child(tableProperties, "tblInd")), "w:tblInd"));
        table.Properties.Add(new StructureProperty("Marginesy komórek", OoxmlXml.DescribeAttributes(OoxmlXml.Child(tableProperties, "tblCellMar")), "w:tblCellMar"));
        table.Properties.Add(new StructureProperty("Obramowania tabeli", OoxmlXml.DescribeAttributes(OoxmlXml.Child(tableProperties, "tblBorders")), "w:tblBorders"));

        AnalyzeTablePlacement(node, table, tableProperties);
        AnalyzeGrid(context, node, table, gridColumns.Length, rows);
        AnalyzeVerticalMerges(context, table, rows, gridColumns.Length);
    }

    private static void AnalyzeTablePlacement(IndexedNode node, InspectedElement table, XElement? tableProperties)
    {
        var floating = OoxmlXml.Child(tableProperties, "tblpPr");

        if (floating is not null)
        {
            table.Properties.Add(new StructureProperty("Tabela pływająca", OoxmlXml.DescribeAttributes(floating), "w:tblpPr"));
            table.Issues.Add(new StructureIssue(
                StructureIssueCodes.TableFloating,
                StructureIssueSeverity.Warning,
                "Tabela pływająca",
                "w:tblpPr — tabela jest pozycjonowana niezależnie od przepływu tekstu i oblewana treścią."));
        }

        if (node.Node.Ancestors().Any(ancestor =>
                OoxmlNamespaces.IsWordprocessing(ancestor.Name.NamespaceName) && ancestor.Name.LocalName == "tc"))
        {
            table.Issues.Add(new StructureIssue(
                StructureIssueCodes.TableNested,
                StructureIssueSeverity.Info,
                "Tabela zagnieżdżona",
                "Tabela leży wewnątrz komórki innej tabeli."));
        }

        if (OoxmlXml.Attribute(OoxmlXml.Child(tableProperties, "tblW"), "type") == "auto")
        {
            table.Issues.Add(new StructureIssue(
                StructureIssueCodes.TableAutoWidth,
                StructureIssueSeverity.Info,
                "Szerokość tabeli auto",
                "w:tblW/@type=auto — szerokość wynika z zawartości (shrink-to-fit), a nie z jawnej wartości."));
        }
    }

    private static void AnalyzeGrid(
        StructureAnalysisContext context,
        IndexedNode node,
        InspectedElement table,
        int gridColumnCount,
        IReadOnlyList<XElement> rows)
    {
        if (OoxmlXml.Child(node.Node, "tblGrid") is null)
        {
            table.Issues.Add(new StructureIssue(
                StructureIssueCodes.TableGridMissing,
                StructureIssueSeverity.Error,
                "Brak siatki tabeli",
                "Tabela nie ma w:tblGrid — szerokości kolumn są niedookreślone."));
            return;
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var rowProperties = OoxmlXml.Child(row, "trPr");
            var gridBefore = OoxmlXml.ChildInt(rowProperties, "gridBefore") ?? 0;
            var gridAfter = OoxmlXml.ChildInt(rowProperties, "gridAfter") ?? 0;
            var cells = OoxmlXml.Children(row, "tc").ToArray();
            var occupied = gridBefore + gridAfter + cells.Sum(GetGridSpan);

            if (gridBefore > 0 || gridAfter > 0)
            {
                (context.FindElement(row) ?? table).Properties.Add(new StructureProperty(
                    "Wiersz wcięty w siatce",
                    $"gridBefore={gridBefore}; gridAfter={gridAfter}",
                    "w:trPr"));
                (context.FindElement(row) ?? table).Issues.Add(new StructureIssue(
                    StructureIssueCodes.TableRowGridOffset,
                    StructureIssueSeverity.Info,
                    "Wiersz nie zaczyna się w pierwszej kolumnie",
                    $"w:gridBefore={gridBefore}, w:gridAfter={gridAfter} — wiersz zajmuje wycinek siatki."));
            }

            if (gridColumnCount > 0 && occupied != gridColumnCount)
            {
                (context.FindElement(row) ?? table).Issues.Add(new StructureIssue(
                    StructureIssueCodes.TableGridMismatch,
                    StructureIssueSeverity.Warning,
                    "Rozjazd wiersza i siatki",
                    $"Wiersz {rowIndex + 1} zajmuje {occupied} kolumn siatki, a w:tblGrid deklaruje {gridColumnCount}."));
            }

            AnnotateCells(context, cells);
        }
    }

    private static void AnnotateCells(StructureAnalysisContext context, IReadOnlyList<XElement> cells)
    {
        foreach (var cell in cells)
        {
            var element = context.FindElement(cell);

            if (element is null)
            {
                continue;
            }

            var cellProperties = OoxmlXml.Children(cell, "tcPr").ToArray();
            var width = cellProperties.Select(properties => OoxmlXml.Child(properties, "tcW")).FirstOrDefault(found => found is not null);
            var verticalMerge = cellProperties.Select(properties => OoxmlXml.Child(properties, "vMerge")).FirstOrDefault(found => found is not null);

            element.Properties.Add(new StructureProperty("Scalenie poziome (gridSpan)", GetGridSpan(cell).ToString(), "w:gridSpan"));
            element.Properties.Add(new StructureProperty("Szerokość komórki", DescribeWidth(width), "w:tcW"));

            if (verticalMerge is not null)
            {
                element.Properties.Add(new StructureProperty(
                    "Scalenie pionowe (vMerge)",
                    OoxmlXml.Val(verticalMerge) ?? "continue",
                    "w:vMerge"));
            }

            if (cellProperties.Length > 1)
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.TableCellDuplicateProperties,
                    StructureIssueSeverity.Warning,
                    "Kilka bloków w:tcPr w komórce",
                    $"Komórka zawiera {cellProperties.Length} bloków w:tcPr (poza schematem). Open XML SDK widzi tylko pierwszy — właściwości z pozostałych są niewidoczne dla warstwy semantycznej."));
            }

            if (cellProperties.Any(properties => OoxmlXml.Child(properties, "hMerge") is not null))
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.TableCellHorizontalMerge,
                    StructureIssueSeverity.Info,
                    "Scalenie poziome hMerge",
                    "Komórka używa w:hMerge zamiast w:gridSpan — starszy wariant scalenia poziomego."));
            }
        }
    }

    private static void AnalyzeVerticalMerges(
        StructureAnalysisContext context,
        InspectedElement table,
        IReadOnlyList<XElement> rows,
        int gridColumnCount)
    {
        if (gridColumnCount <= 0)
        {
            return;
        }

        var mergeActive = new bool[gridColumnCount];

        foreach (var row in rows)
        {
            var column = OoxmlXml.ChildInt(OoxmlXml.Child(row, "trPr"), "gridBefore") ?? 0;

            foreach (var cell in OoxmlXml.Children(row, "tc"))
            {
                var span = GetGridSpan(cell);
                var verticalMerge = OoxmlXml.Children(cell, "tcPr")
                    .Select(properties => OoxmlXml.Child(properties, "vMerge"))
                    .FirstOrDefault(found => found is not null);
                var lastColumn = Math.Min(column + span, gridColumnCount);

                if (verticalMerge is null)
                {
                    SetRange(mergeActive, column, lastColumn, false);
                    column += span;
                    continue;
                }

                var isRestart = string.Equals(OoxmlXml.Val(verticalMerge), "restart", StringComparison.OrdinalIgnoreCase);

                if (!isRestart && !AllActive(mergeActive, column, lastColumn))
                {
                    (context.FindElement(cell) ?? table).Issues.Add(new StructureIssue(
                        StructureIssueCodes.TableVerticalMergeWithoutRestart,
                        StructureIssueSeverity.Warning,
                        "Kontynuacja vMerge bez początku",
                        "Komórka kontynuuje scalenie pionowe, choć w poprzednim wierszu nie było aktywnego scalenia dla tych kolumn siatki."));
                }

                SetRange(mergeActive, column, lastColumn, true);
                column += span;
            }
        }
    }

    private static void SetRange(bool[] values, int start, int end, bool value)
    {
        for (var index = Math.Max(0, start); index < end; index++)
        {
            values[index] = value;
        }
    }

    private static bool AllActive(bool[] values, int start, int end)
    {
        for (var index = Math.Max(0, start); index < end; index++)
        {
            if (!values[index])
            {
                return false;
            }
        }

        return end > start;
    }

    private static int GetGridSpan(XElement cell)
    {
        var span = OoxmlXml.Children(cell, "tcPr")
            .Select(properties => OoxmlXml.Val(OoxmlXml.Child(properties, "gridSpan")))
            .FirstOrDefault(value => value is not null);

        return int.TryParse(span, out var value) && value > 0 ? value : 1;
    }

    private static string? DescribeGridWidths(IReadOnlyList<XElement> columns)
    {
        var widths = columns
            .Select(column => OoxmlXml.Attribute(column, "w") ?? OoxmlXml.Attribute(column, "val"))
            .Where(value => value is not null)
            .ToArray();

        return widths.Length == 0 ? null : string.Join(", ", widths);
    }

    private static string? DescribeWidth(XElement? width)
    {
        if (width is null)
        {
            return null;
        }

        var value = OoxmlXml.Attribute(width, "w");
        var type = OoxmlXml.Attribute(width, "type");

        return type switch
        {
            "pct" when int.TryParse(value, out var percent) => $"{percent / 50d:0.##}% (pct {value})",
            "dxa" => $"{OoxmlXml.FormatTwips(value)} (dxa)",
            "auto" => "auto (shrink-to-fit)",
            "nil" => "brak",
            _ => value is null ? type : $"{value} ({type})"
        };
    }
}
