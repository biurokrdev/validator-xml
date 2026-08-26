using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class KeepNextRoundTripTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static string TagOf(string html, string text)
    {
        var i = html.IndexOf(text, StringComparison.Ordinal);
        i.Should().BeGreaterThan(0, $"brak tekstu '{text}'");
        var start = html.LastIndexOf("<p", i, StringComparison.Ordinal);
        return html.Substring(start, html.IndexOf('>', start) - start + 1);
    }

    [Test]
    public void Direct_keepNext_and_keepLines_emit_break_avoid()
    {
        using var ms = Docx(
            new Paragraph(new ParagraphProperties(new KeepNext(), new KeepLines()), new Run(new Text("Etykieta"))),
            new Paragraph(new Run(new Text("Zwykły"))));

        var html = _reader.Convert(ms).Html;

        TagOf(html, "Etykieta").Should().Contain("break-after:avoid").And.Contain("break-inside:avoid");
        TagOf(html, "Zwykły").Should().NotContain("break-after").And.NotContain("break-inside");
    }

    [Test]
    public void Direct_val_false_emits_auto_overriding_style()
    {
        using var ms = Docx(
            new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Label" }, new KeepNext { Val = false }),
                new Run(new Text("Bez"))),
            new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Label" }), new Run(new Text("Ze stylu"))));

        var html = _reader.Convert(ms).Html;

        TagOf(html, "Ze stylu").Should().Contain("break-after:avoid");
        var bez = TagOf(html, "Bez");
        bez.Should().Contain("break-after:auto");
        bez.Should().NotContain("break-after:avoid");
    }

    [Test]
    public void Writer_maps_css_back_to_keepNext_and_keepLines()
    {
        var html = "<p style=\"break-after:avoid;break-inside:avoid;\">Etykieta</p>" +
                   "<p style=\"break-after:auto;\">Wyłączone</p><p>Zwykły</p>";

        using var ms = new MemoryStream(_writer.Convert(html));
        using var doc = WordprocessingDocument.Open(ms, false);
        var paras = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().ToList();

        var p0 = paras[0].ParagraphProperties!;
        p0.GetFirstChild<KeepNext>().Should().NotBeNull();
        (p0.GetFirstChild<KeepNext>()!.Val == null || p0.GetFirstChild<KeepNext>()!.Val!.Value).Should().BeTrue();
        p0.GetFirstChild<KeepLines>().Should().NotBeNull();
        p0.Elements().TakeWhile(e => e is not KeepLines).Should().Contain(e => e is KeepNext);

        var p1 = paras[1].ParagraphProperties!;
        p1.GetFirstChild<KeepNext>()!.Val!.Value.Should().BeFalse();

        paras[2].ParagraphProperties?.GetFirstChild<KeepNext>().Should().BeNull();
    }

    [Test]
    public void KeepNext_survives_save_and_reopen()
    {
        using var ms = Docx(
            new Paragraph(new ParagraphProperties(new KeepNext()), new Run(new Text("Etykieta"))),
            new Paragraph(new Run(new Text("Zwykły"))));
        var first = _reader.Convert(ms).Html;

        using var saved = new MemoryStream(_writer.Convert(first));
        var second = _reader.Convert(saved).Html;

        TagOf(second, "Etykieta").Should().Contain("break-after:avoid");
        TagOf(second, "Zwykły").Should().NotContain("break-after");
    }

    private static MemoryStream Docx(params Paragraph[] paragraphs)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(paragraphs));

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            var styles = new Styles(
                new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                new Style(new StyleName { Val = "Label" }, new StyleParagraphProperties(new KeepNext()))
                    { Type = StyleValues.Paragraph, StyleId = "Label", CustomStyle = true });
            stylesPart.Styles = styles;
            stylesPart.Styles.Save();
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }
}
