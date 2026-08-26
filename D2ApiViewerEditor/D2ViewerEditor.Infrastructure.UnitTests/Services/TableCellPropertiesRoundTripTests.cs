using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TableCellPropertiesRoundTripTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private byte[] RoundTrip(byte[] original)
    {
        string html;
        using (var s = new MemoryStream(original)) html = _reader.Convert(s).Html;
        using var orig = new MemoryStream(original);
        return _writer.ConvertPreservingPackage(html, orig);
    }

    private static TableCell FirstCell(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        return doc.MainDocumentPart!.Document.Body!.Descendants<TableCell>().First();
    }

    [Test]
    public void Reader_emits_source_markers_on_cell()
    {
        using var ms = new MemoryStream(Build(tcwPct: true, tcMarStartEnd: true, hideMark: true, fitText: true));
        var html = _reader.Convert(ms).Html;
        var tdStart = html.IndexOf("<td", StringComparison.Ordinal);
        var td = html.Substring(tdStart, html.IndexOf('>', tdStart) - tdStart + 1);

        td.Should().Contain("data-tcw=\"3000:pct\"");
        td.Should().Contain("data-tcmar-tw=\"top=80;start=100;bottom=80;end=100\"");
        td.Should().Contain("data-hide-mark=\"1\"").And.Contain("data-fit-text=\"1\"");
        td.Should().Contain("padding:5px 6px 5px 6px;");
        html.Should().Contain("data-tbl-cell-mar-tw=\"0,108,0,108\"");
    }

    [Test]
    public void tcW_and_tcMar_survive_save_exactly()
    {
        var cell = FirstCell(RoundTrip(Build(tcwPct: true, tcMarStartEnd: true)));
        var props = cell.TableCellProperties!;

        var tcw = props.TableCellWidth!;
        tcw.Width!.Value.Should().Be("3000");
        tcw.Type!.Value.Should().Be(TableWidthUnitValues.Pct);

        var mar = props.TableCellMargin!;
        mar.TopMargin!.Width!.Value.Should().Be("80");
        mar.StartMargin!.Width!.Value.Should().Be("100", "oryginalna nazwa strony (w:start) zostaje");
        mar.LeftMargin.Should().BeNull();
        mar.EndMargin!.Width!.Value.Should().Be("100");
    }

    [Test]
    public void hideMark_fitText_and_negative_character_spacing_survive_save()
    {
        var docx = RoundTrip(Build(hideMark: true, fitText: true, charSpacing: -10));
        var cell = FirstCell(docx);

        cell.TableCellProperties!.HideMark.Should().NotBeNull();
        cell.TableCellProperties.TableCellFitText.Should().NotBeNull();
        var spacing = cell.Descendants<Spacing>().FirstOrDefault();
        spacing.Should().NotBeNull();
        spacing!.Val!.Value.Should().Be(-10);
    }

    [Test]
    public void Empty_paragraph_in_cell_stays_empty_not_nbsp()
    {
        var docx = RoundTrip(Build(emptyFirstParagraph: true));
        var cell = FirstCell(docx);

        var paras = cell.Elements<Paragraph>().ToList();
        paras.Should().HaveCount(2);
        paras[0].Descendants<Run>().Should().BeEmpty("pusty w:p nie może wrócić jako run z U+00A0");
        paras[1].InnerText.Should().Be("Tekst");
        cell.InnerText.Should().NotContain(" ");
    }

    [Test]
    public void Table_level_cell_margins_survive_save()
    {
        using var ms = new MemoryStream(Build(tblCellMar: 200));
        var html = _reader.Convert(ms).Html;
        html.Should().Contain("data-tbl-cell-mar-tw=\"200,200,200,200\"");

        using var orig = new MemoryStream(Build(tblCellMar: 200));
        using var saved = new MemoryStream(_writer.ConvertPreservingPackage(html, orig));
        using var doc = WordprocessingDocument.Open(saved, false);
        var mar = doc.MainDocumentPart!.Document.Body!.Descendants<TableCellMarginDefault>().First();
        mar.TopMargin!.Width!.Value.Should().Be("200");
        mar.TableCellLeftMargin!.Width!.Value.Should().Be((short)200);
        var cell = doc.MainDocumentPart.Document.Body.Descendants<TableCell>().First();
        cell.TableCellProperties!.TableCellMargin.Should().BeNull();
    }

    [Test]
    public void vMerge_continuation_cells_carry_origin_tcW_and_tcMar()
    {
        var html = "<table><colgroup><col style=\"width:343px;\" data-w-tw=\"5156\" /><col style=\"width:343px;\" data-w-tw=\"5156\" /></colgroup>"
            + "<tr><td rowspan=\"2\" data-tcw=\"5156:dxa\" data-tcmar-tw=\"top=80;start=100;bottom=80;end=100\">A</td><td>B</td></tr>"
            + "<tr><td>C</td></tr></table>";
        using var ms = new MemoryStream(_writer.Convert(html));
        using var doc = WordprocessingDocument.Open(ms, false);
        var rows = doc.MainDocumentPart!.Document.Body!.Descendants<TableRow>().ToList();
        var cont = rows[1].Elements<TableCell>().First().TableCellProperties!;

        cont.VerticalMerge.Should().NotBeNull();
        cont.VerticalMerge!.Val.Should().BeNull("kontynuacja = vMerge bez w:val");
        cont.TableCellWidth!.Width!.Value.Should().Be("5156");
        cont.TableCellMargin!.StartMargin!.Width!.Value.Should().Be("100");
        cont.Elements().Select(e => e.LocalName).ToList().Should().ContainInOrder("tcW", "vMerge", "tcMar");
    }

    private static byte[] Build(bool tcwPct = false, bool tcMarStartEnd = false, bool hideMark = false,
        bool fitText = false, int? charSpacing = null, bool emptyFirstParagraph = false, int? tblCellMar = null)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var tcPr = new TableCellProperties();
            tcPr.Append(tcwPct
                ? new TableCellWidth { Width = "3000", Type = TableWidthUnitValues.Pct }
                : new TableCellWidth { Width = "5156", Type = TableWidthUnitValues.Dxa });
            if (tcMarStartEnd)
                tcPr.Append(new TableCellMargin(
                    new TopMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new StartMargin { Width = "100", Type = TableWidthUnitValues.Dxa },
                    new BottomMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new EndMargin { Width = "100", Type = TableWidthUnitValues.Dxa }));
            if (fitText) tcPr.Append(new TableCellFitText());
            if (hideMark) tcPr.Append(new HideMark());

            var run = new Run(new Text("Tekst"));
            if (charSpacing.HasValue)
                run.PrependChild(new RunProperties(new Spacing { Val = charSpacing.Value }));
            var cell = new TableCell(tcPr);
            if (emptyFirstParagraph) cell.Append(new Paragraph());
            cell.Append(new Paragraph(run));

            var tblPr = new TableProperties(new TableStyle { Val = "TableGrid" });
            if (tblCellMar is { } m)
                tblPr.Append(new TableCellMarginDefault(
                    new TopMargin { Width = m.ToString(), Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = (short)m, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = m.ToString(), Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = (short)m, Type = TableWidthValues.Dxa }));
            tblPr.Append(new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto });

            var table = new Table(tblPr, new TableGrid(new GridColumn { Width = "5156" }), new TableRow(cell));
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("Przed."))), table,
                new SectionProperties(new PageSize { Width = 12240, Height = 15840 },
                    new PageMargin { Top = 964, Bottom = 964, Left = 964, Right = 964, Header = 720, Footer = 720 })));

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(
                new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                new Style(new StyleName { Val = "Table Grid" }) { Type = StyleValues.Table, StyleId = "TableGrid" });
            stylesPart.Styles.Save();
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }
}
