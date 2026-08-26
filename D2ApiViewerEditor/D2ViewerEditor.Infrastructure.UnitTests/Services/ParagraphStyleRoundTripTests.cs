using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class ParagraphStyleRoundTripTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static string NoteParagraphTag(string html)
    {
        var i = html.IndexOf("Nota w stylu", StringComparison.Ordinal);
        i.Should().BeGreaterThan(0);
        var start = html.LastIndexOf("<p", i, StringComparison.Ordinal);
        return html.Substring(start, html.IndexOf('>', start) - start + 1);
    }

    [Test]
    public void Named_custom_paragraph_style_gets_data_style_id_and_style_level_formatting()
    {
        using var stream = new MemoryStream(Build());

        var content = _reader.Convert(stream);
        var tag = NoteParagraphTag(content.Html);

        tag.Should().Contain("data-style-id=\"FixtureNote\"");
        tag.Should().Contain("font-size:8.5pt");
        tag.Should().Contain("font-style:italic");
    }

    [Test]
    public void Default_paragraph_style_does_not_get_data_style_id()
    {
        using var stream = new MemoryStream(Build());

        var content = _reader.Convert(stream);
        var i = content.Html.IndexOf("Zwykły akapit", StringComparison.Ordinal);
        var start = content.Html.LastIndexOf("<p", i, StringComparison.Ordinal);
        var tag = content.Html.Substring(start, content.Html.IndexOf('>', start) - start + 1);

        tag.Should().NotContain("data-style-id");
    }

    [Test]
    public void Style_level_formatting_survives_save_and_reopen()
    {
        var original = Build();
        string firstHtml;
        using (var s = new MemoryStream(original))
            firstHtml = _reader.Convert(s).Html;

        using var originalStream = new MemoryStream(original);
        var saved = _writer.ConvertPreservingPackage(firstHtml, originalStream);
        string secondHtml;
        using (var s = new MemoryStream(saved))
            secondHtml = _reader.Convert(s).Html;

        var tag = NoteParagraphTag(secondHtml);
        tag.Should().Contain("data-style-id=\"FixtureNote\"");
        tag.Should().Contain("font-size:8.5pt", "po zapisie akapit ma nadal rozmiar ze STYLU na <p>, nie tylko w spanie");
        tag.Should().Contain("font-style:italic");
    }

    [Test]
    public void Saved_package_carries_pStyle_for_custom_style()
    {
        var original = Build();
        string html;
        using (var s = new MemoryStream(original))
            html = _reader.Convert(s).Html;

        using var originalStream = new MemoryStream(original);
        var saved = _writer.ConvertPreservingPackage(html, originalStream);

        using var ms = new MemoryStream(saved);
        using var doc = WordprocessingDocument.Open(ms, false);
        var styleIds = doc.MainDocumentPart!.Document.Body!
            .Descendants<ParagraphStyleId>().Select(p => p.Val?.Value).ToList();
        styleIds.Should().Contain("FixtureNote");
    }

    private static byte[] Build()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Zwykły akapit w stylu domyślnym."))),
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "FixtureNote" }),
                    new Run(new Text("Nota w stylu FixtureNote.")))));

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            var styles = new Styles(new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "21" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = "276", LineRule = LineSpacingRuleValues.Auto }))));
            styles.Append(new Style(new StyleName { Val = "Normal" })
            {
                Type = StyleValues.Paragraph, StyleId = "Normal", Default = true
            });
            styles.Append(new Style(
                new StyleName { Val = "Fixture Note" },
                new StyleParagraphProperties(new SpacingBetweenLines { After = "80" }),
                new StyleRunProperties(new Italic(), new FontSize { Val = "17" }))
            {
                Type = StyleValues.Paragraph, StyleId = "FixtureNote", CustomStyle = true
            });
            stylesPart.Styles = styles;
            stylesPart.Styles.Save();
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }
}
