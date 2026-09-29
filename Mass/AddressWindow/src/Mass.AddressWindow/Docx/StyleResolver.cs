using System.Xml.Linq;

namespace Mass.AddressWindow.Docx;

internal enum Alignment
{
    Left,
    Center,
    Right,
    Justify,
}

internal sealed class ParagraphProps
{
    public required IReadOnlyList<XElement> PPrChain { get; init; }

    public required IReadOnlyList<XElement> StyleRPrChain { get; init; }

    public Alignment Alignment { get; init; }

    public double SpaceBeforeMm { get; init; }

    public double SpaceAfterMm { get; init; }

    public string LineRule { get; init; } = "auto";

    public double LineValue { get; init; } = 1.0;

    public double IndentLeftMm { get; init; }

    public double IndentRightMm { get; init; }

    public XElement? FramePr { get; init; }

    public bool PageBreakBefore { get; init; }

    public double MarkFontSizePt { get; init; }
}

internal readonly record struct RunProps(double FontSizePt, bool Italic, bool Underline, bool Caps, bool Hidden);

internal sealed class StyleResolver
{
    private const double WordDefaultFontSizePt = 10;
    private const int MaxStyleDepth = 32;

    private static readonly XName PPr = Ns.W + "pPr";
    private static readonly XName RPr = Ns.W + "rPr";
    private static readonly XName Val = Ns.W + "val";

    private readonly Dictionary<string, XElement> _styles = new(StringComparer.Ordinal);
    private readonly XElement? _pPrDefault;
    private readonly XElement? _rPrDefault;
    private readonly string? _defaultParagraphStyleId;

    public StyleResolver(XDocument? styles)
    {
        var root = styles?.Root;
        if (root is null)
        {
            return;
        }

        var docDefaults = root.Element(Ns.W + "docDefaults");
        _pPrDefault = docDefaults?.Element(Ns.W + "pPrDefault")?.Element(PPr);
        _rPrDefault = docDefaults?.Element(Ns.W + "rPrDefault")?.Element(RPr);

        foreach (var style in root.Elements(Ns.W + "style"))
        {
            var id = (string?)style.Attribute(Ns.W + "styleId");
            if (id is null)
            {
                continue;
            }

            _styles.TryAdd(id, style);
            var isDefault = style.Attribute(Ns.W + "default") is { } flag && IsOn(flag.Value);
            if ((string?)style.Attribute(Ns.W + "type") == "paragraph" && isDefault)
            {
                _defaultParagraphStyleId ??= id;
            }
        }
    }

    public ParagraphProps ResolveParagraph(XElement paragraph)
    {
        var pPr = paragraph.Element(PPr);
        var styleId = (string?)pPr?.Element(Ns.W + "pStyle")?.Attribute(Val) ?? _defaultParagraphStyleId;
        var styleChain = StyleChain(styleId).ToList();

        var pPrChain = new List<XElement>();
        if (pPr is not null)
        {
            pPrChain.Add(pPr);
        }

        pPrChain.AddRange(styleChain.Select(s => s.Element(PPr)).OfType<XElement>());
        if (_pPrDefault is not null)
        {
            pPrChain.Add(_pPrDefault);
        }

        var styleRPr = styleChain.Select(s => s.Element(RPr)).OfType<XElement>().ToList();

        var lineRule = Attr(pPrChain, "spacing", "lineRule") ?? "auto";
        var lineRaw = Units.ParseDouble(Attr(pPrChain, "spacing", "line"));
        var lineValue = lineRule == "auto"
            ? (lineRaw ?? 240) / 240.0
            : Units.TwipsToMm(lineRaw ?? 240);

        var markRPr = pPr?.Element(RPr);
        var markChain = markRPr is null ? styleRPr : [markRPr, .. styleRPr];

        return new ParagraphProps
        {
            PPrChain = pPrChain,
            StyleRPrChain = styleRPr,
            Alignment = ParseAlignment(Attr(pPrChain, "jc", "val")),
            SpaceBeforeMm = TwipsAttr(pPrChain, "spacing", "before"),
            SpaceAfterMm = TwipsAttr(pPrChain, "spacing", "after"),
            LineRule = lineRule,
            LineValue = lineValue,
            IndentLeftMm = TwipsAttr(pPrChain, "ind", "left", "start"),
            IndentRightMm = TwipsAttr(pPrChain, "ind", "right", "end"),
            FramePr = pPrChain.Select(p => p.Element(Ns.W + "framePr")).FirstOrDefault(f => f is not null),
            PageBreakBefore = Toggle(pPrChain, "pageBreakBefore"),
            MarkFontSizePt = FontSize(markChain),
        };
    }

    public RunProps ResolveRun(XElement run, ParagraphProps paragraph)
    {
        var rPr = run.Element(RPr);
        var chain = new List<XElement>();
        if (rPr is not null)
        {
            chain.Add(rPr);
        }

        var runStyle = (string?)rPr?.Element(Ns.W + "rStyle")?.Attribute(Val);
        chain.AddRange(StyleChain(runStyle).Select(s => s.Element(RPr)).OfType<XElement>());
        chain.AddRange(paragraph.StyleRPrChain);

        var underline = Attr(chain, "u", "val");
        return new RunProps(
            FontSize(chain),
            Toggle(chain, "i"),
            underline is not null && underline != "none",
            Toggle(chain, "caps"),
            Toggle(chain, "vanish"));
    }

    private double FontSize(IEnumerable<XElement> rPrChain)
    {
        var halfPoints = Units.ParseDouble(Attr(Append(rPrChain, _rPrDefault), "sz", "val"));
        return halfPoints is > 0 ? halfPoints.Value / 2.0 : WordDefaultFontSizePt;
    }

    private bool Toggle(IEnumerable<XElement> chain, string property)
    {
        var element = Append(chain, property is "pageBreakBefore" ? null : _rPrDefault)
            .Select(c => c.Element(Ns.W + property))
            .FirstOrDefault(e => e is not null);
        return element is not null && IsOn((string?)element.Attribute(Val));
    }

    private IEnumerable<XElement> StyleChain(string? styleId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (styleId is not null && visited.Count < MaxStyleDepth && visited.Add(styleId)
               && _styles.TryGetValue(styleId, out var style))
        {
            yield return style;
            styleId = (string?)style.Element(Ns.W + "basedOn")?.Attribute(Val);
        }
    }

    private static IEnumerable<XElement> Append(IEnumerable<XElement> chain, XElement? last) =>
        last is null ? chain : chain.Append(last);

    private static string? Attr(IEnumerable<XElement> chain, string child, params string[] attributes)
    {
        foreach (var container in chain)
        {
            var element = container.Element(Ns.W + child);
            if (element is null)
            {
                continue;
            }

            foreach (var attribute in attributes)
            {
                var value = (string?)element.Attribute(Ns.W + attribute);
                if (value is not null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static double TwipsAttr(IEnumerable<XElement> chain, string child, params string[] attributes)
    {
        var raw = Attr(chain, child, attributes);
        if (raw is null)
        {
            return 0;
        }

        var probe = new XElement("x", new XAttribute("v", raw));
        return Units.TwipsAttrToMm(probe, "v") ?? 0;
    }

    internal static bool IsOn(string? value) => value is null or "1" or "true" or "on";

    private static Alignment ParseAlignment(string? jc) => jc switch
    {
        "center" => Alignment.Center,
        "right" or "end" => Alignment.Right,
        "both" or "distribute" or "thaiDistribute" or "lowKashida" or "mediumKashida" or "highKashida" => Alignment.Justify,
        _ => Alignment.Left,
    };
}
