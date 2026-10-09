using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxPipeline.Internal;

/// <summary>Formanty zawartości (<c>w:sdt</c>): wyszukiwanie po tagu/tytule, usuwanie, rozpakowywanie.</summary>
internal static class ContentControls
{
    public static List<SdtElement> Find(IEnumerable<OpenXmlElement> scopes, string tagPattern)
    {
        var regex = new Regex("^" + Regex.Escape(tagPattern.Trim()).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return scopes.SelectMany(s => s.Descendants<SdtElement>())
            .Where(s => regex.IsMatch(s.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value ?? string.Empty)
                        || regex.IsMatch(s.SdtProperties?.GetFirstChild<SdtAlias>()?.Val?.Value ?? string.Empty))
            .ToList();
    }

    public static bool IsAttached(OpenXmlElement element) => element.Ancestors<OpenXmlPartRootElement>().Any();

    /// <summary>Usuwa formant z zawartością; formant komórki tylko opróżnia komórkę; pusty akapit po formancie w tekście znika, gdy komórka ma inne akapity.</summary>
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
            paragraph.Remove();
        EnsureCellEndsWithParagraph(parent);
    }

    /// <summary>Zdejmuje ramkę formantu: zawartość trafia w jego miejsce.</summary>
    public static void Unwrap(SdtElement sdt)
    {
        var content = sdt.ChildElements.FirstOrDefault(c => c.LocalName == "sdtContent");
        foreach (var child in content?.ChildElements.ToList() ?? [])
        {
            child.Remove();
            sdt.InsertBeforeSelf(child);
        }

        var parent = sdt.Parent;
        sdt.Remove();
        EnsureCellEndsWithParagraph(parent);
    }

    public static void EnsureCellEndsWithParagraph(OpenXmlElement? container)
    {
        if (container is TableCell cell && cell.LastChild is not Paragraph) cell.Append(new Paragraph());
    }
}
