using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;
using DomainFootnote = D2ViewerEditor.Domain.Models.Footnote;
using DomainEndnote = D2ViewerEditor.Domain.Models.Endnote;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class GeneratedPackageValidityTests
{
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup() => _writer = new HtmlToDocxConverter();

    private const string RichHtml =
        "<p>Wstęp<sup class=\"footnote-ref\" data-footnote-id=\"fn-1\">1</sup>" +
        " i koniec<sup class=\"endnote-ref\" data-endnote-id=\"en-1\">i</sup></p>" +
        "<a href=\"#_Toc1\" data-anchor=\"_Toc1\" style=\"color:inherit;\">Rozdział I</a>" +
        "<ol data-num-id=\"1\" data-abstract-num-id=\"1\" data-ilvl=\"0\" data-num-fmt=\"decimal\"" +
        " data-lvl-text=\"%1.\" data-marker-color=\"ED7D31\" style=\"--marker-color:#ED7D31;\">" +
        "<li data-ind-left-tw=\"284\" data-ind-hanging-tw=\"142\">Punkt</li></ol>" +
        "<table data-tbl-style=\"TableGrid\" style=\"border-collapse:collapse;width:400px;table-layout:fixed;\">" +
        "<colgroup><col style=\"width:200px;\" data-w-tw=\"3000\"><col style=\"width:200px;\" data-w-tw=\"3000\"></colgroup>" +
        "<tr><td style=\"border-top:none;border-left:none;border-bottom:0.7px solid #D9D9D9;border-right:none;padding:0px 7px;\">A</td>" +
        "<td style=\"border-width: medium 0.7px 0.7px; border-style: none solid solid;" +
        " border-color: currentcolor rgb(217, 217, 217) rgb(217, 217, 217);\">B</td></tr>" +
        "<tr><td style=\"border: 0.7px solid rgb(255, 255, 255);\">C</td>" +
        "<td style=\"border-top: 0px solid #000000; border-bottom: 0.7px solid transparent;\">D</td></tr></table>" +
        "<div class=\"docx-textbox\" data-textbox=\"1\" data-width-emu=\"1828800\" data-height-emu=\"457200\"" +
        " data-tb-anchor=\"ctr\" data-tb-ins=\"182880 91440 182880 91440\"" +
        " style=\"display:inline-block;width:192px;min-height:48px;\"><p>Miejsce na barcode</p></div>" +
        "<p>Koniec</p>";

    private byte[] ConvertRich() => _writer.Convert(
        RichHtml,
        header: new HeaderFooterContent
        {
            Html = "Strona <span class=\"field-page\">{page}</span> z <span class=\"field-numpages\">{pages}</span>"
        },
        footnotes: [new DomainFootnote { Id = "fn-1", Html = "<p>Przypis dolny.</p>" }],
        endnotes: [new DomainEndnote { Id = "en-1", Html = "<p>Przypis końcowy.</p>" }],
        footnoteNumberFormat: "decimal",
        endnoteNumberFormat: "lowerRoman");

    private static void AssertSchemaValid(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(doc).ToList();
        errors.Should().BeEmpty("Word odmawia otwarcia pakietu z błędami schematu; błędy: "
            + string.Join("; ", errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
    }

    [Test]
    public void Convert_RichDocument_IsSchemaValid() => AssertSchemaValid(ConvertRich());

    [Test]
    public void Convert_RunPropertiesInAnyCssOrder_AreSchemaOrdered()
    {
        const string span =
            "<span style=\"font-weight:bold;font-style:italic;text-decoration:underline line-through;font-size:14pt;" +
            "font-family:Arial;color:#ff0000;background-color:#ffff00;letter-spacing:1pt;vertical-align:super;\">x</span>";
        const string html = "<p>" + span + " <span style=\"font-size:9pt;font-family:'Times New Roman',serif;font-weight:bold;\">y</span></p>";

        var bytes = _writer.Convert(html,
            header: new HeaderFooterContent { Html = "<p>" + span + "</p>" },
            footnotes: [new DomainFootnote { Id = "fn-1", Html = "<p>" + span + "</p>" }]);

        AssertSchemaValid(bytes);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var rPr = doc.MainDocumentPart!.Document.Body!.Descendants<RunProperties>().First(p => p.RunFonts != null);
        rPr.ChildElements.First().Should().BeOfType<RunFonts>("rFonts musi być pierwszym dzieckiem rPr bez rStyle");
        rPr.Elements<Bold>().Should().NotBeEmpty();
        rPr.Elements<FontSize>().Single().Val!.Value.Should().Be("28");
    }

    [Test]
    public void Convert_WritesStandardCorePropertiesPart_NotOpcPsmdcp()
    {
        var bytes = _writer.Convert("<p>Treść</p>",
            new DocumentMetadata { Title = "Tytuł testowy", Author = "Autor" });

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var corePart = doc.CoreFilePropertiesPart;
        corePart.Should().NotBeNull();
        corePart!.Uri.OriginalString.Should().Be("/docProps/core.xml");
        using var reader = new StreamReader(corePart.GetStream());
        var xml = reader.ReadToEnd();
        xml.Should().Contain("Tytuł testowy").And.Contain("<dc:creator>Autor</dc:creator>");

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        zip.Entries.Select(e => e.FullName).Should().NotContain(name => name.EndsWith(".psmdcp"));
    }

    [Test]
    public void Write_SdtProps_DropDanglingPlaceholderAndDataBinding()
    {
        var sourceProps = new SdtProperties(
            new SdtPlaceholder(new DocPartReference { Val = "DefaultPlaceholder_-1854013440" }),
            new DataBinding { XPath = "/root/x", StoreItemId = "{11111111-2222-3333-4444-555555555555}" },
            new Tag { Val = "T1" });
        var encoded = System.Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(sourceProps.OuterXml));

        var bytes = _writer.Convert(
            $"<p><span class=\"sdt-inline\" data-sdt-props=\"{encoded}\">X</span></p>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var sdtPr = doc.MainDocumentPart!.Document!.Body!
            .Descendants<SdtRun>().Single().SdtProperties!;
        sdtPr.Elements<SdtPlaceholder>().Should().BeEmpty();
        sdtPr.Elements<DataBinding>().Should().BeEmpty();
        sdtPr.GetFirstChild<Tag>()!.Val!.Value.Should().Be("T1",
            "tożsamość formantu (tag) musi przeżyć czyszczenie odwołań");
    }

    [Test]
    public void ConvertPreservingPackage_RichDocument_IsSchemaValid()
    {
        using var original = new MemoryStream(ConvertRich());
        var result = _writer.ConvertPreservingPackage(RichHtml, original,
            footnotes: [new DomainFootnote { Id = "fn-1", Html = "<p>Przypis dolny.</p>" }],
            endnotes: [new DomainEndnote { Id = "en-1", Html = "<p>Przypis końcowy.</p>" }],
            footnoteNumberFormat: "decimal", endnoteNumberFormat: "lowerRoman");

        AssertSchemaValid(result);
    }
}
