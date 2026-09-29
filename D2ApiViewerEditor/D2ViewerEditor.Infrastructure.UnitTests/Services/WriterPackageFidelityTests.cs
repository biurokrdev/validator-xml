using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using D2ViewerEditor.Infrastructure.UnitTests.Fixtures;
using FluentAssertions;
using NUnit.Framework;
using Ap = DocumentFormat.OpenXml.ExtendedProperties;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

/// <summary>
/// Porównanie realnego oryginału Worda z zapisem z edytora (2026-09-27) pokazało, co writer psuł mimo
/// identycznej treści: settings.xml bez compatibilityMode (Tryb zgodności), „Nagwek1" → „Heading1",
/// numbering.xml z nowymi numId przy starych odwołaniach w stylach, utracone lang/kern/szCs/znak akapitu,
/// dryf wcięć przez px, odległości nagłówka z powietrza, obrazy w /media i Default xml = typ głównej części.
/// Te testy pilnują każdego z tych punktów (P1–P5).
/// </summary>
[TestFixture]
public class WriterPackageFidelityTests
{
    private HtmlToDocxConverter _writer = null!;
    private DocxToHtmlConverter _reader = null!;

    [SetUp]
    public void Setup()
    {
        _writer = new HtmlToDocxConverter();
        _reader = new DocxToHtmlConverter();
    }

    private const string PngDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static void AssertSchemaValid(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(doc).ToList();
        errors.Should().BeEmpty(string.Join("; ", errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
    }

    private static XDocument ReadZipXml(byte[] package, string entryName)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName) ?? throw new AssertionException($"Brak wpisu {entryName}");
        using var reader = new StreamReader(entry.Open());
        return XDocument.Parse(reader.ReadToEnd());
    }

    private static IReadOnlyList<string> ZipEntries(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    [Test]
    public void Convert_FromScratch_WritesCompatibilityModeCanonicalContentTypesAndMediaFolder()
    {
        var bytes = _writer.Convert($"<p>Tekst</p><p><img src=\"{PngDataUri}\" alt=\"px\" /></p>");

        AssertSchemaValid(bytes);

        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var types = ReadZipXml(bytes, "[Content_Types].xml").Root!;
        types.Elements(ct + "Default").Single(d => (string?)d.Attribute("Extension") == "xml")
            .Attribute("ContentType")!.Value.Should().Be("application/xml", "Default xml = typ głównej części sprawiał, że każda część .xml udawała dokument");
        types.Elements(ct + "Override").Should().Contain(o => (string?)o.Attribute("PartName") == "/word/document.xml");

        var entries = ZipEntries(bytes);
        entries.Should().Contain(e => e.StartsWith("word/media/", StringComparison.Ordinal));
        entries.Should().NotContain(e => e.StartsWith("media/", StringComparison.Ordinal));

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var settings = doc.MainDocumentPart!.DocumentSettingsPart!.Settings!;
        settings.GetFirstChild<Compatibility>()!.Elements<CompatibilitySetting>()
            .Should().Contain(c => c.Name!.Value == CompatSettingNameValues.CompatibilityMode && c.Val!.Value == "15",
                "bez compatibilityMode=15 Word otwiera plik w Trybie zgodności");
        settings.GetFirstChild<DefaultTabStop>()!.Val!.Value.Should().Be((short)708);
        doc.MainDocumentPart.ImageParts.Should().ContainSingle(p => p.Uri.OriginalString.StartsWith("/word/media/"));
        doc.MainDocumentPart.Document.Body!.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().Single().Embed!.Value
            .Should().Be(doc.MainDocumentPart.GetIdOfPart(doc.MainDocumentPart.ImageParts.Single()));
    }

    private static byte[] BuildWordLikeOriginal()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var themeFonts = new RunFonts { AsciiTheme = ThemeFontValues.MinorHighAnsi, HighAnsiTheme = ThemeFontValues.MinorHighAnsi, ComplexScriptTheme = ThemeFontValues.MinorBidi };

            var heading = new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Nagwek1" }),
                new Run(new RunProperties((RunFonts)themeFonts.CloneNode(true), new Languages { Val = "en-US" }, new Kern { Val = 28U },
                        new FontSize { Val = "56" }, new FontSizeComplexScript { Val = "56" }),
                    new Text("Wstęp")));
            var listItem = new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Listanumerowana" }, new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 2 })),
                new Run(new Text("Punkt")));
            var indented = new Paragraph(
                new ParagraphProperties(new Indentation { Left = "280", Right = "160" }, new ParagraphMarkRunProperties(new FontSize { Val = "56" })),
                new Run(new RunProperties(new Languages { Val = "pl-PL" }), new Text("Wcięty")));
            var sectPr = new SectionProperties(
                new PageSize { Width = 11906U, Height = 16838U },
                new PageMargin { Top = 851, Right = 1417U, Bottom = 1417, Left = 1417U, Header = 708U, Footer = 708U, Gutter = 0U },
                new Columns { Space = "708" },
                new DocGrid { LinePitch = 360 });
            main.Document = new Document(new Body(heading, listItem, indented, sectPr));

            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(
                new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "24" }))),
                new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normalny", Default = true },
                new Style(new StyleName { Val = "heading 1" }, new BasedOn { Val = "Normalny" }, new PrimaryStyle(),
                    new StyleParagraphProperties(new KeepNext(), new OutlineLevel { Val = 0 }),
                    new StyleRunProperties(new Color { Val = "2F5496" }, new FontSize { Val = "40" })) { Type = StyleValues.Paragraph, StyleId = "Nagwek1" },
                new Style(new StyleName { Val = "List Number" }, new BasedOn { Val = "Normalny" },
                    new StyleParagraphProperties(new NumberingProperties(new NumberingId { Val = 2 }))) { Type = StyleValues.Paragraph, StyleId = "Listanumerowana" });

            var numbering = main.AddNewPart<NumberingDefinitionsPart>();
            numbering.Numbering = new Numbering(
                new AbstractNum(new MultiLevelType { Val = MultiLevelValues.SingleLevel },
                    new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal }, new ParagraphStyleIdInLevel { Val = "Listanumerowana" },
                        new LevelText { Val = "%1." }, new LevelJustification { Val = LevelJustificationValues.Left },
                        new PreviousParagraphProperties(new Indentation { Left = "360", Hanging = "360" })) { LevelIndex = 0 }) { AbstractNumberId = 0 },
                new AbstractNum(new MultiLevelType { Val = MultiLevelValues.SingleLevel },
                    new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "" },
                        new LevelJustification { Val = LevelJustificationValues.Left },
                        new PreviousParagraphProperties(new Indentation { Left = "360", Hanging = "360" })) { LevelIndex = 0 }) { AbstractNumberId = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
                new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 2 });

            var settingsPart = main.AddNewPart<DocumentSettingsPart>();
            var template = settingsPart.AddExternalRelationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate", new Uri("file:///C:/Normal.dotm"));
            settingsPart.Settings = new Settings(
                new Zoom { Percent = "100" },
                new AttachedTemplate { Id = template.Id },
                new DefaultTabStop { Val = 708 },
                new CharacterSpacingControl { Val = CharacterSpacingValues.DoNotCompress },
                new Compatibility(new CompatibilitySetting { Name = CompatSettingNameValues.CompatibilityMode, Uri = "http://schemas.microsoft.com/office/word", Val = "15" }),
                new ThemeFontLanguages { Val = "pl-PL" });

            var app = doc.AddExtendedFilePropertiesPart();
            app.Properties = new Ap.Properties(new Ap.Template("Normal.dotm"), new Ap.Pages("7"), new Ap.Application("Microsoft Office Word"), new Ap.ApplicationVersion("16.0000"));
        }

        return ms.ToArray();
    }

    [Test]
    public void RoundTrip_ThroughReaderAndPassThrough_KeepsWhatWordKeeps()
    {
        var original = BuildWordLikeOriginal();
        var content = _reader.Convert(new MemoryStream(original));
        content.Html.Should().Contain("data-style-id=\"Nagwek1\"").And.Contain("data-mark-rpr=").And.Contain("data-lang=\"en-US\"")
            .And.Contain("data-kern=\"28\"").And.Contain("data-sz-cs=\"56\"").And.Contain("data-rfonts-theme=\"ascii:minorHAnsi;hAnsi:minorHAnsi;cs:minorBidi\"")
            .And.Contain("data-ind-left-tw=\"280\"");

        var saved = _writer.ConvertPreservingPackage(content.Html, new MemoryStream(original), content.Metadata, content.Header, content.Footer,
            content.Margins, content.PageSize, content.SectionHeadersFooters, content.Footnotes, content.Endnotes,
            content.FootnoteNumberFormat, content.EndnoteNumberFormat);

        AssertSchemaValid(saved);

        using var doc = WordprocessingDocument.Open(new MemoryStream(saved), false);
        var main = doc.MainDocumentPart!;
        var body = main.Document.Body!;

        // P1 — settings.xml oryginału z nałożonymi elementami edytora; relacje (attachedTemplate) usunięte.
        var settings = main.DocumentSettingsPart!.Settings!;
        settings.GetFirstChild<Compatibility>()!.Elements<CompatibilitySetting>()
            .Should().Contain(c => c.Name!.Value == CompatSettingNameValues.CompatibilityMode && c.Val!.Value == "15");
        settings.GetFirstChild<DefaultTabStop>()!.Val!.Value.Should().Be((short)708);
        settings.GetFirstChild<Zoom>().Should().NotBeNull();
        settings.GetFirstChild<CharacterSpacingControl>().Should().NotBeNull();
        settings.GetFirstChild<ThemeFontLanguages>()!.Val!.Value.Should().Be("pl-PL");
        settings.GetFirstChild<AttachedTemplate>().Should().BeNull("element z relacją do nieistniejącej części uszkodziłby pakiet");
        settings.Elements().Select(e => e.LocalName).Should().ContainInOrder("zoom", "defaultTabStop", "characterSpacingControl", "compat", "themeFontLang");

        // P2 — nagłówek wskazuje oryginalny styl, który istnieje w zachowanym styles.xml.
        var headingStyle = body.Elements<Paragraph>().First().ParagraphProperties!.ParagraphStyleId!.Val!.Value;
        headingStyle.Should().Be("Nagwek1");
        main.StyleDefinitionsPart!.Styles!.Elements<Style>().Should().Contain(s => s.StyleId!.Value == "Nagwek1");

        // P3 — numbering.xml oryginału zostaje (styl „Listanumerowana" → numId 2 → decimal), listy edytora dopisane pod nowymi id.
        var numbering = main.NumberingDefinitionsPart!.Numbering!;
        var num2 = numbering.Elements<NumberingInstance>().Single(n => n.NumberID!.Value == 2);
        var abstract0 = numbering.Elements<AbstractNum>().Single(a => a.AbstractNumberId!.Value == num2.AbstractNumId!.Val!.Value);
        abstract0.Descendants<NumberingFormat>().First().Val!.Value.Should().Be(NumberFormatValues.Decimal);
        abstract0.Descendants<ParagraphStyleIdInLevel>().First().Val!.Value.Should().Be("Listanumerowana");
        var referencedNumIds = body.Descendants<NumberingId>().Select(n => (int)n.Val!.Value).Distinct().ToList();
        referencedNumIds.Should().NotBeEmpty();
        referencedNumIds.Should().OnlyContain(id => numbering.Elements<NumberingInstance>().Any(n => n.NumberID!.Value == id),
            "każde w:numId w treści musi wskazywać istniejący w:num po przepisaniu identyfikatorów");

        // P4 — app.xml oryginału, odległości nagłówka/stopki i w:cols z oryginalnego sectPr.
        var appProps = doc.ExtendedFilePropertiesPart!.Properties!;
        appProps.Pages!.Text.Should().Be("7");
        appProps.Application!.Text.Should().Be("Doc2 D2Tools");
        var pgMar = body.Elements<SectionProperties>().Last().GetFirstChild<PageMargin>()!;
        pgMar.Header!.Value.Should().Be(708U);
        pgMar.Footer!.Value.Should().Be(708U);
        pgMar.Gutter!.Value.Should().Be(0U);
        body.Elements<SectionProperties>().Last().GetFirstChild<Columns>().Should().NotBeNull();

        // P5 — właściwości runu i akapitu spoza CSS wracają; wcięcia w dokładnych twipsach.
        var headingRun = body.Elements<Paragraph>().First().Elements<Run>().First().RunProperties!;
        headingRun.Languages!.Val!.Value.Should().Be("en-US");
        headingRun.Kern!.Val!.Value.Should().Be(28U);
        headingRun.FontSizeComplexScript!.Val!.Value.Should().Be("56");
        headingRun.RunFonts!.AsciiTheme!.Value.Should().Be(ThemeFontValues.MinorHighAnsi);
        headingRun.RunFonts.ComplexScriptTheme!.Value.Should().Be(ThemeFontValues.MinorBidi);
        headingRun.RunFonts.Ascii.Should().BeNull("odwołanie do motywu zastępuje literalną nazwę, gdy użytkownik nie zmienił fontu");

        var indented = body.Elements<Paragraph>().First(p => p.InnerText == "Wcięty");
        indented.ParagraphProperties!.Indentation!.Left!.Value.Should().Be("280");
        indented.ParagraphProperties.Indentation.Right!.Value.Should().Be("160");
        indented.ParagraphProperties.ParagraphMarkRunProperties!.GetFirstChild<FontSize>()!.Val!.Value.Should().Be("56");
        indented.Elements<Run>().First().RunProperties!.Languages!.Val!.Value.Should().Be("pl-PL");
    }

    [Test]
    public void ConvertPreservingPackage_BandDistances_ComeBackFromOriginalUnlessUserResizedTheBand()
    {
        // dokument_tabele_orginal.docx: margines 964, odległości 720/720, tylko stopka. Writer liczył odległość z wysokości
        // pasma (przyciętej do 0,8 cm / domyślnej 0,5") i zapisywał header=244, footer=511 — stopka lądowała wyżej w Wordzie.
        const string FooterXml =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:p><w:r><w:t>Stopka</w:t></w:r></w:p></w:ftr>""";
        var body = """<w:p><w:r><w:t>Treść</w:t></w:r></w:p>"""
                   + """<w:sectPr><w:footerReference w:type="default" r:id="rId9"/><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="964" w:right="964" w:bottom="964" w:left="964" w:header="720" w:footer="720" w:gutter="0"/></w:sectPr>""";
        var original = new UnitTests.Fixtures.OoxmlTestPackageBuilder()
            .WithMainDocument(UnitTests.Services.DocumentHealth.DocumentHealthCorpus.Document(body))
            .WithPart("word/footer1.xml", FooterXml, "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml")
            .WithRelationship("word/document.xml", "rId9", UnitTests.Fixtures.OoxmlTestPackageBuilder.RelationshipType("footer"), "footer1.xml")
            .Build();

        var content = _reader.Convert(new MemoryStream(original));
        content.Footer.Should().NotBeNull("fixture ma stopkę");

        byte[] Save() => _writer.ConvertPreservingPackage(content.Html, new MemoryStream(original), content.Metadata, content.Header, content.Footer,
            content.Margins, content.PageSize, content.SectionHeadersFooters, content.Footnotes, content.Endnotes,
            content.FootnoteNumberFormat, content.EndnoteNumberFormat);

        static PageMargin MarginsOf(byte[] package)
        {
            using var doc = WordprocessingDocument.Open(new MemoryStream(package), false);
            return (PageMargin)doc.MainDocumentPart!.Document.Body!.Elements<SectionProperties>().Last().GetFirstChild<PageMargin>()!.CloneNode(true);
        }

        var unchanged = MarginsOf(Save());
        unchanged.Header!.Value.Should().Be(720u, "pakiet nie ma nagłówka — odległość wraca z oryginału");
        unchanged.Footer!.Value.Should().Be(720u, "pasmo stopki nie było zmieniane — odległość wraca z oryginału");

        content.Footer!.Height = 0.3 + content.Footer.Height; // użytkownik powiększył pasmo stopki w edytorze
        var resized = MarginsOf(Save());
        resized.Footer!.Value.Should().NotBe(720u, "zmiana pasma w edytorze ma pierwszeństwo przed oryginałem");
        resized.Header!.Value.Should().Be(720u);
    }

    [Test]
    public void RoundTrip_ExplicitBoldAndItalicOff_SurviveReaderAndWriter()
    {
        // dokument_tabele_orginal.docx: 146× <w:b w:val="0"/> znikało w round-tripie — reader nie emitował font-weight:normal,
        // więc tekst w stylu pogrubionym (np. wiersz nagłówkowy tabeli) po zapisie znów był pogrubiony.
        var body = """<w:p><w:r><w:rPr><w:b w:val="0"/><w:i w:val="0"/><w:sz w:val="18"/></w:rPr><w:t>Reference</w:t></w:r></w:p>"""
                   + UnitTests.Services.DocumentHealth.DocumentHealthCorpus.SectionA4;
        var original = new UnitTests.Fixtures.OoxmlTestPackageBuilder()
            .WithMainDocument(UnitTests.Services.DocumentHealth.DocumentHealthCorpus.Document(body))
            .Build();

        var content = _reader.Convert(new MemoryStream(original));
        content.Html.Should().Contain("font-weight:normal;").And.Contain("font-style:normal;");

        var saved = _writer.Convert(content.Html);
        AssertSchemaValid(saved);

        using var doc = WordprocessingDocument.Open(new MemoryStream(saved), false);
        var run = doc.MainDocumentPart!.Document.Body!.Descendants<Run>().First(r => r.InnerText == "Reference");
        run.RunProperties!.Bold!.Val!.Value.Should().BeFalse("jawne wyłączenie pogrubienia musi wrócić jako w:b val=0");
        run.RunProperties.Italic!.Val!.Value.Should().BeFalse();
    }

    [Test]
    public void ConvertPreservingPackage_WhenEditorChangedFont_KeepsLiteralFontOverTheme()
    {
        var original = BuildWordLikeOriginal();
        var content = _reader.Convert(new MemoryStream(original));
        // Symulacja edycji w GUI: użytkownik zmienił krój nagłówka na Arial (CSS), a atrybuty readera zostały
        // (fixture nie ma motywu, więc reader nie rozwiązał nazwy — dopisujemy ją jak przy realnym dokumencie).
        const string themeAttr = "data-rfonts-theme=\"ascii:minorHAnsi;hAnsi:minorHAnsi;cs:minorBidi\" style=\"";
        content.Html.Should().Contain(themeAttr);
        var html = content.Html.Replace(themeAttr, "data-rfonts-theme=\"ascii:minorHAnsi;hAnsi:minorHAnsi;cs:minorBidi\" data-font-resolved=\"Calibri\" style=\"font-family:'Arial';");

        var saved = _writer.ConvertPreservingPackage(html, new MemoryStream(original));

        using var doc = WordprocessingDocument.Open(new MemoryStream(saved), false);
        var headingRun = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().First().Elements<Run>().First().RunProperties!;
        headingRun.RunFonts!.Ascii!.Value.Should().Be("Arial");
        headingRun.RunFonts.AsciiTheme.Should().BeNull("użytkownik zmienił krój — motyw nie może go nadpisać");
    }
}
