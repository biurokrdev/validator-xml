using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Office2013.Word;
using DocumentFormat.OpenXml.Wordprocessing;

namespace TableTable.Internal;

/// <summary>Formanty zawartości Word (<c>w:sdt</c>): wyszukiwanie po tagu, usuwanie, rozpakowywanie, sekcje powtarzane.</summary>
internal static class ContentControls
{
    public static Regex TagRegex(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool Matches(string? name, string pattern) => name != null && TagRegex(pattern).IsMatch(name);

    /// <summary>Formanty o pasującym tagu lub tytule, w kolejności dokumentu (zewnętrzne przed wewnętrznymi).</summary>
    public static List<SdtElement> Find(IEnumerable<OpenXmlElement> scopes, string tagPattern)
    {
        var regex = TagRegex(tagPattern);
        bool Match(SdtElement s) => regex.IsMatch(TagOf(s) ?? string.Empty) || regex.IsMatch(s.SdtProperties?.GetFirstChild<SdtAlias>()?.Val?.Value ?? string.Empty);
        return scopes.SelectMany(scope => (scope is SdtElement self ? [self] : Enumerable.Empty<SdtElement>()).Concat(scope.Descendants<SdtElement>()))
            .Where(Match)
            .ToList();
    }

    public static string? TagOf(SdtElement sdt) => sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value;

    /// <summary><c>w:sdtContent</c> formantu (typ zależy od poziomu: run, blok, wiersz, komórka).</summary>
    public static OpenXmlElement? ContentOf(SdtElement sdt) => sdt.ChildElements.FirstOrDefault(c => c.LocalName == "sdtContent");

    public static bool IsRepeatingSection(SdtElement sdt) => sdt.SdtProperties?.GetFirstChild<SdtRepeatedSection>() != null;

    public static bool IsRepeatingSectionItem(SdtElement sdt) => sdt.SdtProperties?.GetFirstChild<SdtRepeatedSectionItem>() != null;

    public static bool IsSectionPart(SdtElement sdt) => IsRepeatingSection(sdt) || IsRepeatingSectionItem(sdt);

    /// <summary>Elementy sekcji powtarzanej (<c>w15:repeatingSectionItem</c>); gdy ich brak – cała zawartość jako jeden element.</summary>
    public static List<OpenXmlElement> RepeatingSectionItems(SdtElement section)
    {
        var content = ContentOf(section) ?? throw new InvalidOperationException("Sekcja powtarzana nie ma zawartości (w:sdtContent).");
        var items = content.ChildElements.OfType<SdtElement>().Where(IsRepeatingSectionItem).Cast<OpenXmlElement>().ToList();
        return items.Count > 0 ? items : content.ChildElements.ToList();
    }

    public static SdtElement? FindRepeatingSection(OpenXmlElement scope) =>
        scope is SdtElement self && IsRepeatingSection(self) ? self : scope.Descendants<SdtElement>().FirstOrDefault(IsRepeatingSection);

    /// <summary>Czy element nadal jest osadzony w którymś z zakresów (nie został odłączony przez usunięcie przodka).</summary>
    public static bool IsAttached(OpenXmlElement element, IEnumerable<OpenXmlElement> scopes) =>
        scopes.Any(s => ReferenceEquals(element, s) || element.Ancestors().Any(a => ReferenceEquals(a, s)));

    /// <summary>Usuwa formant z zawartością; formant komórki tylko opróżnia komórkę. Pusty akapit po formancie w tekście znika, gdy komórka ma inne akapity.</summary>
    public static void Remove(SdtElement sdt)
    {
        if (sdt is SdtCell)
        {
            var cell = sdt.Descendants<TableCell>().FirstOrDefault();
            if (cell != null)
            {
                foreach (var child in cell.ChildElements.Where(c => c is not TableCellProperties).ToList()) child.Remove();
                cell.Append(new Paragraph());
            }

            Unwrap(sdt);
            return;
        }

        var parent = sdt.Parent;
        var paragraph = sdt.Ancestors<Paragraph>().FirstOrDefault();
        sdt.Remove();

        if (paragraph?.Parent is TableCell container
            && paragraph.ChildElements.All(c => c is ParagraphProperties or BookmarkStart or BookmarkEnd or ProofError)
            && container.Elements<Paragraph>().Count() > 1)
        {
            paragraph.Remove();
            OpenXmlHelpers.EnsureCellEndsWithParagraph(container);
        }

        OpenXmlHelpers.EnsureCellEndsWithParagraph(parent);
    }

    /// <summary>Zdejmuje ramkę formantu: zawartość trafia w jego miejsce.</summary>
    public static void Unwrap(SdtElement sdt)
    {
        foreach (var child in ContentOf(sdt)?.ChildElements.ToList() ?? [])
        {
            child.Remove();
            sdt.InsertBeforeSelf(child);
        }

        var parent = sdt.Parent;
        sdt.Remove();
        OpenXmlHelpers.EnsureCellEndsWithParagraph(parent);
    }
}
