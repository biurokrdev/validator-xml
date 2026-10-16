using System.Text;

namespace Mass.AddressWindow.Rules.Labels;

/// <summary>Odczytany kod Code 128 / GS1-128.</summary>
/// <param name="Text">Zakodowany tekst bez znaków sterujących (FNC1 jest pominięty).</param>
/// <param name="IsGs1">Kod zaczyna się od FNC1, czyli jest kodem GS1-128.</param>
/// <param name="ModulePx">Zmierzona szerokość najwęższej kreski w pikselach obrazu.</param>
/// <param name="Rows">Ile wierszy obrazu dało ten sam odczyt.</param>
internal sealed record Code128Reading(string Text, bool IsGs1, double ModulePx, int Rows);

/// <summary>
/// Dekoder Code 128 z obrazu w skali szarości, bez zależności zewnętrznych. Każdy wiersz obrazu jest czytany
/// dwiema metodami: najpierw z szerokości kresek mierzonych między pikselami (dokładna przy ostrym obrazie),
/// a gdy to zawiedzie, z próbkowania zaczernienia modułami (odporna na rozmycie, które zostaje po zmniejszeniu
/// obrazu przez Worda przy zapisie do PDF, gdzie najwęższa kreska ma niecałe 2 piksele).
/// </summary>
internal static class Code128Decoder
{
    private const int StartA = 103;
    private const int StartB = 104;
    private const int StartC = 105;
    private const int Stop = 106;
    private const int Fnc1 = 102;
    private const int Shift = 98;
    private const int CodeC = 99;
    private const int CodeB = 100;
    private const int CodeA = 101;

    // Jak w Mass.RLabel.Barcode.Code128.Patterns: kolejne szerokości kreska/przerwa, każdy symbol ma 11 modułów, stop 13.
    private static readonly string[] Patterns =
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

    private const double MaxAverageVariance = 0.25;
    private const double MaxIndividualVariance = 0.7;

    /// <summary>
    /// Próbuje odczytać kod z kilkudziesięciu wierszy obrazu i zwraca odczyt, który powtórzył się najczęściej.
    /// Najpierw szybką metodą z szerokości kresek; wolniejsze próbkowanie modułami dopiero wtedy, gdy żaden
    /// wiersz nie dał się odczytać szybko (obraz rozmyty po zmniejszeniu).
    /// </summary>
    public static Code128Reading? Decode(GrayImage image)
    {
        return Vote(image, 48, DecodeRowFromRuns) ?? Vote(image, 16, DecodeRowBySampling);
    }

    private static Code128Reading? Vote(GrayImage image, int rowCount, Func<GrayImage, int, bool, Code128Reading?> decodeRow)
    {
        var votes = new Dictionary<(string Text, bool Gs1), (int Rows, double ModuleSum)>();
        rowCount = Math.Min(image.Height, rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            var y = (int)((i + 0.5) * image.Height / rowCount);
            foreach (var reversed in new[] { false, true })
            {
                var reading = decodeRow(image, y, reversed);
                if (reading is null)
                {
                    continue;
                }

                var key = (reading.Text, reading.IsGs1);
                votes[key] = votes.TryGetValue(key, out var v)
                    ? (v.Rows + 1, v.ModuleSum + reading.ModulePx)
                    : (1, reading.ModulePx);
                break;
            }
        }

        if (votes.Count == 0)
        {
            return null;
        }

        var best = votes.MaxBy(v => v.Value.Rows);
        return new Code128Reading(best.Key.Text, best.Key.Gs1, best.Value.ModuleSum / best.Value.Rows, best.Value.Rows);
    }

    private static Code128Reading? DecodeRowFromRuns(GrayImage image, int y, bool reversed)
    {
        var runs = Runs(image.Row(y), reversed);
        return runs.Count < 20 ? null : DecodeFromRuns(runs);
    }

    private static Code128Reading? DecodeRowBySampling(GrayImage image, int y, bool reversed)
    {
        var row = image.Row(y);
        var runs = Runs(row, reversed);
        return runs.Count < 20 ? null : DecodeBySampling(row, reversed, runs);
    }

    private static Code128Reading? DecodeFromRuns(List<Run> runs)
    {
        // Szukamy symbolu startu: kreska, po której 6 kolejnych szerokości pasuje do Start A/B/C,
        // poprzedzona cichą strefą (przerwa co najmniej 3 moduły).
        for (var i = 1; i + 6 < runs.Count; i += 2)
        {
            if (!runs[i].IsBar)
            {
                continue;
            }

            var start = BestMatch(runs, i, 6, StartA, StartC);
            if (start.Symbol < 0)
            {
                continue;
            }

            var module = Sum(runs, i, 6) / 11.0;
            if (runs[i - 1].Width < 3 * module)
            {
                continue;
            }

            var reading = DecodeFrom(runs, i, start.Symbol, module);
            if (reading is not null)
            {
                return reading;
            }
        }

        return null;
    }

    private static Code128Reading? DecodeFrom(List<Run> runs, int startIndex, int startSymbol, double module)
    {
        var symbols = new List<int> { startSymbol };
        var i = startIndex + 6;
        var modulesTotal = 11;
        while (true)
        {
            if (i + 7 > runs.Count)
            {
                return null;
            }

            var stop = Match(runs, i, 7, Stop);
            var sym = BestMatch(runs, i, 6, 0, StartC);
            if (stop <= MaxAverageVariance && (sym.Symbol < 0 || stop <= sym.Variance))
            {
                modulesTotal += 13;
                var width = Sum(runs, startIndex, i + 7 - startIndex);
                return Interpret(symbols, width / modulesTotal);
            }

            if (sym.Symbol < 0)
            {
                return null;
            }

            symbols.Add(sym.Symbol);
            modulesTotal += 11;
            i += 6;
            if (symbols.Count > 80)
            {
                return null;
            }
        }
    }

    private static Code128Reading? Interpret(List<int> symbols, double modulePx)
    {
        if (symbols.Count < 3)
        {
            return null;
        }

        var checksum = symbols[0];
        for (var k = 1; k < symbols.Count - 1; k++)
        {
            checksum += k * symbols[k];
        }

        if (checksum % 103 != symbols[^1])
        {
            return null;
        }

        var text = new StringBuilder();
        var codeSet = symbols[0] switch { StartA => 'A', StartB => 'B', _ => 'C' };
        var isGs1 = symbols.Count > 2 && symbols[1] == Fnc1;
        var shift = false;
        for (var k = 1; k < symbols.Count - 1; k++)
        {
            var s = symbols[k];
            var set = shift ? (codeSet == 'A' ? 'B' : 'A') : codeSet;
            shift = false;
            if (set == 'C')
            {
                switch (s)
                {
                    case < 100:
                        text.Append((char)('0' + s / 10)).Append((char)('0' + s % 10));
                        break;
                    case CodeB:
                        codeSet = 'B';
                        break;
                    case CodeA:
                        codeSet = 'A';
                        break;
                    case Fnc1:
                        break;
                    default:
                        return null;
                }

                continue;
            }

            switch (s)
            {
                case < 64:
                    text.Append((char)(s + 32));
                    break;
                case < 96:
                    text.Append(set == 'A' ? (char)(s - 64) : (char)(s + 32));
                    break;
                case Shift:
                    shift = true;
                    break;
                case CodeC:
                    codeSet = 'C';
                    break;
                case CodeB:
                    codeSet = 'B';
                    break;
                case CodeA:
                    codeSet = 'A';
                    break;
                case Fnc1:
                    break;
                default:
                    return null; // FNC2–4 i inne znaki sterujące nie występują w numerach nadawczych
            }
        }

        return text.Length == 0 ? null : new Code128Reading(text.ToString(), isGs1, modulePx, 1);
    }

    // ------------------------------------------------------------------ metoda 2: próbkowanie modułami

    private const double MaxStartScore = 0.3;

    /// <summary>
    /// Zamiast mierzyć krawędzie, liczy średnie zaczernienie każdego modułu (kreska = 1, przerwa = 0) i dobiera
    /// wzór o najmniejszym odchyleniu. Rozmycie rozkłada zaczernienie między sąsiednie moduły symetrycznie,
    /// więc właściwy wzór nadal jest najbliższy. Kandydatów na start daje zgrubny pomiar kresek.
    /// </summary>
    private static Code128Reading? DecodeBySampling(ReadOnlySpan<byte> row, bool reversed, List<Run> runs)
    {
        var ink = Ink(row, reversed);
        if (ink is null)
        {
            return null;
        }

        var prefix = new double[ink.Length + 1];
        for (var x = 0; x < ink.Length; x++)
        {
            prefix[x + 1] = prefix[x] + ink[x];
        }

        var position = 0.0;
        for (var i = 0; i < runs.Count; i++)
        {
            var runStart = position;
            position += runs[i].Width;
            if (!runs[i].IsBar || i == 0 || i + 6 >= runs.Count)
            {
                continue;
            }

            var module = Sum(runs, i, 6) / 11.0;
            if (module < 0.9 || runs[i - 1].Width < 3 * module || !RoughlyStart(runs, i))
            {
                continue;
            }

            var (x0, m, score, start) = RefineStart(prefix, runStart, module);
            if (start < 0 || score > MaxStartScore)
            {
                continue;
            }

            var reading = DecodeSymbols(prefix, x0, m, start);
            if (reading is not null)
            {
                return reading;
            }
        }

        return null;
    }

    /// <summary>Zgrubny filtr kandydatów: szerokości kresek z grubsza pasują do któregoś symbolu startu (luźny próg).</summary>
    private static bool RoughlyStart(List<Run> runs, int from)
    {
        for (var s = StartA; s <= StartC; s++)
        {
            if (Match(runs, from, 6, s, maxIndividual: 1.0) <= 0.6)
            {
                return true;
            }
        }

        return false;
    }

    private static (double X, double Module, double Score, int Start) RefineStart(double[] prefix, double x0, double module)
    {
        var best = (X: x0, Module: module, Score: double.PositiveInfinity, Start: -1);
        for (var dx = -1.0; dx <= 1.0; dx += 0.1)
        {
            for (var dm = -0.06; dm <= 0.06; dm += 0.02)
            {
                var m = module * (1 + dm);
                var x = x0 + dx;
                for (var s = StartA; s <= StartC; s++)
                {
                    var score = SampleScore(prefix, x, m, Patterns[s]);
                    if (score < best.Score)
                    {
                        best = (x, m, score, s);
                    }
                }
            }
        }

        return best;
    }

    private static Code128Reading? DecodeSymbols(double[] prefix, double x, double module, int start)
    {
        var symbols = new List<int> { start };
        var m = module;
        x += 11 * m;
        var modulesTotal = 11;
        var startX = x - 11 * m;
        while (symbols.Count <= 80)
        {
            var best = (Symbol: -1, Score: double.PositiveInfinity, X: x, Module: m);
            foreach (var dm in new[] { -0.03, 0.0, 0.03 })
            {
                var m2 = m * (1 + dm);
                foreach (var dx in new[] { -0.3, -0.15, 0.0, 0.15, 0.3 })
                {
                    var x2 = x + dx * m2;
                    if (x2 < 0 || x2 + 11 * m2 > prefix.Length - 1)
                    {
                        continue;
                    }

                    for (var s = 0; s <= Stop; s++)
                    {
                        if (s is StartA or StartB or StartC)
                        {
                            continue;
                        }

                        if (s == Stop && x2 + 13 * m2 > prefix.Length - 1)
                        {
                            continue;
                        }

                        var score = SampleScore(prefix, x2, m2, Patterns[s]);
                        if (score < best.Score)
                        {
                            best = (s, score, x2, m2);
                        }
                    }
                }
            }

            if (best.Symbol < 0 || best.Score > 0.45)
            {
                return null;
            }

            if (best.Symbol == Stop)
            {
                modulesTotal += 13;
                var end = best.X + 13 * best.Module;
                return Interpret(symbols, (end - startX) / modulesTotal);
            }

            symbols.Add(best.Symbol);
            modulesTotal += 11;
            m = best.Module;
            x = best.X + 11 * m;
        }

        return null;
    }

    /// <summary>Średnie odchylenie zaczernienia modułów od wzoru (0 = idealnie).</summary>
    private static double SampleScore(double[] prefix, double x, double module, string pattern)
    {
        var total = 0.0;
        var count = 0;
        var isBar = true;
        var position = x;
        foreach (var c in pattern)
        {
            var width = c - '0';
            for (var k = 0; k < width; k++)
            {
                var mean = Integral(prefix, position, position + module) / module;
                total += Math.Abs(mean - (isBar ? 1 : 0));
                count++;
                position += module;
            }

            isBar = !isBar;
        }

        return total / count;
    }

    private static double Integral(double[] prefix, double a, double b)
    {
        static double At(double[] prefix, double t)
        {
            if (t <= 0)
            {
                return 0;
            }

            if (t >= prefix.Length - 1)
            {
                return prefix[^1];
            }

            var i = (int)t;
            return prefix[i] + (prefix[i + 1] - prefix[i]) * (t - i);
        }

        return At(prefix, b) - At(prefix, a);
    }

    /// <summary>Zaczernienie 0–1 każdego piksela wiersza (1 = czarny), znormalizowane do rozpiętości wiersza.</summary>
    private static double[]? Ink(ReadOnlySpan<byte> row, bool reversed)
    {
        var sorted = row.ToArray();
        Array.Sort(sorted);
        double low = sorted[sorted.Length / 20];
        double high = sorted[sorted.Length * 19 / 20];
        if (high - low < 48)
        {
            return null;
        }

        var ink = new double[row.Length];
        for (var x = 0; x < row.Length; x++)
        {
            var value = (high - row[x]) / (high - low);
            ink[reversed ? row.Length - 1 - x : x] = Math.Clamp(value, 0, 1);
        }

        return ink;
    }

    private readonly record struct Run(double Width, bool IsBar);

    /// <summary>Szerokości kolejnych kresek i przerw z krawędziami mierzonymi między pikselami (interpolacja liniowa).</summary>
    private static List<Run> Runs(ReadOnlySpan<byte> row, bool reversed)
    {
        var sorted = row.ToArray();
        Array.Sort(sorted);
        var low = sorted[sorted.Length / 10];
        var high = sorted[sorted.Length * 9 / 10];
        if (high - low < 48)
        {
            return [];
        }

        var threshold = (low + high) / 2.0;
        var runs = new List<Run>();
        var isBar = row[0] < threshold;
        var lastEdge = 0.0;
        for (var x = 0; x + 1 < row.Length; x++)
        {
            var a = row[x] - threshold;
            var b = row[x + 1] - threshold;
            if (a == 0 || a < 0 == b < 0)
            {
                continue;
            }

            var edge = x + a / (a - b);
            runs.Add(new Run(edge - lastEdge, isBar));
            lastEdge = edge;
            isBar = !isBar;
        }

        runs.Add(new Run(row.Length - lastEdge, isBar));
        if (reversed)
        {
            runs.Reverse();
        }

        return runs;
    }

    private static double Sum(List<Run> runs, int from, int count)
    {
        var sum = 0.0;
        for (var k = 0; k < count; k++)
        {
            sum += runs[from + k].Width;
        }

        return sum;
    }

    private static (int Symbol, double Variance) BestMatch(List<Run> runs, int from, int count, int first, int last)
    {
        var bestSymbol = -1;
        var bestVariance = MaxAverageVariance;
        for (var s = first; s <= last; s++)
        {
            var variance = Match(runs, from, count, s);
            if (variance < bestVariance)
            {
                bestVariance = variance;
                bestSymbol = s;
            }
        }

        return (bestSymbol, bestVariance);
    }

    /// <summary>Średnie odchylenie zmierzonych szerokości od wzoru, w modułach; jak w ZXing. Powyżej progu zwraca nieskończoność.</summary>
    private static double Match(List<Run> runs, int from, int count, int symbol, double maxIndividual = MaxIndividualVariance)
    {
        var pattern = Patterns[symbol];
        if (pattern.Length != count || from + count > runs.Count)
        {
            return double.PositiveInfinity;
        }

        var total = Sum(runs, from, count);
        var modules = 0;
        foreach (var c in pattern)
        {
            modules += c - '0';
        }

        var unit = total / modules;
        if (unit < 0.6)
        {
            return double.PositiveInfinity;
        }

        var totalVariance = 0.0;
        for (var k = 0; k < count; k++)
        {
            var expected = (pattern[k] - '0') * unit;
            var variance = Math.Abs(runs[from + k].Width - expected) / unit;
            if (variance > maxIndividual)
            {
                return double.PositiveInfinity;
            }

            totalVariance += variance;
        }

        return totalVariance / count;
    }
}
