using System.Globalization;
using System.Xml.Linq;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

internal static class OoxmlXml
{
    private const double EmusPerPoint = 12_700d;
    private const double TwipsPerPoint = 20d;

    public static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    public static IEnumerable<XElement> Children(XElement? parent, string localName) =>
        parent?.Elements().Where(element => element.Name.LocalName == localName) ?? [];

    public static string? Attribute(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    public static string? RelationshipAttribute(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(attribute =>
            OoxmlNamespaces.IsOfficeRelationship(attribute.Name.NamespaceName) &&
            attribute.Name.LocalName == localName)?.Value;

    public static string? Val(XElement? element) => Attribute(element, "val");

    public static string? ChildVal(XElement? parent, string localName)
    {
        var child = Child(parent, localName);
        return child is null ? null : Val(child) ?? child.Value;
    }

    public static int? ChildInt(XElement? parent, string localName) =>
        int.TryParse(ChildVal(parent, localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static int? AttributeInt(XElement? element, string localName) =>
        int.TryParse(Attribute(element, localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static long? AttributeLong(XElement? element, string localName) =>
        long.TryParse(Attribute(element, localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static bool IsToggleEnabled(XElement? element)
    {
        if (element is null)
        {
            return false;
        }

        return Val(element) is null or "1" or "true" or "on";
    }

    public static bool IsOn(string? value) => value is "1" or "true" or "on";

    public static string? FormatEmu(string? rawValue)
    {
        if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var emu))
        {
            return rawValue;
        }

        return $"{emu} EMU ({emu / EmusPerPoint:0.##} pt)";
    }

    public static string? FormatTwips(long? twips) =>
        twips is null ? null : $"{twips} tw ({twips.Value / TwipsPerPoint:0.##} pt)";

    public static string? FormatTwips(string? rawValue) =>
        long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips)
            ? FormatTwips(twips)
            : rawValue;

    public static string? FormatHalfPoints(string? rawValue) =>
        long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var halfPoints)
            ? $"{halfPoints / 2d:0.##} pt"
            : rawValue;

    public static string? DescribeAttributes(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var attributes = element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
            .Select(attribute => $"{attribute.Name.LocalName}={attribute.Value}")
            .ToArray();

        if (attributes.Length > 0)
        {
            return string.Join("; ", attributes);
        }

        var children = element.Elements()
            .Select(child => $"{child.Name.LocalName}({DescribeAttributes(child)})")
            .ToArray();

        return children.Length == 0 ? null : string.Join("; ", children);
    }

    public static string DescribeTwipsAttributes(XElement element) =>
        string.Join(
            "; ",
            element.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                .Select(attribute => long.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? $"{attribute.Name.LocalName}={value} ({value / TwipsPerPoint:0.##}pt)"
                    : $"{attribute.Name.LocalName}={attribute.Value}"));
}
