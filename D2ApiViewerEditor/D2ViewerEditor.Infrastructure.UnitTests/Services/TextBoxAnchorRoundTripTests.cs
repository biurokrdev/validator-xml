using System.Text;
using System.Text.RegularExpressions;
using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TextBoxAnchorRoundTripTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private const string AnchoredTextBoxBody = @"<w:p><w:r><w:t>Akapit kotwica</w:t></w:r><w:r>
  <w:drawing>
    <wp:anchor behindDoc=""0"" relativeHeight=""1"" allowOverlap=""1"" simplePos=""0""
      locked=""0"" layoutInCell=""1"">
      <wp:simplePos x=""0"" y=""0""/>
      <wp:positionH relativeFrom=""page""><wp:posOffset>914400</wp:posOffset></wp:positionH>
      <wp:positionV relativeFrom=""margin""><wp:posOffset>457200</wp:posOffset></wp:positionV>
      <wp:extent cx=""1828800"" cy=""457200""/>
      <wp:wrapNone/>
      <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
        <wps:wsp><wps:txbx><w:txbxContent>
          <w:p><w:r><w:t>Tresc pola Doc2</w:t></w:r></w:p>
        </w:txbxContent></wps:txbx></wps:wsp>
      </a:graphicData></a:graphic>
    </wp:anchor>
  </w:drawing>
</w:r></w:p>
<w:p><w:r><w:t>Nastepny akapit</w:t></w:r></w:p>";

    [Test]
    public void Reader_AnchoredTextBox_EmitsAnchorMetadata()
    {
        using var ms = DocxFromRawBody(AnchoredTextBoxBody);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("data-pos-mode=\"front\"");
        
        html.Should().Contain("data-x-emu=\"914400\"");
        html.Should().Contain("data-y-emu=\"457200\"");
        html.Should().Contain("data-width-emu=\"1828800\"");
        html.Should().Contain("data-height-emu=\"457200\"");
    }

    [Test]
    public void Reader_AnchoredTextBox_IsHoistedBeforeAnchorParagraph()
    {
        using var ms = DocxFromRawBody(AnchoredTextBoxBody);

        var html = _reader.Convert(ms).Html;

        var divIndex = html.IndexOf("docx-textbox", StringComparison.Ordinal);
        var paraIndex = html.IndexOf("Akapit kotwica", StringComparison.Ordinal);
        divIndex.Should().BeGreaterThan(-1);
        paraIndex.Should().BeGreaterThan(-1);
        divIndex.Should().BeLessThan(paraIndex, "textbox kotwiczy do NASTĘPNEGO akapitu");

        Regex.IsMatch(html, @"<p[^>]*>[^<]*<div class=""docx-textbox""").Should().BeFalse();
    }

    [Test]
    public void Reader_TextBox_NoBakedTechnicalBorder()
    {
        using var ms = DocxFromRawBody(AnchoredTextBoxBody);

        var html = _reader.Convert(ms).Html;

        html.Should().NotContain("border:1px solid #ccc");
    }

    [Test]
    public void Reader_TextBox_RealShapeOutline_BecomesDocumentBorder()
    {
        const string body = @"<w:p><w:r>
  <w:drawing><wp:inline><wp:extent cx=""1000000"" cy=""500000""/>
    <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
      <wps:wsp>
        <wps:spPr><a:ln w=""19050""><a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill></a:ln></wps:spPr>
        <wps:txbx><w:txbxContent><w:p><w:r><w:t>Ramka dokumentowa</w:t></w:r></w:p></w:txbxContent></wps:txbx>
      </wps:wsp>
    </a:graphicData></a:graphic>
  </wp:inline></w:drawing>
</w:r></w:p>";
        using var ms = DocxFromRawBody(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("border:2px solid #FF0000;");
        html.Should().Contain("data-border-width=\"2\"");
        html.Should().Contain("data-border-color=\"#FF0000\"");
    }

    [Test]
    public void Reader_TextBoxBodyPr_AnchorAndInsets_AreRendered()
    {
        const string body = @"<w:p><w:r>
  <w:drawing><wp:inline><wp:extent cx=""1828800"" cy=""457200""/>
    <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
      <wps:wsp>
        <wps:txbx><w:txbxContent><w:p><w:r><w:t>Miejsce na barcode</w:t></w:r></w:p></w:txbxContent></wps:txbx>
        <wps:bodyPr anchor=""ctr"" lIns=""182880"" tIns=""91440"" rIns=""182880"" bIns=""91440""/>
      </wps:wsp>
    </a:graphicData></a:graphic>
  </wp:inline></w:drawing>
</w:r></w:p>";
        using var ms = DocxFromRawBody(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("data-tb-anchor=\"ctr\"");
        html.Should().Contain("justify-content:center",
            "a:bodyPr@anchor=ctr = treść wyśrodkowana w pionie");
        
        html.Should().Contain("padding:9.6px 19.2px 9.6px 19.2px");
        html.Should().Contain("data-tb-ins=\"182880 91440 182880 91440\"");
    }

    [Test]
    public void Reader_TextBoxWithoutBodyPr_UsesWordDefaultInsets()
    {
        using var ms = DocxFromRawBody(AnchoredTextBoxBody);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("padding:4.8px 9.6px 4.8px 9.6px");
        html.Should().NotContain("data-tb-anchor", "brak anchor = domyślne top, bez flexa");
    }

    [Test]
    public void Writer_TextBoxBodyPr_RoundTripsAnchorAndInsets()
    {
        var html =
            "<div class=\"docx-textbox\" data-textbox=\"1\" data-width-emu=\"1828800\" data-height-emu=\"457200\""
            + " data-tb-anchor=\"ctr\" data-tb-ins=\"182880 91440 182880 91440\""
            + " style=\"display:inline-block;width:192px;min-height:48px;\">"
            + "<p>Miejsce na barcode</p></div>"
            + "<p>Akapit</p>";

        var docx = _writer.Convert(html);

        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var bodyPr = doc.MainDocumentPart!.Document!.Body!
            .Descendants<DocumentFormat.OpenXml.Office2010.Word.DrawingShape.TextBodyProperties>()
            .Single();
        bodyPr.Anchor!.Value.Should().Be(DocumentFormat.OpenXml.Drawing.TextAnchoringTypeValues.Center,
            "wyśrodkowanie pionowe nie może ginąć przy zapisie");
        bodyPr.LeftInset!.Value.Should().Be(182880);
        bodyPr.TopInset!.Value.Should().Be(91440);
        bodyPr.RightInset!.Value.Should().Be(182880);
        bodyPr.BottomInset!.Value.Should().Be(91440);
    }

    [Test]
    public void Writer_FloatingTextBox_EmitsAnchoredShapeInNextParagraph()
    {
        var html =
            "<p>przed</p>"
            + "<div class=\"docx-textbox\" data-textbox=\"1\" data-pos-mode=\"front\""
            + " data-x-emu=\"914400\" data-y-emu=\"457200\" data-width-emu=\"1828800\" data-height-emu=\"457200\""
            + " style=\"position:absolute;left:96px;top:48px;width:192px;min-height:48px;\">"
            + "<p>Tresc pola Doc2</p></div>"
            + "<p>Akapit kotwica</p>";

        var docx = _writer.Convert(html);

        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var anchor = body.Descendants<Wp.Anchor>().Single();
        anchor.Descendants<TextBoxContent>().Single().InnerText.Should().Contain("Tresc pola Doc2");
        anchor.Descendants<Wp.HorizontalPosition>().Single().PositionOffset!.Text.Should().Be("914400");
        anchor.Descendants<Wp.VerticalPosition>().Single().PositionOffset!.Text.Should().Be("457200");
        anchor.Descendants<Wp.Extent>().First().Cx!.Value.Should().Be(1828800);

        var anchorParagraph = anchor.Ancestors<Paragraph>().Single();
        anchorParagraph.InnerText.Should().Contain("Akapit kotwica");
    }

    [Test]
    public void Writer_InlineTextBox_EmitsInlineShape()
    {
        var html =
            "<div class=\"docx-textbox\" data-textbox=\"1\" data-width-emu=\"1000000\" data-height-emu=\"500000\""
            + " style=\"display:inline-block;width:105px;min-height:52px;\"><p>Pole w tekscie</p></div>"
            + "<p>dalej</p>";

        var docx = _writer.Convert(html);

        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        body.Descendants<Wp.Anchor>().Should().BeEmpty();
        var inline = body.Descendants<Wp.Inline>().Single();
        inline.Descendants<TextBoxContent>().Single().InnerText.Should().Contain("Pole w tekscie");
    }

    [Test]
    public void RoundTrip_FloatingTextBox_PreservesAnchorParagraphOffsetsAndContent()
    {
        using var ms = DocxFromRawBody(AnchoredTextBoxBody);
        var html1 = _reader.Convert(ms).Html;

        var docx2 = _writer.Convert(html1);
        using var ms2 = new MemoryStream(docx2);
        var html2 = _reader.Convert(ms2).Html;

        html2.Should().Contain("docx-textbox");
        html2.Should().Contain("Tresc pola Doc2");
        Regex.Matches(html2, "Tresc pola Doc2").Count.Should().Be(1, "round-trip nie może dublować treści");
        html2.Should().Contain("data-x-emu=\"914400\"");
        html2.Should().Contain("data-y-emu=\"457200\"");

        html2.IndexOf("docx-textbox", StringComparison.Ordinal)
            .Should().BeLessThan(html2.IndexOf("Akapit kotwica", StringComparison.Ordinal));
    }

    [Test]
    public void RoundTrip_TwoCycles_DoesNotGrowParagraphCount()
    {
        using var ms = DocxFromRawBody(AnchoredTextBoxBody);
        var html1 = _reader.Convert(ms).Html;

        var docx2 = _writer.Convert(html1);
        using var ms2 = new MemoryStream(docx2);
        var html2 = _reader.Convert(ms2).Html;

        var docx3 = _writer.Convert(html2);
        using var ms3 = new MemoryStream(docx3);
        using var doc2 = WordprocessingDocument.Open(new MemoryStream(docx2), false);
        using var doc3 = WordprocessingDocument.Open(ms3, false);

        var count2 = doc2.MainDocumentPart!.Document.Body!.Descendants<Paragraph>()
            .Count(p => p.Ancestors<TextBoxContent>().Any() == false);
        var count3 = doc3.MainDocumentPart!.Document.Body!.Descendants<Paragraph>()
            .Count(p => p.Ancestors<TextBoxContent>().Any() == false);

        count3.Should().Be(count2, "każdy cykl zapisu nie może dokładać pustych akapitów");
    }

    [Test]
    public void RoundTrip_TextBoxBorder_SurvivesAsShapeOutline()
    {
        var html =
            "<div class=\"docx-textbox\" data-textbox=\"1\" data-pos-mode=\"front\""
            + " data-x-emu=\"100\" data-y-emu=\"200\" data-width-emu=\"1000000\" data-height-emu=\"500000\""
            + " data-border-width=\"2\" data-border-color=\"#FF0000\" data-border-style=\"solid\""
            + " style=\"position:absolute;left:0px;top:0px;\"><p>Ramka</p></div>"
            + "<p>kotwica</p>";

        var docx = _writer.Convert(html);
        using var ms = new MemoryStream(docx);
        var roundTripped = _reader.Convert(ms).Html;

        roundTripped.Should().Contain("border:2px solid #FF0000;");
        roundTripped.Should().Contain("data-border-color=\"#FF0000\"");
    }

    [TestCase("wrapSquare", "square")]
    [TestCase("wrapTopAndBottom", "topAndBottom")]
    public void Reader_AnchoredImageWrapMode_IsSerializedToDataWrap(string wrapXml, string expected)
    {
        var wrapElement = wrapXml == "wrapSquare" ? @"<wp:wrapSquare wrapText=""bothSides""/>" : "<wp:wrapTopAndBottom/>";
        var body = ImageAnchorBody(wrapElement);
        using var ms = DocxWithImage(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain($"data-wrap=\"{expected}\"");
    }

    [Test]
    public void Writer_ImageWithDataWrapSquare_EmitsWrapSquare()
    {
        var html =
            "<p><img src=\"data:image/png;base64," + PngBase64 + "\""
            + " style=\"width:100px;height:50px;\" data-width-emu=\"952500\" data-height-emu=\"476250\""
            + " data-pos-mode=\"front\" data-x-emu=\"914400\" data-y-emu=\"457200\" data-wrap=\"square\" /></p>";

        var docx = _writer.Convert(html);
        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);

        var anchor = doc.MainDocumentPart!.Document.Body!.Descendants<Wp.Anchor>().Single();
        anchor.GetFirstChild<Wp.WrapSquare>().Should().NotBeNull();
        anchor.GetFirstChild<Wp.WrapNone>().Should().BeNull();
    }

    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private static string ImageAnchorBody(string wrapElement) => $@"<w:p><w:r>
  <w:drawing>
    <wp:anchor behindDoc=""0"" relativeHeight=""1"" allowOverlap=""1"" simplePos=""0"" locked=""0"" layoutInCell=""1"">
      <wp:simplePos x=""0"" y=""0""/>
      <wp:positionH relativeFrom=""page""><wp:posOffset>914400</wp:posOffset></wp:positionH>
      <wp:positionV relativeFrom=""margin""><wp:posOffset>457200</wp:posOffset></wp:positionV>
      <wp:extent cx=""952500"" cy=""476250""/>
      {wrapElement}
      <a:graphic><a:graphicData uri=""http://schemas.openxmlformats.org/drawingml/2006/picture"">
        <pic:pic xmlns:pic=""http://schemas.openxmlformats.org/drawingml/2006/picture"">
          <pic:nvPicPr><pic:cNvPr id=""1"" name=""img""/><pic:cNvPicPr/></pic:nvPicPr>
          <pic:blipFill><a:blip r:embed=""rIdImg1"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
          <pic:spPr><a:xfrm><a:off x=""0"" y=""0""/><a:ext cx=""952500"" cy=""476250""/></a:xfrm>
            <a:prstGeom prst=""rect""><a:avLst/></a:prstGeom></pic:spPr>
        </pic:pic>
      </a:graphicData></a:graphic>
    </wp:anchor>
  </w:drawing>
</w:r></w:p>";

    private static MemoryStream DocxFromRawBody(string bodyInnerXml)
    {
        const string doc = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""
  xmlns:wp=""http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing""
  xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main""
  xmlns:mc=""http://schemas.openxmlformats.org/markup-compatibility/2006""
  xmlns:wps=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape""
  xmlns:v=""urn:schemas-microsoft-com:vml""
  xmlns:w10=""urn:schemas-microsoft-com:office:word"">
  <w:body>{BODY}</w:body>
</w:document>";

        var ms = new MemoryStream();
        using (var wpd = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = wpd.AddMainDocumentPart();
            using var w = new StreamWriter(mainPart.GetStream(FileMode.Create), Encoding.UTF8);
            w.Write(doc.Replace("{BODY}", bodyInnerXml));
        }
        ms.Position = 0;
        return ms;
    }

    private static MemoryStream DocxWithImage(string bodyInnerXml)
    {
        var ms = new MemoryStream();
        using (var wpd = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = wpd.AddMainDocumentPart();

            var imagePart = mainPart.AddImagePart(ImagePartType.Png, "rIdImg1");
            using (var s = imagePart.GetStream(FileMode.Create))
            {
                var bytes = System.Convert.FromBase64String(PngBase64);
                s.Write(bytes, 0, bytes.Length);
            }

            const string doc = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""
  xmlns:wp=""http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing""
  xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main""
  xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <w:body>{BODY}</w:body>
</w:document>";
            using var w = new StreamWriter(mainPart.GetStream(FileMode.Create), Encoding.UTF8);
            w.Write(doc.Replace("{BODY}", bodyInnerXml));
        }
        ms.Position = 0;
        return ms;
    }
}
