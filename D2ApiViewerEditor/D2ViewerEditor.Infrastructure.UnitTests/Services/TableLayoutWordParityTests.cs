using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TableLayoutWordParityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static List<int> ColWidths(string html, int tableStart = 0)
    {
        var cg = html.IndexOf("<colgroup>", tableStart, StringComparison.Ordinal);
        var end = html.IndexOf("</colgroup>", cg, StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Matches(html.Substring(cg, end - cg), "width:(\\d+)px")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
    }

    private static string TableTag(string html, int from = 0)
    {
        var i = html.IndexOf("<table", from, StringComparison.Ordinal);
        return html.Substring(i, html.IndexOf('>', i) - i + 1);
    }

    [Test]
    public void Autofit_without_tblW_uses_tcW_preferred_widths_not_stale_grid()
    {
        using var ms = new MemoryStream(Build(layoutFixed: false, tblWDxa: null, grid: new[] { 1800, 3000, 2400 }, tcW: 3437));
        var html = _reader.Convert(ms).Html;

        ColWidths(html).Should().Equal(new[] { 229, 229, 229 });
        html.Should().Contain("data-w-tw=\"3437\"");
    }

    [Test]
    public void Fixed_layout_keeps_grid_even_if_tcW_differs()
    {
        using var ms = new MemoryStream(Build(layoutFixed: true, tblWDxa: 7200, grid: new[] { 1800, 3000, 2400 }, tcW: 3437));
        var html = _reader.Convert(ms).Html;

        ColWidths(html).Should().Equal(new[] { 120, 200, 160 });
    }

    [Test]
    public void Autofit_tcW_wider_than_page_falls_back_to_grid()
    {
        using var ms = new MemoryStream(Build(layoutFixed: false, tblWDxa: null, grid: new[] { 1800, 3000, 2400 }, tcW: 6000));
        var html = _reader.Convert(ms).Html;

        ColWidths(html).Should().Equal(new[] { 120, 200, 160 });
    }

    [Test]
    public void Cell_spacing_renders_double_gap_and_outer_table_border_and_round_trips()
    {
        using var ms = new MemoryStream(Build(layoutFixed: false, tblWDxa: null, grid: new[] { 3437, 3437, 3437 }, tcW: 3437, cellSpacing: 120));
        var html = _reader.Convert(ms).Html;
        var tag = TableTag(html);

        tag.Should().Contain("border-spacing:16px", "odstęp między komórkami = 2 × 120 tw (pomiar PDF: 12 pt)");
        tag.Should().MatchRegex("border-top:[^;]*#808080").And.MatchRegex("border-left:[^;]*#808080")
            .And.MatchRegex("border-bottom:[^;]*#808080").And.MatchRegex("border-right:[^;]*#808080");
        tag.Should().NotContain("border:", "skrót border: dałby writerowi także insideH/V");
        tag.Should().Contain("width:735px").And.Contain("position:relative;left:-24px;");
        tag.Should().NotContain("margin-left:-", "margin-left wróciłby jako ujemny w:tblInd");

        using var orig = new MemoryStream(Build(layoutFixed: false, tblWDxa: null, grid: new[] { 3437, 3437, 3437 }, tcW: 3437, cellSpacing: 120));
        using var saved = new MemoryStream(_writer.ConvertPreservingPackage(html, orig));
        using var doc = WordprocessingDocument.Open(saved, false);
        var tblPr = doc.MainDocumentPart!.Document.Body!.Descendants<TableProperties>().First();
        tblPr.GetFirstChild<TableCellSpacing>()!.Width!.Value.Should().Be("120");
        var borders = tblPr.GetFirstChild<TableBorders>();
        borders.Should().NotBeNull("obramowanie zewnętrzne tabeli wraca do tblBorders");
        borders!.TopBorder!.Color!.Value.Should().Be("808080");
        borders.LeftBorder.Should().NotBeNull();
        borders.BottomBorder.Should().NotBeNull();
        borders.RightBorder.Should().NotBeNull();
        borders.InsideHorizontalBorder.Should().BeNull("longhandy niosą tylko krawędzie zewnętrzne");
        borders.InsideVerticalBorder.Should().BeNull();
    }

    [Test]
    public void Nested_table_is_clamped_to_cell_inner_width()
    {
        using var ms = new MemoryStream(Build(layoutFixed: false, tblWDxa: null, grid: new[] { 10312 }, tcW: 10312, nested: true, tcMarSide: 100));
        var html = _reader.Convert(ms).Html;
        var outerCols = ColWidths(html);
        var nestedStart = html.IndexOf("<table", html.IndexOf("<td", StringComparison.Ordinal), StringComparison.Ordinal);
        var nestedCols = ColWidths(html, nestedStart);

        outerCols.Should().Equal(new[] { 687 });
        nestedCols.Sum().Should().BeLessThanOrEqualTo(674).And.BeGreaterThan(660);
    }

    private static byte[] Build(bool layoutFixed, int? tblWDxa, int[] grid, int tcW, int? cellSpacing = null,
        bool nested = false, int tcMarSide = 100)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            TableCell Cell(string text, int w, bool withNested)
            {
                var tcPr = new TableCellProperties(
                    new TableCellWidth { Width = w.ToString(), Type = TableWidthUnitValues.Dxa },
                    new TableCellMargin(
                        new TopMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                        new StartMargin { Width = tcMarSide.ToString(), Type = TableWidthUnitValues.Dxa },
                        new BottomMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                        new EndMargin { Width = tcMarSide.ToString(), Type = TableWidthUnitValues.Dxa }));
                var cell = new TableCell(tcPr, new Paragraph(new Run(new Text(text))));
                if (withNested)
                {
                    var nestedTbl = new Table(
                        new TableProperties(new TableStyle { Val = "TableGrid" }, new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto }),
                        new TableGrid(new GridColumn { Width = "5156" }, new GridColumn { Width = "5156" }),
                        new TableRow(
                            new TableCell(new TableCellProperties(new TableCellWidth { Width = "5156", Type = TableWidthUnitValues.Dxa }), new Paragraph(new Run(new Text("nested")))),
                            new TableCell(new TableCellProperties(new TableCellWidth { Width = "5156", Type = TableWidthUnitValues.Dxa }), new Paragraph(new Run(new Text("table"))))));
                    cell.Append(nestedTbl);
                    cell.Append(new Paragraph());
                }
                return cell;
            }

            var tblPr = new TableProperties(new TableStyle { Val = "TableGrid" });
            tblPr.Append(tblWDxa is { } wd
                ? new TableWidth { Width = wd.ToString(), Type = TableWidthUnitValues.Dxa }
                : new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto });
            tblPr.Append(new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                new StartBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                new BottomBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                new EndBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 8, Color = "808080" }));
            tblPr.Append(new TableLayout { Type = layoutFixed ? TableLayoutValues.Fixed : TableLayoutValues.Autofit });
            if (cellSpacing is { } cs)
                tblPr.Append(new TableCellSpacing { Width = cs.ToString(), Type = TableWidthUnitValues.Dxa });

            var tblGrid = new TableGrid(grid.Select(g => new GridColumn { Width = g.ToString() }));
            var row = new TableRow();
            for (var i = 0; i < grid.Length; i++) row.Append(Cell("c" + i, tcW, nested && i == 0));
            var table = new Table(tblPr, tblGrid, row);

            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("Przed."))), table,
                new SectionProperties(new PageSize { Width = 12240, Height = 15840 },
                    new PageMargin { Top = 964, Bottom = 964, Left = 964, Right = 964, Header = 720, Footer = 720 })));

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(
                new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                new Style(new StyleName { Val = "Table Grid" },
                    new StyleTableProperties(new TableBorders(
                        new TopBorder { Val = BorderValues.Single, Size = 4, Color = "000000" },
                        new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "000000" },
                        new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "000000" },
                        new RightBorder { Val = BorderValues.Single, Size = 4, Color = "000000" },
                        new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "000000" },
                        new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "000000" })))
                { Type = StyleValues.Table, StyleId = "TableGrid" });
            stylesPart.Styles.Save();
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }
}
