using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed partial class ContentControlAnalyzer : IStructureAnalyzer
{
    private static readonly string[] ControlTypes =
    [
        "checkbox", "date", "dropDownList", "comboBox", "picture", "text", "richText",
        "repeatingSection", "repeatingSectionItem", "group", "citation", "bibliography", "docPartObj", "docPartList"
    ];

    public void Analyze(StructureAnalysisContext context)
    {
        var customXmlItems = BuildCustomXmlItemIndex(context);

        foreach (var node in context.WordprocessingNodes("sdt"))
        {
            AnalyzeContentControl(context, node, customXmlItems);
        }
    }

    private void AnalyzeContentControl(
        StructureAnalysisContext context,
        IndexedNode node,
        IReadOnlyDictionary<string, CustomXmlItem> customXmlItems)
    {
        var element = node.Element;
        var properties = OoxmlXml.Child(node.Node, "sdtPr");

        if (properties is null)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.ContentControlPropertiesMissing,
                StructureIssueSeverity.Warning,
                "Brak właściwości formantu",
                "Formant nie ma elementu w:sdtPr."));
            return;
        }

        AddPropertyValue(element, properties, "alias", "Alias formantu");
        AddPropertyValue(element, properties, "tag", "Tag formantu");
        AddPropertyValue(element, properties, "id", "Identyfikator formantu");
        AddPropertyValue(element, properties, "lock", "Blokada formantu");

        var controlType = properties.Elements()
            .Select(child => child.Name.LocalName)
            .FirstOrDefault(name => ControlTypes.Contains(name, StringComparer.Ordinal));

        if (controlType is not null)
        {
            element.Properties.Add(new StructureProperty("Typ formantu", controlType, "w:sdtPr"));
        }

        if (OoxmlXml.Child(properties, "placeholder") is not null)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.ContentControlPlaceholder,
                StructureIssueSeverity.Info,
                "Tekst zastępczy formantu",
                "w:placeholder odwołuje się do wpisu w glosariuszu (word/glossary). Brak glosariusza po regeneracji pakietu zostawia wiszące odwołanie."));
        }

        if (OoxmlXml.Child(properties, "dataBinding") is { } dataBinding)
        {
            AnalyzeDataBinding(context, element, dataBinding, customXmlItems);
        }
    }

    private void AnalyzeDataBinding(
        StructureAnalysisContext context,
        InspectedElement element,
        XElement dataBinding,
        IReadOnlyDictionary<string, CustomXmlItem> customXmlItems)
    {
        var attributes = dataBinding.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value, StringComparer.OrdinalIgnoreCase);

        var storeItemId = NormalizeStoreItemId(attributes.GetValueOrDefault("storeItemID"));

        element.Properties.Add(new StructureProperty(
            "Powiązanie danych",
            string.Join("; ", attributes.Select(pair => $"{pair.Key}={pair.Value}")),
            "w:dataBinding"));
        element.Issues.Add(new StructureIssue(
            StructureIssueCodes.ContentControlDataBinding,
            StructureIssueSeverity.Warning,
            "Formant powiązany z danymi",
            "Formant jest powiązany z customXml. Odtworzenie i edycja wymagają semantyki storeItemID i XPath."));

        if (string.IsNullOrWhiteSpace(storeItemId))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.ContentControlStoreItemIdMissing,
                StructureIssueSeverity.Error,
                "Brak storeItemID",
                "w:dataBinding nie deklaruje storeItemID."));
            return;
        }

        if (!customXmlItems.TryGetValue(storeItemId, out var item))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.ContentControlCustomXmlItemNotFound,
                StructureIssueSeverity.Error,
                "Nie znaleziono części customXml",
                $"Żaden magazyn customXml o itemID '{storeItemId}' nie został rozwiązany w pakiecie."));
            return;
        }

        element.Properties.Add(new StructureProperty(
            "Część customXml",
            item.ItemPartPath,
            PropertySources.ResolvedReference,
            item.PropertiesPartPath));

        ValidateXPath(context, element, item, attributes.GetValueOrDefault("xpath"), attributes.GetValueOrDefault("prefixMappings"));
    }

    private void ValidateXPath(
        StructureAnalysisContext context,
        InspectedElement element,
        CustomXmlItem item,
        string? xpath,
        string? prefixMappings)
    {
        if (string.IsNullOrWhiteSpace(xpath))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.ContentControlXPathMissing,
                StructureIssueSeverity.Error,
                "Brak XPath powiązania",
                "w:dataBinding nie deklaruje wyrażenia XPath."));
            return;
        }

        var mappings = ParsePrefixMappings(prefixMappings);
        var namespaceManager = new XmlNamespaceManager(new NameTable());

        foreach (var mapping in mappings)
        {
            namespaceManager.AddNamespace(mapping.Key, mapping.Value);
        }

        try
        {
            XPathExpression.Compile(xpath, namespaceManager);
        }
        catch (XPathException exception)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.ContentControlXPathInvalid,
                StructureIssueSeverity.Error,
                "Niepoprawny XPath powiązania",
                $"XPath powiązania nie daje się sparsować: {exception.Message}"));
            return;
        }

        element.Properties.Add(new StructureProperty("XPath powiązania", xpath, "w:dataBinding", item.ItemPartPath));

        if (mappings.Count > 0)
        {
            element.Properties.Add(new StructureProperty(
                "Mapowanie prefiksów XPath",
                string.Join("; ", mappings.Select(pair => $"{pair.Key}={pair.Value}")),
                "w:dataBinding"));
        }

        MatchXPathAgainstItem(context, element, item, xpath, namespaceManager);
    }

    private void MatchXPathAgainstItem(
        StructureAnalysisContext context,
        InspectedElement element,
        CustomXmlItem item,
        string xpath,
        XmlNamespaceManager namespaceManager)
    {
        if (!context.Package.XmlParts.TryGetValue(item.ItemPartPath, out var itemPart))
        {
            return;
        }

        XElement? root;

        try
        {
            root = context.XmlLoader.Load(itemPart.Content).Root;
        }
        catch (XmlException)
        {
            return;
        }

        if (root is null)
        {
            return;
        }

        try
        {
            var matches = root.XPathSelectElements(xpath, namespaceManager).Take(2).Count();

            element.Properties.Add(new StructureProperty(
                "Trafienia XPath",
                matches >= 2 ? "2+" : matches.ToString(),
                PropertySources.ResolvedReference,
                item.ItemPartPath));

            if (matches == 0)
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.ContentControlXPathNoMatch,
                    StructureIssueSeverity.Info,
                    "XPath nie trafia w element",
                    "XPath jest składniowo poprawny, ale nie wskazał elementu w powiązanej części customXml. Wyrażenia zwracające atrybut lub wartość skalarną nadal mogą być poprawne."));
            }
        }
        catch (XPathException)
        {
        }
    }

    private Dictionary<string, CustomXmlItem> BuildCustomXmlItemIndex(StructureAnalysisContext context)
    {
        var result = new Dictionary<string, CustomXmlItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var propertiesPart in context.Package.XmlParts.Values.Where(IsCustomXmlPropertiesPart))
        {
            XElement? root;

            try
            {
                root = context.XmlLoader.Load(propertiesPart.Content).Root;
            }
            catch (XmlException)
            {
                continue;
            }

            var itemId = NormalizeStoreItemId(OoxmlXml.Attribute(root, "itemID"));

            if (string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            var itemPartPath = context.Opc.Relationships.FindSourceByTarget(propertiesPart.Path, "customXmlProps");

            if (!string.IsNullOrWhiteSpace(itemPartPath) && context.Package.XmlParts.ContainsKey(itemPartPath))
            {
                result.TryAdd(itemId, new CustomXmlItem(itemPartPath, propertiesPart.Path));
            }
        }

        return result;
    }

    private static bool IsCustomXmlPropertiesPart(RawPackagePart part) =>
        part.ContentType?.Contains("customXmlProperties+xml", StringComparison.OrdinalIgnoreCase) == true ||
        part.Path.Contains("customXml/itemProps", StringComparison.OrdinalIgnoreCase);

    private static void AddPropertyValue(InspectedElement element, XElement properties, string childName, string label)
    {
        var value = OoxmlXml.Val(OoxmlXml.Child(properties, childName));

        if (!string.IsNullOrWhiteSpace(value))
        {
            element.Properties.Add(new StructureProperty(label, value, $"w:{childName}"));
        }
    }

    private static Dictionary<string, string> ParsePrefixMappings(string? mappings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(mappings))
        {
            return result;
        }

        foreach (Match match in PrefixMappingRegex().Matches(mappings))
        {
            var prefix = match.Groups["prefix"].Value;
            var uri = match.Groups["uri"].Value;

            if (prefix.Length > 0 && uri.Length > 0)
            {
                result[prefix] = uri;
            }
        }

        return result;
    }

    private static string? NormalizeStoreItemId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('{', '}').ToUpperInvariant();

    [GeneratedRegex("""xmlns:(?<prefix>[A-Za-z_][\w.-]*)\s*=\s*['"](?<uri>[^'"]+)['"]""", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixMappingRegex();

    private sealed record CustomXmlItem(string ItemPartPath, string PropertiesPartPath);
}
