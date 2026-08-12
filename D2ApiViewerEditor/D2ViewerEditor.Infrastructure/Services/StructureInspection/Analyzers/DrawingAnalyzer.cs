using System.Globalization;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class DrawingAnalyzer : IStructureAnalyzer
{
    private const long SuspiciousExtentEmu = 20_000_000;

    public void Analyze(StructureAnalysisContext context)
    {
        foreach (var node in context.Nodes.Where(node =>
                     OoxmlNamespaces.IsWordprocessingDrawing(node.Element.NamespaceUri) &&
                     node.Element.LocalName is "anchor" or "inline"))
        {
            AnalyzeDrawing(node);
        }

        foreach (var node in context.Nodes.Where(node =>
                     OoxmlNamespaces.IsWordprocessingDrawing(node.Element.NamespaceUri) &&
                     node.Element.LocalName == "posOffset"))
        {
            AnalyzeOffset(node);
        }

        foreach (var node in context.Nodes)
        {
            AnalyzeSpecialFeature(node);
        }
    }

    private static void AnalyzeDrawing(IndexedNode node)
    {
        var element = node.Element;
        var source = node.Node;
        var isAnchor = element.LocalName == "anchor";

        element.Issues.Add(isAnchor
            ? new StructureIssue(
                StructureIssueCodes.DrawingAnchored,
                StructureIssueSeverity.Info,
                "Grafika pływająca",
                "Obiekt używa wp:anchor i jest pozycjonowany niezależnie od przepływu tekstu.")
            : new StructureIssue(
                StructureIssueCodes.DrawingInline,
                StructureIssueSeverity.Info,
                "Grafika w tekście",
                "Obiekt jest osadzony w przepływie tekstu (wp:inline)."));

        AddDistances(element, source);
        AddExtent(element, source);
        AddRelativeSize(element, source);
        AddEffectExtent(element, source);
        AddWrapping(element, source);
        AddTransformAndCrop(element, source);
        AddImageRelationships(element, source);

        if (isAnchor)
        {
            AddAnchorFlags(element, source);
            AddPosition(element, source, "positionH", "Poziomo");
            AddPosition(element, source, "positionV", "Pionowo");
        }
    }

    private static void AddAnchorFlags(InspectedElement element, XElement source)
    {
        foreach (var (attribute, label) in new[]
                 {
                     ("relativeHeight", "Kolejność nakładania (z-order)"),
                     ("behindDoc", "Za treścią"),
                     ("locked", "Zablokowany"),
                     ("layoutInCell", "Pozycjonowanie w komórce"),
                     ("allowOverlap", "Dozwolone nakładanie"),
                     ("simplePos", "Tryb uproszczonej pozycji")
                 })
        {
            var value = OoxmlXml.Attribute(source, attribute);

            if (value is not null)
            {
                element.Properties.Add(new StructureProperty(label, value, "wp:anchor"));
            }
        }

        if (OoxmlXml.Child(source, "simplePos") is { } simplePosition)
        {
            element.Properties.Add(new StructureProperty(
                "Pozycja uproszczona", OoxmlXml.DescribeAttributes(simplePosition), "wp:simplePos"));
        }

        if (OoxmlXml.IsOn(OoxmlXml.Attribute(source, "behindDoc")))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DrawingBehindDocument,
                StructureIssueSeverity.Warning,
                "Grafika za treścią",
                "behindDoc=1 — obiekt renderuje się pod treścią dokumentu."));
        }

        if (OoxmlXml.IsOn(OoxmlXml.Attribute(source, "allowOverlap")))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DrawingOverlapAllowed,
                StructureIssueSeverity.Info,
                "Dozwolone nakładanie",
                "allowOverlap=1 — obiekt może nachodzić na inne obiekty pływające."));
        }

        var layoutInCell = OoxmlXml.Attribute(source, "layoutInCell");

        if (layoutInCell is not null && !OoxmlXml.IsOn(layoutInCell))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DrawingOutsideCellLayout,
                StructureIssueSeverity.Warning,
                "Pozycjonowanie poza komórką",
                "layoutInCell=0 — obiekt w tabeli jest pozycjonowany względem strony, nie komórki."));
        }

        if (OoxmlXml.IsOn(OoxmlXml.Attribute(source, "simplePos")))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DrawingSimplePosition,
                StructureIssueSeverity.Warning,
                "Uproszczone pozycjonowanie",
                "simplePos=1 — obowiązuje wp:simplePos, a nie wp:positionH/wp:positionV."));
        }
    }

    private static void AddDistances(InspectedElement element, XElement source)
    {
        foreach (var (attribute, label) in new[]
                 {
                     ("distT", "Odstęp góra"), ("distB", "Odstęp dół"),
                     ("distL", "Odstęp lewo"), ("distR", "Odstęp prawo")
                 })
        {
            var value = OoxmlXml.Attribute(source, attribute);

            if (value is not null)
            {
                element.Properties.Add(new StructureProperty(label, OoxmlXml.FormatEmu(value), "wp:anchor/inline"));
            }
        }
    }

    private static void AddExtent(InspectedElement element, XElement source)
    {
        var extent = OoxmlXml.Child(source, "extent");

        if (extent is null)
        {
            return;
        }

        var width = ReadEmu(extent, "cx");
        var height = ReadEmu(extent, "cy");

        element.Properties.Add(new StructureProperty(
            "Rozmiar nominalny",
            $"szerokość={OoxmlXml.FormatEmu(width.ToString(CultureInfo.InvariantCulture))}; wysokość={OoxmlXml.FormatEmu(height.ToString(CultureInfo.InvariantCulture))}",
            "wp:extent"));

        if (width == 0 || height == 0)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DrawingZeroExtent,
                StructureIssueSeverity.Warning,
                "Zerowy rozmiar obiektu",
                $"wp:extent ma cx={width}, cy={height} — rozmiar trzeba wyliczyć z osadzonej grafiki."));
        }

        if (width > SuspiciousExtentEmu || height > SuspiciousExtentEmu)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DrawingHugeExtent,
                StructureIssueSeverity.Warning,
                "Nietypowo duży obiekt",
                $"wp:extent ma cx={width}, cy={height} EMU — obiekt jest większy niż jakakolwiek realna strona."));
        }
    }

    private static void AddRelativeSize(InspectedElement element, XElement source)
    {
        foreach (var relativeSize in source.Elements().Where(child => child.Name.LocalName is "sizeRelH" or "sizeRelV"))
        {
            var percent = relativeSize.Descendants()
                .FirstOrDefault(child => child.Name.LocalName is "pctWidth" or "pctHeight")?.Value;

            element.Properties.Add(new StructureProperty(
                relativeSize.Name.LocalName == "sizeRelH" ? "Szerokość względna" : "Wysokość względna",
                $"relativeFrom={OoxmlXml.Attribute(relativeSize, "relativeFrom") ?? "(brak)"}; procent={percent ?? "(brak)"}",
                relativeSize.Name.LocalName));

            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DrawingRelativeSize,
                StructureIssueSeverity.Info,
                "Rozmiar względny grafiki",
                "Obiekt używa rozmiaru procentowego zamiast wyłącznie bezwzględnych wymiarów."));
        }
    }

    private static void AddEffectExtent(InspectedElement element, XElement source)
    {
        var effectExtent = OoxmlXml.Child(source, "effectExtent");

        if (effectExtent is null)
        {
            return;
        }

        element.Properties.Add(new StructureProperty(
            "Rozmiar efektów", OoxmlXml.DescribeAttributes(effectExtent), "wp:effectExtent"));
        element.Issues.Add(new StructureIssue(
            StructureIssueCodes.DrawingEffectExtent,
            StructureIssueSeverity.Info,
            "Obiekt z marginesem efektów",
            "Obiekt ma effectExtent — układ musi uwzględnić efekty wizualne wychodzące poza rozmiar nominalny."));
    }

    private static void AddWrapping(InspectedElement element, XElement source)
    {
        var wrap = source.Elements().FirstOrDefault(child => child.Name.LocalName.StartsWith("wrap", StringComparison.Ordinal));

        if (wrap is null)
        {
            if (element.LocalName == "anchor")
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.DrawingWrapMissing,
                    StructureIssueSeverity.Warning,
                    "Brak trybu oblewania",
                    "Kotwica nie zawiera elementu wrap* — sposób oblewania tekstem jest niedookreślony."));
            }

            return;
        }

        element.Properties.Add(new StructureProperty(
            "Oblewanie tekstem",
            $"{wrap.Name.LocalName}; {OoxmlXml.DescribeAttributes(wrap)}",
            $"wp:{wrap.Name.LocalName}"));

        var wrapPolygon = wrap.Descendants().FirstOrDefault(child => child.Name.LocalName == "wrapPolygon");

        if (wrapPolygon is not null)
        {
            var points = wrapPolygon.Elements().Count(child => child.Name.LocalName is "start" or "lineTo");
            element.Properties.Add(new StructureProperty(
                "Wielokąt oblewania",
                $"punkty={points}; edited={OoxmlXml.Attribute(wrapPolygon, "edited") ?? "(brak)"}",
                "wp:wrapPolygon"));
        }

        if (wrap.Name.LocalName is "wrapTight" or "wrapThrough")
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.DrawingComplexWrap,
                StructureIssueSeverity.Warning,
                "Złożone oblewanie tekstem",
                $"Obiekt używa {wrap.Name.LocalName} — oblewanie idzie po wielokącie, a nie po prostokącie."));
        }
    }

    private static void AddPosition(InspectedElement element, XElement source, string localName, string label)
    {
        var position = OoxmlXml.Child(source, localName);

        if (position is null)
        {
            return;
        }

        element.Properties.Add(new StructureProperty(
            $"{label}: odniesienie",
            OoxmlXml.Attribute(position, "relativeFrom"),
            $"wp:{localName}"));

        var alignment = OoxmlXml.Child(position, "align")?.Value;
        var offset = OoxmlXml.Child(position, "posOffset")?.Value;

        element.Properties.Add(new StructureProperty(
            $"{label}: pozycja",
            alignment is not null ? $"align={alignment}" : OoxmlXml.FormatEmu(offset),
            $"wp:{localName}"));
    }

    private static void AddTransformAndCrop(InspectedElement element, XElement source)
    {
        var transform = source.Descendants().FirstOrDefault(child =>
            OoxmlNamespaces.IsDrawingMain(child.Name.NamespaceName) && child.Name.LocalName == "xfrm");

        if (transform is not null)
        {
            element.Properties.Add(new StructureProperty("Transformacja", OoxmlXml.DescribeAttributes(transform), "a:xfrm"));

            var rotation = OoxmlXml.Attribute(transform, "rot");

            if (!string.IsNullOrWhiteSpace(rotation) ||
                OoxmlXml.IsOn(OoxmlXml.Attribute(transform, "flipH")) ||
                OoxmlXml.IsOn(OoxmlXml.Attribute(transform, "flipV")))
            {
                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.DrawingTransform,
                    StructureIssueSeverity.Info,
                    "Obrót lub odbicie obiektu",
                    "Obiekt używa obrotu i/lub odbicia. Sprawdź, czy edytor stosuje transformacje DrawingML w prawidłowym układzie współrzędnych."));
            }
        }

        var crop = source.Descendants().FirstOrDefault(child =>
            OoxmlNamespaces.IsDrawingMain(child.Name.NamespaceName) && child.Name.LocalName == "srcRect");

        if (crop is not null && crop.Attributes().Any())
        {
            element.Properties.Add(new StructureProperty("Kadrowanie obrazu", OoxmlXml.DescribeAttributes(crop), "a:srcRect"));
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.ImageCropped,
                StructureIssueSeverity.Info,
                "Kadrowanie obrazu",
                "Obraz jest kadrowany prostokątem źródłowym DrawingML (a:srcRect)."));
        }
    }

    private static void AddImageRelationships(InspectedElement element, XElement source)
    {
        foreach (var blip in source.Descendants().Where(child =>
                     OoxmlNamespaces.IsDrawingMain(child.Name.NamespaceName) && child.Name.LocalName == "blip"))
        {
            var embed = OoxmlXml.RelationshipAttribute(blip, "embed");
            var link = OoxmlXml.RelationshipAttribute(blip, "link");

            if (embed is not null)
            {
                element.Properties.Add(new StructureProperty("Obraz osadzony (r:embed)", embed, "a:blip"));
            }

            if (link is null)
            {
                continue;
            }

            element.Properties.Add(new StructureProperty("Obraz linkowany (r:link)", link, "a:blip"));
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.ImageLinked,
                StructureIssueSeverity.Warning,
                "Obraz linkowany",
                "Obiekt wskazuje obraz przez r:link — render zależy od zasobu rozwiązywanego osobno, także zewnętrznego."));
        }
    }

    private static void AnalyzeOffset(IndexedNode node)
    {
        if (!long.TryParse(node.Node.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) || offset >= 0)
        {
            return;
        }

        node.Element.Issues.Add(new StructureIssue(
            StructureIssueCodes.DrawingNegativeOffset,
            StructureIssueSeverity.Warning,
            "Ujemne przesunięcie pozycji",
            $"Przesunięcie wynosi {offset} EMU — obiekt wychodzi poza obszar odniesienia (np. poza margines strony)."));
    }

    private static void AnalyzeSpecialFeature(IndexedNode node)
    {
        var issue = node.Element.Category switch
        {
            ElementCategories.SvgImage => new StructureIssue(
                StructureIssueCodes.DrawingSvg,
                StructureIssueSeverity.Warning,
                "Grafika SVG",
                "Dokument zawiera treść SVG. Sprawdź, czy edytor obsługuje rozszerzenie SVG i jego obraz zastępczy."),
            ElementCategories.Chart => new StructureIssue(
                StructureIssueCodes.DrawingChart,
                StructureIssueSeverity.Warning,
                "Wykres",
                "Dokument zawiera wykres z osobnymi częściami pakietu i relationshipami."),
            ElementCategories.SmartArt => new StructureIssue(
                StructureIssueCodes.DrawingSmartArt,
                StructureIssueSeverity.Warning,
                "SmartArt / diagram",
                "Dokument zawiera diagram DrawingML wymagający części z danymi i układem."),
            ElementCategories.GroupedShape => new StructureIssue(
                StructureIssueCodes.DrawingGroupedShape,
                StructureIssueSeverity.Warning,
                "Grupa kształtów",
                "Dokument zawiera zgrupowane kształty. Edytor musi zachować transformacje grupy i zagnieżdżone układy współrzędnych."),
            ElementCategories.EmbeddedObject => new StructureIssue(
                StructureIssueCodes.EmbeddedObject,
                StructureIssueSeverity.Warning,
                "Obiekt osadzony (OLE)",
                "Dokument zawiera obiekt OLE. Render może wymagać obrazu podglądu albo aplikacji hosta."),
            ElementCategories.TextBoxContent => new StructureIssue(
                StructureIssueCodes.TextBoxContent,
                StructureIssueSeverity.Info,
                "Treść w polu tekstowym",
                "Tekst leży w polu tekstowym kształtu, a nie w głównym przepływie akapitów."),
            ElementCategories.LegacyDrawingContainer => new StructureIssue(
                StructureIssueCodes.LegacyPictureContainer,
                StructureIssueSeverity.Info,
                "Kontener grafiki legacy",
                "w:pict zawiera grafikę VML — sprawdź, czy edytor odtwarza ją przez pass-through XML."),
            _ => null
        };

        if (issue is not null)
        {
            node.Element.Issues.Add(issue);
        }

        if (node.Element.Category == ElementCategories.LegacyVml && IsSignificantVmlShape(node.Element.LocalName))
        {
            node.Element.Properties.Add(new StructureProperty("Identyfikator kształtu", OoxmlXml.Attribute(node.Node, "id"), "VML"));
            node.Element.Properties.Add(new StructureProperty("Styl kształtu", OoxmlXml.Attribute(node.Node, "style"), "VML"));
            node.Element.Issues.Add(new StructureIssue(
                StructureIssueCodes.LegacyVmlShape,
                StructureIssueSeverity.Warning,
                "Kształt VML (legacy)",
                "Treść używa VML zamiast DrawingML i wymaga osobnej ścieżki renderowania."));
        }
    }

    private static bool IsSignificantVmlShape(string localName) =>
        localName is "shape" or "group" or "rect" or "roundrect" or "oval" or "line"
            or "polyline" or "curve" or "image" or "textbox" or "shapetype";

    private static long ReadEmu(XElement extent, string attributeName) =>
        OoxmlXml.AttributeLong(extent, attributeName) ?? 0;
}
