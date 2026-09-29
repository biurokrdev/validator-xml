using Mass.RLabel.Barcode;

namespace Mass.RLabel.Tests;

public class Code128Tests
{
    [Fact]
    public void PatternTable_Has107SymbolsOf11ModulesAndStopOf13()
    {
        Assert.Equal(107, Code128.Patterns.Length);
        for (var i = 0; i < 106; i++)
        {
            Assert.Equal(6, Code128.Patterns[i].Length);
            Assert.Equal(11, Code128.Patterns[i].Sum(c => c - '0'));
        }

        Assert.Equal("2331112", Code128.Patterns[106]);
    }

    [Fact]
    public void PatternTable_EveryBarSumIsEven()
    {
        // Własność Code 128 (parzystość kresek), wykrywa literówki w tabeli.
        for (var i = 0; i < Code128.Patterns.Length; i++)
        {
            var bars = Code128.Patterns[i].Where((_, index) => index % 2 == 0).Sum(c => c - '0');
            Assert.True(bars % 2 == 0, $"Symbol {i}: {Code128.Patterns[i]}");
        }
    }

    [Fact]
    public void PatternTable_HasNoDuplicates() =>
        Assert.Equal(Code128.Patterns.Length, Code128.Patterns.Distinct().Count());

    [Fact]
    public void Gs1_Sscc_HasStartC_Fnc1_TenPairs_Check_Stop()
    {
        var modules = Code128.EncodeGs1("00759007731512000621");

        // 1 start + 1 FNC1 + 10 par + 1 suma = 13 symboli × 11 + stop 13
        Assert.Equal(13 * 11 + 13, modules.Length);
        var bits = Code128.ToBitString(modules);
        Assert.StartsWith(Expand("211232") + Expand("411131"), bits); // Start C (105), FNC1 (102)
        Assert.EndsWith(Expand("2331112"), bits);
        Assert.True(modules[0]);
        Assert.True(modules[^1]);
    }

    [Fact]
    public void Gs1_OddDigitCount_IsRejected() =>
        Assert.Throws<ArgumentException>(() => Code128.EncodeGs1("123"));

    [Fact]
    public void S10_UsesCodeCForDigitRun()
    {
        // RR (B) + 8 cyfr (C) + "9PL" (B): StartB, R, R, CodeC, 4 pary, CodeB, 9, P, L, suma = 13 symboli
        var modules = Code128.Encode("RR473124829PL");

        Assert.Equal(13 * 11 + 13, modules.Length);
        Assert.StartsWith(Expand("211214"), Code128.ToBitString(modules)); // Start B
    }

    [Fact]
    public void Checksum_IsComputedAsWeightedSumMod103()
    {
        // Znany przykład "PJJ123C" w zestawie B: Start B (104) + P 48·1 + J 42·2 + J 42·3 + „1” 17·4 + „2” 18·5 + „3” 19·6 + C 35·7 = 879; 879 mod 103 = 55.
        var modules = Code128.Encode("PJJ123C");
        var symbols = Decode(modules);

        Assert.Equal([104, 48, 42, 42, 17, 18, 19, 35, 55, 106], symbols);
    }

    [Fact]
    public void Encoding_RoundTrips_ThroughSymbolDecoder()
    {
        var modules = Code128.Encode("RR473124829PL");
        var symbols = Decode(modules);

        Assert.Equal([104, 50, 50, 99, 47, 31, 24, 82, 100, 25, 48, 44], symbols[..12]);
        Assert.Equal(106, symbols[^1]);
        var expectedCheck = (symbols[0] + symbols.Skip(1).Take(symbols.Count - 3).Select((s, i) => (i + 1) * s).Sum()) % 103;
        Assert.Equal(expectedCheck, symbols[^2]);
    }

    private static string Expand(string pattern)
    {
        var sb = new System.Text.StringBuilder();
        for (var e = 0; e < pattern.Length; e++)
        {
            sb.Append(e % 2 == 0 ? '1' : '0', pattern[e] - '0');
        }

        return sb.ToString();
    }

    /// <summary>Odczyt symboli z modułów po szerokościach elementów (testowy dekoder).</summary>
    private static List<int> Decode(bool[] modules)
    {
        var widths = new List<int>();
        for (var i = 0; i < modules.Length;)
        {
            var run = 1;
            while (i + run < modules.Length && modules[i + run] == modules[i])
            {
                run++;
            }

            widths.Add(run);
            i += run;
        }

        var symbols = new List<int>();
        var pos = 0;
        while (pos < widths.Count)
        {
            var take = pos + 7 == widths.Count ? 7 : 6;
            var pattern = string.Concat(widths.Skip(pos).Take(take));
            symbols.Add(Array.IndexOf(Code128.Patterns, pattern));
            pos += take;
        }

        return symbols;
    }
}
