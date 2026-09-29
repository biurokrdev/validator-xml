using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

/// <summary>
/// Hasz Merkle poddrzewa XML: nazwa + posortowane atrybuty (bez pomijanych) + tekst bezpośredni +
/// hasze dzieci. Identyczny hasz = identyczne poddrzewo (z dokładnością do pomijanych atrybutów
/// i elementów), więc wyrównanie list dzieci może pomijać całe niezmienione akapity i tabele bez
/// schodzenia w głąb. Wynik memoizowany per element (tożsamość referencyjna).
/// </summary>
public sealed class XmlSubtreeHasher
{
    private readonly Dictionary<XElement, string> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly IReadOnlySet<XName> _ignoredAttributes;
    private readonly IReadOnlySet<XName> _ignoredElements;

    public XmlSubtreeHasher(IReadOnlySet<XName> ignoredAttributes, IReadOnlySet<XName> ignoredElements)
    {
        _ignoredAttributes = ignoredAttributes;
        _ignoredElements = ignoredElements;
    }

    /// <summary>Ile atrybutów pominięto przy haszowaniu (każdy element haszowany raz, więc licznik = liczba wystąpień w drzewie).</summary>
    public int IgnoredAttributeCount { get; private set; }

    public bool IsIgnoredAttribute(XName name) => _ignoredAttributes.Contains(name);

    public bool IsIgnoredElement(XName name) => _ignoredElements.Contains(name);

    public string Hash(XElement element)
    {
        if (_cache.TryGetValue(element, out var cached))
        {
            return cached;
        }

        var builder = new StringBuilder();
        builder.Append(element.Name.NamespaceName).Append('}').Append(element.Name.LocalName).Append('\u0001');
        IgnoredAttributeCount += element.Attributes().Count(attribute =>
            !attribute.IsNamespaceDeclaration && _ignoredAttributes.Contains(attribute.Name));

        foreach (var attribute in element.Attributes()
                     .Where(attribute => !attribute.IsNamespaceDeclaration && !_ignoredAttributes.Contains(attribute.Name))
                     .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
                     .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
        {
            builder.Append(attribute.Name.NamespaceName).Append('}').Append(attribute.Name.LocalName)
                .Append('=').Append(attribute.Value).Append('\u0002');
        }

        builder.Append('\u0003').Append(DirectText(element)).Append('\u0004');

        foreach (var child in element.Elements().Where(child => !_ignoredElements.Contains(child.Name)))
        {
            builder.Append(Hash(child)).Append('\u0005');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        _cache[element] = hash;

        return hash;
    }

    /// <summary>Tekst bezpośredni elementu (bez tekstu dzieci) — w OOXML niesie go tylko liść (w:t, w:instrText…).</summary>
    public static string DirectText(XElement element) =>
        string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value));
}
