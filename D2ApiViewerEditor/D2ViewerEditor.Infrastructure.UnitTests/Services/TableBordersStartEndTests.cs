using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TableBordersStartEndTests
{
    private DocxToHtmlConverter _reader = null!;

    [SetUp]
    public void SetUp() => _reader = new DocxToHtmlConverter();

    [Test]
    public void Table_level_start_end_borders_win_over_style_left_right()
    {
        using var ms = new MemoryStream(Build(cellLevel: false));
        var html = _reader.Convert(ms).Html;
        var tds = TdTags(html);

        tds.Should().HaveCount(2);
        tds[0].Should().Contain("border-left:").And.MatchRegex("border-left:[^;]*#808080");
        tds[0].Should().NotMatchRegex("border-left:[^;]*#000000", "w:start (808080) ma pierwszeństwo przed stylem TableGrid");
        tds[1].Should().MatchRegex("border-right:[^;]*#808080");
    }

    [Test]
    public void Cell_level_start_end_borders_are_read()
    {
        using var ms = new MemoryStream(Build(cellLevel: true));
        var html = _reader.Convert(ms).Html;
        var tds = TdTags(html);

        tds[0].Should().MatchRegex("border-left:[^;]*#FF0000");
        tds[0].Should().MatchRegex("border-right:[^;]*#FF0000");
    }

    private static List<string> TdTags(string html)
    {
        var list = new List<string>();
        var idx = 0;
        while ((idx = html.IndexOf("<td", idx, StringComparison.Ordinal)) >= 0)
        {
            var end = html.IndexOf('>', idx);
            list.Add(html.Substring(idx, end - idx + 1));
            idx = end;
        }
        return list;
    }

    private static byte[] Build(bool cellLevel)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            TableCell Cell(string text)
            {
                var tcPr = new TableCellProperties(new TableCellWidth { Width = "5156", Type = TableWidthUnitValues.Dxa });
                if (cellLevel)
                    tcPr.Append(new TableCellBorders(
                        new StartBorder { Val = BorderValues.Single, Size = 8, Color = "FF0000" },
                        new EndBorder { Val = BorderValues.Single, Size = 8, Color = "FF0000" }));
                return new TableCell(tcPr, new Paragraph(new Run(new Text(text))));
            }

            var tblPr = new TableProperties(new TableStyle { Val = "TableGrid" });
            if (!cellLevel)
                tblPr.Append(new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                    new StartBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                    new BottomBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                    new EndBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 8, Color = "808080" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 8, Color = "808080" }));
            tblPr.Append(new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto });

            var table = new Table(tblPr,
                new TableGrid(new GridColumn { Width = "5156" }, new GridColumn { Width = "5156" }),
                new TableRow(Cell("A"), Cell("B")));
            mainPart.Document = new Document(new Body(table,
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
