namespace Mass.AddressWindow.Rules.Labels;

/// <summary>Numer nadawczy odczytany z nalepki, po rozpoznaniu rodzaju i sprawdzeniu cyfry kontrolnej.</summary>
internal sealed record RegisteredNumberInfo(
    RegisteredMailType? Type,
    string Normalized,
    string? HumanReadable,
    bool IsValid,
    string? Problem);

/// <summary>
/// Reguły numerów nadawczych: te same co w Mass.RLabel (generator nalepek), powtórzone tutaj, żeby biblioteka
/// walidacji nie zależała od biblioteki rysującej. Krajowy: GS1 SSCC, 20 cyfr, cyfra kontrolna mod 10.
/// Zagraniczny: UPU S10, 13 znaków, cyfra kontrolna ważona mod 11.
/// </summary>
internal static class RegisteredNumberFormat
{
    public static string Normalize(string raw)
    {
        var chars = raw.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray();
        return new string(chars);
    }

    public static RegisteredNumberInfo Parse(string raw)
    {
        var n = Normalize(raw);
        if (n.Length == 20 && n.All(char.IsAsciiDigit))
        {
            if (!n.StartsWith("00", StringComparison.Ordinal))
            {
                return Invalid(RegisteredMailType.Domestic, n, $"numer krajowy musi zaczynać się od identyfikatora GS1 „00”, a zaczyna się od „{n[..2]}”");
            }

            var expected = Gs1CheckDigit(n[2..19]);
            var human = $"(00){n[2..10]} {n[10]} {n[11..19]} {n[19]}";
            return n[19] == expected
                ? new RegisteredNumberInfo(RegisteredMailType.Domestic, n, human, true, null)
                : new RegisteredNumberInfo(RegisteredMailType.Domestic, n, human, false,
                    $"zła cyfra kontrolna: jest {n[19]}, powinna być {expected}");
        }

        if (n.Length == 13 && char.IsAsciiLetterUpper(n[0]) && char.IsAsciiLetterUpper(n[1])
            && n[2..11].All(char.IsAsciiDigit) && char.IsAsciiLetterUpper(n[11]) && char.IsAsciiLetterUpper(n[12]))
        {
            var expected = S10CheckDigit(n[2..10]);
            var human = $"{n[..2]} {n[2..5]} {n[5..8]} {n[8..11]} {n[11..]}";
            return n[10] == expected
                ? new RegisteredNumberInfo(RegisteredMailType.International, n, human, true, null)
                : new RegisteredNumberInfo(RegisteredMailType.International, n, human, false,
                    $"zła cyfra kontrolna: jest {n[10]}, powinna być {expected}");
        }

        return Invalid(null, n,
            n.Length == 0
                ? "kod kreskowy nie zawiera numeru"
                : $"„{n}” nie jest numerem nadawczym: krajowy ma 20 cyfr (np. 00759007731512000621), zagraniczny 13 znaków (np. RR473124829PL)");
    }

    private static RegisteredNumberInfo Invalid(RegisteredMailType? type, string normalized, string problem) =>
        new(type, normalized, null, false, problem);

    /// <summary>GS1 mod 10 dla 17 cyfr: od prawej wagi 3, 1, 3, 1…</summary>
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
