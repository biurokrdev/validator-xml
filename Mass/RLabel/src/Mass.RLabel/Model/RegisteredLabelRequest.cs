namespace Mass.RLabel;

/// <summary>Parametry nalepki R do wygenerowania.</summary>
public sealed class RegisteredLabelRequest
{
    /// <summary>Domyślna szerokość nalepki (mm).</summary>
    public const double DefaultWidthMm = 65;

    /// <summary>Domyślna wysokość nalepki (mm).</summary>
    public const double DefaultHeightMm = 25;

    /// <summary>Domyślna rozdzielczość (punkty na cal).</summary>
    public const int DefaultDpi = 300;

    /// <summary>
    /// Numer nadawczy. Może być w postaci z nalepki (nawias, spacje, myślniki, małe litery), np.
    /// <c>(00) 7 5900773 1 51200062 1</c> albo <c>rr473124829pl</c>. Znaki inne niż litery i cyfry są pomijane.
    /// </summary>
    public required string Number { get; init; }

    /// <summary>Krajowa (SSCC) albo zagraniczna (S10).</summary>
    public required MailType Type { get; init; }

    /// <summary>Szerokość nalepki w milimetrach. Domyślnie 65 mm.</summary>
    public double WidthMm { get; init; } = DefaultWidthMm;

    /// <summary>Wysokość nalepki w milimetrach. Domyślnie 25 mm.</summary>
    public double HeightMm { get; init; } = DefaultHeightMm;

    /// <summary>Rozdzielczość w DPI. Domyślnie 300. Zapisywana w pliku, więc drukarka odtworzy wymiar w mm.</summary>
    public int Dpi { get; init; } = DefaultDpi;

    /// <summary>PNG (domyślnie) albo JPEG.</summary>
    public LabelImageFormat Format { get; init; } = LabelImageFormat.Png;

    /// <summary>Jakość JPEG 1–100. Domyślnie 92. Nie ma znaczenia dla PNG.</summary>
    public int JpegQuality { get; init; } = 92;

    /// <summary>Czy rysować ramkę wokół nalepki. Domyślnie nie, jak na nalepce Poczty Polskiej.</summary>
    public bool DrawBorder { get; init; }

    /// <summary>
    /// Czy sprawdzać cyfrę kontrolną numeru. Domyślnie tak: numer ze złą cyfrą kontrolną to najpewniej literówka,
    /// a nalepka z takim numerem byłaby bezwartościowa. Wyłączenie ma sens tylko do testów.
    /// </summary>
    public bool ValidateCheckDigit { get; init; } = true;
}
