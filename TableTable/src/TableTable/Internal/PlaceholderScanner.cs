using System.Text.RegularExpressions;

namespace TableTable.Internal;

/// <summary>Rozpoznawanie znaczników <c>&lt;%nazwa%&gt;</c> w tekście (ograniczniki konfigurowalne).</summary>
internal sealed class PlaceholderScanner
{
    private readonly Regex _regex;

    public PlaceholderScanner(string tagOpen, string tagClose)
    {
        if (string.IsNullOrEmpty(tagOpen) || string.IsNullOrEmpty(tagClose)) throw new ArgumentException("Ograniczniki znacznika nie mogą być puste.");
        _regex = new Regex(Regex.Escape(tagOpen) + @"\s*(.+?)\s*" + Regex.Escape(tagClose), RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    public bool ContainsTag(string text) => _regex.IsMatch(text);

    /// <summary>Unikalne znaczniki (pełny tekst i nazwa) w kolejności wystąpienia.</summary>
    public IEnumerable<(string Tag, string Name)> Find(string text) =>
        _regex.Matches(text).Select(m => (m.Value, m.Groups[1].Value.Trim())).DistinctBy(m => m.Value);
}
