using System.Xml.Linq;

namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

public static class ElementIdentityKeys
{
    public static readonly IReadOnlyList<Func<XElement, string?>> Selectors = [Primary, Secondary, PropertyChild];

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
