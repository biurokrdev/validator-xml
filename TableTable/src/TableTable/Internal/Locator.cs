using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace TableTable.Internal;

/// <summary>Szablon rekordu: elementy do klonowania oraz nadmiarowe elementy sekcji (dodane w Wordzie przy projektowaniu), usuwane razem z nim.</summary>
internal sealed record RecordTemplate(List<OpenXmlElement> Elements, List<OpenXmlElement> Extra);

/// <summary>Cel wiązania: tag formantu (gdy jest) i tabela albo sekcja powtarzana.</summary>
internal sealed record BindingTarget(string? Tag, OpenXmlElement Element);

/// <summary>Wyszukiwanie tabel i szablonu rekordu w dokumencie.</summary>
internal static class Locator
{
    public static IEnumerable<OpenXmlPartRootElement> Roots(WordprocessingDocument document)
    {
        var main = document.MainDocumentPart ?? throw new InvalidOperationException("Dokument nie zawiera MainDocumentPart.");
        if (main.Document != null) yield return main.Document;
        foreach (var header in main.HeaderParts) if (header.Header != null) yield return header.Header;
        foreach (var footer in main.FooterParts) if (footer.Footer != null) yield return footer.Footer;
    }

    /// <summary>
    /// Tabela (lub sekcja powtarzana) według wiązania: <c>tag</c> = formant o tym tagu, a gdy go nie ma – zakładka Word o tej nazwie
    /// (szablony z DocXTableArchitect); <c>bookmark-name</c> = tylko zakładka; bez obu – pierwsza tabela ze znacznikami.
    /// </summary>
    public static OpenXmlElement? Find(WordprocessingDocument document, CustomTableDataBinding binding, PlaceholderScanner scanner)
    {
        var roots = Roots(document).ToList();

        if (!string.IsNullOrWhiteSpace(binding.Tag))
            return ContentControls.Find(roots, binding.Tag).Select(ResolveTarget).FirstOrDefault(t => t != null) ?? FindByBookmark(roots, binding.Tag);

        if (!string.IsNullOrWhiteSpace(binding.BookmarkName))
            return FindByBookmark(roots, binding.BookmarkName);

        return TopLevelTables(roots).FirstOrDefault(t => scanner.ContainsTag(t.InnerText));
    }

    /// <summary>Zakładka wewnątrz tabeli albo bezpośrednio przed nią (w tej samej części dokumentu).</summary>
    private static Table? FindByBookmark(List<OpenXmlPartRootElement> roots, string name)
    {
        var start = roots.SelectMany(r => r.Descendants<BookmarkStart>())
            .FirstOrDefault(b => string.Equals(b.Name?.Value, name, StringComparison.OrdinalIgnoreCase));
        if (start == null) return null;
        var root = start.Ancestors<OpenXmlPartRootElement>().First();
        return start.Ancestors<Table>().FirstOrDefault()
               ?? TopLevelTables([root]).FirstOrDefault(t => t.IsAfter(start));
    }

    public static string Describe(CustomTableDataBinding binding) =>
        !string.IsNullOrWhiteSpace(binding.Tag) ? $"Nie znaleziono formantu o tagu '{binding.Tag}' (Deweloper → Właściwości → Tag) ani zakładki o tej nazwie w tabeli lub przed nią."
        : !string.IsNullOrWhiteSpace(binding.BookmarkName) ? $"Nie znaleziono tabeli dla zakładki '{binding.BookmarkName}' (zakładka musi leżeć w tabeli albo przed nią)."
        : "Żadna tabela w dokumencie nie zawiera znaczników.";

    /// <summary>Wszystkie cele w dokumencie: formanty z tagiem wskazujące tabelę/sekcję (zewnętrzne przed wewnętrznymi), potem tabele bez tagu ze znacznikami.</summary>
    public static List<BindingTarget> Discover(WordprocessingDocument document, PlaceholderScanner scanner)
    {
        var found = new List<BindingTarget>();
        bool Overlaps(OpenXmlElement e) => found.Any(t => ReferenceEquals(t.Element, e)
                                                          || e.Ancestors().Any(a => ReferenceEquals(a, t.Element))
                                                          || t.Element.Ancestors().Any(a => ReferenceEquals(a, e)));

        foreach (var root in Roots(document))
        {
            foreach (var sdt in root.Descendants<SdtElement>())
            {
                var tag = ContentControls.TagOf(sdt);
                if (string.IsNullOrWhiteSpace(tag) || Overlaps(sdt)) continue;
                var target = ResolveTarget(sdt);
                if (target != null && !Overlaps(target)) found.Add(new BindingTarget(tag, target));
            }

            foreach (var table in TopLevelTables([root]))
                if (!Overlaps(table) && scanner.ContainsTag(table.InnerText)) found.Add(new BindingTarget(null, table));
        }

        return found;
    }

    /// <summary>Formant → cel: sekcja powtarzana (on sam lub w środku), tabela w środku, albo tabela-przodek (formant wiersza/komórki).</summary>
    public static OpenXmlElement? ResolveTarget(SdtElement sdt)
    {
        if (ContentControls.IsRepeatingSection(sdt)) return sdt;
        return sdt.Descendants<SdtElement>().FirstOrDefault(ContentControls.IsRepeatingSection)
               ?? (OpenXmlElement?)sdt.Descendants<Table>().FirstOrDefault(t => !t.Ancestors<Table>().Any(a => a.IsAfter(sdt)))
               ?? (sdt is SdtRow or SdtCell ? sdt.Ancestors<Table>().FirstOrDefault() : null);
    }

    /// <summary>
    /// Szablon rekordu: pierwszy element sekcji powtarzanej, a w zwykłej tabeli blok wierszy od pierwszego do ostatniego ze znacznikiem
    /// (lub <paramref name="rowCount"/> wierszy). <see langword="null"/> z powodem, gdy szablonu nie ma.
    /// </summary>
    public static RecordTemplate? FindTemplate(OpenXmlElement target, PlaceholderScanner scanner, int? rowCount, out string reason)
    {
        reason = string.Empty;
        var section = ContentControls.FindRepeatingSection(target);
        if (section != null)
        {
            var items = ContentControls.RepeatingSectionItems(section);
            if (items.Count == 0) { reason = "Sekcja powtarzana jest pusta."; return null; }
            return new RecordTemplate([items[0]], items.Skip(1).ToList());
        }

        if (target is not Table table) { reason = "Wskazany element nie jest tabelą ani sekcją powtarzaną."; return null; }

        // Wiersze tabeli, także owinięte formantem wiersza (w:sdt z w:tr w środku – np. lista wierszy).
        var rows = table.ChildElements.Where(c => c is TableRow or SdtRow).ToList();
        var first = rows.FindIndex(r => scanner.ContainsTag(r.InnerText));
        if (first < 0) { reason = "Tabela nie zawiera żadnego znacznika."; return null; }
        var last = rowCount is > 0 ? Math.Min(rows.Count - 1, first + rowCount.Value - 1) : rows.FindLastIndex(r => scanner.ContainsTag(r.InnerText));
        return new RecordTemplate(rows.GetRange(first, last - first + 1), []);
    }

    private static IEnumerable<Table> TopLevelTables(IEnumerable<OpenXmlElement> roots) =>
        roots.SelectMany(r => r.Descendants<Table>()).Where(t => !t.Ancestors<Table>().Any());
}
