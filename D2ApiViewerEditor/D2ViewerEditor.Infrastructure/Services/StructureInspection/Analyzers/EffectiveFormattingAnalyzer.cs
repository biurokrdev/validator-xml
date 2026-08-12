using System.Globalization;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class EffectiveFormattingAnalyzer : IStructureAnalyzer
{
    private static readonly HashSet<string> ToggleProperties = new(StringComparer.Ordinal)
    {
        "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike",
        "outline", "shadow", "emboss", "imprint", "vanish", "webHidden", "rtl", "cs",
        "keepNext", "keepLines", "pageBreakBefore", "widowControl", "contextualSpacing"
    };

    private static readonly HashSet<string> NonFormattingProperties = new(StringComparer.Ordinal)
    {
        "pStyle", "rStyle", "numPr", "sectPr", "pPrChange", "rPrChange"
    };

    public void Analyze(StructureAnalysisContext context)
    {
        var stylesPart = context.FindRelatedPart("styles", "styles+xml");

        if (stylesPart is null)
        {
            AddMissingStylesIssue(context);
            return;
        }

        var stylesRoot = context.XmlLoader.Load(stylesPart.Content).Root;

        if (stylesRoot is null)
        {
            return;
        }

        var styles = ParseStyles(stylesRoot);
        var defaults = ParseDocumentDefaults(stylesRoot);
        var theme = ParseThemeFonts(context);

        var defaultParagraphStyle = FindDefaultStyle(styles, "paragraph");
        var defaultCharacterStyle = FindDefaultStyle(styles, "character");
        var defaultTableStyle = FindDefaultStyle(styles, "table");

        foreach (var node in context.WordprocessingNodes("p"))
        {
            AnalyzeParagraph(context, node, styles, defaults, defaultParagraphStyle);
        }

        foreach (var node in context.WordprocessingNodes("r"))
        {
            AnalyzeRun(context, node, styles, defaults, theme, defaultParagraphStyle, defaultCharacterStyle);
        }

        foreach (var node in context.WordprocessingNodes("tbl"))
        {
            AnalyzeTable(context, node, styles, defaultTableStyle);
        }

        ValidateStyleGraph(context, styles, stylesPart.Path);
    }

    private static void AnalyzeParagraph(
        StructureAnalysisContext context,
        IndexedNode node,
        IReadOnlyDictionary<string, StyleDefinition> styles,
        DocumentDefaults defaults,
        string? defaultParagraphStyle)
    {
        var properties = OoxmlXml.Child(node.Node, "pPr");
        var explicitStyleId = OoxmlXml.ChildVal(properties, "pStyle");
        var styleId = explicitStyleId ?? defaultParagraphStyle;

        var inherited = new Dictionary<string, ResolvedProperty>(StringComparer.Ordinal);
        Apply(inherited, defaults.Paragraph, PropertySources.DocumentDefault, "docDefaults");
        ApplyStyleChain(inherited, styles, styleId, useParagraphProperties: true, []);

        var direct = ExtractProperties(properties);
        AddEffectiveProperties(node.Element, inherited, direct, "w:pPr");
        AddStyleMetadata(node.Element, styleId, styles, explicitStyleId is null);
        AnnotateRedundantContainer(context.FindElement(properties), inherited, direct);
    }

    private static void AnalyzeRun(
        StructureAnalysisContext context,
        IndexedNode node,
        IReadOnlyDictionary<string, StyleDefinition> styles,
        DocumentDefaults defaults,
        ThemeFonts theme,
        string? defaultParagraphStyle,
        string? defaultCharacterStyle)
    {
        var paragraph = node.Node.Ancestors().FirstOrDefault(ancestor =>
            OoxmlNamespaces.IsWordprocessing(ancestor.Name.NamespaceName) && ancestor.Name.LocalName == "p");
        var paragraphStyleId = OoxmlXml.ChildVal(OoxmlXml.Child(paragraph, "pPr"), "pStyle") ?? defaultParagraphStyle;

        var properties = OoxmlXml.Child(node.Node, "rPr");
        var explicitCharacterStyleId = OoxmlXml.ChildVal(properties, "rStyle");
        var characterStyleId = explicitCharacterStyleId ?? defaultCharacterStyle;

        var inherited = new Dictionary<string, ResolvedProperty>(StringComparer.Ordinal);
        Apply(inherited, defaults.Run, PropertySources.DocumentDefault, "docDefaults");
        ApplyStyleChain(inherited, styles, paragraphStyleId, useParagraphProperties: false, []);
        ApplyStyleChain(inherited, styles, characterStyleId, useParagraphProperties: false, []);

        var direct = ExtractProperties(properties);
        AddEffectiveProperties(node.Element, inherited, direct, "w:rPr");
        AddStyleMetadata(node.Element, characterStyleId, styles, explicitCharacterStyleId is null);
        AddThemeFontResolution(node.Element, inherited, direct, theme);
        AnnotateRedundantContainer(context.FindElement(properties), inherited, direct);
    }

    private static void AnalyzeTable(
        StructureAnalysisContext context,
        IndexedNode node,
        IReadOnlyDictionary<string, StyleDefinition> styles,
        string? defaultTableStyle)
    {
        var properties = OoxmlXml.Child(node.Node, "tblPr");
        var explicitStyleId = OoxmlXml.ChildVal(properties, "tblStyle");
        var styleId = explicitStyleId ?? defaultTableStyle;

        var inherited = new Dictionary<string, ResolvedProperty>(StringComparer.Ordinal);
        ApplyStyleChain(inherited, styles, styleId, useParagraphProperties: true, []);

        var direct = ExtractTableProperties(properties);
        AddEffectiveProperties(node.Element, inherited, direct, "w:tblPr");
        AddStyleMetadata(node.Element, styleId, styles, explicitStyleId is null);
        AnnotateRedundantContainer(context.FindElement(properties), inherited, direct);
        AnnotateConditionalTableStyle(node.Element, styleId, styles);
    }

    private static void AnnotateConditionalTableStyle(
        InspectedElement table,
        string? styleId,
        IReadOnlyDictionary<string, StyleDefinition> styles)
    {
        if (styleId is null || !styles.TryGetValue(styleId, out var style))
        {
            return;
        }

        var regions = OoxmlXml.Children(style.Source, "tblStylePr")
            .Select(element => OoxmlXml.Attribute(element, "type"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (regions.Length == 0)
        {
            return;
        }

        table.Properties.Add(new StructureProperty(
            "Warunkowe regiony stylu tabeli",
            string.Join(", ", regions),
            PropertySources.TableStyle,
            style.Id));
        table.Issues.Add(new StructureIssue(
            StructureIssueCodes.TableStyleConditionalFormatting,
            StructureIssueSeverity.Info,
            "Warunkowe formatowanie stylu tabeli",
            "Styl tabeli zawiera regiony warunkowe (firstRow/lastRow/banding). Formatowanie komórki zależy od jej pozycji i ustawień tblLook."));
    }

    private static Dictionary<string, StyleDefinition> ParseStyles(XElement stylesRoot)
    {
        var result = new Dictionary<string, StyleDefinition>(StringComparer.Ordinal);

        foreach (var style in stylesRoot.Elements().Where(element =>
                     OoxmlNamespaces.IsWordprocessing(element.Name.NamespaceName) &&
                     element.Name.LocalName == "style"))
        {
            var styleId = OoxmlXml.Attribute(style, "styleId");

            if (string.IsNullOrWhiteSpace(styleId))
            {
                continue;
            }

            var type = OoxmlXml.Attribute(style, "type") ?? "unknown";
            var isTableStyle = type.Equals("table", StringComparison.OrdinalIgnoreCase);

            result[styleId] = new StyleDefinition(
                styleId,
                type,
                OoxmlXml.IsOn(OoxmlXml.Attribute(style, "default")),
                OoxmlXml.ChildVal(style, "name") ?? styleId,
                OoxmlXml.ChildVal(style, "basedOn"),
                isTableStyle
                    ? ExtractTableProperties(OoxmlXml.Child(style, "tblPr"))
                    : ExtractProperties(OoxmlXml.Child(style, "pPr")),
                ExtractProperties(OoxmlXml.Child(style, "rPr")),
                style);
        }

        return result;
    }

    private static DocumentDefaults ParseDocumentDefaults(XElement stylesRoot)
    {
        var docDefaults = OoxmlXml.Child(stylesRoot, "docDefaults");

        return new DocumentDefaults(
            ExtractProperties(OoxmlXml.Child(OoxmlXml.Child(docDefaults, "pPrDefault"), "pPr")),
            ExtractProperties(OoxmlXml.Child(OoxmlXml.Child(docDefaults, "rPrDefault"), "rPr")));
    }

    private static ThemeFonts ParseThemeFonts(StructureAnalysisContext context)
    {
        var themePart = context.FindRelatedPart("theme", "theme+xml");

        if (themePart is null)
        {
            return ThemeFonts.Empty;
        }

        var fontScheme = context.XmlLoader.Load(themePart.Content).Root?.Descendants()
            .FirstOrDefault(element =>
                OoxmlNamespaces.IsDrawingMain(element.Name.NamespaceName) &&
                element.Name.LocalName == "fontScheme");

        if (fontScheme is null)
        {
            return ThemeFonts.Empty;
        }

        var major = OoxmlXml.Child(fontScheme, "majorFont");
        var minor = OoxmlXml.Child(fontScheme, "minorFont");

        return new ThemeFonts(
            Typeface(major, "latin"), Typeface(minor, "latin"),
            Typeface(major, "ea"), Typeface(minor, "ea"),
            Typeface(major, "cs"), Typeface(minor, "cs"));

        static string? Typeface(XElement? fontGroup, string localName) =>
            OoxmlXml.Attribute(OoxmlXml.Child(fontGroup, localName), "typeface");
    }

    private static void AddEffectiveProperties(
        InspectedElement target,
        IReadOnlyDictionary<string, ResolvedProperty> inherited,
        IReadOnlyDictionary<string, string> direct,
        string directReference)
    {
        var effective = new Dictionary<string, ResolvedProperty>(inherited, StringComparer.Ordinal);

        foreach (var pair in direct)
        {
            var isRedundant = inherited.TryGetValue(pair.Key, out var inheritedProperty) &&
                              AreEquivalent(pair.Key, inheritedProperty.Value, pair.Value);

            effective[pair.Key] = new ResolvedProperty(pair.Value, PropertySources.Direct, directReference, isRedundant);

            if (isRedundant)
            {
                target.Issues.Add(new StructureIssue(
                    StructureIssueCodes.RedundantDirectFormatting,
                    StructureIssueSeverity.Warning,
                    "Nadmiarowe formatowanie bezpośrednie",
                    $"Właściwość bezpośrednia '{FriendlyName(pair.Key)}' powiela odziedziczoną wartość efektywną '{FormatValue(pair.Key, pair.Value)}' (źródło: {inheritedProperty!.Source})."));
            }
        }

        foreach (var pair in effective.OrderBy(pair => FriendlyName(pair.Key), StringComparer.Ordinal))
        {
            target.Properties.Add(new StructureProperty(
                $"Efektywne: {FriendlyName(pair.Key)}",
                FormatValue(pair.Key, pair.Value.Value),
                pair.Value.Source,
                pair.Value.SourceReference,
                pair.Value.IsRedundant));
        }
    }

    private static void AnnotateRedundantContainer(
        InspectedElement? propertyContainer,
        IReadOnlyDictionary<string, ResolvedProperty> inherited,
        IReadOnlyDictionary<string, string> direct)
    {
        if (propertyContainer is null)
        {
            return;
        }

        foreach (var pair in direct)
        {
            if (!inherited.TryGetValue(pair.Key, out var inheritedProperty) ||
                !AreEquivalent(pair.Key, inheritedProperty.Value, pair.Value))
            {
                continue;
            }

            propertyContainer.Properties.Add(new StructureProperty(
                FriendlyName(pair.Key),
                FormatValue(pair.Key, pair.Value),
                PropertySources.Direct,
                $"identyczne z odziedziczonym ({inheritedProperty.Source})",
                IsRedundant: true));
        }
    }

    private static void AddStyleMetadata(
        InspectedElement target,
        string? styleId,
        IReadOnlyDictionary<string, StyleDefinition> styles,
        bool isDefaultStyle)
    {
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return;
        }

        if (!styles.TryGetValue(styleId, out var style))
        {
            target.Issues.Add(new StructureIssue(
                StructureIssueCodes.StyleNotFound,
                StructureIssueSeverity.Error,
                "Nie znaleziono stylu",
                $"Element odwołuje się do stylu '{styleId}', ale styles.xml nie zawiera takiej definicji."));
            return;
        }

        target.Properties.Add(new StructureProperty(
            "Styl",
            $"{style.Name} ({style.Id})",
            isDefaultStyle ? PropertySources.DefaultStyle : PropertySources.Style,
            style.BasedOn is null ? null : $"basedOn={style.BasedOn}"));
    }

    private static void AddThemeFontResolution(
        InspectedElement target,
        IReadOnlyDictionary<string, ResolvedProperty> inherited,
        IReadOnlyDictionary<string, string> direct,
        ThemeFonts theme)
    {
        var fonts = direct.GetValueOrDefault("rFonts")
                    ?? (inherited.TryGetValue("rFonts", out var inheritedFonts) ? inheritedFonts.Value : null);

        if (string.IsNullOrWhiteSpace(fonts))
        {
            return;
        }

        var attributes = ParseCanonicalAttributes(fonts);
        var explicitFont = FirstValue(attributes, "ascii", "hAnsi", "eastAsia", "cs");
        var themeKey = FirstValue(attributes, "asciiTheme", "hAnsiTheme", "eastAsiaTheme", "cstheme");
        var resolved = explicitFont ?? theme.Resolve(themeKey);

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            target.Properties.Add(new StructureProperty(
                "Rozwiązana czcionka",
                resolved,
                explicitFont is not null ? PropertySources.EffectiveFormatting : PropertySources.Theme,
                themeKey));
        }
    }

    private static void ApplyStyleChain(
        IDictionary<string, ResolvedProperty> target,
        IReadOnlyDictionary<string, StyleDefinition> styles,
        string? styleId,
        bool useParagraphProperties,
        HashSet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(styleId) || !styles.TryGetValue(styleId, out var style) || !visited.Add(style.Id))
        {
            return;
        }

        ApplyStyleChain(target, styles, style.BasedOn, useParagraphProperties, visited);
        Apply(target, useParagraphProperties ? style.ParagraphProperties : style.RunProperties, PropertySources.Style, style.Id);
    }

    private static void Apply(
        IDictionary<string, ResolvedProperty> target,
        IReadOnlyDictionary<string, string> properties,
        string source,
        string? sourceReference)
    {
        foreach (var pair in properties)
        {
            target[pair.Key] = new ResolvedProperty(pair.Value, source, sourceReference, false);
        }
    }

    private static Dictionary<string, string> ExtractProperties(XElement? properties)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (properties is null)
        {
            return result;
        }

        foreach (var property in properties.Elements())
        {
            if (!OoxmlNamespaces.IsWordprocessing(property.Name.NamespaceName) ||
                NonFormattingProperties.Contains(property.Name.LocalName))
            {
                continue;
            }

            result[property.Name.LocalName] = CanonicalValue(property);
        }

        return result;
    }

    private static Dictionary<string, string> ExtractTableProperties(XElement? tableProperties)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (tableProperties is null)
        {
            return result;
        }

        foreach (var property in tableProperties.Elements())
        {
            if (!OoxmlNamespaces.IsWordprocessing(property.Name.NamespaceName) ||
                property.Name.LocalName == "tblStyle")
            {
                continue;
            }

            result[$"table.{property.Name.LocalName}"] = CanonicalValue(property);
        }

        return result;
    }

    private static string CanonicalValue(XElement property)
    {
        var localName = property.Name.LocalName;
        var value = OoxmlXml.Val(property);

        if (ToggleProperties.Contains(localName))
        {
            return NormalizeToggle(value);
        }

        if (property.HasAttributes)
        {
            return string.Join(
                ";",
                property.Attributes()
                    .Where(attribute => !attribute.IsNamespaceDeclaration)
                    .OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                    .Select(attribute => $"{attribute.Name.LocalName}={attribute.Value}"));
        }

        return string.IsNullOrWhiteSpace(value) ? property.Value.Trim() : value;
    }

    private static string FormatValue(string propertyName, string value)
    {
        if (propertyName is "sz" or "szCs")
        {
            var raw = CanonicalAttribute(value, "val") ?? value;

            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var halfPoints))
            {
                return $"{halfPoints / 2m:0.##} pt";
            }
        }

        return CanonicalAttribute(value, "val") ?? value;
    }

    private static string FriendlyName(string propertyName) => propertyName switch
    {
        "b" => "pogrubienie",
        "i" => "kursywa",
        "u" => "podkreślenie",
        "strike" => "przekreślenie",
        "sz" => "rozmiar czcionki",
        "rFonts" => "czcionka",
        "color" => "kolor",
        "vanish" => "tekst ukryty",
        "rtl" => "kierunek RTL",
        "jc" => "wyrównanie",
        "spacing" => "odstępy",
        "ind" => "wcięcia",
        _ => propertyName
    };

    private static bool AreEquivalent(string propertyName, string left, string right) =>
        ToggleProperties.Contains(propertyName)
            ? NormalizeToggle(left) == NormalizeToggle(right)
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeToggle(string? value) => value switch
    {
        null or "1" or "true" or "on" => "true",
        "0" or "false" or "off" => "false",
        _ => value
    };

    private static void ValidateStyleGraph(
        StructureAnalysisContext context,
        IReadOnlyDictionary<string, StyleDefinition> styles,
        string stylesPartPath)
    {
        var indexedStyles = context.Nodes
            .Where(node =>
                node.Element.PartPath.Equals(stylesPartPath, StringComparison.OrdinalIgnoreCase) &&
                node.Element.LocalName == "style")
            .ToDictionary(
                node => OoxmlXml.Attribute(node.Node, "styleId") ?? string.Empty,
                node => node.Element,
                StringComparer.Ordinal);

        foreach (var style in styles.Values.Where(style => !string.IsNullOrWhiteSpace(style.BasedOn)))
        {
            if (!indexedStyles.TryGetValue(style.Id, out var element))
            {
                continue;
            }

            if (!styles.ContainsKey(style.BasedOn!))
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.StyleBasedOnNotFound,
                    StructureIssueSeverity.Error,
                    "Brak stylu bazowego",
                    $"Styl '{style.Id}' dziedziczy po nieistniejącym stylu '{style.BasedOn}'."));
            }

            if (HasInheritanceCycle(style.Id, styles))
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.StyleInheritanceCycle,
                    StructureIssueSeverity.Error,
                    "Cykl dziedziczenia stylów",
                    $"Styl '{style.Id}' uczestniczy w cyklu basedOn."));
            }
        }
    }

    private static bool HasInheritanceCycle(string styleId, IReadOnlyDictionary<string, StyleDefinition> styles)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = styleId;

        while (styles.TryGetValue(current, out var style) && !string.IsNullOrWhiteSpace(style.BasedOn))
        {
            if (!visited.Add(current))
            {
                return true;
            }

            current = style.BasedOn;
        }

        return false;
    }

    private static void AddMissingStylesIssue(StructureAnalysisContext context)
    {
        context.FindPartRoot(context.MainDocumentPartPath)?.Issues.Add(new StructureIssue(
            StructureIssueCodes.StylesPartNotFound,
            StructureIssueSeverity.Warning,
            "Brak części ze stylami",
            "Pakiet nie ma osiągalnej części styles.xml. Formatowanie efektywne można wyliczyć tylko z wartości bezpośrednich."));
    }

    private static string? FindDefaultStyle(IReadOnlyDictionary<string, StyleDefinition> styles, string type) =>
        styles.Values
            .Where(style => style.IsDefault && style.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            .Select(style => style.Id)
            .FirstOrDefault();

    private static Dictionary<string, string> ParseCanonicalAttributes(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');

            if (separator > 0)
            {
                result[segment[..separator]] = segment[(separator + 1)..];
            }
        }

        return result;
    }

    private static string? CanonicalAttribute(string value, string attributeName) =>
        ParseCanonicalAttributes(value).GetValueOrDefault(attributeName);

    private static string? FirstValue(IReadOnlyDictionary<string, string> values, params string[] keys) =>
        keys.Select(values.GetValueOrDefault).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private sealed record StyleDefinition(
        string Id,
        string Type,
        bool IsDefault,
        string Name,
        string? BasedOn,
        IReadOnlyDictionary<string, string> ParagraphProperties,
        IReadOnlyDictionary<string, string> RunProperties,
        XElement Source);

    private sealed record DocumentDefaults(
        IReadOnlyDictionary<string, string> Paragraph,
        IReadOnlyDictionary<string, string> Run);

    private sealed record ResolvedProperty(string Value, string Source, string? SourceReference, bool IsRedundant);

    private sealed record ThemeFonts(
        string? MajorLatin,
        string? MinorLatin,
        string? MajorEastAsia,
        string? MinorEastAsia,
        string? MajorComplexScript,
        string? MinorComplexScript)
    {
        public static ThemeFonts Empty { get; } = new(null, null, null, null, null, null);

        public string? Resolve(string? themeKey) => themeKey switch
        {
            "majorHAnsi" or "majorAscii" => MajorLatin,
            "minorHAnsi" or "minorAscii" => MinorLatin,
            "majorEastAsia" => MajorEastAsia,
            "minorEastAsia" => MinorEastAsia,
            "majorBidi" => MajorComplexScript,
            "minorBidi" => MinorComplexScript,
            _ => null
        };
    }
}

public static class PropertySources
{
    public const string DocumentDefault = "docDefaults";
    public const string Style = "Styl";
    public const string DefaultStyle = "Styl domyślny";
    public const string Direct = "Formatowanie bezpośrednie";
    public const string Theme = "Motyw";
    public const string EffectiveFormatting = "Formatowanie efektywne";
    public const string TableStyle = "Styl tabeli";
    public const string Numbering = "Numeracja";
    public const string ResolvedReference = "Rozwiązane odwołanie";
    public const string DocumentStructure = "Struktura dokumentu";
}
