using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class ParagraphSpacingWordModelTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static Paragraph P(string text, Action<ParagraphProperties>? pPr = null, string? styleId = null)
    {
        var props = new ParagraphProperties();
        if (styleId != null) props.Append(new ParagraphStyleId { Val = styleId });
        pPr?.Invoke(props);
        return new Paragraph(props, new Run(new Text(text)));
    }

    private static MemoryStream Docx(IEnumerable<OpenXmlElement> body, bool sumCompat = false, int? gridPitch = null,
        string? gridType = "lines")
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var b = new Body();
            foreach (var e in body) b.Append(e);
            var sect = new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1417, Right = 1417, Bottom = 1417, Left = 1417, Header = 708, Footer = 708, Gutter = 0 });
            if (gridPitch != null)
            {
                var grid = new DocGrid { LinePitch = gridPitch };
                if (gridType == "lines") grid.Type = DocGridValues.Lines;
                sect.Append(grid);
            }
            b.Append(sect);
            main.Document = new Document(b);

            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(
                new DocDefaults(
                    new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "22" })),
                    new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }))),
                new Style(new StyleName { Val = "Normal" }, new PrimaryStyle(),
                    new StyleParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }))
                    { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                new Style(new StyleName { Val = "heading 1" }, new BasedOn { Val = "Normal" },
                    new StyleParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0" }))
                    { Type = StyleValues.Paragraph, StyleId = "Heading1" },
                new Style(new StyleName { Val = "Body" }, new BasedOn { Val = "Normal" },
                    new StyleParagraphProperties(new SpacingBetweenLines { After = "200" }, new ContextualSpacing()))
                    { Type = StyleValues.Paragraph, StyleId = "Body" });
            styles.Styles.Save();

            var settings = main.AddNewPart<DocumentSettingsPart>();
            var compat = new Compatibility();
            if (sumCompat) compat.Append(new DoNotUseHTMLParagraphAutoSpacing());
            settings.Settings = new Settings(compat);
            settings.Settings.Save();
            main.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    private static List<string> ParagraphStyles(string html) =>
        System.Text.RegularExpressions.Regex.Matches(html, "<p(?:\\s[^>]*?)?\\sstyle=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value).ToList();

    private static SpacingBetweenLines Sp(Action<SpacingBetweenLines> set)
    {
        var s = new SpacingBetweenLines();
        set(s);
        return s;
    }

    private WordprocessingDocument RoundTrip(MemoryStream docx)
    {
        var html = _reader.Convert(docx).Html;
        var bytes = _writer.Convert(html);
        return WordprocessingDocument.Open(new MemoryStream(bytes), false);
    }

    [Test]
    public void Max_AfterAndBefore_UseCollapsingMargins()
    {
        using var ms = Docx(new[] { P("A", pp => pp.Append(Sp(s => s.After = "240"))), P("B", pp => pp.Append(Sp(s => s.Before = "80"))) });
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[0].Should().Be("margin-bottom:12pt;");
        styles[1].Should().Be("margin-top:4pt;");
    }

    [Test]
    public void Sum_WithCompatFlag_UsesPaddingCarrier()
    {
        using var ms = Docx(new[] { P("A", pp => pp.Append(Sp(s => s.After = "240"))), P("B", pp => pp.Append(Sp(s => s.Before = "80"))) }, sumCompat: true);
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[0].Should().Be("padding-bottom:12pt;");
        styles[1].Should().Be("margin-top:4pt;");
    }

    [Test]
    public void Lines_WinOverAbsolute_AndOneLineIs12ptWithoutGrid()
    {
        using var ms = Docx(new[]
        {
            P("A"),
            P("B", pp => pp.Append(Sp(s => { s.Before = "600"; s.BeforeLines = 100; }))),
            P("C", pp => pp.Append(Sp(s => s.AfterLines = 150))),
            P("D")
        });
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[1].Should().Contain("margin-top:12pt;").And.Contain("--w-before-lines:100;").And.NotContain("30pt");
        styles[2].Should().Contain("margin-bottom:18pt;").And.Contain("--w-after-lines:150;");
    }

    [Test]
    public void Lines_UseDocGridLinePitch_WhenGridTypeIsLines()
    {
        using var ms = Docx(new[]
        {
            P("A"),
            P("B", pp => pp.Append(Sp(s => s.BeforeLines = 100))),
            P("C", pp => pp.Append(Sp(s => s.AfterLines = 150)))
        }, gridPitch: 360);
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[1].Should().Contain("margin-top:18pt;");
        styles[2].Should().Contain("margin-bottom:27pt;");
    }

    [Test]
    public void Lines_IgnoreLinePitch_WhenGridTypeIsDefault()
    {
        using var ms = Docx(new[] { P("A"), P("B", pp => pp.Append(Sp(s => s.BeforeLines = 100))) },
            gridPitch: 360, gridType: null);
        ParagraphStyles(_reader.Convert(ms).Html)[1].Should().Contain("margin-top:12pt;");
    }

    [Test]
    public void Lines_RoundTripAsLineAttributes()
    {
        using var ms = Docx(new[]
        {
            P("B", pp => pp.Append(Sp(s => { s.Before = "600"; s.BeforeLines = 100; }))),
            P("C", pp => pp.Append(Sp(s => s.AfterLines = 150)))
        });
        using var doc = RoundTrip(ms);
        var spacings = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>()
            .Select(p => p.ParagraphProperties!.GetFirstChild<SpacingBetweenLines>()!).ToList();
        spacings[0].BeforeLines!.Value.Should().Be(100);
        spacings[1].AfterLines!.Value.Should().Be(150);
    }

    [Test]
    public void AutoSpacing_Renders14pt_AndIgnoresAbsoluteValue()
    {
        using var ms = Docx(new[]
        {
            P("A"),
            P("B", pp => pp.Append(Sp(s => { s.Before = "600"; s.BeforeAutoSpacing = true; }))),
            P("C", pp => pp.Append(Sp(s => s.AfterAutoSpacing = true)))
        });
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[1].Should().Contain("margin-top:14pt;").And.Contain("--w-before-auto:1;").And.NotContain("30pt");
        styles[2].Should().Contain("margin-bottom:14pt;").And.Contain("--w-after-auto:1;");
    }

    [Test]
    public void Contextual_OnPrecedingParagraph_SuppressesWholeBoundary_WhenItsAfterWins()
    {
        using var ms = Docx(new[]
        {
            P("A", pp => { pp.Append(Sp(s => s.After = "200")); pp.Append(new ContextualSpacing()); }),
            P("B", pp => pp.Append(Sp(s => s.Before = "120"))),
            P("C")
        });
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[0].Should().Contain("margin-bottom:10pt;").And.Contain("--w-ctx-next:1;");
        styles[1].Should().Contain("margin-top:6pt;").And.Contain("--w-ctx-prev:1;");
        styles[2].Should().NotContain("--w-ctx-");
    }

    [Test]
    public void Contextual_OnPrecedingParagraph_KeepsFollowingBefore_WhenBeforeWins()
    {
        using var ms = Docx(new[]
        {
            P("A", pp => pp.Append(new ContextualSpacing())),
            P("B", pp => pp.Append(Sp(s => s.Before = "120")))
        });
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[0].Should().Contain("--w-ctx-next:1;");
        styles[1].Should().Contain("margin-top:6pt;").And.NotContain("--w-ctx-prev");
    }

    [Test]
    public void Contextual_OnFollowingParagraph_SuppressesOnlyItsOwnBefore()
    {
        using var ms = Docx(new[]
        {
            P("A", pp => pp.Append(Sp(s => s.After = "80"))),
            P("B", pp => { pp.Append(Sp(s => s.Before = "200")); pp.Append(new ContextualSpacing()); })
        });
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[0].Should().Contain("margin-bottom:4pt;").And.NotContain("--w-ctx-");
        styles[1].Should().Contain("margin-top:10pt;").And.Contain("--w-ctx-prev:1;");
    }

    [Test]
    public void Contextual_DirectFalse_OverridesStyleFlag()
    {
        using var ms = Docx(new[]
        {
            P("A", styleId: "Body"),
            P("B", styleId: "Body"),
            P("C", pp => pp.Append(new ContextualSpacing { Val = false }), styleId: "Body"),
            P("D", styleId: "Body"),
            P("E")
        });
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[0].Should().Contain("--w-contextual-spacing:1;").And.Contain("--w-ctx-next:1;");
        styles[1].Should().Contain("--w-ctx-prev:1;").And.Contain("--w-ctx-next:1;");
        styles[2].Should().Contain("--w-contextual-spacing:0;").And.Contain("--w-ctx-prev:1;").And.NotContain("--w-ctx-next");
        styles[2].Should().Contain("margin-bottom:10pt;");
        styles[3].Should().Contain("--w-ctx-prev:1;").And.NotContain("--w-ctx-next", "E ma inny styl");
    }

    [Test]
    public void Contextual_DirectFalse_RoundTripsAsExplicitFalse()
    {
        using var ms = Docx(new[]
        {
            P("C", pp => pp.Append(new ContextualSpacing { Val = false }), styleId: "Body")
        });
        using var doc = RoundTrip(ms);
        var ctx = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().First()
            .ParagraphProperties!.GetFirstChild<ContextualSpacing>();
        ctx.Should().NotBeNull();
        ctx!.Val!.Value.Should().BeFalse("bez jawnego false flaga ze stylu wróciłaby po zapisie");
    }

    [Test]
    public void Contextual_InSumModel_SuppressesOwnSidesOnly()
    {
        using var ms = Docx(new[]
        {
            P("A", pp => { pp.Append(Sp(s => s.After = "240")); pp.Append(new ContextualSpacing()); }),
            P("B", pp => pp.Append(Sp(s => s.Before = "80")))
        }, sumCompat: true);
        var styles = ParagraphStyles(_reader.Convert(ms).Html);
        styles[0].Should().Contain("--w-ctx-next:1;");
        styles[1].Should().Contain("margin-top:4pt;").And.NotContain("--w-ctx-prev");
    }

    [Test]
    public void DocGrid_RoundTripsThroughContainerAttributes()
    {
        using var ms = Docx(new[] { P("A"), P("B", pp => pp.Append(new SnapToGrid { Val = false })) }, gridPitch: 360);
        var html = _reader.Convert(ms).Html;
        html.Should().Contain("data-doc-grid-type=\"lines\"").And.Contain("data-doc-grid-pitch-tw=\"360\"");
        ParagraphStyles(html)[1].Should().Contain("--w-snap-to-grid:0;");

        using var doc = WordprocessingDocument.Open(new MemoryStream(_writer.Convert(html)), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var grid = body.Elements<SectionProperties>().Single().GetFirstChild<DocGrid>();
        grid.Should().NotBeNull();
        grid!.Type!.Value.Should().Be(DocGridValues.Lines);
        grid.LinePitch!.Value.Should().Be(360);
        var snap = body.Elements<Paragraph>().ElementAt(1).ParagraphProperties!.GetFirstChild<SnapToGrid>();
        snap.Should().NotBeNull();
        snap!.Val!.Value.Should().BeFalse();
    }

    [Test]
    public void NoDocGrid_EmitsNothing()
    {
        using var ms = Docx(new[] { P("A") });
        var html = _reader.Convert(ms).Html;
        html.Should().NotContain("data-doc-grid");
        using var doc = WordprocessingDocument.Open(new MemoryStream(_writer.Convert(html)), false);
        doc.MainDocumentPart!.Document.Body!.Elements<SectionProperties>().Single()
            .GetFirstChild<DocGrid>().Should().BeNull();
    }
}
