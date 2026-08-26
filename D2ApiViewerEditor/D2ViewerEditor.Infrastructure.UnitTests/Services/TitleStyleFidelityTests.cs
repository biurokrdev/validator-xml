using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TitleStyleFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static string TitleTag(string html)
    {
        var i = html.IndexOf("data-style-id=\"Title\"", StringComparison.Ordinal);
        var start = html.LastIndexOf('<', i);
        return html.Substring(start, html.IndexOf('>', i) - start + 1);
    }

    [Test]
    public void Title_style_border_and_font_stack_are_rendered_without_forced_bold()
    {
        using var ms = new MemoryStream(Build());
        var html = _reader.Convert(ms).Html;
        var tag = TitleTag(html);

        tag.Should().MatchRegex("border-bottom:[^;]*#4F81BD").And.Contain("--w-pbdr-source:style");
        tag.Should().Contain("font-family:'Aptos Display','Calibri Light','Segoe UI',sans-serif");
        tag.Should().NotContain("font-weight");
    }

    [Test]
    public void Style_inherited_border_is_not_written_as_direct_pBdr()
    {
        using var ms = new MemoryStream(Build());
        var html = _reader.Convert(ms).Html;
        using var orig = new MemoryStream(Build());
        using var saved = new MemoryStream(_writer.ConvertPreservingPackage(html, orig));
        using var doc = WordprocessingDocument.Open(saved, false);
        var title = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().First();
        title.ParagraphProperties!.ParagraphStyleId!.Val!.Value.Should().Be("Title");
        title.ParagraphProperties.ParagraphBorders.Should().BeNull("kreska pochodzi ze stylu, nie z pPr");
    }

    [Test]
    public void Direct_paragraph_border_still_round_trips()
    {
        using var ms = new MemoryStream(Build(directBorderOnBody: true));
        var html = _reader.Convert(ms).Html;
        var bodyTag = System.Text.RegularExpressions.Regex.Match(html, "<p[^>]*#FF0000[^>]*>").Value;
        bodyTag.Should().NotBeEmpty().And.NotContain("--w-pbdr-source", "bezpośrednie pBdr nie dostaje markera stylu");
        using var orig = new MemoryStream(Build(directBorderOnBody: true));
        using var saved = new MemoryStream(_writer.ConvertPreservingPackage(html, orig));
        using var doc = WordprocessingDocument.Open(saved, false);
        var body = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().ElementAt(1);
        body.ParagraphProperties!.ParagraphBorders!.BottomBorder!.Color!.Value.Should().Be("FF0000");
    }

    private static byte[] Build(bool directBorderOnBody = false)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var title = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Title" }),
                new Run(new Text("MS Word / DOCX")));
            var bodyPPr = new ParagraphProperties();
            if (directBorderOnBody)
                bodyPPr.Append(new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 8, Space = 1, Color = "FF0000" }));
            var body = new Paragraph(bodyPPr, new Run(new Text("Treść.")));
            mainPart.Document = new Document(new Body(title, body,
                new SectionProperties(new PageSize { Width = 12240, Height = 15840 },
                    new PageMargin { Top = 964, Bottom = 964, Left = 964, Right = 964, Header = 720, Footer = 720 })));

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(
                new Style(new StyleName { Val = "Normal" },
                    new StyleRunProperties(new RunFonts { Ascii = "Aptos", HighAnsi = "Aptos" }, new FontSize { Val = "20" }))
                { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                new Style(new StyleName { Val = "Title" }, new BasedOn { Val = "Normal" },
                    new StyleParagraphProperties(
                        new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 8, Space = 4, Color = "4F81BD" }),
                        new SpacingBetweenLines { After = "300", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
                    new StyleRunProperties(new RunFonts { Ascii = "Aptos Display", HighAnsi = "Aptos Display" },
                        new Color { Val = "17365D" }, new FontSize { Val = "48" }))
                { Type = StyleValues.Paragraph, StyleId = "Title" });
            stylesPart.Styles.Save();
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }
}
