namespace Mass.AddressWindow.Domain.Letters;

/// <summary>
/// Pismo w PDF przyjęte do sprawdzenia. Obiekt wartości: jeśli istnieje, to ma nazwę,
/// niepustą treść w dozwolonym rozmiarze i nagłówek pliku PDF.
/// </summary>
public sealed class PdfLetter
{
    public const int MaxSizeBytes = 20 * 1024 * 1024;

    private const int MaxFileNameLength = 255;

    // Specyfikacja PDF dopuszcza śmieci przed nagłówkiem, czytniki szukają go w pierwszym kilobajcie.
    private const int HeaderSearchLength = 1024;

    private static readonly byte[] Header = "%PDF-"u8.ToArray();

    private PdfLetter(string fileName, byte[] content)
    {
        FileName = fileName;
        Content = content;
    }

    public string FileName { get; }

    public byte[] Content { get; }

    public int SizeBytes => Content.Length;

    public static PdfLetter Create(string? fileName, byte[]? content)
    {
        if (content is null || content.Length == 0)
        {
            throw new InvalidLetterException("Plik jest pusty.");
        }

        if (content.Length > MaxSizeBytes)
        {
            throw new InvalidLetterException($"Plik jest za duży. Dopuszczalny rozmiar to {MaxSizeBytes / (1024 * 1024)} MB.");
        }

        var searched = content.AsSpan(0, Math.Min(content.Length, HeaderSearchLength));
        if (searched.IndexOf(Header) < 0)
        {
            throw new InvalidLetterException("Plik nie jest dokumentem PDF.");
        }

        return new PdfLetter(NormalizeFileName(fileName), content);
    }

    private static string NormalizeFileName(string? fileName)
    {
        // Przeglądarki wysyłają samą nazwę, ale klient API może podać pełną ścieżkę.
        var name = fileName?.Trim().Replace('\\', '/') ?? string.Empty;
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.Length == 0)
        {
            return "pismo.pdf";
        }

        return name.Length > MaxFileNameLength ? name[..MaxFileNameLength] : name;
    }
}
