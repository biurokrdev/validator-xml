using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class DefaultLineSpacingFontOrderTests
{
    private DocxToHtmlConverter _reader = null!;

    [SetUp]
    public void SetUp() => _reader = new DocxToHtmlConverter();

    private static string Container(string html) =>
        System.Text.RegularExpressions.Regex.Match(html, "<div class=\"document-content\"[^>]*>").Value;

    [Test]
    public void Container_line_height_uses_default_style_font_not_theme_font()
    {
        using var stream = new MemoryStream(Build(themeMinor: "Cambria", defaultStyleAscii: "Calibri", lineTw: 276));

        var content = _reader.Convert(stream);
        var container = Container(content.Html);

        container.Should().Contain("'Calibri'");
        container.Should().Contain("line-height:1.404");
        container.Should().NotContain("line-height:1.347");
    }

    [Test]
    public void Container_line_height_falls_back_to_theme_font_when_default_style_has_none()
    {
        using var stream = new MemoryStream(Build(themeMinor: "Cambria", defaultStyleAscii: null, lineTw: 276));

        var content = _reader.Convert(stream);
        var container = Container(content.Html);

        container.Should().Contain("Cambria");
        container.Should().Contain("line-height:1.347");
    }

    [Test]
    public void Single_spacing_document_uses_default_style_font_factor()
    {
        using var stream = new MemoryStream(Build(themeMinor: "Cambria", defaultStyleAscii: "Calibri", lineTw: null));

        var content = _reader.Convert(stream);

        Container(content.Html).Should().Contain("line-height:1.221");
    }

    private static byte[] Build(string themeMinor, string? defaultStyleAscii, int? lineTw)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("Treść akapitu bez własnego fontu.")))));

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            var rPrDefault = new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { AsciiTheme = ThemeFontValues.MinorHighAnsi, HighAnsiTheme = ThemeFontValues.MinorHighAnsi },
                new FontSize { Val = "22" }));
            var docDefaults = new DocDefaults(rPrDefault);
            if (lineTw.HasValue)
            {
                docDefaults.Append(new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = lineTw.Value.ToString(), LineRule = LineSpacingRuleValues.Auto })));
            }
            var styles = new Styles(docDefaults);

            var normalRunProps = new StyleRunProperties();
            if (defaultStyleAscii != null)
                normalRunProps.Append(new RunFonts { Ascii = defaultStyleAscii, HighAnsi = defaultStyleAscii });
            normalRunProps.Append(new FontSize { Val = "21" });
            styles.Append(new Style(new StyleName { Val = "Normal" }, normalRunProps)
            {
                Type = StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true
            });
            stylesPart.Styles = styles;
            stylesPart.Styles.Save();

            mainPart.AddNewPart<ThemePart>().FeedXml(ThemeXml(themeMinor));
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    private static string ThemeXml(string minorLatin) =>
        "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"T\">" +
        "<a:themeElements><a:clrScheme name=\"C\">" +
        string.Concat(new[] { "dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" }
            .Select(c => $"<a:{c}><a:srgbClr val=\"000000\"/></a:{c}>")) +
        "</a:clrScheme><a:fontScheme name=\"F\">" +
        "<a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        $"<a:minorFont><a:latin typeface=\"{minorLatin}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"S\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
        "<a:lnStyleLst><a:ln><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>" +
        "</a:themeElements></a:theme>";
}
