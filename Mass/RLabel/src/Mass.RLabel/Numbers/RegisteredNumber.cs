using System.Text.RegularExpressions;

namespace Mass.RLabel.Numbers;

/// <summary>
/// Numer nadawczy listu poleconego po normalizacji, z rozbiciem na części i cyfrą kontrolną.
/// Algorytmy cyfr kontrolnych są takie same jak w aplikacji Mass (GS1 mod 10 i UPU S10 ważony mod 11).
/// </summary>
internal sealed partial class RegisteredNumber
{
    [GeneratedRegex("[^A-Z0-9]")]
    private static partial Regex NotAlphanumeric();

    private RegisteredNumber(MailType type, string normalized, string humanReadable, char expectedCheckDigit)
    {
        Type = type;
        Normalized = normalized;
        HumanReadable = humanReadable;
        ExpectedCheckDigit = expectedCheckDigit;
    }

    public MailType Type { get; }

    /// <summary>Same wielkie litery i cyfry, np. <c>00759007731512000621</c> albo <c>RR473124829PL</c>.</summary>
    public string Normalized { get; }

    /// <summary>Postać drukowana pod kodem kreskowym.</summary>
    public string HumanReadable { get; }

    /// <summary>Cyfra kontrolna policzona z numeru.</summary>
    public char ExpectedCheckDigit { get; }

    /// <summary>Cyfra kontrolna zapisana w numerze.</summary>
    public char ActualCheckDigit => Type == MailType.Domestic ? Normalized[19] : Normalized[10];

    public bool HasValidCheckDigit => ActualCheckDigit == ExpectedCheckDigit;

    /// <summary>Treść do zakodowania w kodzie kreskowym (dla SSCC bez nawiasu; FNC1 dodaje koder GS1-128).</summary>
    public string BarcodeContent => Normalized;

    public static RegisteredNumber Parse(string raw, MailType type, bool validateCheckDigit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        var normalized = NotAlphanumeric().Replace(raw.ToUpperInvariant(), "");

        var number = type switch
        {
            MailType.Domestic => ParseDomestic(normalized),
            MailType.International => ParseInternational(normalized),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Nieznany rodzaj przesyłki."),
        };

        if (validateCheckDigit && !number.HasValidCheckDigit)
        {
            throw new ArgumentException(
                $"Numer „{normalized}” ma złą cyfrę kontrolną: jest {number.ActualCheckDigit}, powinna być {number.ExpectedCheckDigit}.",
                nameof(raw));
        }

        return number;
    }

    private static RegisteredNumber ParseDomestic(string n)
    {
        if (n.Length != 20 || !n.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"Numer krajowy musi mieć 20 cyfr (SSCC z identyfikatorem aplikacji 00), a „{n}” ma {n.Length} znaków.",
                "number");
        }

        if (!n.StartsWith("00", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Numer krajowy musi zaczynać się od identyfikatora aplikacji GS1 „00”, a „{n}” zaczyna się od „{n[..2]}”.", "number");
        }

        // Jak na nalepce Poczty Polskiej: (00)IAC+prefiks S1 serial K  =>  (00)75900773 1 51200062 1
        var human = $"(00){n[2..10]} {n[10]} {n[11..19]} {n[19]}";
        return new RegisteredNumber(MailType.Domestic, n, human, Gs1CheckDigit(n[2..19]));
    }

    private static RegisteredNumber ParseInternational(string n)
    {
        var ok = n.Length == 13
                 && char.IsAsciiLetterUpper(n[0]) && char.IsAsciiLetterUpper(n[1])
                 && n[2..11].All(char.IsAsciiDigit)
                 && char.IsAsciiLetterUpper(n[11]) && char.IsAsciiLetterUpper(n[12]);
        if (!ok)
        {
            throw new ArgumentException(
                $"Numer zagraniczny musi mieć postać UPU S10: 2 litery, 9 cyfr, 2 litery kraju (np. RR473124829PL), a podano „{n}”.",
                "number");
        }

        // Zapis czytelny wg S10: RR 473 124 829 PL
        var human = $"{n[..2]} {n[2..5]} {n[5..8]} {n[8..11]} {n[11..]}";
        return new RegisteredNumber(MailType.International, n, human, S10CheckDigit(n[2..10]));
    }

    /// <summary>GS1 mod 10 dla 17 cyfr (IAC + prefiks + S1 + serial): od prawej wagi 3,1,3,1…</summary>
    internal static char Gs1CheckDigit(string digits17)
    {
        var sum = 0;
        for (var i = 0; i < digits17.Length; i++)
        {
            var fromRight = digits17.Length - 1 - i;
            sum += (digits17[i] - '0') * (fromRight % 2 == 0 ? 3 : 1);
        }

        return (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>UPU S10: 8 cyfr serialu z wagami 8 6 4 2 3 5 9 7, 11 − (suma mod 11); 10 → 0, 11 → 5.</summary>
    internal static char S10CheckDigit(string serial8)
    {
        int[] weights = [8, 6, 4, 2, 3, 5, 9, 7];
        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            sum += (serial8[i] - '0') * weights[i];
        }

        var check = 11 - sum % 11;
        check = check switch { 10 => 0, 11 => 5, _ => check };
        return (char)('0' + check);
    }
}
