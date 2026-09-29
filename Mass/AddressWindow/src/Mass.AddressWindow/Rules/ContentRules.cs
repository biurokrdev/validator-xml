using System.Globalization;
using System.Text.RegularExpressions;

namespace Mass.AddressWindow.Rules;

internal static partial class ContentRules
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    [GeneratedRegex(@"^\d{2}-\d{3}\s+\p{L}", RegexOptions.CultureInvariant)]
    private static partial Regex PostalCodeLine();

    [GeneratedRegex(@"(?<!\d)(\d{2}\s*[-–]\s*\d{3}|\d{5})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PostalCodeLike();

    [GeneratedRegex(@"^\d{2}-\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPostalCode();

    [GeneratedRegex(@"^[\p{Lu}][\p{Lu}\s\-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryLine();

    [GeneratedRegex(@"«[^»]*»|MERGEFIELD|\{\{[^}]*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex MergeField();

    [GeneratedRegex(@"[^\p{L}\p{M}\p{N}\s.,\-–/'""„”&()«»:;#{}]", RegexOptions.CultureInvariant)]
    private static partial Regex Unusual();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static readonly HashSet<string> PolandNames = new(StringComparer.OrdinalIgnoreCase) { "POLSKA", "POLAND" };

    public static List<(string Text, AddressLine Source)> NormalizeLines(
        IAddressCandidate block, WindowRole role, List<ValidationIssue> issues)
    {
        var lines = block.Lines
            .Select(l => (Text: Whitespace().Replace(l.Text, " ").Trim(), Source: l))
            .ToList();

        var first = lines.FindIndex(l => l.Text.Length > 0);
        var last = lines.FindLastIndex(l => l.Text.Length > 0);
        if (first < 0)
        {
            return [];
        }

        lines = lines.GetRange(first, last - first + 1);
        if (lines.Any(l => l.Text.Length == 0))
        {
            issues.Add(new ValidationIssue(
                IssueCodes.EmptyLineInside, IssueSeverity.Warning,
                "Adres zawiera pusty wiersz. Wiersze adresu powinny następować bezpośrednio po sobie.",
                role));
        }

        return lines.Where(l => l.Text.Length > 0).ToList();
    }

    public static void Check(
        IAddressCandidate block,
        IReadOnlyList<(string Text, AddressLine Source)> lines,
        AddressContentRules rules,
        WindowRole role,
        List<ValidationIssue> issues)
    {
        if (lines.Count == 0)
        {
            issues.Add(new ValidationIssue(IssueCodes.AddressEmpty, IssueSeverity.Error, "Blok adresowy jest pusty.", role));
            return;
        }

        var hasMergeFields = lines.Any(l => MergeField().IsMatch(l.Text));
        if (hasMergeFields)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.MergeFieldsPresent, IssueSeverity.Warning,
                "Adres zawiera pola korespondencji seryjnej. Długość wierszy i kod pocztowy sprawdzono tylko dla szablonu, "
                + "zweryfikuj też dokumenty po scaleniu.",
                role));
        }

        CheckLineCount(lines.Count, rules, role, issues);
        CheckLineLengths(lines, rules, role, issues);
        if (rules.RequirePostalCodeLine)
        {
            CheckPostalCode(lines.Select(l => l.Text).ToList(), rules, hasMergeFields, role, issues);
        }

        CheckTypography(block, lines, rules, role, issues);

        var unusual = lines
            .SelectMany(l => Unusual().Matches(MergeField().Replace(l.Text, "")).Select(m => m.Value))
            .Distinct()
            .ToList();
        if (unusual.Count > 0)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.UnusualCharacters, IssueSeverity.Warning,
                $"Adres zawiera nietypowe znaki: {string.Join(" ", unusual.Select(u => $"„{u}”"))}. Mogą utrudnić odczyt maszynowy.",
                role));
        }
    }

    private static void CheckLineCount(int count, AddressContentRules rules, WindowRole role, List<ValidationIssue> issues)
    {
        if (count < rules.MinLines)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.TooFewLines, IssueSeverity.Error,
                $"Adres ma {count} {Lines(count)}, wymagane są co najmniej {rules.MinLines}.",
                role));
        }
        else if (count > rules.MaxLines)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.TooManyLines, IssueSeverity.Error,
                $"Adres ma {count} {Lines(count)}, dopuszczalne jest najwyżej {rules.MaxLines}.",
                role));
        }
    }

    private static void CheckLineLengths(
        IReadOnlyList<(string Text, AddressLine Source)> lines, AddressContentRules rules, WindowRole role, List<ValidationIssue> issues)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var (text, source) = lines[i];
            if (text.Length > rules.MaxCharactersPerLine)
            {
                issues.Add(new ValidationIssue(
                    IssueCodes.LineTooLong, IssueSeverity.Error,
                    $"Wiersz {i + 1} ma {text.Length} znaków, dopuszczalne jest {rules.MaxCharactersPerLine}: „{text}”.",
                    role));
            }

            if (source.VisualLines > 1)
            {
                issues.Add(new ValidationIssue(
                    IssueCodes.LineWraps, IssueSeverity.Warning,
                    $"Wiersz {i + 1} prawdopodobnie nie zmieści się w szerokości pola i zostanie zawinięty: „{text}”.",
                    role));
            }
        }
    }

    private static void CheckPostalCode(
        IReadOnlyList<string> lines, AddressContentRules rules, bool hasMergeFields, WindowRole role, List<ValidationIssue> issues)
    {
        var last = lines[^1];
        var severity = hasMergeFields ? IssueSeverity.Warning : IssueSeverity.Error;

        if (PostalCodeLine().IsMatch(last))
        {
            return;
        }

        if (CountryLine().IsMatch(last) && lines.Count >= 2)
        {
            var isPoland = PolandNames.Contains(last);
            if (!isPoland && rules.AllowForeignAddress)
            {
                return;
            }

            if (isPoland && PostalCodeLine().IsMatch(lines[^2]))
            {
                return;
            }
        }

        var postalIndex = lines.ToList().FindIndex(l => PostalCodeLine().IsMatch(l));
        if (postalIndex >= 0)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.TextAfterPostalCodeLine, severity,
                $"Wiersz z kodem pocztowym („{lines[postalIndex]}”) musi być ostatnim wierszem adresu "
                + "(w adresie zagranicznym przedostatnim, przed nazwą kraju wielkimi literami).",
                role));
            return;
        }

        var match = lines.Select(l => PostalCodeLike().Match(l)).FirstOrDefault(m => m.Success);
        var hint = match switch
        {
            null => "",
            _ when ValidPostalCode().IsMatch(match.Value) =>
                $" Kod „{match.Value}” jest w adresie, ale nie na początku ostatniego wiersza.",
            _ => $" Znaleziono „{match.Value}”; wymagany format to NN-NNN, potem spacja i miejscowość.",
        };
        issues.Add(new ValidationIssue(
            IssueCodes.PostalCodeLineMissing, severity,
            "Ostatni wiersz adresu musi zawierać kod pocztowy w formacie NN-NNN i miejscowość (np. „00-940 Warszawa”)"
            + (rules.AllowForeignAddress ? ", a w adresie zagranicznym nazwę kraju wielkimi literami." : ".")
            + hint,
            role));
    }

    private static void CheckTypography(
        IAddressCandidate block,
        IReadOnlyList<(string Text, AddressLine Source)> lines,
        AddressContentRules rules,
        WindowRole role,
        List<ValidationIssue> issues)
    {
        var sizes = lines.SelectMany(l => l.Source.FontSizes).ToList();
        if (sizes.Count > 0)
        {
            var min = sizes.Min();
            var max = sizes.Max();
            if (min < rules.MinFontSizePt)
            {
                issues.Add(new ValidationIssue(
                    IssueCodes.FontTooSmall, IssueSeverity.Error,
                    $"Czcionka adresu ma {Pt(min)}, minimum to {Pt(rules.MinFontSizePt)}.",
                    role));
            }

            if (max > rules.MaxFontSizePt)
            {
                issues.Add(new ValidationIssue(
                    IssueCodes.FontTooLarge, IssueSeverity.Warning,
                    $"Czcionka adresu ma {Pt(max)}, zalecane maksimum to {Pt(rules.MaxFontSizePt)}.",
                    role));
            }
        }

        if (rules.WarnOnItalicOrUnderline && lines.Any(l => l.Source.Italic || l.Source.Underline))
        {
            issues.Add(new ValidationIssue(
                IssueCodes.DecoratedText, IssueSeverity.Warning,
                "Adres zawiera kursywę lub podkreślenie, co utrudnia odczyt maszynowy. Użyj zwykłego kroju.",
                role));
        }

        if (rules.WarnOnNonLeftAlignment && block.NonLeftAligned)
        {
            issues.Add(new ValidationIssue(
                IssueCodes.NonLeftAlignment, IssueSeverity.Warning,
                "Adres powinien być wyrównany do lewej.",
                role));
        }
    }

    private static string Pt(double value) => value.ToString("0.#", Pl) + " pt";

    private static string Lines(int count) => count switch
    {
        1 => "wiersz",
        _ when count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 => "wiersze",
        _ => "wierszy",
    };
}
