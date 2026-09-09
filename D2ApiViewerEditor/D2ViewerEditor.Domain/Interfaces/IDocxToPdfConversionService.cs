namespace D2ViewerEditor.Domain.Interfaces;

public interface IDocxToPdfConversionService
{
    Task<DocxToPdfConversionResult> ConvertAsync(
        byte[] docxBytes,
        string fileName,
        CancellationToken cancellationToken = default);
}

public record DocxToPdfConversionResult(byte[] PdfBytes, string FileName);
