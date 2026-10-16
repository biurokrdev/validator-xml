using Mass.AddressWindow.Api.Documents;

namespace Mass.AddressWindow.Api.Conversion;

// Technical failure of the DOCX -> PDF step; never a verdict about the letter itself.
public sealed class DocumentConversionException(string message, bool converterUnavailable = false, Exception? inner = null)
    : Exception(message, inner)
{
    public bool ConverterUnavailable { get; } = converterUnavailable;
}

// Integration point for the external service that renders DOCX to PDF. Today only the mock implements it;
// a real implementation sends the document over HTTP and returns the PDF it gets back.
public interface IDocxToPdfConverter
{
    Task<byte[]> ConvertAsync(LetterDocument docx, CancellationToken cancellationToken);
}
