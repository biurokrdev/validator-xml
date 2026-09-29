using System.Xml.Linq;

namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

/// <summary>
/// Klucze tożsamości elementów OOXML: para elementów o tej samej nazwie i tym samym kluczu to TEN SAM
/// obiekt niezależnie od pozycji na liście (relationship po Id, styl po styleId, przypis po w:id,
/// akapit po w14:paraId…). Bez tego zmiana kolejności w kolekcji nieuporządkowanej (.rels,
/// [Content_Types].xml, styles.xml) wyglądałaby jak seria zmian atrybutów w „trzecim elemencie".
/// Klucz zapasowy (np. Type+Target relationshipu) działa, gdy identyfikatory zostały przenumerowane.
/// </summary>
public static class ElementIdentityKeys
{
    public static readonly IReadOnlyList<Func<XElement, string?>> Selectors = [Primary, Secondary, PropertyChild];

    /// <summary>
    /// Kontenery właściwości OOXML: każde dziecko występuje najwyżej raz i znaczy to samo niezależnie od pozycji
    /// (schemat narzuca kolejność, Word czyta dowolną). Dziecko takiego kontenera jest parowane PO NAZWIE
    /// niezależnie od pozycji — inaczej `w:shd` przesunięte za `w:tcMar` (writer pisze kolejność ze schematu,
    /// generator oryginału inną) wyglądało jak „shd tylko w oryginale” + „shd tylko po zapisie”.
    /// `w:tabs` (wiele `w:tab`) i `w:rPrChange`-podobne kontenery listowe celowo poza listą.
    /// </summary>
    private static readonly HashSet<string> PropertyContainers = new(StringComparer.Ordinal)
    {
        "rPr", "pPr", "tcPr", "trPr", "tblPr", "tblPrEx", "sectPr", "tblCellMar", "tcMar", "tblBorders", "tcBorders",
        "pBdr", "numPr", "framePr", "pgMar", "pgSz", "tblLook", "rPrDefault", "pPrDefault", "docDefaults",
    };

    private static string? PropertyChild(XElement element) =>
        element.Parent is { } parent && PropertyContainers.Contains(parent.Name.LocalName) ? "prop" : null;

    private static string? Primary(XElement element) => element.Name.LocalName switch
    {
        "Relationship" => Attr(element, "Id"),
        "Default" => Attr(element, "Extension"),
        "Override" => Attr(element, "PartName"),
        "style" => Attr(element, "styleId"),
        "num" => Attr(element, "numId"),
        "abstractNum" => Attr(element, "abstractNumId"),
        "lvl" or "lvlOverride" => Attr(element, "ilvl"),
        "footnote" or "endnote" or "comment" or "bookmarkStart" or "bookmarkEnd" or
            "commentRangeStart" or "commentRangeEnd" or "permStart" or "permEnd" => Attr(element, "id"),
        "docPr" => Attr(element, "id"),
        "font" or "lsdException" => Attr(element, "name"),
        "tblStylePr" => Attr(element, "type"),
        "Choice" => Attr(element, "Requires"),
        "p" or "tr" => Attr(element, "paraId"),
        "sdt" => SdtId(element),
        _ => null
    };

    private static string? Secondary(XElement element) => element.Name.LocalName switch
    {
        "Relationship" => Combine(Attr(element, "Type"), Attr(element, "Target")),
        "style" => Combine(Attr(element, "type"), element.Elements().FirstOrDefault(child => child.Name.LocalName == "name") is { } name ? Attr(name, "val") : null),
        _ => null
    };

    private static string? Attr(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => !attribute.IsNamespaceDeclaration && attribute.Name.LocalName == localName)?.Value;

    private static string? SdtId(XElement sdt)
    {
        var properties = sdt.Elements().FirstOrDefault(child => child.Name.LocalName == "sdtPr");
        var id = properties?.Elements().FirstOrDefault(child => child.Name.LocalName == "id");

        return id is null ? null : Attr(id, "val");
    }

    private static string? Combine(string? first, string? second) =>
        first is null || second is null ? null : $"{first}|{second}";
}
