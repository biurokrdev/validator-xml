using System.Text.RegularExpressions;

namespace Mass.Domain;

/// <summary>
/// Składanie, rozkładanie i walidacja numerów nadawczych przesyłek poleconych.
/// Algorytmy są identyczne z funkcjami SQL <c>mass.fn_gs1_check_digit</c>,
/// <c>mass.fn_s10_check_digit</c> i <c>mass.fn_registered_number_format</c>.
/// </summary>
public static partial class RegisteredNumberFormatter
{
    /// <summary>Globalny prefiks przedsiębiorstwa GS1 Poczty Polskiej (590 = Polska, 0773 = PP S.A.).</summary>
    public const string PocztaPolskaGs1Prefix = "5900773";

    public const int SerialMin = 0;
    public const int SerialMax = 99_999_999;

    private static readonly int[] S10Weights = { 8, 6, 4, 2, 3, 5, 9, 7 };

    // ---------------------------------------------------------------- cyfry kontrolne

    /// <summary>Cyfra kontrolna GS1 (mod 10, wagi 3/1 licząc od prawej).</summary>
    public static int Gs1CheckDigit(ReadOnlySpan<char> digits)
    {
        if (digits.IsEmpty) throw new ArgumentException("Pusty ciąg cyfr.", nameof(digits));

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var c = digits[digits.Length - 1 - i];
            if (!char.IsAsciiDigit(c)) throw new ArgumentException("Oczekiwano samych cyfr.", nameof(digits));
            sum += (c - '0') * (i % 2 == 0 ? 3 : 1);
        }

        return (10 - sum % 10) % 10;
    }

    /// <summary>Cyfra kontrolna UPU S10 (ważony mod 11 na 8-cyfrowym numerze).</summary>
    public static int S10CheckDigit(ReadOnlySpan<char> serial8)
    {
        if (serial8.Length != 8) throw new ArgumentException("Oczekiwano 8 cyfr.", nameof(serial8));

        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            var c = serial8[i];
            if (!char.IsAsciiDigit(c)) throw new ArgumentException("Oczekiwano samych cyfr.", nameof(serial8));
            sum += (c - '0') * S10Weights[i];
        }

        var check = 11 - sum % 11;
        return check switch { 10 => 0, 11 => 5, _ => check };
    }

    // ---------------------------------------------------------------- składanie

    /// <summary>Krajowy numer SSCC: 20 cyfr z prefiksem "00".</summary>
    public static string FormatDomestic(int iacDigit, int kindDigit, int serial)
    {
        if (iacDigit is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(iacDigit), "IAC musi być z zakresu 1-9.");
        if (kindDigit is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(kindDigit), "S1 musi być cyfrą.");
        ValidateSerial(serial);

        var body = $"{iacDigit}{PocztaPolskaGs1Prefix}{kindDigit}{serial:D8}";
        return $"00{body}{Gs1CheckDigit(body)}";
    }

    /// <summary>Zagraniczny numer S10: 13 znaków, np. RR473124829PL.</summary>
    public static string FormatInternational(string serviceIndicator, int serial, string countryCode = "PL")
    {
        if (!ServiceIndicatorRegex().IsMatch(serviceIndicator))
            throw new ArgumentException("Wskaźnik usługi musi być z zakresu RA..RZ.", nameof(serviceIndicator));
        if (!CountryCodeRegex().IsMatch(countryCode))
            throw new ArgumentException("Kod kraju musi mieć 2 wielkie litery.", nameof(countryCode));
        ValidateSerial(serial);

        var serialText = serial.ToString("D8");
        return $"{serviceIndicator}{serialText}{S10CheckDigit(serialText)}{countryCode}";
    }

    // ---------------------------------------------------------------- rozkładanie

    /// <summary>
    /// Rozkłada pełny numer (np. z pierwszej nalepki na rolce) na części. Akceptuje zapis
    /// z nawiasem "(00)" i spacjami. Zwraca <c>false</c>, gdy format lub cyfra kontrolna są błędne.
    /// </summary>
    public static bool TryParse(string? input, out ParsedRegisteredNumber result)
        => TryParse(input, out result, out _);

    /// <summary>Jak <see cref="TryParse(string?, out ParsedRegisteredNumber)"/>, ale z opisem przyczyny odrzucenia.</summary>
    public static bool TryParse(string? input, out ParsedRegisteredNumber result, out string? error)
    {
        result = default;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Numer jest pusty.";
            return false;
        }

        var text = Normalize(input);

        if (DomesticRegex().Match(text) is { Success: true } d)
        {
            var body = text.Substring(2, 17);
            var iac = text[2] - '0';
            var kind = text[10] - '0';
            var serial = int.Parse(d.Groups["serial"].Value);
            var check = text[19] - '0';
            var expected = Gs1CheckDigit(body);

            if (expected != check)
            {
                error = $"Błędna cyfra kontrolna GS1: jest {check}, powinna być {expected}.";
                return false;
            }

            result = new ParsedRegisteredNumber(
                RegisteredNumberPoolType.Domestic, serial, text,
                IacDigit: iac, KindDigit: kind, ServiceIndicator: null, CountryCode: null);
            return true;
        }

        if (InternationalRegex().Match(text) is { Success: true } i)
        {
            var serialText = i.Groups["serial"].Value;
            var check = text[10] - '0';
            var expected = S10CheckDigit(serialText);

            if (expected != check)
            {
                error = $"Błędna cyfra kontrolna S10: jest {check}, powinna być {expected}.";
                return false;
            }

            result = new ParsedRegisteredNumber(
                RegisteredNumberPoolType.International, int.Parse(serialText), text,
                IacDigit: null, KindDigit: null,
                ServiceIndicator: i.Groups["svc"].Value, CountryCode: i.Groups["cc"].Value);
            return true;
        }

        error = text.Length == 20 && text.All(char.IsAsciiDigit)
            ? "20 cyfr, ale prefiks nie jest numerem SSCC Poczty Polskiej (oczekiwano 00 + IAC + 5900773)."
            : "Nieznany format: oczekiwano 20 cyfr SSCC lub 13 znaków UPU S10 (RRnnnnnnnnnPL).";
        return false;
    }

    private static string Normalize(string input)
    {
        var text = input.Trim().ToUpperInvariant().Replace(" ", string.Empty).Replace("-", string.Empty);
        return text.StartsWith("(00)", StringComparison.Ordinal) ? "00" + text[4..] : text;
    }

    private static void ValidateSerial(int serial)
    {
        if (serial is < SerialMin or > SerialMax)
            throw new ArgumentOutOfRangeException(nameof(serial), $"Numer musi być z zakresu {SerialMin}-{SerialMax}.");
    }

    // 00 + IAC(1-9) + 5900773 + S1 + 8 cyfr + cyfra kontrolna
    [GeneratedRegex(@"^00[1-9]5900773[0-9](?<serial>[0-9]{8})[0-9]$")]
    private static partial Regex DomesticRegex();

    // RA..RZ + 8 cyfr + cyfra kontrolna + kod kraju
    [GeneratedRegex(@"^(?<svc>R[A-Z])(?<serial>[0-9]{8})[0-9](?<cc>[A-Z]{2})$")]
    private static partial Regex InternationalRegex();

    [GeneratedRegex(@"^R[A-Z]$")]
    private static partial Regex ServiceIndicatorRegex();

    [GeneratedRegex(@"^[A-Z]{2}$")]
    private static partial Regex CountryCodeRegex();
}

/// <summary>Wynik rozłożenia pełnego numeru nadania na części.</summary>
public readonly record struct ParsedRegisteredNumber(
    RegisteredNumberPoolType Type,
    int Serial,
    string FullNumber,
    int? IacDigit,
    int? KindDigit,
    string? ServiceIndicator,
    string? CountryCode);
