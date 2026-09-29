namespace Mass.RLabel;

/// <summary>Wygenerowana grafika nalepki.</summary>
public sealed class RegisteredLabelImage
{
    internal RegisteredLabelImage(
        byte[] bytes,
        LabelImageFormat format,
        int widthPx,
        int heightPx,
        int dpi,
        string normalizedNumber,
        string humanReadableNumber,
        double barcodeModuleMm)
    {
        Bytes = bytes;
        Format = format;
        WidthPx = widthPx;
        HeightPx = heightPx;
        Dpi = dpi;
        NormalizedNumber = normalizedNumber;
        HumanReadableNumber = humanReadableNumber;
        BarcodeModuleMm = barcodeModuleMm;
    }

    /// <summary>Zawartość pliku PNG/JPEG.</summary>
    public byte[] Bytes { get; }

    /// <summary>Zawartość pliku zakodowana w Base64 (bez prefiksu <c>data:</c>).</summary>
    public string Base64 => Convert.ToBase64String(Bytes);

    /// <summary>Gotowy adres <c>data:image/png;base64,...</c> do wstawienia w <c>&lt;img src&gt;</c>.</summary>
    public string DataUri => $"data:{ContentType};base64,{Base64}";

    /// <summary>Format pliku.</summary>
    public LabelImageFormat Format { get; }

    /// <summary>Typ MIME: <c>image/png</c> albo <c>image/jpeg</c>.</summary>
    public string ContentType => Format == LabelImageFormat.Png ? "image/png" : "image/jpeg";

    /// <summary>Rozszerzenie pliku bez kropki.</summary>
    public string FileExtension => Format == LabelImageFormat.Png ? "png" : "jpg";

    /// <summary>Szerokość w pikselach.</summary>
    public int WidthPx { get; }

    /// <summary>Wysokość w pikselach.</summary>
    public int HeightPx { get; }

    /// <summary>Rozdzielczość zapisana w pliku.</summary>
    public int Dpi { get; }

    /// <summary>Numer po normalizacji (same wielkie litery i cyfry), taki jak zakodowany w kodzie kreskowym.</summary>
    public string NormalizedNumber { get; }

    /// <summary>Numer w postaci wydrukowanej pod kodem, np. <c>(00)75900773 1 51200062 1</c>.</summary>
    public string HumanReadableNumber { get; }

    /// <summary>Szerokość najwęższej kreski kodu (moduł) w mm. Poniżej ok. 0,25 mm skanery mogą mieć trudności.</summary>
    public double BarcodeModuleMm { get; }
}
