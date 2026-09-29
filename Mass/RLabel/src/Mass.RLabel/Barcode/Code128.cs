using System.Text;

namespace Mass.RLabel.Barcode;

/// <summary>
/// Koder Code 128 i GS1-128 do postaci ciągu modułów (kreska/przerwa). Bez zależności zewnętrznych.
/// </summary>
internal static class Code128
{
    public const int Fnc1 = 102;
    private const int CodeC = 99;
    private const int CodeB = 100;
    private const int StartB = 104;
    private const int StartC = 105;
    private const int Stop = 106;

    /// <summary>Szerokość cichej strefy po każdej stronie kodu (w modułach), wg ISO/IEC 15417.</summary>
    public const int QuietZoneModules = 10;

    /// <summary>
    /// Wzory symboli 0–106: kolejne szerokości kreska, przerwa, kreska, przerwa, kreska, przerwa (stop ma 7 elementów).
    /// Każdy symbol ma 11 modułów, stop 13.
    /// </summary>
    internal static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    /// <summary>
    /// GS1-128 z jednym identyfikatorem aplikacji o stałej długości (np. SSCC „00” + 18 cyfr):
    /// Start C, FNC1, dane w zestawie C, suma kontrolna, Stop. Wymaga parzystej liczby cyfr.
    /// </summary>
    public static bool[] EncodeGs1(string digits)
    {
        if (digits.Length == 0 || digits.Length % 2 != 0 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("GS1-128 w zestawie C wymaga parzystej liczby cyfr.", nameof(digits));
        }

        var symbols = new List<int> { StartC, Fnc1 };
        for (var i = 0; i < digits.Length; i += 2)
        {
            symbols.Add((digits[i] - '0') * 10 + (digits[i + 1] - '0'));
        }

        return ToModules(symbols);
    }

    /// <summary>
    /// Code 128 dla tekstu ASCII 32–126. Ciągi co najmniej 4 cyfr są kodowane w zestawie C (dwie cyfry na symbol),
    /// reszta w zestawie B.
    /// </summary>
    public static bool[] Encode(string text)
    {
        if (text.Length == 0 || text.Any(c => c < 32 || c > 126))
        {
            throw new ArgumentException("Code 128 w tej implementacji obsługuje niepusty tekst ze znaków ASCII 32–126.", nameof(text));
        }

        var symbols = new List<int>();
        var inCodeC = false;
        var i = 0;
        while (i < text.Length)
        {
            var digitRun = DigitRunLength(text, i);
            var useC = digitRun >= 4 || (digitRun == text.Length && digitRun >= 2);
            if (useC)
            {
                symbols.Add(symbols.Count == 0 ? StartC : inCodeC ? -1 : CodeC);
                inCodeC = true;
                var pairs = digitRun / 2; // nieparzysta ostatnia cyfra zostaje dla zestawu B
                for (var p = 0; p < pairs; p++, i += 2)
                {
                    symbols.Add((text[i] - '0') * 10 + (text[i + 1] - '0'));
                }
            }
            else
            {
                symbols.Add(symbols.Count == 0 ? StartB : inCodeC ? CodeB : -1);
                inCodeC = false;

                // W zestawie B: bierz znaki aż do początku następnego ciągu co najmniej 4 cyfr.
                var end = i;
                do
                {
                    end++;
                }
                while (end < text.Length && DigitRunLength(text, end) < 4);

                for (; i < end; i++)
                {
                    symbols.Add(text[i] - 32);
                }
            }
        }

        symbols.RemoveAll(s => s == -1);
        return ToModules(symbols);
    }

    private static int DigitRunLength(string text, int from)
    {
        var n = 0;
        while (from + n < text.Length && char.IsAsciiDigit(text[from + n]))
        {
            n++;
        }

        return n;
    }

    /// <summary>Dodaje sumę kontrolną i stop, zamienia symbole na moduły (true = kreska).</summary>
    internal static bool[] ToModules(IReadOnlyList<int> symbols)
    {
        var checksum = symbols[0];
        for (var i = 1; i < symbols.Count; i++)
        {
            checksum += i * symbols[i];
        }

        var all = symbols.Append(checksum % 103).Append(Stop);
        var modules = new List<bool>();
        foreach (var symbol in all)
        {
            var pattern = Patterns[symbol];
            for (var e = 0; e < pattern.Length; e++)
            {
                var width = pattern[e] - '0';
                var isBar = e % 2 == 0;
                for (var w = 0; w < width; w++)
                {
                    modules.Add(isBar);
                }
            }
        }

        return modules.ToArray();
    }

    /// <summary>Zapis modułów jako tekst do testów, np. „11010010000…”.</summary>
    internal static string ToBitString(bool[] modules)
    {
        var sb = new StringBuilder(modules.Length);
        foreach (var m in modules)
        {
            sb.Append(m ? '1' : '0');
        }

        return sb.ToString();
    }
}
