using System.IO.Compression;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services;
using D2ViewerEditor.Infrastructure.UnitTests.Fixtures;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;
using WpEndnote = DocumentFormat.OpenXml.Wordprocessing.Endnote;
using DomainEndnote = D2ViewerEditor.Domain.Models.Endnote;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class EndnoteFidelityTests
{
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private DocumentContent Import(byte[] docx) => _reader.Convert(new MemoryStream(docx));

    private static string Decoded(string html) => System.Net.WebUtility.HtmlDecode(html);

    private static bool IsUserEndnote(WpEndnote endnote) =>
        endnote.Type == null ||
        (endnote.Type.Value != FootnoteEndnoteValues.Separator &&
         endnote.Type.Value != FootnoteEndnoteValues.ContinuationSeparator);

    private byte[] Export(DocumentContent content) =>
        _writer.Convert(content.Html, content.Metadata, content.Header, content.Footer,
            content.Margins, content.PageSize, content.SectionHeadersFooters, content.Footnotes, content.Endnotes);


    [Test]
    public void Import_ReadsEndnoteNumberFormatFromSettings()
    {
        Import(EndnoteTestDocuments.EndnoteWithNumberFormat(NumberFormatValues.Decimal))
            .EndnoteNumberFormat.Should().Be("decimal",
                "format numeracji z w:endnotePr/w:numFmt ma trafić do modelu dla GUI");

        Import(EndnoteTestDocuments.SingleEndnote()).EndnoteNumberFormat.Should().BeNull();
    }

    [Test]
    public void Import_SectionLevelEndnoteNumberFormat_IsRead()
    {
        Import(EndnoteTestDocuments.EndnoteWithSectionNumberFormat(NumberFormatValues.UpperLetter))
            .EndnoteNumberFormat.Should().Be("upperLetter");
    }

    [Test]
    public void PreservingExport_KeepsEndnoteNumberFormatInSettings()
    {
        var original = EndnoteTestDocuments.EndnoteWithNumberFormat(NumberFormatValues.Decimal);
        var content = Import(original);

        var result = _writer.ConvertPreservingPackage(content.Html, new MemoryStream(original),
            content.Metadata, content.Header, content.Footer, content.Margins, content.PageSize,
            content.SectionHeadersFooters, content.Footnotes, content.Endnotes);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result), false);
        var endnotePr = doc.MainDocumentPart!.DocumentSettingsPart?.Settings?
            .GetFirstChild<EndnoteDocumentWideProperties>();
        endnotePr.Should().NotBeNull("eksport nie może zmieniać typu numeracji endnotes");
        endnotePr!.GetFirstChild<NumberingFormat>()!.Val!.Value.Should().Be(NumberFormatValues.Decimal);
    }

    [Test]
    public void Export_ModelNoteFormats_AreEmittedInSettings()
    {
        var content = Import(EndnoteTestDocuments.SingleEndnote());

        var bytes = _writer.Convert(content.Html, content.Metadata, content.Header, content.Footer,
            content.Margins, content.PageSize, content.SectionHeadersFooters, content.Footnotes,
            content.Endnotes, footnoteNumberFormat: "decimal", endnoteNumberFormat: "decimal");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var settings = doc.MainDocumentPart!.DocumentSettingsPart!.Settings!;
        settings.GetFirstChild<FootnoteDocumentWideProperties>()!
            .GetFirstChild<NumberingFormat>()!.Val!.Value.Should().Be(NumberFormatValues.Decimal);
        settings.GetFirstChild<EndnoteDocumentWideProperties>()!
            .GetFirstChild<NumberingFormat>()!.Val!.Value.Should().Be(NumberFormatValues.Decimal);
    }

    [Test]
    public void PreservingExport_ModelFormat_WinsOverOriginalSettings()
    {
        var original = EndnoteTestDocuments.EndnoteWithNumberFormat(NumberFormatValues.LowerRoman);
        var content = Import(original);

        var result = _writer.ConvertPreservingPackage(content.Html, new MemoryStream(original),
            content.Metadata, content.Header, content.Footer, content.Margins, content.PageSize,
            content.SectionHeadersFooters, content.Footnotes, content.Endnotes,
            footnoteNumberFormat: null, endnoteNumberFormat: "decimal");

        using var doc = WordprocessingDocument.Open(new MemoryStream(result), false);
        doc.MainDocumentPart!.DocumentSettingsPart!.Settings!
            .GetFirstChild<EndnoteDocumentWideProperties>()!
            .GetFirstChild<NumberingFormat>()!.Val!.Value
            .Should().Be(NumberFormatValues.Decimal,
                "format z edytora (to co widział użytkownik) wygrywa z v1");
    }

    [Test]
    public void PreservingExport_DoesNotEmitDanglingSeparatorReferences()
    {
        var original = EndnoteTestDocuments.EndnoteWithNumberFormat(NumberFormatValues.Decimal);
        var content = Import(original);

        var result = _writer.ConvertPreservingPackage(content.Html, new MemoryStream(original),
            content.Metadata, content.Header, content.Footer, content.Margins, content.PageSize,
            content.SectionHeadersFooters, content.Footnotes, content.Endnotes);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result), false);
        var settings = doc.MainDocumentPart!.DocumentSettingsPart!.Settings!;
        settings.Descendants<FootnoteSpecialReference>().Should().BeEmpty(
            "odwołanie do separatorów bez części footnotes.xml = uszkodzony dokument w Wordzie");
        settings.Descendants<EndnoteSpecialReference>().Should().BeEmpty();
        settings.GetFirstChild<FootnoteDocumentWideProperties>().Should().BeNull(
            "footnotePr z samymi odwołaniami do separatorów nie niesie żadnej treści");
        settings.GetFirstChild<EndnoteDocumentWideProperties>()!
            .GetFirstChild<NumberingFormat>()!.Val!.Value.Should().Be(NumberFormatValues.Decimal);
    }

    [Test]
    public void PreservingExport_SectionLevelFormat_SurvivesAsDocumentWide()
    {
        var original = EndnoteTestDocuments.EndnoteWithSectionNumberFormat(NumberFormatValues.UpperLetter);
        var content = Import(original);

        var result = _writer.ConvertPreservingPackage(content.Html, new MemoryStream(original),
            content.Metadata, content.Header, content.Footer, content.Margins, content.PageSize,
            content.SectionHeadersFooters, content.Footnotes, content.Endnotes);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result), false);
        doc.MainDocumentPart!.DocumentSettingsPart?.Settings?
            .GetFirstChild<EndnoteDocumentWideProperties>()?
            .GetFirstChild<NumberingFormat>()?.Val?.Value
            .Should().Be(NumberFormatValues.UpperLetter,
                "sekcyjny override (regenerowany sectPr go nie niesie) wraca jako document-wide");
    }

    [Test]
    public void PreservingExport_ModelFormat_MergesOriginalNumStartAndNumRestart()
    {
        var original = EndnoteTestDocuments.EndnoteWithFullNumberProperties(
            NumberFormatValues.LowerRoman, numStart: 5, RestartNumberValues.EachSection);
        var content = Import(original);

        var result = _writer.ConvertPreservingPackage(content.Html, new MemoryStream(original),
            content.Metadata, content.Header, content.Footer, content.Margins, content.PageSize,
            content.SectionHeadersFooters, content.Footnotes, content.Endnotes,
            footnoteNumberFormat: null, endnoteNumberFormat: "decimal");

        using var doc = WordprocessingDocument.Open(new MemoryStream(result), false);
        var settings = doc.MainDocumentPart!.DocumentSettingsPart!.Settings!;
        var endnotePr = settings.GetFirstChild<EndnoteDocumentWideProperties>()!;
        endnotePr.GetFirstChild<NumberingFormat>()!.Val!.Value.Should().Be(NumberFormatValues.Decimal,
            "format z modelu edytora wygrywa z oryginałem");
        endnotePr.GetFirstChild<NumberingStart>().Should().NotBeNull("w:numStart nie może zginąć przy scaleniu");
        ((int)endnotePr.GetFirstChild<NumberingStart>()!.Val!.Value).Should().Be(5);
        endnotePr.GetFirstChild<NumberingRestart>()!.Val!.Value.Should().Be(RestartNumberValues.EachSection);
        settings.Descendants<EndnoteSpecialReference>().Should().BeEmpty(
            "odwołania do separatorów nie mogą wrócić przy scalaniu");

        var validator = new OpenXmlValidator(FileFormatVersions.Office2013);
        var errors = validator.Validate(doc.MainDocumentPart.DocumentSettingsPart).ToList();
        errors.Should().BeEmpty("scalony settings.xml musi być schema-valid: {0}",
            string.Join(" | ", errors.Select(e => e.Description)));
    }


    [Test]
    public void Import_ReferenceStyledBySuperscriptCharacterStyle_RendersSingleSupWithoutCssRaise()
    {
        using var ms = new MemoryStream();
        using (var document = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();

            var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(new Style(
                new StyleRunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }))
            {
                Type = StyleValues.Character,
                StyleId = "EndnoteReference",
                StyleName = new StyleName { Val = "endnote reference" }
            });
            stylesPart.Styles.Save();

            main.Document = new Document(new Body(new Paragraph(
                new Run(new Text("Tekst")),
                new Run(
                    new RunProperties(new RunStyle { Val = "EndnoteReference" }),
                    new EndnoteReference { Id = 1 }))));

            var endnotesPart = main.AddNewPart<EndnotesPart>();
            endnotesPart.Endnotes = new Endnotes(
                new WpEndnote(new Paragraph(new Run(new Text("Treść przypisu.")))) { Id = 1 });
            endnotesPart.Endnotes.Save();
            main.Document.Save();
        }

        var html = _reader.Convert(new MemoryStream(ms.ToArray())).Html;

        System.Text.RegularExpressions.Regex.Matches(html, "<sup").Count.Should().Be(1,
            "znacznik odwołania ma DOKŁADNIE jeden (semantyczny) sup");
        html.Should().Contain("class=\"endnote-ref\"");
        html.Should().NotContain("vertical-align:super", "podniesienie niesie sam semantyczny sup");
        html.Should().NotContain("font-size:smaller");
    }

    [Test]
    public void Import_DirectSuperscriptOnReferenceRuns_DoesNotNestSups()
    {
        var content = Import(EndnoteTestDocuments.FootnoteAndEndnote());

        content.Html.Should().NotContain("<sup><sup");
        System.Text.RegularExpressions.Regex.Matches(content.Html, "<sup").Count.Should().Be(2,
            "jeden sup na odwołanie dolne i jeden na końcowe — bez dublowania");
    }

    [Test]
    public void Import_SingleEndnote_ProducesModelWithReferenceAndContent()
    {
        var content = Import(EndnoteTestDocuments.SingleEndnote());

        content.Endnotes.Should().NotBeNull();
        content.Endnotes!.Should().ContainSingle();
        content.Endnotes[0].Id.Should().Be("en-1");
        content.Endnotes[0].Html.Should().Contain("Jedyny przypis końcowy.");
        content.Html.Should().Contain(
            "<sup class=\"endnote-ref\" data-endnote-id=\"en-1\" aria-label=\"Przypis końcowy 1\">1</sup>");
    }

    [Test]
    public void Import_TwoEndnotes_ProducesModelWithReferencesAndContent()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());

        content.Endnotes.Should().NotBeNull();
        content.Endnotes!.Should().HaveCount(2);
        content.Endnotes[0].Id.Should().Be("en-1");
        content.Endnotes[1].Id.Should().Be("en-2");

        Decoded(content.Endnotes[0].Html).Should().Contain("zażółć gęślą jaźń").And.Contain("€");
        content.Endnotes[1].Html.Should().Contain("Drugi akapit tego samego przypisu końcowego.");
    }

    [Test]
    public void Import_References_RenderAsSupWithStableIdAndDisplayNumber()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());

        content.Html.Should().Contain(
            "<sup class=\"endnote-ref\" data-endnote-id=\"en-1\" aria-label=\"Przypis końcowy 1\">1</sup>");
        content.Html.Should().Contain(
            "<sup class=\"endnote-ref\" data-endnote-id=\"en-2\" aria-label=\"Przypis końcowy 2\">2</sup>");
        content.Html.IndexOf("en-1", StringComparison.Ordinal)
            .Should().BeLessThan(content.Html.IndexOf("en-2", StringComparison.Ordinal));
    }

    [Test]
    public void Import_PreservesEndnoteFormatting()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());

        content.Endnotes![1].Html.Should().Contain("<strong>");
        content.Endnotes[1].Html.Should().Contain("<em>");
    }

    [Test]
    public void Import_TechnicalSeparators_AreNotUserEndnotes()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());

        content.Endnotes!.Select(e => e.Id).Should().OnlyContain(id => id == "en-1" || id == "en-2");
        content.Endnotes.Should().HaveCount(2, "separator i continuationSeparator nie są przypisami użytkownika");
    }

    [Test]
    public void Import_SharedEndnote_TwoReferencesShareOneEntryAndNumber()
    {
        var content = Import(EndnoteTestDocuments.SharedEndnoteReferencedTwice());

        content.Endnotes!.Should().ContainSingle("jeden przypis końcowy wskazywany dwukrotnie = jedna treść");
        System.Text.RegularExpressions.Regex.Matches(content.Html, "data-endnote-id=\"en-1\"")
            .Count.Should().Be(2);
        content.Html.Should().NotContain("en-1\" aria-label=\"Przypis końcowy 2");
    }

    [Test]
    public void Import_OrphanReference_IsHandledWithoutCrash()
    {
        var content = Import(EndnoteTestDocuments.OrphanReference());

        content.Endnotes.Should().NotBeNull();
        content.Endnotes!.Should().ContainSingle();
        content.Endnotes[0].Html.Should().BeEmpty();
        content.Html.Should().Contain("data-endnote-id=\"en-5\"");
    }


    [Test]
    public void Import_FootnoteAndEndnote_AreKeptDistinct()
    {
        var content = Import(EndnoteTestDocuments.FootnoteAndEndnote());

        content.Footnotes.Should().ContainSingle();
        content.Endnotes.Should().ContainSingle();
        content.Footnotes![0].Html.Should().Contain("Treść przypisu DOLNEGO.");
        content.Endnotes![0].Html.Should().Contain("Treść przypisu KOŃCOWEGO.");

        content.Html.Should().Contain("class=\"footnote-ref\"");
        content.Html.Should().Contain("class=\"endnote-ref\"");
    }

    [Test]
    public void Export_FootnoteAndEndnote_ProduceBothPartsWithCorrectReferences()
    {
        var content = Import(EndnoteTestDocuments.FootnoteAndEndnote());
        var docx = Export(content);

        using var archive = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        archive.GetEntry("word/footnotes.xml").Should().NotBeNull();
        archive.GetEntry("word/endnotes.xml").Should().NotBeNull();

        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        doc.MainDocumentPart!.Document.Body!.Descendants<FootnoteReference>().Should().ContainSingle();
        doc.MainDocumentPart.Document.Body.Descendants<EndnoteReference>().Should().ContainSingle();
    }


    [Test]
    public void Export_ProducesValidZipWithEndnotesPartRelationshipAndContentType()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());
        var docx = Export(content);

        using var archive = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);

        archive.GetEntry("word/endnotes.xml").Should().NotBeNull("część przypisów końcowych musi istnieć");

        var contentTypes = XDocument.Load(archive.GetEntry("[Content_Types].xml")!.Open());
        contentTypes.Descendants().Any(e =>
                (string?)e.Attribute("PartName") == "/word/endnotes.xml" ||
                (string?)e.Attribute("ContentType") ==
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.endnotes+xml")
            .Should().BeTrue("wpis content-type dla endnotes.xml");

        var rels = XDocument.Load(archive.GetEntry("word/_rels/document.xml.rels")!.Open());
        rels.Descendants().Any(e => ((string?)e.Attribute("Type"))?.EndsWith("/endnotes") == true)
            .Should().BeTrue("relacja z głównej części do części przypisów końcowych");
    }

    [Test]
    public void Export_EndnotesXml_HasSeparatorsAndUserEndnotesWithUniqueIds()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());
        var docx = Export(content);

        using var archive = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        var endnotesXml = XDocument.Load(archive.GetEntry("word/endnotes.xml")!.Open());
        XName En(string n) => XName.Get(n, WNs);

        var endnotes = endnotesXml.Descendants(En("endnote")).ToList();

        endnotes.Any(e => (string?)e.Attribute(En("type")) == "separator").Should().BeTrue();
        endnotes.Any(e => (string?)e.Attribute(En("type")) == "continuationSeparator").Should().BeTrue();

        var ids = endnotes.Select(e => (int)e.Attribute(En("id"))!).ToList();
        ids.Should().OnlyHaveUniqueItems();

        var userIds = endnotes
            .Where(e => e.Attribute(En("type")) == null)
            .Select(e => (int)e.Attribute(En("id"))!)
            .ToList();
        userIds.Should().OnlyContain(id => id >= 1);
        userIds.Should().HaveCount(2);
    }

    [Test]
    public void Export_EveryBodyReferenceHasMatchingEndnoteContent()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());
        var docx = Export(content);

        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        var refIds = doc.MainDocumentPart!.Document.Body!
            .Descendants<EndnoteReference>().Select(r => r.Id!.Value).ToList();
        var contentIds = doc.MainDocumentPart.EndnotesPart!.Endnotes!
            .Elements<WpEndnote>()
            .Where(IsUserEndnote)
            .Select(e => e.Id!.Value)
            .ToList();

        refIds.Should().HaveCount(2);
        refIds.Should().OnlyHaveUniqueItems();
        refIds.Should().BeSubsetOf(contentIds, "każde odwołanie musi mieć treść (brak osieroconych)");
        contentIds.Should().BeEquivalentTo(refIds, "brak nieużywanych przypisów końcowych");
    }

    [Test]
    public void Export_GeneratesSchemaValidDocx()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());
        var docx = Export(content);

        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2013);
        var errors = validator.Validate(doc).ToList();

        errors.Should().BeEmpty(
            "wygenerowany dokument z przypisami końcowymi musi przejść walidację OOXML: {0}",
            string.Join(" | ", errors.Select(e => e.Description)));
    }


    [Test]
    public void RoundTrip_PreservesEndnoteCountContentAndFormatting()
    {
        var first = Import(EndnoteTestDocuments.TwoEndnotes());
        var exported = Export(first);
        var second = Import(exported);

        second.Endnotes.Should().HaveCount(first.Endnotes!.Count);

        Decoded(second.Endnotes![0].Html).Should().Contain("zażółć gęślą jaźń").And.Contain("€");
        second.Endnotes[1].Html.Should().Contain("Drugi akapit tego samego przypisu końcowego.");
        second.Endnotes[1].Html.Should().Contain("<strong>").And.Contain("<em>");

        second.Html.Should().Contain("data-endnote-id=\"en-1\"");
        second.Html.Should().Contain("data-endnote-id=\"en-2\"");
        second.Html.IndexOf("en-1", StringComparison.Ordinal)
            .Should().BeLessThan(second.Html.IndexOf("en-2", StringComparison.Ordinal));
    }


    [Test]
    public void Edit_AddEndnote_AppearsInExportAndReimport()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());

        content.Html += "<p>Nowy akapit z odwołaniem" +
            "<sup class=\"endnote-ref\" data-endnote-id=\"en-new\" aria-label=\"Przypis końcowy 3\">3</sup>.</p>";
        content.Endnotes!.Add(new DomainEndnote { Id = "en-new", Html = "<p>Świeżo dodany przypis końcowy.</p>" });

        var reimported = Import(Export(content));

        reimported.Endnotes!.Should().HaveCount(3);
        reimported.Endnotes.Last().Html.Should().Contain("Świeżo dodany przypis końcowy.");
    }

    [Test]
    public void Edit_ChangeEndnoteContent_IsPersisted()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());
        content.Endnotes![0].Html = "<p>Zmieniona treść pierwszego przypisu końcowego.</p>";

        var reimported = Import(Export(content));

        reimported.Endnotes![0].Html.Should().Contain("Zmieniona treść pierwszego przypisu końcowego.");
        reimported.Endnotes[0].Html.Should().NotContain("zażółć gęślą jaźń");
    }

    [Test]
    public void Edit_DeleteEndnote_RemovesReferenceAndContent_NoOrphans()
    {
        var content = Import(EndnoteTestDocuments.TwoEndnotes());

        content.Html = System.Text.RegularExpressions.Regex.Replace(
            content.Html, "<sup class=\"endnote-ref\" data-endnote-id=\"en-2\"[^>]*>.*?</sup>", "");
        content.Endnotes = content.Endnotes!.Where(e => e.Id != "en-2").ToList();

        var docx = Export(content);
        var reimported = Import(docx);

        reimported.Endnotes.Should().ContainSingle();
        reimported.Html.Should().NotContain("en-2");

        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        var refIds = doc.MainDocumentPart!.Document.Body!
            .Descendants<EndnoteReference>().Select(r => r.Id!.Value).ToList();
        var contentIds = doc.MainDocumentPart.EndnotesPart!.Endnotes!
            .Elements<WpEndnote>()
            .Where(IsUserEndnote)
            .Select(e => e.Id!.Value).ToList();
        contentIds.Should().BeEquivalentTo(refIds);
    }

    [Test]
    public void Edit_ReorderReferences_RecomputesDisplayNumbers()
    {
        var endnotes = new List<DomainEndnote>
        {
            new() { Id = "en-a", Html = "<p>Przypis końcowy A.</p>" },
            new() { Id = "en-b", Html = "<p>Przypis końcowy B.</p>" }
        };
        var html =
            "<p>Najpierw B<sup class=\"endnote-ref\" data-endnote-id=\"en-b\">1</sup>, " +
            "potem A<sup class=\"endnote-ref\" data-endnote-id=\"en-a\">2</sup>.</p>";

        var docx = _writer.Convert(html, endnotes: endnotes);
        var reimported = Import(docx);

        reimported.Endnotes![0].Html.Should().Contain("Przypis końcowy B.", "pierwsze odwołanie w treści dotyczy B");
        reimported.Endnotes[1].Html.Should().Contain("Przypis końcowy A.");

        var firstId = reimported.Endnotes[0].Id;
        var secondId = reimported.Endnotes[1].Id;
        reimported.Html.Should().Contain($"data-endnote-id=\"{firstId}\" aria-label=\"Przypis końcowy 1\">1</sup>");
        reimported.Html.Should().Contain($"data-endnote-id=\"{secondId}\" aria-label=\"Przypis końcowy 2\">2</sup>");
    }


    [Test]
    public void NoEndnotes_ImportProducesNoEndnoteModel()
    {
        var content = Import(EndnoteTestDocuments.NoEndnotes());

        content.Endnotes.Should().BeNull();
        content.Html.Should().NotContain("endnote-ref");
    }

    [Test]
    public void NoEndnotes_ExportDoesNotCreateEndnotesPart()
    {
        var content = Import(EndnoteTestDocuments.NoEndnotes());
        var docx = Export(content);

        using var archive = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        archive.GetEntry("word/endnotes.xml").Should().BeNull(
            "dokument bez przypisów końcowych nie dostaje nadmiarowej części endnotes.xml");
    }

    [Test]
    public void NoEndnotes_RoundTripStaysClean()
    {
        var content = Import(EndnoteTestDocuments.NoEndnotes());
        var second = Import(Export(content));

        second.Endnotes.Should().BeNull();
        Decoded(second.Html).Should().Contain("Dokument bez przypisów końcowych.");
    }
}
