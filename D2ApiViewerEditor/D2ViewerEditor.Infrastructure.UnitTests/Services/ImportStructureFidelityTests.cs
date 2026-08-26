using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class ImportStructureFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private const string SectPr = @"<w:sectPr>
  <w:pgSz w:w=""11906"" w:h=""16838""/>
  <w:pgMar w:top=""1417"" w:right=""1417"" w:bottom=""1417"" w:left=""1417"" w:header=""708"" w:footer=""708""/>
</w:sectPr>";

    private const string SimpleTable = @"<w:tbl>
  <w:tblPr><w:tblW w:w=""5000"" w:type=""dxa""/></w:tblPr>
  <w:tblGrid><w:gridCol w:w=""2500""/><w:gridCol w:w=""2500""/></w:tblGrid>
  <w:tr><w:tc><w:p><w:r><w:t>Cell A</w:t></w:r></w:p></w:tc>
        <w:tc><w:p><w:r><w:t>Cell B</w:t></w:r></w:p></w:tc></w:tr>
</w:tbl>";

    private static MemoryStream DocxWithBody(string bodyInnerXml, string? stylesXml = null)
    {
        var doc = $@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:body>{bodyInnerXml}{SectPr}</w:body>
</w:document>";
        var ms = new MemoryStream();
        using (var docx = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = docx.AddMainDocumentPart();
            mainPart.FeedXml(doc);
            if (stylesXml != null)
            {
                var stylesPart = mainPart.AddNewPart<DocumentFormat.OpenXml.Packaging.StyleDefinitionsPart>();
                stylesPart.FeedXml(stylesXml);
            }
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void TableInsideCustomXmlBlock_IsRendered_InSourceOrder()
    {
        var html = _reader.Convert(DocxWithBody(
            @"<w:p><w:r><w:t>Before</w:t></w:r></w:p>" +
            $@"<w:customXml w:element=""section"">{SimpleTable}</w:customXml>" +
            @"<w:p><w:r><w:t>After</w:t></w:r></w:p>")).Html;

        html.Should().Contain("<table", "tabela w customXml nie może znikać z podglądu");
        html.Should().Contain("Cell A").And.Contain("Cell B");
        html.IndexOf("Before", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("Cell A", StringComparison.Ordinal));
        html.IndexOf("Cell B", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("After", StringComparison.Ordinal));
    }

    [Test]
    public void NestedCustomXml_UnwrapsRecursively()
    {
        var html = _reader.Convert(DocxWithBody(
            @"<w:customXml w:element=""outer""><w:customXml w:element=""inner"">" +
            @"<w:p><w:r><w:t>Zagnieżdżony</w:t></w:r></w:p>" +
            @"</w:customXml></w:customXml>")).Html;

        html.Should().Contain("Zagnieżdżony");
    }

    [Test]
    public void PageBreakInsideTableCell_DoesNotEmitDivForm()
    {
        var html = _reader.Convert(DocxWithBody(
            @"<w:tbl><w:tblPr><w:tblW w:w=""5000"" w:type=""dxa""/></w:tblPr>
              <w:tblGrid><w:gridCol w:w=""5000""/></w:tblGrid>
              <w:tr><w:tc><w:p><w:r><w:t>Part1</w:t></w:r><w:r><w:br w:type=""page""/></w:r>
              <w:r><w:t>Part2</w:t></w:r></w:p></w:tc></w:tr></w:tbl>")).Html;

        html.Should().NotContain("<div class=\"page-break\"></div>");
        html.Should().Contain("<span class=\"page-break\"", "marker inline zachowuje w:br bez rozcinania tabeli");
        html.Should().Contain("Part1").And.Contain("Part2");
    }

    [Test]
    public void PageBreakOnlyParagraphInsideCell_EmitsInlineMarker()
    {
        var html = _reader.Convert(DocxWithBody(
            @"<w:tbl><w:tblPr><w:tblW w:w=""5000"" w:type=""dxa""/></w:tblPr>
              <w:tblGrid><w:gridCol w:w=""5000""/></w:tblGrid>
              <w:tr><w:tc><w:p><w:r><w:t>Treść</w:t></w:r></w:p>
              <w:p><w:r><w:br w:type=""page""/></w:r></w:p></w:tc></w:tr></w:tbl>")).Html;

        html.Should().NotContain("<div class=\"page-break\"></div>");
        html.Should().Contain("<span class=\"page-break\"");
    }

    [Test]
    public void BodyLevelPageBreak_KeepsDivForm()
    {
        var html = _reader.Convert(DocxWithBody(
            @"<w:p><w:r><w:t>Strona 1</w:t></w:r></w:p>
              <w:p><w:r><w:br w:type=""page""/></w:r></w:p>
              <w:p><w:r><w:t>Strona 2</w:t></w:r></w:p>")).Html;

        html.Should().Contain("<div class=\"page-break\"></div>",
            "body-level page break napędza splitter stron GUI — forma div musi zostać");
    }

    private const string DocDefaultsAfter160 = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:styles xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:docDefaults>
    <w:pPrDefault><w:pPr><w:spacing w:after=""160"" w:line=""278"" w:lineRule=""auto""/></w:pPr></w:pPrDefault>
  </w:docDefaults>
</w:styles>";

    [Test]
    public void EmptyRunParagraph_KeepsLineBox()
    {
        var html = _reader.Convert(DocxWithBody(
            @"<w:p><w:r><w:t>Przed</w:t></w:r></w:p>
              <w:p><w:r><w:rPr><w:b/></w:rPr></w:r></w:p>
              <w:p><w:r><w:t>Po</w:t></w:r></w:p>")).Html;

        var middle = System.Text.RegularExpressions.Regex.Match(html,
            "Przed.*?</p>(<p.*?</p>)", System.Text.RegularExpressions.RegexOptions.Singleline);
        middle.Success.Should().BeTrue();
        middle.Groups[1].Value.Should().Contain("&nbsp;", "pusty akapit musi mieć line box jak w Wordzie");
    }

    [Test]
    public void ListItemsWithoutContextualSpacing_CarryDocumentDefaultAfterSpacing()
    {
        var html = _reader.Convert(DocxWithBody(
            @"<w:p><w:pPr><w:numPr><w:ilvl w:val=""0""/><w:numId w:val=""1""/></w:numPr></w:pPr>
                <w:r><w:t>Punkt pierwszy</w:t></w:r></w:p>
              <w:p><w:pPr><w:numPr><w:ilvl w:val=""0""/><w:numId w:val=""1""/></w:numPr></w:pPr>
                <w:r><w:t>Punkt drugi</w:t></w:r></w:p>",
            DocDefaultsAfter160)).Html;

        var li = System.Text.RegularExpressions.Regex.Match(html, "<li[^>]*>");
        li.Success.Should().BeTrue();
        li.Value.Should().Contain("margin-bottom:8pt", "160tw = 8pt odstępu po każdym punkcie jak w Wordzie");
    }

    [Test]
    public void HorizontalRuleOnlyParagraph_RendersAsSingleLineWithoutAfterSpacing()
    {
        var html = _reader.Convert(DocxWithBody(
            @"<w:p><w:r><w:pict xmlns:v=""urn:schemas-microsoft-com:vml""
                xmlns:o=""urn:schemas-microsoft-com:office:office"">
                <v:rect o:hr=""t"" o:hrstd=""t"" fillcolor=""#a0a0a0"" style=""width:0;height:1.5pt""/>
              </w:pict></w:r></w:p>",
            DocDefaultsAfter160)).Html;

        var p = System.Text.RegularExpressions.Regex.Match(html, "<p[^>]*>");
        p.Success.Should().BeTrue();
        p.Value.Should().Contain("line-height:normal").And.Contain("padding-bottom:0");
        html.Should().Contain("docx-hr");
        html.Should().MatchRegex(@"docx-hr[^>]*margin-top:calc\(\(1lh - \d+px\)/2\)",
            "rula centruje się w jednej linii fontu");
    }

    [Test]
    public void InlinePageBreakMarkerInCell_RoundTripsToWBrPage()
    {
        var html = "<table><tr><td><p>Part1<span class=\"page-break\" " +
                   "style=\"display:block;height:0;overflow:hidden;\"></span>Part2</p></td></tr></table>";

        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var cell = doc.MainDocumentPart!.Document.Body!.Descendants<TableCell>().Single();
        cell.Descendants<Break>().Should().Contain(b => b.Type != null && b.Type.Value == BreakValues.Page,
            "marker inline musi wracać jako w:br type=page w komórce");
    }
}
