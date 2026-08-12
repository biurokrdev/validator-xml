using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TrackedRevisionAcceptanceTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static InsertedRun Ins(string text) => new(
        new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }))
    { Author = "Recenzent", Id = "1" };

    private static DeletedRun Del(string text) => new(
        new Run(new DeletedText(text) { Space = SpaceProcessingModeValues.Preserve }))
    { Author = "Recenzent", Id = "2" };

    private static MemoryStream BuildBody(params OpenXmlElement[] blocks)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var block in blocks) body.Append(block);
            body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 }));
            mainPart.Document = new Document(body);
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void InsertedText_IsKeptInContent()
    {
        using var docx = BuildBody(new Paragraph(
            new Run(new Text("Stała treść ") { Space = SpaceProcessingModeValues.Preserve }),
            Ins("wstawiona w rewizji"),
            new Run(new Text(" i dalej") { Space = SpaceProcessingModeValues.Preserve })));

        var html = _reader.Convert(docx).Html;

        html.Should().Contain("Stała treść ");
        html.Should().Contain("wstawiona w rewizji");
        html.Should().Contain(" i dalej");
    }

    [Test]
    public void DeletedText_IsDropped_NotResurrected()
    {
        using var docx = BuildBody(new Paragraph(
            new Run(new Text("Zostaje")),
            Del("skasowane w rewizji")));

        var html = _reader.Convert(docx).Html;

        html.Should().Contain("Zostaje");
        html.Should().NotContain("skasowane w rewizji");
    }

    [Test]
    public void MoveToContent_IsKept_MoveFromContent_IsDropped()
    {
        using var docx = BuildBody(new Paragraph(
            new MoveFromRun(new Run(new DeletedText("stare miejsce"))) { Author = "R", Id = "3" },
            new MoveToRun(new Run(new Text("nowe miejsce"))) { Author = "R", Id = "4" }));

        var html = _reader.Convert(docx).Html;

        html.Should().Contain("nowe miejsce");
        html.Should().NotContain("stare miejsce");
    }

    [Test]
    public void TableRowDeletedInRevision_IsRemoved()
    {
        var deletedRow = new TableRow(
            new TableRowProperties(new Deleted { Author = "R", Id = "5" }),
            new TableCell(new Paragraph(new Run(new DeletedText("wiersz skasowany")))));
        var keptRow = new TableRow(
            new TableCell(new Paragraph(new Run(new Text("wiersz zostaje")))));
        using var docx = BuildBody(new Table(
            new TableProperties(),
            new TableGrid(new GridColumn { Width = "5000" }),
            deletedRow, keptRow));

        var html = _reader.Convert(docx).Html;

        html.Should().Contain("wiersz zostaje");
        html.Should().NotContain("wiersz skasowany");
    }

    [Test]
    public void InsertedRunInsideHeader_IsKept()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var headerPart = mainPart.AddNewPart<HeaderPart>();
            headerPart.Header = new Header(new Paragraph(Ins("nagłówek z rewizji")));
            headerPart.Header.Save();
            mainPart.Document.Body!.Append(new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(headerPart) },
                new PageSize { Width = 11906, Height = 16838 }));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var content = _reader.Convert(ms);

        content.Header!.Html.Should().Contain("z rewizji");
    }

    [Test]
    public void RoundTrip_AcceptedRevision_BecomesPlainText()
    {
        using var docx = BuildBody(new Paragraph(
            new Run(new Text("Stała ") { Space = SpaceProcessingModeValues.Preserve }),
            Ins("wstawiona"),
            Del("skasowana")));

        var bytes = _writer.Convert(_reader.Convert(docx).Html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        body.InnerText.Should().Contain("Stała ").And.Contain("wstawiona");
        body.InnerText.Should().NotContain("skasowana");
        body.Descendants<InsertedRun>().Should().BeEmpty();
        body.Descendants<DeletedRun>().Should().BeEmpty();
    }

    [Test]
    public void SourceStream_IsNotMutated_OriginalStillHasRevisions()
    {
        using var docx = BuildBody(new Paragraph(Ins("wstawiona")));
        var original = docx.ToArray();

        _reader.Convert(new MemoryStream(original));

        using var doc = WordprocessingDocument.Open(new MemoryStream(original), false);
        doc.MainDocumentPart!.Document.Body!.Descendants<InsertedRun>().Should().NotBeEmpty();
    }
}
