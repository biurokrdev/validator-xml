using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TablePaginationPropertiesTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static string RowTag(string html, int index)
    {
        var i = -1;
        for (var k = 0; k <= index; k++) i = html.IndexOf("<tr", i + 1, StringComparison.Ordinal);
        return html.Substring(i, html.IndexOf('>', i) - i + 1);
    }

    [Test]
    public void CantSplit_inherited_from_table_style_is_effective_marker_not_direct()
    {
        using var ms = new MemoryStream(Build(styleCantSplit: true, directCantSplitRow1: false));
        var html = _reader.Convert(ms).Html;

        RowTag(html, 0).Should().Contain("data-cant-split-eff=\"1\"").And.NotContain("data-cant-split=\"1\"");
        RowTag(html, 1).Should().Contain("data-cant-split-eff=\"1\"");
    }

    [Test]
    public void Direct_cantSplit_wins_and_round_trips_as_direct_only()
    {
        using var ms = new MemoryStream(Build(styleCantSplit: true, directCantSplitRow1: true));
        var html = _reader.Convert(ms).Html;
        RowTag(html, 1).Should().Contain("data-cant-split=\"1\"").And.NotContain("data-cant-split-eff");

        using var orig = new MemoryStream(Build(styleCantSplit: true, directCantSplitRow1: true));
        using var saved = new MemoryStream(_writer.ConvertPreservingPackage(html, orig));
        using var doc = WordprocessingDocument.Open(saved, false);
        var rows = doc.MainDocumentPart!.Document.Body!.Descendants<TableRow>().ToList();
        rows[0].TableRowProperties?.Elements<CantSplit>().Any().Should().NotBe(true, "odziedziczone ze stylu nie staje się bezpośrednie");
        rows[1].TableRowProperties!.Elements<CantSplit>().Any().Should().BeTrue();
    }

    [Test]
    public void Direct_cantSplit_val_0_allows_split_despite_style()
    {
        using var ms = new MemoryStream(Build(styleCantSplit: true, directCantSplitRow1: true, directVal: "0"));
        var html = _reader.Convert(ms).Html;
        RowTag(html, 1).Should().Contain("data-cant-split=\"0\"").And.NotContain("data-cant-split-eff");
        RowTag(html, 0).Should().Contain("data-cant-split-eff=\"1\"");

        using var orig = new MemoryStream(Build(styleCantSplit: true, directCantSplitRow1: true, directVal: "0"));
        using var saved = new MemoryStream(_writer.ConvertPreservingPackage(html, orig));
        using var doc = WordprocessingDocument.Open(saved, false);
        var row1 = doc.MainDocumentPart!.Document.Body!.Descendants<TableRow>().ElementAt(1);
        var cs = row1.TableRowProperties!.Elements<CantSplit>().Single();
        cs.Val!.Value.Should().Be(OnOffOnlyValues.Off);
    }

    private static byte[] Build(bool styleCantSplit, bool directCantSplitRow1, string? directVal = null)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            TableRow Row(string text, bool cantSplit)
            {
                var tr = new TableRow();
                if (cantSplit)
                {
                    var cs = new CantSplit();
                    if (directVal != null) cs.SetAttribute(new OpenXmlAttribute("w", "val", "http://schemas.openxmlformats.org/wordprocessingml/2006/main", directVal));
                    tr.Append(new TableRowProperties(cs));
                }
                tr.Append(new TableCell(new TableCellProperties(new TableCellWidth { Width = "5000", Type = TableWidthUnitValues.Dxa }),
                    new Paragraph(new Run(new Text(text)))));
                return tr;
            }
            var tblPr = new TableProperties(new TableStyle { Val = "FixtureCantSplit" },
                new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto });
            var table = new Table(tblPr, new TableGrid(new GridColumn { Width = "5000" }), Row("a", false), Row("b", directCantSplitRow1));
            mainPart.Document = new Document(new Body(table,
                new SectionProperties(new PageSize { Width = 12240, Height = 15840 },
                    new PageMargin { Top = 1020, Bottom = 1020, Left = 1020, Right = 1020, Header = 397, Footer = 397 })));

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            var tableStyle = new Style(new StyleName { Val = "Fixture CantSplit Table" }) { Type = StyleValues.Table, StyleId = "FixtureCantSplit" };
            if (styleCantSplit)
                tableStyle.Append(new TableStyleConditionalFormattingTableRowProperties(new CantSplit()));
            stylesPart.Styles = new Styles(
                new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                tableStyle);
            stylesPart.Styles.Save();
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }
}
