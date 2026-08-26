using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TableCellMarginAndIndentFidelityTests
{
    private DocxToHtmlConverter _reader = null!;

    [SetUp]
    public void Setup() => _reader = new DocxToHtmlConverter();

    private static MemoryStream BuildDocx(Action<MainDocumentPart, Body> build)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            build(mainPart, mainPart.Document.Body!);
            mainPart.Document.Body!.Append(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 }));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void Read_PartialTcMar_KeepsDefaultLeftRightMargins()
    {
        var html = _reader.Convert(BuildDocx((_, body) =>
        {
            var cell = new TableCell(
                new TableCellProperties(
                    new TableCellWidth { Width = "5000", Type = TableWidthUnitValues.Dxa },
                    new TableCellMargin(
                        new TopMargin { Width = "57", Type = TableWidthUnitValues.Dxa },
                        new BottomMargin { Width = "57", Type = TableWidthUnitValues.Dxa })),
                new Paragraph(new Run(new Text("X"))));
            body.Append(new Table(
                new TableProperties(),
                new TableGrid(new GridColumn { Width = "5000" }),
                new TableRow(cell)));
        })).Html;

        System.Text.RegularExpressions.Regex.Match(html, "<td[^>]*>").Value
            .Should().Contain("padding:3px 7px 3px 7px");
    }

    [Test]
    public void Read_HangingIndent_DoesNotShiftWholeParagraph()
    {
        var html = _reader.Convert(BuildDocx((_, body) =>
            body.Append(new Paragraph(
                new ParagraphProperties(new Indentation { Left = "720", Hanging = "360" }),
                new Run(new Text("Wysunięcie")))))).Html;

        var pTag = System.Text.RegularExpressions.Regex.Match(html, "<p[^>]*>").Value;
        pTag.Should().Contain("margin-left:48px");
        pTag.Should().Contain("text-indent:-24px");
        pTag.Should().NotContain("padding-left:24px",
            "dodatkowy padding przesuwał cały akapit w prawo o hanging");
    }

    [Test]
    public void Read_ContextualSpacingListInCell_CollapsesSpacingBetweenItems()
    {
        var html = _reader.Convert(BuildDocx((mainPart, body) =>
        {
            var level = new Level { LevelIndex = 0 };
            level.Append(new StartNumberingValue { Val = 1 });
            level.Append(new NumberingFormat { Val = NumberFormatValues.Bullet });
            level.Append(new LevelText { Val = "•" });
            level.Append(new LevelJustification { Val = LevelJustificationValues.Left });
            var abstractNum = new AbstractNum(level) { AbstractNumberId = 1 };
            var num = new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 };
            var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
            numberingPart.Numbering = new Numbering(abstractNum, num);
            numberingPart.Numbering.Save();

            Paragraph Item(string text) => new(
                new ParagraphProperties(
                    new NumberingProperties(
                        new NumberingLevelReference { Val = 0 },
                        new NumberingId { Val = 1 }),
                    new ContextualSpacing()),
                new Run(new Text(text)));

            body.Append(new Table(
                new TableProperties(),
                new TableGrid(new GridColumn { Width = "5000" }),
                new TableRow(new TableCell(
                    new TableCellProperties(new TableCellWidth { Width = "5000", Type = TableWidthUnitValues.Dxa }),
                    Item("Pierwszy"), Item("Drugi")))));
        })).Html;

        var liTags = System.Text.RegularExpressions.Regex.Matches(html, "<li[^>]*>")
            .Select(m => m.Value).ToList();
        liTags.Should().HaveCount(2);
        liTags[0].Should().Contain("--w-ctx-next:1",
            "sąsiad tego samego stylu poniżej — odstęp po zniesiony");
        liTags[1].Should().Contain("--w-ctx-prev:1",
            "sąsiad tego samego stylu powyżej — odstęp przed zniesiony");
    }
}
