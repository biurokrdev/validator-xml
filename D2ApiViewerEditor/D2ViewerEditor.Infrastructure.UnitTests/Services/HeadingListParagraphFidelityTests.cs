using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class HeadingListParagraphFidelityTests
{
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup() => _writer = new HtmlToDocxConverter();

    private static Paragraph FirstParagraph(byte[] docx, string containsText)
    {
        var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        return (Paragraph)doc.MainDocumentPart!.Document.Body!
            .Descendants<Paragraph>()
            .First(p => p.InnerText.Contains(containsText))
            .CloneNode(true);
    }

    [Test]
    public void Heading_KeepsSpacingIndentShadingAndBorders_OnExport()
    {
        var html = "<h2 style=\"margin-top:12pt;padding-bottom:6pt;margin-left:28.35pt;" +
                   "background-color:#EEEEEE;border-bottom:1px solid #FF0000;\">Tytuł rozdziału</h2>";

        var para = FirstParagraph(_writer.Convert(html), "Tytuł rozdziału");
        var props = para.ParagraphProperties!;

        props.ParagraphStyleId!.Val!.Value.Should().Be("Heading2");
        var spacing = props.GetFirstChild<SpacingBetweenLines>()!;
        spacing.Before!.Value.Should().Be("240", "12pt = 240 twips");
        spacing.After!.Value.Should().Be("120", "6pt = 120 twips");
        props.GetFirstChild<Indentation>()!.Left!.Value.Should().Be("567", "28.35pt ≈ 1 cm = 567 twips");
        props.GetFirstChild<Shading>()!.Fill!.Value.Should().Be("EEEEEE");
        props.GetFirstChild<ParagraphBorders>()!.BottomBorder.Should().NotBeNull();
    }

    [Test]
    public void Heading_PageBreakBefore_SurvivesExport()
    {
        var html = "<h1 style=\"page-break-before:always;\">Nowy rozdział</h1>";

        var para = FirstParagraph(_writer.Convert(html), "Nowy rozdział");

        para.ParagraphProperties!.GetFirstChild<PageBreakBefore>().Should().NotBeNull();
    }

    [Test]
    public void ListItem_KeepsSpacingAndShading_ButIndentationStaysWithDataContract()
    {
        var html = "<ol><li data-ind-left-tw=\"850\" data-ind-hanging-tw=\"360\" " +
                   "style=\"margin-left:37px;margin-top:6pt;padding-bottom:3pt;background-color:#FFF2CC;\">Punkt A</li></ol>";

        var para = FirstParagraph(_writer.Convert(html), "Punkt A");
        var props = para.ParagraphProperties!;

        var spacing = props.GetFirstChild<SpacingBetweenLines>()!;
        spacing.Before!.Value.Should().Be("120");
        spacing.After!.Value.Should().Be("60");
        props.GetFirstChild<Shading>()!.Fill!.Value.Should().Be("FFF2CC");
        var ind = props.GetFirstChild<Indentation>()!;
        ind.Left!.Value.Should().Be("850");
        ind.Hanging!.Value.Should().Be("360");
    }

    [Test]
    public void ParagraphIndent_FromDialogContract_PtAndNegativeTextIndent()
    {
        var html = "<p style=\"margin-left:56.7pt;margin-right:14.2pt;text-indent:-28.35pt;\">Akapit z wysunięciem</p>";

        var para = FirstParagraph(_writer.Convert(html), "Akapit z wysunięciem");
        var ind = para.ParagraphProperties!.GetFirstChild<Indentation>()!;

        ind.Left!.Value.Should().Be("1134", "56.7pt = 2 cm");
        ind.Right!.Value.Should().Be("284", "14.2pt ≈ 0.5 cm");
        ind.Hanging!.Value.Should().Be("567", "ujemny text-indent = w:ind hanging");
        ind.FirstLine.Should().BeNull();
    }
}
