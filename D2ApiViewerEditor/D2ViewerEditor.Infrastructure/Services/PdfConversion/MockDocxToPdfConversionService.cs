using D2ViewerEditor.Domain.Interfaces;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;

namespace D2ViewerEditor.Infrastructure.Services.PdfConversion;

public class MockDocxToPdfConversionService : IDocxToPdfConversionService
{
    private const string MockFooterNote = "Podglad PDF - atrapa konwersji (mock uslugi zewnetrznej)";

    private const int MaxLines = 20_000;

    private readonly ILogger<MockDocxToPdfConversionService> _logger;

    public MockDocxToPdfConversionService(ILogger<MockDocxToPdfConversionService> logger)
    {
        _logger = logger;
    }

    public Task<DocxToPdfConversionResult> ConvertAsync(
        byte[] docxBytes,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "MOCK DOCX→PDF: zewnętrzna usługa konwertująca nie jest podłączona — zwracam zastępczy PDF. " +
            "Plik wejściowy: {FileName}, {Bytes} B.",
            fileName, docxBytes.Length);

        var title = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(title)) title = "dokument";

        var lines = ExtractLines(docxBytes);
        var pdfBytes = SimplePdfWriter.Build(title, lines, MockFooterNote);

        return Task.FromResult(new DocxToPdfConversionResult(pdfBytes, $"{title}.pdf"));
    }

    private IReadOnlyList<string> ExtractLines(byte[] docxBytes)
    {
        var lines = new List<string>();

        try
        {
            using var stream = new MemoryStream(docxBytes, writable: false);
            using var package = WordprocessingDocument.Open(stream, isEditable: false);

            var body = package.MainDocumentPart?.Document?.Body;
            if (body == null)
                return new[] { "(Dokument nie zawiera tresci.)" };

            AppendBlocks(body, lines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MOCK DOCX→PDF: nie udało się odczytać treści pakietu DOCX.");
            return new[] { "(Nie udalo sie odczytac tresci dokumentu DOCX.)" };
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return lines.Count > 0 ? lines : new List<string> { "(Dokument jest pusty.)" };
    }

    private static void AppendBlocks(OpenXmlCompositeElement container, List<string> lines)
    {
        foreach (var child in container.ChildElements)
        {
            if (lines.Count >= MaxLines) return;

            switch (child)
            {
                case Paragraph paragraph:
                    lines.Add(paragraph.InnerText.Trim());
                    break;

                case Table table:
                    foreach (var row in table.Elements<TableRow>())
                    {
                        if (lines.Count >= MaxLines) return;
                        var cells = row.Elements<TableCell>()
                            .Select(c => c.InnerText.Replace('\n', ' ').Trim());
                        lines.Add(string.Join(" | ", cells));
                    }
                    lines.Add(string.Empty);
                    break;

                case SdtBlock sdt:
                    var sdtContent = sdt.Descendants<SdtContentBlock>().FirstOrDefault();
                    if (sdtContent != null) AppendBlocks(sdtContent, lines);
                    break;
            }
        }
    }
}
