using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class ImageExportFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");

    [SetUp]
    public void SetUp()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    [Test]
    public void SniffImageContentType_recognizes_signatures()
    {
        HtmlToDocxConverter.SniffImageContentType(Png).Should().Be("image/png");
        HtmlToDocxConverter.SniffImageContentType(Jpeg).Should().Be("image/jpeg");
        HtmlToDocxConverter.SniffImageContentType(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).Should().BeNull();
    }

    [Test]
    public void Jpeg_bytes_declared_as_png_get_a_jpeg_part_on_export()
    {
        var html = $"<p><img src=\"data:image/png;base64,{Convert.ToBase64String(Jpeg)}\" style=\"width:60px;height:20px;\" /></p>";
        using var ms = new MemoryStream(_writer.Convert(html));
        using var doc = WordprocessingDocument.Open(ms, false);
        var part = doc.MainDocumentPart!.GetPartsOfType<ImagePart>().Single();
        part.ContentType.Should().Be("image/jpeg");
        part.Uri.ToString().Should().MatchRegex(@".jpe?g$");
    }

    [Test]
    public void TextBox_with_inner_image_keeps_text_and_one_image()
    {
        using var ms = new MemoryStream(BuildTextBoxWithImage());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-textbox").And.Contain("J) textbox:");
        System.Text.RegularExpressions.Regex.Matches(html, "<img ").Count.Should().Be(1, "obraz wnętrza pola raz, bez kopii rozciągniętej do rozmiaru pola");
        var imgTag = System.Text.RegularExpressions.Regex.Match(html, "<img [^>]*>").Value;
        imgTag.Should().Contain("data-width-emu=\"1143000\"").And.NotContain("1600000", "wymiar POLA nie może stać się wymiarem obrazu");
        html.IndexOf("docx-textbox", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("<img ", StringComparison.Ordinal), "obraz siedzi WEWNĄTRZ pola tekstowego");

        using var orig = new MemoryStream(BuildTextBoxWithImage());
        using var saved = new MemoryStream(_writer.ConvertPreservingPackage(html, orig));
        using var doc = WordprocessingDocument.Open(saved, false);
        var mp = doc.MainDocumentPart!;
        mp.Document.Body!.InnerText.Should().Contain("J) textbox:");
        mp.GetPartsOfType<ImagePart>().Count().Should().Be(1);
        mp.Document.Body.Descendants<A.Blip>().Count(b => b.Embed?.Value != null).Should().Be(1);
    }

    private static byte[] BuildTextBoxWithImage()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mp = doc.AddMainDocumentPart();
            var ip = mp.AddImagePart(ImagePartType.Png);
            using (var st = new MemoryStream(Png)) ip.FeedData(st);
            var rid = mp.GetIdOfPart(ip);
            var pic = new PIC.Picture(
                new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 13, Name = "P13" }, new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(new A.Blip { Embed = rid }, new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 1143000, Cy = 381000 }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
            var inner = new Drawing(new DW.Inline(
                new DW.Extent { Cx = 1143000, Cy = 381000 }, new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = 13, Name = "Img13" }, new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(pic) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
            const string mc = "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
            var choice = "<mc:Choice " + mc + " Requires=\"wps\" xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"114300\" distR=\"114300\" simplePos=\"0\" relativeHeight=\"251660288\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\"><wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"column\"><wp:posOffset>3000000</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>100000</wp:posOffset></wp:positionV><wp:extent cx=\"1600000\" cy=\"700000\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/><wp:docPr id=\"14\" name=\"TextBox14\"/><wp:cNvGraphicFramePr/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr txBox=\"1\"/><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1600000\" cy=\"700000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></wps:spPr><wps:txbx><w:txbxContent><w:p><w:r><w:t xml:space=\"preserve\">J) textbox: </w:t>" + inner.OuterXml + "</w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice><mc:Fallback " + mc + "><w:pict/></mc:Fallback>";
            var run = new Run(new Text("Podpis: ") { Space = SpaceProcessingModeValues.Preserve });
            run.Append(new AlternateContent { InnerXml = choice });
            mp.Document = new Document(new Body(new Paragraph(run),
                new SectionProperties(new PageSize { Width = 11906, Height = 16838 },
                    new PageMargin { Top = 1417, Bottom = 1417, Left = 1417, Right = 1417, Header = 708, Footer = 708 })));
            mp.Document.Save();
        }
        return ms.ToArray();
    }
}
