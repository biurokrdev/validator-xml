using System.IO.Compression;

namespace Mass.AddressWindow.Api.Documents;

public enum DocumentFormat
{
    Pdf,

    Docx,
}

// The request cannot be accepted: empty, too large, or neither PDF nor DOCX.
public sealed class DocumentRejectedException(string message) : Exception(message);

// The file looked like a PDF but cannot be parsed (corrupted, encrypted): a technical problem, not a verdict.
public sealed class UnreadableDocumentException(string message) : Exception(message);

// A letter accepted for inspection: non-empty, within the size limit, and recognised as PDF or DOCX by content.
public sealed class LetterDocument
{
    public const int MaxSizeBytes = 20 * 1024 * 1024;

    private const int MaxFileNameLength = 255;

    private LetterDocument(string fileName, byte[] content, DocumentFormat format)
    {
        FileName = fileName;
        Content = content;
        Format = format;
    }

    public string FileName { get; }

    public byte[] Content { get; }

    public DocumentFormat Format { get; }

    public static LetterDocument Create(string? fileName, byte[]? content)
    {
        if (content is null || content.Length == 0)
        {
            throw new DocumentRejectedException("Plik jest pusty.");
        }

        if (content.Length > MaxSizeBytes)
        {
            throw new DocumentRejectedException($"Plik jest za duży. Dopuszczalny rozmiar to {MaxSizeBytes / (1024 * 1024)} MB.");
        }

        var format = DocumentFormatDetector.Detect(content)
            ?? throw new DocumentRejectedException("Plik nie jest dokumentem PDF ani DOCX (rozpoznanie po zawartości, nie po nazwie).");

        return new LetterDocument(NormalizeFileName(fileName, format), content, format);
    }

    public LetterDocument AsConvertedPdf(byte[] pdf) =>
        new(Path.ChangeExtension(FileName, ".pdf"), pdf, DocumentFormat.Pdf);

    private static string NormalizeFileName(string? fileName, DocumentFormat format)
    {
        var name = (fileName ?? string.Empty).Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.Length == 0)
        {
            name = format == DocumentFormat.Pdf ? "pismo.pdf" : "pismo.docx";
        }

        return name.Length > MaxFileNameLength ? name[..MaxFileNameLength] : name;
    }
}

public static class DocumentFormatDetector
{
    // The PDF spec allows junk before the header; readers look for it within the first kilobyte.
    private const int PdfHeaderSearchLength = 1024;

    private static readonly byte[] PdfHeader = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipHeader = "PK\u0003\u0004"u8.ToArray();

    public static DocumentFormat? Detect(byte[] content)
    {
        var head = content.AsSpan(0, Math.Min(content.Length, PdfHeaderSearchLength));
        if (head.IndexOf(PdfHeader) >= 0)
        {
            return DocumentFormat.Pdf;
        }

        return content.AsSpan().StartsWith(ZipHeader) && IsWordPackage(content) ? DocumentFormat.Docx : null;
    }

    private static bool IsWordPackage(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            return zip.Entries.Any(e => e.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
