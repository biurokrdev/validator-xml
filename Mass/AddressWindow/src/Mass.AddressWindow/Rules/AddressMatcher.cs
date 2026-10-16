using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Mass.AddressWindow.Pdf.Text;

namespace Mass.AddressWindow.Rules;

/// <summary>
/// Rozpoznaje, czy blok tekstu przypomina adres, i porównuje go z oczekiwanymi danymi odbiorcy.
/// Porównanie jest odporne na wielkość liter, znaki diakrytyczne, interpunkcję i odstępy, bo PDF i DOCX
/// zapisują ten sam adres z drobnymi różnicami składu.
/// </summary>
internal static partial class AddressMatcher
{
    [GeneratedRegex(@"(?<!\d)(\d{2}\s*[-–]\s*\d{3}|\d{5})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PostalCodeLike();

    [GeneratedRegex(@"^[\p{Lu}][\p{Lu}\s\-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryLine();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotAlphanumeric();

    private const int MaxAddressLikeLines = 8;
    private const int MaxAddressLikeLineLength = 60;

    /// <summary>
    /// Blok ma budowę adresu: 2–8 krótkich wierszy z kodem pocztowym albo nazwą kraju, albo najwyżej 6 krótkich wierszy
    /// (adres z brakującym kodem). Długie wiersze to treść pisma, nie adres.
    /// </summary>
    public static bool LooksLikeAddress(TextBlock block)
    {
        var lines = NonEmptyLines(block);
        if (lines.Count is < 2 or > MaxAddressLikeLines || lines.Any(l => l.Length > MaxAddressLikeLineLength))
        {
            return false;
        }

        return lines.Count <= 6 || lines.Any(l => PostalCodeLike().IsMatch(l) || CountryLine().IsMatch(l));
    }

    /// <summary>Ile oczekiwanych wierszy odbiorcy występuje w bloku (po normalizacji).</summary>
    public static int MatchedLines(TextBlock block, IReadOnlyList<string> expected)
    {
        var present = NonEmptyLines(block).Select(Normalize).ToHashSet(StringComparer.Ordinal);
        return expected.Select(Normalize).Count(e => e.Length > 0 && present.Contains(e));
    }

    public static IReadOnlyList<string> MissingLines(TextBlock block, IReadOnlyList<string> expected)
    {
        var present = NonEmptyLines(block).Select(Normalize).ToHashSet(StringComparer.Ordinal);
        return expected.Where(e => Normalize(e).Length > 0 && !present.Contains(Normalize(e))).ToList();
    }

    public static List<string> NonEmptyLines(TextBlock block) =>
        block.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();

    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c == 'ł' ? 'l' : c == 'Ł' ? 'L' : c);
            }
        }

        return NotAlphanumeric().Replace(builder.ToString(), " ").Trim().ToLowerInvariant();
    }

    public static string Snippet(TextBlock block)
    {
        var text = string.Join(" / ", NonEmptyLines(block));
        return text.Length <= 60 ? text : text[..59] + "…";
    }
}
