using System.Xml.Linq;

namespace Mass.AddressWindow.Docx;

internal sealed class PageSetup
{
    private const double A4Width = 210;
    private const double A4Height = 297;
    private const double DefaultMargin = 25;

    public double Width { get; private init; } = A4Width;

    public double Height { get; private init; } = A4Height;

    public double MarginTop { get; private init; } = DefaultMargin;

    public double MarginBottom { get; private init; } = DefaultMargin;

    public double MarginLeft { get; private init; } = DefaultMargin;

    public double MarginRight { get; private init; } = DefaultMargin;

    public double HeaderDistance { get; private init; } = 12.5;

    public string? FirstPageHeaderRelId { get; private init; }

    public double ContentLeft => MarginLeft;

    public double ContentWidth => Math.Max(10, Width - MarginLeft - MarginRight);

    public double ContentBottom => Height - MarginBottom;

    public bool IsA4Portrait => Math.Abs(Width - A4Width) < 2 && Math.Abs(Height - A4Height) < 2;

    public static PageSetup FromBody(XElement body)
    {
        var sectPr = body.Descendants(Ns.W + "sectPr")
            .FirstOrDefault(s => s.Parent == body || s.Parent?.Name == Ns.W + "pPr");
        if (sectPr is null)
        {
            return new PageSetup();
        }

        var size = sectPr.Element(Ns.W + "pgSz");
        var margins = sectPr.Element(Ns.W + "pgMar");
        var titlePage = sectPr.Element(Ns.W + "titlePg") is { } t && StyleResolver.IsOn((string?)t.Attribute(Ns.W + "val"));
        var headerType = titlePage ? "first" : "default";
        var headerRef = sectPr.Elements(Ns.W + "headerReference")
            .FirstOrDefault(h => ((string?)h.Attribute(Ns.W + "type") ?? "default") == headerType);

        return new PageSetup
        {
            Width = Units.TwipsAttrToMm(size, Ns.W + "w") ?? A4Width,
            Height = Units.TwipsAttrToMm(size, Ns.W + "h") ?? A4Height,
            MarginTop = Math.Abs(Units.TwipsAttrToMm(margins, Ns.W + "top") ?? DefaultMargin),
            MarginBottom = Math.Abs(Units.TwipsAttrToMm(margins, Ns.W + "bottom") ?? DefaultMargin),
            MarginLeft = Units.TwipsAttrToMm(margins, Ns.W + "left") ?? Units.TwipsAttrToMm(margins, Ns.W + "start") ?? DefaultMargin,
            MarginRight = Units.TwipsAttrToMm(margins, Ns.W + "right") ?? Units.TwipsAttrToMm(margins, Ns.W + "end") ?? DefaultMargin,
            HeaderDistance = Units.TwipsAttrToMm(margins, Ns.W + "header") ?? 12.5,
            FirstPageHeaderRelId = (string?)headerRef?.Attribute(Ns.R + "id"),
        };
    }

    public (double Start, double End, bool Estimated) Horizontal(string? relativeFrom) => relativeFrom switch
    {
        "page" => (0, Width, false),
        "leftMargin" or "insideMargin" or "left-margin-area" or "inner-margin-area" => (0, MarginLeft, false),
        "rightMargin" or "outsideMargin" or "right-margin-area" or "outer-margin-area" => (Width - MarginRight, Width, false),
        "character" or "char" => (MarginLeft, Width - MarginRight, true),
        _ => (MarginLeft, Width - MarginRight, false),
    };

    public (double Start, double End)? Vertical(string? relativeFrom) => relativeFrom switch
    {
        "page" => (0, Height),
        "margin" => (MarginTop, Height - MarginBottom),
        "topMargin" or "insideMargin" or "top-margin-area" or "inner-margin-area" => (0, MarginTop),
        "bottomMargin" or "outsideMargin" or "bottom-margin-area" or "outer-margin-area" => (Height - MarginBottom, Height),
        _ => null,
    };
}
