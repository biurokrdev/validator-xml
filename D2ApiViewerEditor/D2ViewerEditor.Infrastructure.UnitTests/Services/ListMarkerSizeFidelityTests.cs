using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

/// <summary>
/// Rozmiar numeru/punktatora listy (zgłoszenie: „wielkość punktatorów inna niż tekstu").
/// Word skaluje znacznik rPr-em poziomu (w:lvl/w:rPr/w:sz) lub rPr-em znaku końca akapitu;
/// my dziedziczyliśmy rozmiar kontenera (default dokumentu). Kontrakt jak dla koloru
/// (14104878): var --marker-font-size + data-marker-size / data-mark-size (round-trip).
/// </summary>
[TestFixture]
public class ListMarkerSizeFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static MemoryStream BuildListDocx(string? lvlSzHalfPoints, string? markSzHalfPoints, string? runSzHalfPoints)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();

            var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
            var lvl = new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.Decimal },
                new LevelText { Val = "%1." },
                new PreviousParagraphProperties(new Indentation { Left = "720", Hanging = "360" }))
            { LevelIndex = 0 };
            if (lvlSzHalfPoints != null)
                lvl.Append(new NumberingSymbolRunProperties(new FontSize { Val = lvlSzHalfPoints }));
            numberingPart.Numbering = new Numbering(
                new AbstractNum(lvl) { AbstractNumberId = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
            numberingPart.Numbering.Save();

            var pPr = new ParagraphProperties(
                new NumberingProperties(
                    new NumberingLevelReference { Val = 0 },
                    new NumberingId { Val = 1 }));
            if (markSzHalfPoints != null)
                pPr.Append(new ParagraphMarkRunProperties(new FontSize { Val = markSzHalfPoints }));

            var run = new Run(new Text("Pozycja listy"));
            if (runSzHalfPoints != null)
                run.RunProperties = new RunProperties(new FontSize { Val = runSzHalfPoints });

            mainPart.Document = new Document(new Body(
                new Paragraph(pPr, run),
                new SectionProperties(new PageSize { Width = 11906, Height = 16838 })));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void LevelRunProps_Size_EmitsContainerVarAndRoundTripAttr()
    {
        var html = _reader.Convert(BuildListDocx("18", null, null)).Html;

        html.Should().Contain("--marker-font-size:9pt");
        html.Should().Contain("data-marker-size=\"18\"");
    }

    [Test]
    public void ParagraphMarkRunProps_Size_EmitsItemVarAndRoundTripAttr()
    {
        var html = _reader.Convert(BuildListDocx(null, "24", "24")).Html;

        html.Should().Contain("--marker-font-size:12pt");
        html.Should().Contain("data-mark-size=\"24\"");
    }

    [Test]
    public void FirstRunSize_FallbackIsDisplayOnly()
    {
        // Brak sz w poziomie i na znaku akapitu — marker skaluje się z tekstem (var),
        // ale NIC nie round-tripuje (wartość pochodna, nie źródłowa).
        var html = _reader.Convert(BuildListDocx(null, null, "32")).Html;

        html.Should().Contain("--marker-font-size:16pt");
        html.Should().NotContain("data-mark-size");
        html.Should().NotContain("data-marker-size");
    }

    [Test]
    public void RoundTrip_LevelSize_ComesBackInNumberingDefinition()
    {
        var html = _reader.Convert(BuildListDocx("18", null, null)).Html;
        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var lvl = doc.MainDocumentPart!.NumberingDefinitionsPart!.Numbering!
            .Descendants<Level>().First(l => l.LevelIndex!.Value == 0);
        lvl.NumberingSymbolRunProperties!.GetFirstChild<FontSize>()!.Val!.Value.Should().Be("18");
    }

    [Test]
    public void RoundTrip_ParagraphMarkSize_ComesBackInParagraphMarkRunProps()
    {
        var html = _reader.Convert(BuildListDocx(null, "24", "24")).Html;
        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var para = doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>()
            .First(p => p.InnerText.Contains("Pozycja listy"));
        para.ParagraphProperties!.GetFirstChild<ParagraphMarkRunProperties>()!
            .GetFirstChild<FontSize>()!.Val!.Value.Should().Be("24");
    }
}
