using System.Text;
using D2ViewerEditor.Infrastructure.Services.PdfConversion;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class MockDocxToPdfConversionServiceTests
{
    private MockDocxToPdfConversionService _service = null!;

    [SetUp]
    public void Setup()
    {
        _service = new MockDocxToPdfConversionService(NullLogger<MockDocxToPdfConversionService>.Instance);
    }

    [Test]
    public async Task ConvertAsync_ShouldReturnPdfFile()
    {
        var docx = BuildDocx("Pierwszy akapit dokumentu.", "Drugi akapit dokumentu.");

        var result = await _service.ConvertAsync(docx, "umowa.docx");

        result.FileName.Should().Be("umowa.pdf");
        AsText(result.PdfBytes).Should().StartWith("%PDF-1.");
        AsText(result.PdfBytes).Should().Contain("%%EOF");
    }

    [Test]
    public async Task ConvertAsync_ShouldCarryDocumentTextIntoPdf()
    {
        var docx = BuildDocx("Tresc kontrolna alfa", "Tresc kontrolna beta");

        var result = await _service.ConvertAsync(docx, "raport.docx");

        var pdf = AsText(result.PdfBytes);
        pdf.Should().Contain("Tresc kontrolna alfa");
        pdf.Should().Contain("Tresc kontrolna beta");
        pdf.Should().Contain("(raport) Tj");
    }

    [Test]
    public async Task ConvertAsync_ShouldTransliteratePolishDiacritics()
    {
        var docx = BuildDocx("Zażółć gęślą jaźń");

        var result = await _service.ConvertAsync(docx, "polski.docx");

        AsText(result.PdfBytes).Should().Contain("Zazólc gesla jazn");
    }

    [Test]
    public async Task ConvertAsync_ShouldFlattenTableRows()
    {
        var docx = BuildDocxWithTable(("Lewa", "Prawa"), ("Dol lewy", "Dol prawy"));

        var result = await _service.ConvertAsync(docx, "tabela.docx");

        AsText(result.PdfBytes).Should().Contain("Lewa | Prawa");
        AsText(result.PdfBytes).Should().Contain("Dol lewy | Dol prawy");
    }

    [Test]
    public async Task ConvertAsync_WithLongDocument_ShouldPaginate()
    {
        var paragraphs = Enumerable.Range(1, 300).Select(i => $"Linia numer {i}").ToArray();
        var docx = BuildDocx(paragraphs);

        var result = await _service.ConvertAsync(docx, "dlugi.docx");

        var pdf = AsText(result.PdfBytes);
        pdf.Should().Contain("/Type /Pages");
        pdf.Should().Contain("strona 2 z ");
    }

    [Test]
    public async Task ConvertAsync_WithUnreadableInput_ShouldStillReturnPdf()
    {
        var garbage = Encoding.UTF8.GetBytes("to nie jest pakiet DOCX");

        var result = await _service.ConvertAsync(garbage, "uszkodzony.docx");

        AsText(result.PdfBytes).Should().StartWith("%PDF-1.");
        AsText(result.PdfBytes).Should().Contain("Nie udalo sie odczytac tresci");
    }

    [Test]
    public async Task ConvertAsync_WithEmptyDocument_ShouldReturnSinglePagePdf()
    {
        var docx = BuildDocx();

        var result = await _service.ConvertAsync(docx, "pusty.docx");

        var pdf = AsText(result.PdfBytes);
        pdf.Should().Contain("/Count 1");
        pdf.Should().Contain("Dokument jest pusty");
    }

    private static byte[] BuildDocx(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = package.AddMainDocumentPart();
            var body = new Body();
            foreach (var text in paragraphs)
                body.AppendChild(new Paragraph(new Run(new Text(text))));
            main.Document = new Document(body);
        }

        return ms.ToArray();
    }

    private static byte[] BuildDocxWithTable(params (string Left, string Right)[] rows)
    {
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = package.AddMainDocumentPart();
            var table = new Table();
            foreach (var (left, right) in rows)
            {
                table.AppendChild(new TableRow(
                    new TableCell(new Paragraph(new Run(new Text(left)))),
                    new TableCell(new Paragraph(new Run(new Text(right))))));
            }

            main.Document = new Document(new Body(table));
        }

        return ms.ToArray();
    }

    private static string AsText(byte[] pdfBytes) => Encoding.Latin1.GetString(pdfBytes);
}
