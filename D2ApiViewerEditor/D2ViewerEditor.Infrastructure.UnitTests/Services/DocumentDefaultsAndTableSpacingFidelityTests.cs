using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class DocumentDefaultsAndTableSpacingFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }


    private static MemoryStream BuildQutableLikeDocx()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(
                new DocDefaults(
                    new RunPropertiesDefault(new RunPropertiesBaseStyle(
                        new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" },
                        new FontSize { Val = "24" })),
                    new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                        new SpacingBetweenLines { After = "160", Line = "278", LineRule = LineSpacingRuleValues.Auto }))),
                new Style(
                    new StyleParagraphProperties(
                        new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
                    new StyleTableProperties(new TableBorders(
                        new TopBorder { Val = BorderValues.Single, Size = 4 },
                        new LeftBorder { Val = BorderValues.Single, Size = 4 },
                        new BottomBorder { Val = BorderValues.Single, Size = 4 },
                        new RightBorder { Val = BorderValues.Single, Size = 4 },
                        new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                        new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })))
                {
                    Type = StyleValues.Table,
                    StyleId = "Tabela-Siatka",
                    StyleName = new StyleName { Val = "Table Grid" }
                });
            stylesPart.Styles.Save();

            body.Append(new Paragraph(new Run(new Text("Lorem ipsum"))));

            var table = new Table();
            table.Append(new TableProperties(
                new TableStyle { Val = "Tabela-Siatka" },
                new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto }));
            table.Append(new TableGrid(
                new GridColumn { Width = "3020" },
                new GridColumn { Width = "3021" },
                new GridColumn { Width = "3021" }));

            static TableCell Cell(string text, bool center = false, int span = 1)
            {
                var props = new TableCellProperties(
                    new TableCellWidth { Width = (3020 * span).ToString(), Type = TableWidthUnitValues.Dxa });
                if (span > 1) props.Append(new GridSpan { Val = span });
                if (center) props.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
                var para = center
                    ? new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                        new Run(new Text(text)))
                    : new Paragraph(new Run(new Text(text)));
                return new TableCell(props, para);
            }

            var r1 = new TableRow(new TableRowProperties(new TableRowHeight { Val = 684 }));
            r1.Append(Cell("A", center: true));
            r1.Append(Cell("B", center: true));
            r1.Append(Cell("C", center: true));
            table.Append(r1);
            var r2 = new TableRow();
            r2.Append(Cell("D"));
            r2.Append(Cell("E", center: true, span: 2));
            table.Append(r2);
            var r3 = new TableRow();
            r3.Append(Cell("F"));
            r3.Append(Cell("G"));
            r3.Append(Cell("H"));
            table.Append(r3);

            body.Append(table);
            body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 }));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    private static string ContainerTag(string html)
    {
        var start = html.IndexOf("<div class=\"document-content\"", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "reader powinien emitować kontener document-content");
        var end = html.IndexOf('>', start);
        return html.Substring(start, end - start + 1);
    }


    [Test]
    public void Read_DocDefaults_EmitsContainerDataAttributesAndLineHeight()
    {
        using var docx = BuildQutableLikeDocx();
        var container = ContainerTag(_reader.Convert(docx).Html);

        container.Should().Contain("data-default-after-tw=\"160\"");
        container.Should().Contain("data-default-line=\"278\"");
        container.Should().Contain("data-default-line-rule=\"auto\"");
        container.Should().Contain("font-size:12pt;");
        container.Should().Contain("line-height:1.414;");
    }

    [Test]
    public void Read_TableStyleParagraphSpacing_AppliedInlineToCellParagraphs()
    {
        using var docx = BuildQutableLikeDocx();
        var html = _reader.Convert(docx).Html;

        var cellParagraph = System.Text.RegularExpressions.Regex.Match(
            html, "<td[^>]*><p style=\"([^\"]*)\"");
        cellParagraph.Success.Should().BeTrue();
        cellParagraph.Groups[1].Value.Should().Contain("padding-bottom:0pt;");
        cellParagraph.Groups[1].Value.Should().Contain("line-height:1.221;");
        cellParagraph.Groups[1].Value.Should().Contain("--w-line-tw:240;");

        var bodyParagraph = System.Text.RegularExpressions.Regex.Match(
            html, "<p style=\"([^\"]*)\"><span[^>]*>Lorem ipsum");
        bodyParagraph.Success.Should().BeTrue();
        bodyParagraph.Groups[1].Value.Should().NotContain("margin-bottom");
    }

    [Test]
    public void Read_TableStyleAtLeastLineSpacing_EmitsMaxFormLikeMainParagraphPath()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(
                new Style(
                    new StyleParagraphProperties(
                        new SpacingBetweenLines { Line = "200", LineRule = LineSpacingRuleValues.AtLeast }))
                {
                    Type = StyleValues.Table,
                    StyleId = "TabelaAtLeast",
                    StyleName = new StyleName { Val = "Tabela AtLeast" }
                });
            stylesPart.Styles.Save();

            var table = new Table();
            table.Append(new TableProperties(new TableStyle { Val = "TabelaAtLeast" }));
            table.Append(new TableRow(new TableCell(new Paragraph(new Run(new Text("X"))))));
            body.Append(table);
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;

        var cellParagraph = System.Text.RegularExpressions.Regex.Match(
            html, "<td[^>]*><p style=\"([^\"]*)\"");
        cellParagraph.Success.Should().BeTrue();
        cellParagraph.Groups[1].Value.Should().Contain("line-height:max(10pt, var(--w-line-single, 1.2em));");
        cellParagraph.Groups[1].Value.Should().Contain("--w-line-rule:atLeast;");
    }

    [Test]
    public void Read_TableGridColumns_CarryExactTwips()
    {
        using var docx = BuildQutableLikeDocx();
        var html = _reader.Convert(docx).Html;

        html.Should().Contain("data-w-tw=\"3020\"");
        html.Should().Contain("data-w-tw=\"3021\"");
    }


    [Test]
    public void Write_ContainerDefaults_RestoredIntoDocDefaults()
    {
        var html = "<div class=\"document-content\" data-default-after-tw=\"160\" data-default-line=\"278\"" +
                   " data-default-line-rule=\"auto\" style=\"font-family:'Calibri',sans-serif;font-size:12pt;line-height:1.158;\">" +
                   "<p style=\"\">Tekst</p></div>";
        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var docDefaults = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!.DocDefaults!;
        docDefaults.RunPropertiesDefault!.RunPropertiesBaseStyle!.GetFirstChild<FontSize>()!
            .Val!.Value.Should().Be("24"); 
        var spacing = docDefaults.ParagraphPropertiesDefault!.ParagraphPropertiesBaseStyle!
            .GetFirstChild<SpacingBetweenLines>()!;
        spacing.After!.Value.Should().Be("160");
        spacing.Line!.Value.Should().Be("278");
        spacing.LineRule!.Value.Should().Be(LineSpacingRuleValues.Auto);
    }

    [Test]
    public void Write_WithoutContainer_KeepsConfiguredFallbackDefaults()
    {
        var bytes = _writer.Convert("<p>Tekst</p>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var docDefaults = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!.DocDefaults!;
        docDefaults.RunPropertiesDefault!.RunPropertiesBaseStyle!.GetFirstChild<FontSize>()!
            .Val!.Value.Should().Be("22"); 
        docDefaults.ParagraphPropertiesDefault!.ParagraphPropertiesBaseStyle!
            .GetFirstChild<SpacingBetweenLines>()!.Line!.Value.Should().Be("259");
    }

    [Test]
    public void Write_StyledTableWithoutCssBorder_DoesNotEmitTblBordersOverride()
    {
        var html = "<table data-tbl-style=\"Tabela-Siatka\" style=\"border-collapse:collapse;width:auto;\">" +
                   "<tr><td style=\"border-top:0.7px solid #000000;\"><p>x</p></td></tr></table>";
        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var tblPr = doc.MainDocumentPart!.Document.Body!.Descendants<TableProperties>().First();
        tblPr.GetFirstChild<TableBorders>().Should().BeNull(
            "bezpośrednie tblBorders val=none nadpisywałoby obramowania stylu tabeli w Wordzie");
        tblPr.GetFirstChild<TableStyle>()!.Val!.Value.Should().Be("Tabela-Siatka");
    }

    [Test]
    public void Write_NoBordersMarker_StillEmitsExplicitNone()
    {
        var html = "<table data-no-borders=\"1\" data-tbl-style=\"Tabela-Siatka\" style=\"border-collapse:collapse;\">" +
                   "<tr><td><p>x</p></td></tr></table>";
        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var borders = doc.MainDocumentPart!.Document.Body!.Descendants<TableProperties>().First()
            .GetFirstChild<TableBorders>();
        borders.Should().NotBeNull("jawny brak obramowań oryginału musi wrócić jako val=none");
        borders!.TopBorder!.Val!.Value.Should().Be(BorderValues.None);
    }

    [Test]
    public void Write_ColgroupWithExactTwips_ProducesExactGridAndCellWidths()
    {
        var html = "<table style=\"border-collapse:collapse;width:604px;table-layout:fixed;\">" +
                   "<colgroup><col style=\"width:201px;\" data-w-tw=\"3020\" /><col style=\"width:201px;\" data-w-tw=\"3021\" /></colgroup>" +
                   "<tr><td style=\"width:201px;\"><p>a</p></td><td style=\"width:201px;\"><p>b</p></td></tr>" +
                   "<tr><td colspan=\"2\" style=\"width:402px;\"><p>scalona</p></td></tr></table>";
        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var grid = doc.MainDocumentPart!.Document.Body!.Descendants<TableGrid>().First();
        grid.Elements<GridColumn>().Select(c => c.Width!.Value).Should().Equal("3020", "3021");

        var cells = doc.MainDocumentPart.Document.Body!.Descendants<TableCell>().ToList();
        cells[0].TableCellProperties!.TableCellWidth!.Width!.Value.Should().Be("3020");
        cells[1].TableCellProperties!.TableCellWidth!.Width!.Value.Should().Be("3021");
        cells[2].TableCellProperties!.TableCellWidth!.Width!.Value.Should().Be("6041"); 
    }

    [Test]
    public void Write_ManualColumnResize_PxFallbackWins()
    {
        var html = "<table style=\"border-collapse:collapse;\">" +
                   "<colgroup><col style=\"width:300px;\" /></colgroup>" +
                   "<tr><td style=\"width:300px;\"><p>a</p></td></tr></table>";
        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var grid = doc.MainDocumentPart!.Document.Body!.Descendants<TableGrid>().First();
        grid.Elements<GridColumn>().First().Width!.Value.Should().Be("4500"); 
    }


    [Test]
    public void RoundTrip_QutableScenario_IsStableAndSchemaValid()
    {
        using var docx = BuildQutableLikeDocx();
        var pass1 = _reader.Convert(docx);
        var export1 = _writer.Convert(pass1.Html, pass1.Metadata, pass1.Header, pass1.Footer, pass1.Margins, pass1.PageSize);

        using (var exported = WordprocessingDocument.Open(new MemoryStream(export1), false))
        {
            var styles = exported.MainDocumentPart!.StyleDefinitionsPart!.Styles!;
            styles.DocDefaults!.RunPropertiesDefault!.RunPropertiesBaseStyle!
                .GetFirstChild<FontSize>()!.Val!.Value.Should().Be("24");
            styles.DocDefaults.ParagraphPropertiesDefault!.ParagraphPropertiesBaseStyle!
                .GetFirstChild<SpacingBetweenLines>()!.Line!.Value.Should().Be("278");

            var cellParagraphSpacing = exported.MainDocumentPart.Document.Body!
                .Descendants<TableCell>().First()
                .Descendants<SpacingBetweenLines>().FirstOrDefault();
            cellParagraphSpacing.Should().NotBeNull();
            cellParagraphSpacing!.After!.Value.Should().Be("0");
            cellParagraphSpacing.Line!.Value.Should().Be("240");

            exported.MainDocumentPart.Document.Body!.Descendants<TableGrid>().First()
                .Elements<GridColumn>().Select(c => c.Width!.Value)
                .Should().Equal("3020", "3021", "3021");

            exported.MainDocumentPart.Document.Body!.Descendants<GridSpan>()
                .Select(g => (int)g.Val!.Value!).Should().Contain(2);
            exported.MainDocumentPart.Document.Body!.Descendants<TableCellVerticalAlignment>()
                .Any(v => v.Val!.Value == TableVerticalAlignmentValues.Center).Should().BeTrue();

            var validator = new OpenXmlValidator(FileFormatVersions.Office2013);
            validator.Validate(exported).Should().BeEmpty();
        }

        var pass2 = _reader.Convert(new MemoryStream(export1));
        var export2 = _writer.Convert(pass2.Html, pass2.Metadata, pass2.Header, pass2.Footer, pass2.Margins, pass2.PageSize);
        using var exported2 = WordprocessingDocument.Open(new MemoryStream(export2), false);
        exported2.MainDocumentPart!.Document.Body!.Descendants<TableGrid>().First()
            .Elements<GridColumn>().Select(c => c.Width!.Value)
            .Should().Equal("3020", "3021", "3021");
    }

    [Test]
    public void Write_CenteredParagraphWithSpacing_PPrChildrenInSchemaOrder()
    {
        var html = "<p style=\"margin-bottom:0pt;line-height:1;text-align:center;\">x</p>";
        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var pPr = doc.MainDocumentPart!.Document.Body!.Descendants<ParagraphProperties>().First();
        var names = pPr.ChildElements.Select(e => e.LocalName).ToList();
        names.IndexOf("spacing").Should().BeLessThan(names.IndexOf("jc"),
            "schemat CT_PPrBase wymaga w:spacing przed w:jc");
    }
}
