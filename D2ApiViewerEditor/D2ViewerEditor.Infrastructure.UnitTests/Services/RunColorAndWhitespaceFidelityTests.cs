using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class RunColorAndWhitespaceFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static MemoryStream BuildDocx(
        Action<MainDocumentPart> customizeParts,
        params OpenXmlElement[] bodyElements)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            customizeParts(mainPart);
            var body = mainPart.Document.Body!;
            foreach (var el in bodyElements) body.Append(el);
            body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 }));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    private static void AddGrayParagraphStyle(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(new Style(
            new StyleRunProperties(new Color { Val = "595959" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "SzaryStyl",
            StyleName = new StyleName { Val = "SzaryStyl" }
        });
        stylesPart.Styles.Save();
    }

    private static void AddMinimalTheme(MainDocumentPart mainPart)
    {
        static Drawing.SystemColor Sys(string last) =>
            new() { Val = Drawing.SystemColorValues.WindowText, LastColor = last };
        static Drawing.RgbColorModelHex Rgb(string hex) => new() { Val = hex };

        var themePart = mainPart.AddNewPart<ThemePart>();
        themePart.Theme = new Drawing.Theme(
            new Drawing.ThemeElements(
                new Drawing.ColorScheme(
                    new Drawing.Dark1Color(Sys("1B1B1B")),
                    new Drawing.Light1Color(Rgb("FFFFFF")),
                    new Drawing.Dark2Color(Rgb("44546A")),
                    new Drawing.Light2Color(Rgb("E7E6E6")),
                    new Drawing.Accent1Color(Rgb("4472C4")),
                    new Drawing.Accent2Color(Rgb("ED7D31")),
                    new Drawing.Accent3Color(Rgb("A5A5A5")),
                    new Drawing.Accent4Color(Rgb("FFC000")),
                    new Drawing.Accent5Color(Rgb("5B9BD5")),
                    new Drawing.Accent6Color(Rgb("70AD47")),
                    new Drawing.Hyperlink(Rgb("0563C1")),
                    new Drawing.FollowedHyperlinkColor(Rgb("954F72")))
                { Name = "Office" },
                new Drawing.FontScheme(
                    new Drawing.MajorFont(new Drawing.LatinFont { Typeface = "Calibri Light" },
                        new Drawing.EastAsianFont { Typeface = "" }, new Drawing.ComplexScriptFont { Typeface = "" }),
                    new Drawing.MinorFont(new Drawing.LatinFont { Typeface = "Calibri" },
                        new Drawing.EastAsianFont { Typeface = "" }, new Drawing.ComplexScriptFont { Typeface = "" }))
                { Name = "Office" },
                new Drawing.FormatScheme(
                    new Drawing.FillStyleList(
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })),
                    new Drawing.LineStyleList(
                        new Drawing.Outline(), new Drawing.Outline(), new Drawing.Outline()),
                    new Drawing.EffectStyleList(
                        new Drawing.EffectStyle(new Drawing.EffectList()),
                        new Drawing.EffectStyle(new Drawing.EffectList()),
                        new Drawing.EffectStyle(new Drawing.EffectList())),
                    new Drawing.BackgroundFillStyleList(
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                        new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })))
                { Name = "Office" })
        ) { Name = "Office" };
        themePart.Theme.Save();
    }

    [Test]
    public void Read_ColorAuto_ResetsInheritedStyleColorToBlack()
    {
        var para = new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "SzaryStyl" }),
            new Run(new Text("Szary ze stylu")),
            new Run(new RunProperties(new Color { Val = "auto" }),
                new Text("Czarny (auto)")));

        var html = _reader.Convert(BuildDocx(AddGrayParagraphStyle, para)).Html;

        html.Should().Contain("color:#595959", "styl akapitu niesie szary inline");
        var autoSpan = System.Text.RegularExpressions.Regex.Match(
            html, "<span[^>]*>Czarny \\(auto\\)</span>").Value;
        autoSpan.Should().Contain("color:#000000",
            "w:color val=auto to w Wordzie czarny — musi zresetować szary odziedziczony ze stylu");
    }

    [Test]
    public void Read_ThemeColorText1_ResolvesFromThemeDark1()
    {
        var para = new Paragraph(
            new Run(new RunProperties(new Color { ThemeColor = ThemeColorValues.Text1 }),
                new Text("Motyw text1")));

        var html = _reader.Convert(BuildDocx(AddMinimalTheme, para)).Html;

        System.Text.RegularExpressions.Regex.Match(html, "<span[^>]*>Motyw text1</span>").Value
            .Should().Contain("color:#1B1B1B", "text1 mapuje się na dark1 (domyślne clrSchemeMapping Worda)");
    }

    [Test]
    public void Read_MultipleSpaces_AreKeptVerbatimInHtml()
    {
        var para = new Paragraph(new Run(
            new Text("A    B") { Space = SpaceProcessingModeValues.Preserve }));

        var html = _reader.Convert(BuildDocx(_ => { }, para)).Html;

        html.Should().Contain("A    B");
    }

    [Test]
    public void RoundTrip_MultipleSpaces_SurviveWithPreserve()
    {
        var para = new Paragraph(new Run(
            new Text("A    B") { Space = SpaceProcessingModeValues.Preserve }));
        var html = _reader.Convert(BuildDocx(_ => { }, para)).Html;

        using var doc = WordprocessingDocument.Open(new MemoryStream(_writer.Convert(html)), false);
        var text = doc.MainDocumentPart!.Document!.Body!.Descendants<Text>()
            .First(t => t.Text.Contains('A'));
        text.Text.Should().Be("A    B");
        text.Space!.Value.Should().Be(SpaceProcessingModeValues.Preserve);
    }

    [Test]
    public void RoundTrip_LeadingTrailingSpacesSplitAcrossRuns_KeepSpaceCount()
    {
        var para = new Paragraph(
            new Run(new Text("Pierwsze zdanie.") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new RunProperties(new Bold()), new Text("  Drugie zdanie. ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new Text(" koniec ") { Space = SpaceProcessingModeValues.Preserve }));
        var html = _reader.Convert(BuildDocx(_ => { }, para)).Html;

        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "")
            .Should().Contain("Pierwsze zdanie.  Drugie zdanie.  koniec ");

        using var doc = WordprocessingDocument.Open(new MemoryStream(_writer.Convert(html)), false);
        var joined = string.Concat(doc.MainDocumentPart!.Document!.Body!.Descendants<Text>().Select(t => t.Text));
        joined.Should().Be("Pierwsze zdanie.  Drugie zdanie.  koniec ");
        doc.MainDocumentPart.Document.Body.Descendants<Text>()
            .Where(t => t.Text.StartsWith(' ') || t.Text.EndsWith(' '))
            .Should().OnlyContain(t => t.Space != null && t.Space.Value == SpaceProcessingModeValues.Preserve);
    }

    [Test]
    public void RoundTrip_NonBreakingSpaceInText_IsKept()
    {
        var para = new Paragraph(new Run(new Text("10 zł") { Space = SpaceProcessingModeValues.Preserve }));
        var html = _reader.Convert(BuildDocx(_ => { }, para)).Html;

        using var doc = WordprocessingDocument.Open(new MemoryStream(_writer.Convert(html)), false);
        doc.MainDocumentPart!.Document!.Body!.Descendants<Text>().First().Text.Should().Be("10 zł");
    }

    [Test]
    public void ParagraphSpacing_And_RunCharacterSpacing_AreNotConfused()
    {
        var para = new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { Before = "240" }),
            new Run(new RunProperties(new Spacing { Val = 20 }), new Text("rozstrzelone")),
            new Run(new Text(" zwykłe")));
        var html = _reader.Convert(BuildDocx(_ => { }, para)).Html;

        var p = System.Text.RegularExpressions.Regex.Match(html, "<p style=\"([^\"]*)\"").Groups[1].Value;
        p.Should().Contain("margin-top:12pt;").And.NotContain("letter-spacing");
        html.Should().MatchRegex("<span style=\"[^\"]*letter-spacing:1pt;[^\"]*\">rozstrzelone</span>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(_writer.Convert(html)), false);
        var paragraph = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();
        paragraph.ParagraphProperties!.GetFirstChild<SpacingBetweenLines>()!.Before!.Value.Should().Be("240");
        var runs = paragraph.Elements<Run>().ToList();
        runs.First(r => r.InnerText == "rozstrzelone").RunProperties!.GetFirstChild<Spacing>()!.Val!.Value.Should().Be(20);
        runs.First(r => r.InnerText == " zwykłe").RunProperties?.GetFirstChild<Spacing>().Should().BeNull();
    }

    [Test]
    public void Read_TabsWithoutStops_ArePositionedOnDefaultTabGrid()
    {
        var para = new Paragraph(
            new Run(new Text("A")),
            new Run(new TabChar()),
            new Run(new Text("B")),
            new Run(new TabChar()),
            new Run(new Text("C")));

        var html = _reader.Convert(BuildDocx(_ => { }, para)).Html;

        html.Should().Contain("docx-tab-seg");
        html.Should().Contain("left:47px");
        html.Should().Contain("left:94px");
    }

    [Test]
    public void RoundTrip_TabInsideTableCell_KeepsTabChar()
    {
        var table = new Table(
            new TableProperties(),
            new TableGrid(new GridColumn { Width = "5000" }),
            new TableRow(new TableCell(
                new TableCellProperties(new TableCellWidth { Width = "5000", Type = TableWidthUnitValues.Dxa }),
                new Paragraph(
                    new Run(new Text("X")),
                    new Run(new TabChar()),
                    new Run(new Text("Y"))))));

        var html = _reader.Convert(BuildDocx(_ => { }, table)).Html;

        using var doc = WordprocessingDocument.Open(new MemoryStream(_writer.Convert(html)), false);
        var cellPara = doc.MainDocumentPart!.Document!.Body!
            .Descendants<TableCell>().First().Descendants<Paragraph>().First();
        cellPara.Descendants<TabChar>().Should().HaveCount(1,
            "tab w komórce nie może degradować do spacji ani ginąć");
        cellPara.InnerText.Should().Contain("X").And.Contain("Y");
    }
}
