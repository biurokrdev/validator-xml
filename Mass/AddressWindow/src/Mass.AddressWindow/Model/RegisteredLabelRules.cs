namespace Mass.AddressWindow;

/// <summary>Rodzaj przesyłki poleconej odczytany z numeru nadawczego na nalepce R.</summary>
public enum RegisteredMailType
{
    /// <summary>Krajowa: numer GS1 SSCC, 20 cyfr (np. 00759007731512000621), kod kreskowy GS1-128.</summary>
    Domestic,

    /// <summary>Zagraniczna: numer UPU S10, 13 znaków (np. RR473124829PL), kod kreskowy Code 128.</summary>
    International,
}

/// <summary>
/// Reguły dla nalepki R w oknie nadawcy. Położenie i rozmiar są sprawdzane zawsze; tu ustawia się,
/// co ma być sprawdzone w treści nalepki, czyli w jej kodzie kreskowym.
/// </summary>
public sealed class RegisteredLabelRules
{
    /// <summary>
    /// Czy odczytać kod kreskowy i sprawdzić numer nadawczy (format, cyfra kontrolna, rodzaj). Przy <c>false</c>
    /// sprawdzane jest tylko, czy nalepka jest i czy się mieści.
    /// </summary>
    public bool ReadBarcode { get; init; } = true;

    /// <summary>Czy numer krajowy (SSCC, 20 cyfr) jest dopuszczalny.</summary>
    public bool AllowDomestic { get; init; } = true;

    /// <summary>Czy numer zagraniczny (UPU S10, 13 znaków) jest dopuszczalny.</summary>
    public bool AllowInternational { get; init; } = true;

    /// <summary>
    /// Numer, który ma być na nalepce; <c>null</c> = dowolny poprawny. Zapis dowolny (spacje, nawiasy i małe litery
    /// są pomijane), więc można podać numer tak, jak zwrócił go system nadawczy albo jak widnieje na nalepce.
    /// </summary>
    public string? ExpectedNumber { get; init; }

    /// <summary>
    /// Najmniejsza szerokość najwęższej kreski kodu w pikselach obrazu, poniżej której zgłaszane jest ostrzeżenie
    /// <c>LABEL_LOW_RESOLUTION</c>: obraz nalepki został zmniejszony (np. przez Worda przy zapisie do PDF)
    /// i wydrukowany kod może być nieostry.
    /// </summary>
    public double MinBarcodeModulePx { get; init; } = 2.5;

    public static RegisteredLabelRules Default { get; } = new();
}
