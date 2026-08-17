using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class LineSpacingMappingTests
{
    private DocxToHtmlConverter _reader = null!;

    [SetUp]
    public void Setup() => _reader = new DocxToHtmlConverter();

    private string ParagraphCss(SpacingBetweenLines spacing)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var p = new Paragraph(
                new ParagraphProperties(spacing),
                new Run(new Text("Tekst")));
            main.Document = new Document(new Body(p));
            main.Document.Save();
        }
        var html = _reader.Convert(new MemoryStream(ms.ToArray())).Html;
        var m = System.Text.RegularExpressions.Regex.Match(html, "<p[^>]*style=\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : html;
    }

    [Test]
    public void SingleSpacing_MapsToCalibratedLineHeight_WithRoundTripMarker()
        => ParagraphCss(new SpacingBetweenLines { Line = "240", LineRule = LineSpacingRuleValues.Auto })
            .Should().Contain("line-height:1.2;").And.Contain("--w-line-tw:240;");

    [Test]
    public void OneAndHalfSpacing_MapsToCalibratedLineHeight()
        => ParagraphCss(new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.Auto })
            .Should().Contain("line-height:1.8;").And.Contain("--w-line-tw:360;");

    [Test]
    public void DoubleSpacing_MapsToCalibratedLineHeight()
        => ParagraphCss(new SpacingBetweenLines { Line = "480", LineRule = LineSpacingRuleValues.Auto })
            .Should().Contain("line-height:2.4;").And.Contain("--w-line-tw:480;");

    [Test]
    public void OfficeDefaultMultiple108_RoundTripsThroughMarker()
    {
        
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var p = new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines { Line = "259", LineRule = LineSpacingRuleValues.Auto }),
                new Run(new Text("Tekst")));
            main.Document = new Document(new Body(p));
            main.Document.Save();
        }
        var html = _reader.Convert(new MemoryStream(ms.ToArray())).Html;

        var bytes = new HtmlToDocxConverter().Convert(html);

        using var result = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var spacing = result.MainDocumentPart!.Document!.Body!
            .Descendants<SpacingBetweenLines>().Single();
        spacing.Line!.Value.Should().Be("259");
        spacing.LineRule!.Value.Should().Be(LineSpacingRuleValues.Auto);
    }

    [Test]
    public void UnitlessLineHeight_WithoutMarker_KeepsWordMultipleSemantics()
    {
        
        var bytes = new HtmlToDocxConverter().Convert("<p style=\"line-height:1.5;\">Tekst</p>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var spacing = doc.MainDocumentPart!.Document!.Body!
            .Descendants<SpacingBetweenLines>().Single();
        spacing.Line!.Value.Should().Be("360");
        spacing.LineRule!.Value.Should().Be(LineSpacingRuleValues.Auto);
    }

    [Test]
    public void MarkerIsIgnoredForExactPointLineHeight()
    {
        
        var bytes = new HtmlToDocxConverter().Convert(
            "<p style=\"line-height:18pt;--w-line-tw:240;\">Tekst</p>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var spacing = doc.MainDocumentPart!.Document!.Body!
            .Descendants<SpacingBetweenLines>().Single();
        spacing.Line!.Value.Should().Be("360"); 
        spacing.LineRule!.Value.Should().Be(LineSpacingRuleValues.Exact);
    }

    [Test]
    public void ExactSpacing_MapsToPointLineHeight()
        => ParagraphCss(new SpacingBetweenLines { Line = "240", LineRule = LineSpacingRuleValues.Exact })
            .Should().Contain("line-height:12pt;");

    [Test]
    public void AtLeastSpacing_MapsToCssMax_MinimumNotExact()
        
        => ParagraphCss(new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.AtLeast })
            .Should().Contain("line-height:max(18pt, var(--w-line-single, 1.2em));");

    [Test]
    public void AtLeastSpacing_MaxForm_RoundTripsBackToAtLeast()
    {
        var writer = new HtmlToDocxConverter();

        var bytes = writer.Convert(
            "<p style=\"line-height:max(18pt, var(--w-line-single, 1.2em));--w-line-rule:atLeast;\">Tekst</p>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var spacing = doc.MainDocumentPart!.Document!.Body!
            .Descendants<SpacingBetweenLines>().Single();
        spacing.LineRule!.Value.Should().Be(LineSpacingRuleValues.AtLeast);
        spacing.Line!.Value.Should().Be("360");
    }

    [Test]
    public void AtLeastSpacing_IsMarkedForRoundTrip_ExactIsNot()
    {
        
        ParagraphCss(new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.AtLeast })
            .Should().Contain("--w-line-rule:atLeast;");
        ParagraphCss(new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.Exact })
            .Should().NotContain("--w-line-rule");
    }

    [Test]
    public void AtLeastSpacing_RoundTripsBackToAtLeastRule()
    {
        var writer = new HtmlToDocxConverter();

        var bytes = writer.Convert("<p style=\"line-height:18pt;--w-line-rule:atLeast;\">Tekst</p>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var spacing = doc.MainDocumentPart!.Document!.Body!
            .Descendants<SpacingBetweenLines>().Single();
        spacing.LineRule!.Value.Should().Be(LineSpacingRuleValues.AtLeast);
        spacing.Line!.Value.Should().Be("360");
    }

    [Test]
    public void ExactSpacing_RoundTripsBackToExactRule()
    {
        var writer = new HtmlToDocxConverter();

        var bytes = writer.Convert("<p style=\"line-height:18pt;\">Tekst</p>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var spacing = doc.MainDocumentPart!.Document!.Body!
            .Descendants<SpacingBetweenLines>().Single();
        spacing.LineRule!.Value.Should().Be(LineSpacingRuleValues.Exact);
    }

    [Test]
    public void CalibriDocument_UsesCalibriSingleFactor()
    {
        
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(
                new DocDefaults(
                    new RunPropertiesDefault(
                        new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri" }))));
            var p = new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines { Line = "259", LineRule = LineSpacingRuleValues.Auto }),
                new Run(new Text("Tekst")));
            main.Document = new Document(new Body(p));
            main.Document.Save();
        }
        var html = _reader.Convert(new MemoryStream(ms.ToArray())).Html;
        var m = System.Text.RegularExpressions.Regex.Match(html, "<p[^>]*style=\"([^\"]*)\"");

        m.Groups[1].Value.Should().Contain("line-height:1.318;").And.Contain("--w-line-tw:259;");
    }

    [Test]
    public void SpaceBeforeAfter_MapToMarginsInPoints()
    {
        
        var css = ParagraphCss(new SpacingBetweenLines { Before = "240", After = "200" });
        css.Should().Contain("margin-top:12pt;");
        css.Should().Contain("padding-bottom:10pt;");
    }
}
