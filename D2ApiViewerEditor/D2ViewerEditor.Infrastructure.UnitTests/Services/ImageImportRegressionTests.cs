using System.Buffers.Binary;
using System.IO.Compression;
using System.Linq;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using V = DocumentFormat.OpenXml.Vml;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class ImageImportRegressionTests
{

    [Test]
    public void HeaderImageWithCollidingRelId_RendersHeaderImage_NotBodyImage()
    {
        var pngBody = MinimalPng(20, 20);
        var pngHeader = MinimalPng(40, 10);
        var docx = BuildDocxWithBodyAndHeaderImage(pngBody, pngHeader, sharedRelId: "rIdImg");

        var content = new DocxToHtmlConverter().Convert(new MemoryStream(docx));

        var b64Body = Convert.ToBase64String(pngBody);
        var b64Header = Convert.ToBase64String(pngHeader);

        content.Html.Should().Contain(b64Body, "body musi renderować SWÓJ obraz");
        content.Html.Should().NotContain(b64Header, "obraz nagłówka nie może wyciec do body");
        content.Header.Should().NotBeNull();
        content.Header!.Html.Should().Contain(b64Header, "nagłówek musi renderować SWÓJ obraz, nie obraz z body o tym samym rId");
        content.Header.Html.Should().NotContain(b64Body);
    }

    [Test]
    public void HeaderListItemImage_ResolvesRelationshipFromHeaderPart()
    {
        var pngBody = MinimalPng(20, 20);
        var pngHeader = MinimalPng(40, 10);
        var docx = BuildDocxWithHeaderListImage(pngBody, pngHeader, sharedRelId: "rIdImg");

        var content = new DocxToHtmlConverter().Convert(new MemoryStream(docx));

        content.Header.Should().NotBeNull();
        content.Header!.Html.Should().Contain("<li", "akapit listy w komórce nagłówka renderuje się jako element listy");
        content.Header.Html.Should().Contain(Convert.ToBase64String(pngHeader),
            "obraz elementu listy musi pochodzić z części NAGŁÓWKA");
        content.Header.Html.Should().NotContain(Convert.ToBase64String(pngBody));
    }


    [Test]
    public void AlternateContentChoice_WithDrawing_IsRendered()
    {
        var png = MinimalPng(20, 20);
        var docx = BuildDocx(main =>
        {
            var relId = AddPng(main, png);
            var alternate = new AlternateContent(
                new AlternateContentChoice(BuildInlineDrawing(relId, 1)) { Requires = "wps" });
            return new Body(new Paragraph(new Run(alternate)));
        });

        var content = new DocxToHtmlConverter().Convert(new MemoryStream(docx));

        content.Html.Should().Contain("<img", "obraz w mc:Choice nie może być dropowany");
        content.Html.Should().Contain(Convert.ToBase64String(png));
    }

    [Test]
    public void AlternateContentFallback_WithVmlPicture_IsUsedWhenChoiceNotConvertible()
    {
        var png = MinimalPng(20, 20);
        var docx = BuildDocx(main =>
        {
            var relId = AddPng(main, png);
            var vmlShape = new V.Shape(new V.ImageData { RelationshipId = relId }) { Style = "width:75pt;height:50pt" };
            var alternate = new AlternateContent(
                new AlternateContentChoice() { Requires = "wps" },              
                new AlternateContentFallback(new Picture(vmlShape)));
            return new Body(new Paragraph(new Run(alternate)));
        });

        var content = new DocxToHtmlConverter().Convert(new MemoryStream(docx));

        content.Html.Should().Contain("<img", "gdy Choice jest pusty, obraz musi przyjść z mc:Fallback (VML)");
        content.Html.Should().Contain(Convert.ToBase64String(png));
    }


    [Test]
    public void EmzCompressedEmf_WithEmbeddedPng_RendersEmbeddedRaster_NotRawEmz()
    {
        var png = MinimalPng(20, 20);
        var emf = BuildEmf(10000, 5000).Concat(png).ToArray();
        var emz = Gzip(emf);

        var docx = BuildDocx(main =>
        {
            var part = main.AddImagePart("image/x-emz");
            using (var s = new MemoryStream(emz)) part.FeedData(s);
            var relId = main.GetIdOfPart(part);
            return new Body(new Paragraph(new Run(BuildInlineDrawing(relId, 1))));
        });

        var content = new DocxToHtmlConverter().Convert(new MemoryStream(docx));

        content.Html.Should().Contain("<img");
        content.Html.Should().NotContain(" src=\"data:image/x-emz", "przeglądarka nie renderuje EMZ — src musi być rastrem/blankiem");
        content.Html.Should().Contain(" src=\"data:image/png;base64,", "z EMZ da się wydobyć osadzony PNG");
    }

    [Test]
    public void EmzCompressedEmf_PureVector_RendersTransparentBlank_NotBrokenImage()
    {
        var emz = Gzip(BuildEmf(10000, 5000));
        var docx = BuildDocx(main =>
        {
            var part = main.AddImagePart("image/x-emz");
            using (var s = new MemoryStream(emz)) part.FeedData(s);
            var relId = main.GetIdOfPart(part);
            return new Body(new Paragraph(new Run(BuildInlineDrawing(relId, 1))));
        });

        var content = new DocxToHtmlConverter().Convert(new MemoryStream(docx));

        content.Html.Should().NotContain(" src=\"data:image/x-emz");
        content.Html.Should().Contain("data:image/svg+xml;base64,", "czysty wektor bez rastra → przezroczysty blank");
        content.Html.Should().Contain("data-legacy-graphic=\"blank\"");
    }

    [Test]
    public void GraphicService_GzipMetafile_IsDecompressedBeforeDetection()
    {
        var service = new GraphicConversionService();
        var emz = Gzip(BuildEmf(10000, 5000));

        var result = service.ConvertForEditor(new GraphicSource
        {
            Data = emz,
            ContentType = "image/x-emz"
        });

        result.Diagnostics.InputKind.Should().Be(GraphicKind.Emf, "po dekompresji GZIP dane to zwykły EMF");
        result.Diagnostics.AttemptedStrategies.Should().Contain("gzip-decompress");
        result.Web.Should().NotBeNull();
    }


    [Test]
    public void ZeroExtent_UsesIntrinsicImageDimensions_NotZeroPixels()
    {
        var png = MinimalPng(20, 20);
        var docx = BuildDocx(main =>
        {
            var relId = AddPng(main, png);
            return new Body(new Paragraph(new Run(BuildInlineDrawing(relId, 1, cx: 0L, cy: 0L))));
        });

        var content = new DocxToHtmlConverter().Convert(new MemoryStream(docx));

        content.Html.Should().Contain("<img");
        content.Html.Should().NotContain("width:0px", "obraz o zerowych wymiarach jest niewidoczny w edytorze");
        content.Html.Should().NotContain("height:0px");
        content.Html.Should().Contain("width:20px", "wymiary intrinsic z nagłówka PNG (20x20)");
    }


    [Test]
    public void Writer_TiffDataUrl_CreatesTiffPart_NotMislabeledJpeg()
    {
        var html = ImgHtml("image/tiff", MinimalTiffLe());
        var bytes = new HtmlToDocxConverter().Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var parts = doc.MainDocumentPart!.ImageParts.ToList();
        parts.Should().Contain(p => p.ContentType == "image/tiff");
        parts.Should().NotContain(p => p.ContentType == "image/jpeg");
    }

    [Test]
    public void Writer_WebpDataUrl_PreservesWebpContentType()
    {
        var html = ImgHtml("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 });
        var bytes = new HtmlToDocxConverter().Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        doc.MainDocumentPart!.ImageParts.Should().Contain(p => p.ContentType == "image/webp");
    }


    [Test]
    public void LinkedOnlyBlip_IsSkippedGracefully_WithoutBreakingImport()
    {
        var docx = BuildDocx(main =>
        {
            main.AddExternalRelationship(
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
                new Uri("file:///c:/obraz-spoza-pakietu.png"), "rIdExt");
            var drawing = BuildInlineDrawing("ignored", 1);
            var blip = drawing.Descendants<A.Blip>().First();
            blip.Embed = null;
            blip.Link = "rIdExt";
            return new Body(
                new Paragraph(new Run(new Text("przed"))),
                new Paragraph(new Run(drawing)),
                new Paragraph(new Run(new Text("po"))));
        });

        var content = new DocxToHtmlConverter().Convert(new MemoryStream(docx));

        content.Html.Should().Contain("przed").And.Contain("po", "błędny/linkowany obraz nie może wywracać importu dokumentu");
        content.Html.Should().NotContain("<img", "bajtów obrazu linkowanego nie ma w pakiecie");
    }


    private static string ImgHtml(string mime, byte[] data) =>
        $"<p><img src=\"data:{mime};base64,{Convert.ToBase64String(data)}\" style=\"width:100px;height:50px;\" /></p>";

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static string AddPng(MainDocumentPart main, byte[] png)
    {
        var part = main.AddImagePart(ImagePartType.Png);
        using var s = new MemoryStream(png);
        part.FeedData(s);
        return main.GetIdOfPart(part);
    }

    private static byte[] BuildDocx(Func<MainDocumentPart, Body> buildBody)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(buildBody(main));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] BuildDocxWithBodyAndHeaderImage(byte[] bodyPng, byte[] headerPng, string sharedRelId)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();

            var bodyImg = main.AddImagePart(ImagePartType.Png, sharedRelId);
            using (var s = new MemoryStream(bodyPng)) bodyImg.FeedData(s);

            var headerPart = main.AddNewPart<HeaderPart>();
            var headerImg = headerPart.AddImagePart(ImagePartType.Png, sharedRelId);
            using (var s = new MemoryStream(headerPng)) headerImg.FeedData(s);
            headerPart.Header = new Header(new Paragraph(new Run(BuildInlineDrawing(sharedRelId, 2))));
            headerPart.Header.Save();

            var body = new Body(
                new Paragraph(new Run(BuildInlineDrawing(sharedRelId, 1))),
                new SectionProperties(
                    new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(headerPart) }));
            main.Document = new Document(body);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] BuildDocxWithHeaderListImage(byte[] bodyPng, byte[] headerPng, string sharedRelId)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();

            var bodyImg = main.AddImagePart(ImagePartType.Png, sharedRelId);
            using (var s = new MemoryStream(bodyPng)) bodyImg.FeedData(s);

            var numberingPart = main.AddNewPart<NumberingDefinitionsPart>();
            var level = new Level { LevelIndex = 0 };
            level.Append(new StartNumberingValue { Val = 1 });
            level.Append(new NumberingFormat { Val = NumberFormatValues.Decimal });
            level.Append(new LevelText { Val = "%1." });
            var abstractNum = new AbstractNum { AbstractNumberId = 1 };
            abstractNum.Append(level);
            var num = new NumberingInstance { NumberID = 1 };
            num.Append(new AbstractNumId { Val = 1 });
            numberingPart.Numbering = new Numbering(abstractNum, num);
            numberingPart.Numbering.Save();

            var headerPart = main.AddNewPart<HeaderPart>();
            var headerImg = headerPart.AddImagePart(ImagePartType.Png, sharedRelId);
            using (var s = new MemoryStream(headerPng)) headerImg.FeedData(s);

            var listParagraph = new Paragraph(
                new ParagraphProperties(new NumberingProperties(
                    new NumberingLevelReference { Val = 0 },
                    new NumberingId { Val = 1 })),
                new Run(BuildInlineDrawing(sharedRelId, 3)));
            headerPart.Header = new Header(new Table(new TableRow(new TableCell(listParagraph))));
            headerPart.Header.Save();

            var body = new Body(
                new Paragraph(new Run(BuildInlineDrawing(sharedRelId, 1))),
                new SectionProperties(
                    new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(headerPart) }));
            main.Document = new Document(body);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static Drawing BuildInlineDrawing(string relId, uint id, long cx = 990000L, long cy = 495000L) =>
        new(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.DocProperties { Id = id, Name = $"img{id}" },
            new A.Graphic(
                new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"img{id}" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = relId },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = 0L, Y = 0L },
                                new A.Extents { Cx = cx, Cy = cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));

    private static byte[] MinimalPng(int w, int h)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[25];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 13);
        System.Text.Encoding.ASCII.GetBytes("IHDR").CopyTo(ihdr, 4);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(8), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(12), (uint)h);
        ihdr[16] = 8; ihdr[17] = 2;
        bytes.AddRange(ihdr);
        var iend = new byte[12];
        System.Text.Encoding.ASCII.GetBytes("IEND").CopyTo(iend, 4);
        bytes.AddRange(iend);
        return bytes.ToArray();
    }

    private static byte[] BuildEmf(int frameRight, int frameBottom)
    {
        var d = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 88);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(32), frameRight);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(36), frameBottom);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(40), 0x464D4520);
        return d;
    }

    private static byte[] MinimalTiffLe() => new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 };
}
