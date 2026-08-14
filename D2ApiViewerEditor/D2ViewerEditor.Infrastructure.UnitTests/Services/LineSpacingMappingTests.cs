using D2ViewerEditor.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

/// <summary>
/// Pins the OOXML line-spacing → CSS mapping (Issue 5 + PG-09 calibration). Word's w:spacing maps as:
///  - line + lineRule=auto  → unitless line-height CALIBRATED by the font's single-spacing
///    factor (value/240 × WordLineSpacing.SingleFactor), plus a --w-line-tw round-trip marker
///    carrying the original 240ths value. Word's "multiple" is N × single spacing, and single
///    spacing comes from font metrics (~1.15–1.33 em) — raw value/240 rendered ~20% tighter
///    than Word (PG-09, the largest single contributor to document-height drift).
///  - line + lineRule=exact/atLeast → line-height in pt (twips/20)
///  - before/after (twips)  → margin-top/bottom in pt
/// Remaining Word differences (CSS adjacent-margin collapsing) are inherent HTML/CSS limits
/// documented in .ai/DOCX_CONVERSION.md.
/// </summary>
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

    // Dokumenty testowe nie mają styles part (font nieznany) → fallbackowy współczynnik
    // pojedynczego odstępu 1.2 (WordLineSpacing.DefaultSingleFactor).
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
        // Pełny cykl reader → writer: interlinia „Wielokrotność 1,08" (w:line=259 auto,
        // domyślny Normal Office) wraca DOKŁADNIE jako 259/auto — writer bierze marker
        // --w-line-tw, nie skalibrowaną wartość renderową (inaczej każdy auto-save
        // rozciągałby interlinię o współczynnik kalibracji).
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
        // Treść bez markera (starsza / wpisana ręcznie): wartość bezjednostkowa jest
        // mnożnikiem Worda — dotychczasowe zachowanie ×240.
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
        // Stały (pt) line-height wygrywa nad ewentualnym przeterminowanym markerem
        // (np. po nadpisaniu interlinii przez styl pochodny w łańcuchu dedupe).
        var bytes = new HtmlToDocxConverter().Convert(
            "<p style=\"line-height:18pt;--w-line-tw:240;\">Tekst</p>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var spacing = doc.MainDocumentPart!.Document!.Body!
            .Descendants<SpacingBetweenLines>().Single();
        spacing.Line!.Value.Should().Be("360"); // 18pt = 360 twips
        spacing.LineRule!.Value.Should().Be(LineSpacingRuleValues.Exact);
    }

    [Test]
    public void ExactSpacing_MapsToPointLineHeight()
        => ParagraphCss(new SpacingBetweenLines { Line = "240", LineRule = LineSpacingRuleValues.Exact })
            .Should().Contain("line-height:12pt;");

    [Test]
    public void AtLeastSpacing_MapsToCssMax_MinimumNotExact()
        // PG-10: atLeast = „co najmniej" — linia rośnie, gdy treść wyższa (Word nie przycina);
        // render exactowy ściskał linie poniżej pojedynczego odstępu fontu.
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
        // Bez markera writer mapował KAŻDE pt-owe line-height na w:lineRule=exact;
        // atLeast→exact przycina w Wordzie tekst wyższy niż linia.
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
        // docDefaults z Calibri → mnożnik 1,08 (w:line=259) renderuje się jako
        // 259/240 × 1.221 ≈ 1.318 (metryki hhea Calibri), nie surowe 1.079.
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
        // before = margin-top, after = padding-bottom (ADR-0053: padding nie kolapsuje,
        // więc after(A) + before(B) sumują się między akapitami jak w Wordzie).
        var css = ParagraphCss(new SpacingBetweenLines { Before = "240", After = "200" });
        css.Should().Contain("margin-top:12pt;");
        css.Should().Contain("padding-bottom:10pt;");
    }
}
