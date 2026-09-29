using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;
using W14 = DocumentFormat.OpenXml.Office2010.Word;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class SdtContentControlRoundTripTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static MemoryStream DocxWith(OpenXmlElement bodyChild)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(bodyChild, new Paragraph()));
            main.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void DropDownList_TypeTagAndOptions_SurviveRoundTrip()
    {
        var sdt = new SdtBlock(
            new SdtProperties(
                new Tag { Val = "Wybor" },
                new SdtAlias { Val = "Lista" },
                new SdtId { Val = 111 },
                new SdtContentDropDownList(
                    new ListItem { DisplayText = "Opcja A", Value = "A" },
                    new ListItem { DisplayText = "Opcja B", Value = "B" })),
            new SdtContentBlock(new Paragraph(new Run(new Text("Opcja A")))));

        using var ms = DocxWith(sdt);
        var html = _reader.Convert(ms).Html;
        html.Should().Contain("data-sdt-props=");
        html.Should().Contain("Opcja A");

        var outSdt = RoundTripFirstSdt(html);
        var props = outSdt.SdtProperties!;
        props.Elements<Tag>().First().Val!.Value.Should().Be("Wybor");
        var ddl = props.Elements<SdtContentDropDownList>().Should().ContainSingle().Subject;
        var items = ddl.Elements<ListItem>().ToList();
        items.Should().HaveCount(2);
        items[0].DisplayText!.Value.Should().Be("Opcja A");
        items[1].Value!.Value.Should().Be("B");
    }

    [Test]
    public void DatePicker_TypeAndFormat_SurviveRoundTrip()
    {
        var sdt = new SdtBlock(
            new SdtProperties(
                new Tag { Val = "Data" },
                new SdtContentDate(new DateFormat { Val = "dd.MM.yyyy" })
                { FullDate = System.DateTime.Parse("2026-07-06") }),
            new SdtContentBlock(new Paragraph(new Run(new Text("06.07.2026")))));

        using var ms = DocxWith(sdt);
        var html = _reader.Convert(ms).Html;

        var props = RoundTripFirstSdt(html).SdtProperties!;
        var date = props.Elements<SdtContentDate>().Should().ContainSingle().Subject;
        date.GetFirstChild<DateFormat>()!.Val!.Value.Should().Be("dd.MM.yyyy");
    }

    [Test]
    public void InlineTextControl_TypeAndTag_SurviveRoundTrip()
    {
        var sdt = new SdtRun(
            new SdtProperties(
                new Tag { Val = "Imie" },
                new SdtContentText()),
            new SdtContentRun(new Run(new Text("Jan"))));
        var para = new Paragraph(new Run(new Text("Klient: ")), sdt);

        using var ms = DocxWith(para);
        var html = _reader.Convert(ms).Html;
        html.Should().Contain("sdt-inline");

        var bytes = _writer.Convert(html);
        using var outDoc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var outSdt = outDoc.MainDocumentPart!.Document!.Body!.Descendants<SdtRun>().First();
        outSdt.SdtProperties!.Elements<Tag>().First().Val!.Value.Should().Be("Imie");
        outSdt.SdtProperties.Elements<SdtContentText>().Should().ContainSingle();
        outSdt.Descendants<Text>().Any(t => t.Text == "Jan").Should().BeTrue();
    }

    private static SdtRun CheckboxSdt(bool isChecked, string checkedHex, string checkedFont,
        string uncheckedHex, string uncheckedFont, string contentChar, string contentFont)
    {
        return new SdtRun(
            new SdtProperties(
                new Tag { Val = "Zgoda" },
                new W14.SdtContentCheckBox(
                    new W14.Checked { Val = isChecked ? W14.OnOffValues.One : W14.OnOffValues.Zero },
                    new W14.CheckedState { Val = checkedHex, Font = checkedFont },
                    new W14.UncheckedState { Val = uncheckedHex, Font = uncheckedFont })),
            new SdtContentRun(new Run(
                new RunProperties(new RunFonts { Ascii = contentFont, HighAnsi = contentFont }),
                new Text(contentChar))));
    }

    [Test]
    public void CheckBox_Reader_EmitsClickableContract()
    {
        var para = new Paragraph(
            CheckboxSdt(false, "2612", "MS Gothic", "2610", "MS Gothic", "☐", "MS Gothic"),
            new Run(new Text(" Wyrażam zgodę") { Space = SpaceProcessingModeValues.Preserve }));

        using var ms = DocxWith(para);
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("sdt-checkbox");
        html.Should().Contain("data-sdt-checkbox=\"1\"");
        html.Should().Contain("data-checked=\"0\"");
        html.Should().Contain("contenteditable=\"false\"");
        html.Should().Contain("data-sdt-props=");
        html.Should().Contain("Wyrażam zgodę");
    }

    [Test]
    public void CheckBox_WingdingsStates_AreMappedToUnicodeGlyphs()
    {
        var para = new Paragraph(
            CheckboxSdt(true, "00FE", "Wingdings", "00A8", "Wingdings", "", "Wingdings"));

        using var ms = DocxWith(para);
        var html = System.Net.WebUtility.HtmlDecode(_reader.Convert(ms).Html);

        html.Should().Contain("data-checked=\"1\"");
        html.Should().Contain("data-checked-glyph=\"☑\"");
        html.Should().Contain("data-unchecked-glyph=\"☐\"");
        html.Should().NotContain("", "goły znak PUA renderuje się w przeglądarce jako pustka");
    }

    [Test]
    public void CheckBox_ToggledInEditor_WritesCheckedStateAndGlyphInStateFont()
    {
        var para = new Paragraph(
            CheckboxSdt(false, "00FE", "Wingdings", "00A8", "Wingdings", "", "Wingdings"));

        using var ms = DocxWith(para);
        var html = _reader.Convert(ms).Html;
        html = html.Replace("data-checked=\"0\"", "data-checked=\"1\"");

        var bytes = _writer.Convert(html);
        using var outDoc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var outSdt = outDoc.MainDocumentPart!.Document!.Body!.Descendants<SdtRun>().Single();

        var checkBox = outSdt.SdtProperties!.GetFirstChild<W14.SdtContentCheckBox>()!;
        checkBox.GetFirstChild<W14.Checked>()!.Val!.InnerText.Should().Be("1");
        checkBox.GetFirstChild<W14.CheckedState>()!.Val!.Value.Should().Be("00FE");
        checkBox.GetFirstChild<W14.CheckedState>()!.Font!.Value.Should().Be("Wingdings");
        outSdt.SdtProperties!.Elements<Tag>().First().Val!.Value.Should().Be("Zgoda");

        var run = outSdt.Descendants<Run>().Single();
        ((int)run.GetFirstChild<Text>()!.Text[0]).Should().Be(0x00FE);
        run.RunProperties!.RunFonts!.Ascii!.Value.Should().Be("Wingdings");
    }

    [TestCase("cross", "2612", "MS Gothic", "☒")]
    [TestCase("check", "2611", "Segoe UI Symbol", "☑")]
    [TestCase("tick", "2714", "Segoe UI Symbol", "✔")]
    public void CheckBox_MarkChosenInEditor_DefinesCheckedStateAndSurvivesRoundTrip(string mark, string hex, string font, string glyph)
    {
        var html =
            "<p><span class=\"sdt-inline sdt-checkbox\" contenteditable=\"false\" data-sdt-checkbox=\"1\""
            + $" data-checked=\"1\" data-checked-mark=\"{mark}\" data-checked-glyph=\"{glyph}\" data-unchecked-glyph=\"☐\">{glyph}</span> Zgoda</p>";

        var bytes = _writer.Convert(html);
        using var outDoc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var outSdt = outDoc.MainDocumentPart!.Document!.Body!.Descendants<SdtRun>().Single();

        var checkBox = outSdt.SdtProperties!.GetFirstChild<W14.SdtContentCheckBox>()!;
        checkBox.GetFirstChild<W14.CheckedState>()!.Val!.Value.Should().Be(hex);
        checkBox.GetFirstChild<W14.CheckedState>()!.Font!.Value.Should().Be(font);
        checkBox.GetFirstChild<W14.UncheckedState>()!.Val!.Value.Should().Be("2610");
        outSdt.Descendants<Text>().Single().Text.Should().Be(glyph);
        outSdt.Descendants<Run>().Single().RunProperties!.RunFonts!.Ascii!.Value.Should().Be(font);

        var reread = _reader.Convert(new MemoryStream(bytes)).Html;
        reread.Should().Contain($"data-checked-glyph=\"{glyph}\"").And.Contain("data-checked=\"1\"");
        var again = _writer.Convert(reread);
        using var againDoc = WordprocessingDocument.Open(new MemoryStream(again), false);
        againDoc.MainDocumentPart!.Document!.Body!.Descendants<W14.CheckedState>().Single().Val!.Value.Should().Be(hex);
    }

    [Test]
    public void CheckBox_InsertedInEditor_GetsWordDefaultDefinition()
    {
        const string html =
            "<p><span class=\"sdt-inline sdt-checkbox\" contenteditable=\"false\" data-sdt-checkbox=\"1\""
            + " data-checked=\"1\" data-checked-glyph=\"☒\" data-unchecked-glyph=\"☐\">☒</span> Punkt</p>";

        var bytes = _writer.Convert(html);
        using var outDoc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var body = outDoc.MainDocumentPart!.Document!.Body!;
        var outSdt = body.Descendants<SdtRun>().Single();

        var checkBox = outSdt.SdtProperties!.GetFirstChild<W14.SdtContentCheckBox>()!;
        checkBox.GetFirstChild<W14.Checked>()!.Val!.InnerText.Should().Be("1");
        checkBox.GetFirstChild<W14.CheckedState>()!.Val!.Value.Should().Be("2612");
        checkBox.GetFirstChild<W14.UncheckedState>()!.Val!.Value.Should().Be("2610");
        outSdt.Descendants<Text>().Single().Text.Should().Be("☒");
        outSdt.Descendants<Run>().Single().RunProperties!.RunFonts!.Ascii!.Value.Should().Be("MS Gothic");
        body.InnerText.Should().Contain("Punkt");

        var reread = _reader.Convert(new MemoryStream(bytes)).Html;
        reread.Should().Contain("data-sdt-checkbox=\"1\"").And.Contain("data-checked=\"1\"");
    }

    [Test]
    public void DuplicateId_IsDropped_SoWordDoesNotSeeCorruption()
    {
        var sdt = new SdtBlock(
            new SdtProperties(new Tag { Val = "X" }, new SdtId { Val = 999 }),
            new SdtContentBlock(new Paragraph(new Run(new Text("v")))));

        using var ms = DocxWith(sdt);
        var html = _reader.Convert(ms).Html;

        var props = RoundTripFirstSdt(html).SdtProperties!;
        props.Elements<SdtId>().Should().BeEmpty("id jest usuwane — Word nadaje własne, unikając kolizji");
        props.Elements<Tag>().First().Val!.Value.Should().Be("X");
    }

    [Test]
    public void BlockContentControl_InFooter_ContentIsNotDropped()
    {
        using var ms = DocxWithFooterSdt(
            new SdtBlock(
                new SdtProperties(new Tag { Val = "removeif_nondigitalversion" }),
                new SdtContentBlock(new Paragraph(new Run(new Text(
                    "Dokument wygenerowany elektronicznie, nie wymaga pieczeci ani podpisu."))))));

        var footer = _reader.Convert(ms).Footer;
        footer.Should().NotBeNull();
        footer!.Html.Should().Contain("Dokument wygenerowany elektronicznie");
        footer.Html.Should().Contain("sdt-block");
        footer.Html.Should().Contain("data-sdt-tag=\"removeif_nondigitalversion\"");
    }

    private static MemoryStream DocxWithFooterSdt(OpenXmlElement footerChild)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var footerPart = main.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(footerChild);
            footerPart.Footer.Save();
            var rid = main.GetIdOfPart(footerPart);

            var sectPr = new SectionProperties(
                new FooterReference { Type = HeaderFooterValues.Default, Id = rid });
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("body"))), sectPr));
            main.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    private SdtBlock RoundTripFirstSdt(string html)
    {
        var bytes = _writer.Convert(html);
        var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        return doc.MainDocumentPart!.Document!.Body!.Descendants<SdtBlock>().First();
    }
}
