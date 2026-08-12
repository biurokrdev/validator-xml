using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed partial class OoxmlElementClassifier
{
    private const int MaxPreviewLength = 140;

    public string GetCategory(XElement element)
    {
        var namespaceUri = element.Name.NamespaceName;
        var localName = element.Name.LocalName;

        if (namespaceUri == OoxmlNamespaces.MarkupCompatibility)
        {
            return ElementCategories.Compatibility;
        }

        if (OoxmlNamespaces.IsVml(namespaceUri))
        {
            return ElementCategories.LegacyVml;
        }

        if (namespaceUri == OoxmlNamespaces.PackageRelationships)
        {
            return ElementCategories.Relationships;
        }

        if (namespaceUri == OoxmlNamespaces.ContentTypes)
        {
            return ElementCategories.ContentTypes;
        }

        if (OoxmlNamespaces.IsWordprocessingDrawing(namespaceUri))
        {
            return ClassifyDrawingLayout(localName);
        }

        if (OoxmlNamespaces.IsDrawingMain(namespaceUri) || OoxmlNamespaces.IsDrawingPicture(namespaceUri))
        {
            return ClassifyDrawingMl(localName);
        }

        return OoxmlNamespaces.IsWordprocessing(namespaceUri)
            ? ClassifyWordprocessing(localName)
            : ClassifyForeignElement(element);
    }

    public string GetDisplayName(XElement element, string category) => category switch
    {
        ElementCategories.Paragraph => "Akapit",
        ElementCategories.Run => "Run",
        ElementCategories.Text => "Tekst",
        ElementCategories.Table => "Tabela",
        ElementCategories.TableRow => "Wiersz tabeli",
        ElementCategories.TableCell => "Komórka tabeli",
        ElementCategories.AnchoredDrawing => "Grafika pływająca (wp:anchor)",
        ElementCategories.InlineDrawing => "Grafika w tekście (wp:inline)",
        ElementCategories.Header => "Nagłówek",
        ElementCategories.Footer => "Stopka",
        ElementCategories.HeaderReference => "Odwołanie do nagłówka",
        ElementCategories.FooterReference => "Odwołanie do stopki",
        ElementCategories.EmbeddedObject => "Obiekt osadzony (OLE)",
        ElementCategories.TextBoxContent => "Treść pola tekstowego",
        ElementCategories.GroupedShape => "Grupa kształtów",
        ElementCategories.SvgImage => "Grafika SVG",
        ElementCategories.Chart => "Wykres",
        ElementCategories.SmartArt => "SmartArt / diagram",
        ElementCategories.Compatibility => $"Compatibility: {element.Name.LocalName}",
        ElementCategories.LegacyVml => $"VML: {element.Name.LocalName}",
        ElementCategories.UnknownNamespace => $"Nieznane rozszerzenie: {element.Name.LocalName}",
        _ => GetQualifiedName(element)
    };

    public string? GetPreview(XElement element, string category)
    {
        if (category is not (ElementCategories.Paragraph or ElementCategories.Run or ElementCategories.Text
            or ElementCategories.Field or ElementCategories.Hyperlink or ElementCategories.Comment
            or ElementCategories.Footnote or ElementCategories.Endnote))
        {
            return null;
        }

        var text = category == ElementCategories.Text
            ? element.Value
            : string.Concat(element.DescendantsAndSelf().Where(IsTextCarrier).Select(child => child.Value));

        var normalized = WhitespaceRegex().Replace(text, " ").Trim();

        if (normalized.Length == 0)
        {
            return null;
        }

        return normalized.Length <= MaxPreviewLength
            ? normalized
            : $"{normalized[..(MaxPreviewLength - 3)]}...";
    }

    public static string GetQualifiedName(XElement element)
    {
        var prefix = element.GetPrefixOfNamespace(element.Name.Namespace);

        return string.IsNullOrWhiteSpace(prefix)
            ? element.Name.LocalName
            : $"{prefix}:{element.Name.LocalName}";
    }

    private static bool IsTextCarrier(XElement element) =>
        OoxmlNamespaces.IsWordprocessing(element.Name.NamespaceName) &&
        element.Name.LocalName is "t" or "delText" or "instrText";

    private static string ClassifyDrawingLayout(string localName) => localName switch
    {
        "anchor" => ElementCategories.AnchoredDrawing,
        "inline" => ElementCategories.InlineDrawing,
        "positionH" or "positionV" or "simplePos" => ElementCategories.DrawingPosition,
        _ when localName.StartsWith("wrap", StringComparison.Ordinal) => ElementCategories.DrawingWrapping,
        _ => ElementCategories.DrawingLayout
    };

    private static string ClassifyDrawingMl(string localName) => localName switch
    {
        "blip" => ElementCategories.ImageReference,
        "xfrm" => ElementCategories.DrawingTransform,
        "srcRect" => ElementCategories.ImageCrop,
        "graphic" or "graphicData" => ElementCategories.DrawingGraphic,
        "grpSp" or "grpSpPr" => ElementCategories.GroupedShape,
        _ => ElementCategories.DrawingMl
    };

    private static string ClassifyWordprocessing(string localName) => localName switch
    {
        "document" => ElementCategories.Document,
        "body" => ElementCategories.Body,
        "p" => ElementCategories.Paragraph,
        "pPr" => ElementCategories.ParagraphProperties,
        "r" => ElementCategories.Run,
        "rPr" => ElementCategories.RunProperties,
        "t" or "delText" => ElementCategories.Text,
        "tbl" => ElementCategories.Table,
        "tblPr" or "tblPrEx" => ElementCategories.TableProperties,
        "tblGrid" or "gridCol" => ElementCategories.TableGrid,
        "tr" => ElementCategories.TableRow,
        "trPr" => ElementCategories.TableRowProperties,
        "tc" => ElementCategories.TableCell,
        "tcPr" => ElementCategories.TableCellProperties,
        "gridSpan" or "vMerge" or "hMerge" => ElementCategories.TableMerge,
        "drawing" => ElementCategories.Drawing,
        "pict" => ElementCategories.LegacyDrawingContainer,
        "object" => ElementCategories.EmbeddedObject,
        "txbxContent" => ElementCategories.TextBoxContent,
        "hyperlink" => ElementCategories.Hyperlink,
        "fldSimple" or "fldChar" or "instrText" => ElementCategories.Field,
        "sdt" or "sdtPr" or "sdtContent" or "sdtEndPr" => ElementCategories.ContentControl,
        "bookmarkStart" or "bookmarkEnd" => ElementCategories.Bookmark,
        "ins" or "del" or "moveFrom" or "moveTo"
            or "moveFromRangeStart" or "moveFromRangeEnd" or "moveToRangeStart" or "moveToRangeEnd"
            or "pPrChange" or "rPrChange" or "tblPrChange" or "tblGridChange" or "trPrChange"
            or "tcPrChange" or "sectPrChange" or "numberingChange" => ElementCategories.Revision,
        "sectPr" => ElementCategories.SectionProperties,
        "headerReference" => ElementCategories.HeaderReference,
        "footerReference" => ElementCategories.FooterReference,
        "hdr" => ElementCategories.Header,
        "ftr" => ElementCategories.Footer,
        "titlePg" => ElementCategories.SectionSetting,
        "evenAndOddHeaders" => ElementCategories.DocumentSetting,
        "numPr" or "numId" or "ilvl" or "num" or "abstractNum" or "lvl" => ElementCategories.Numbering,
        "footnote" or "footnoteReference" => ElementCategories.Footnote,
        "endnote" or "endnoteReference" => ElementCategories.Endnote,
        "comment" or "commentReference" or "commentRangeStart" or "commentRangeEnd" => ElementCategories.Comment,
        "style" or "styles" or "docDefaults" or "basedOn" or "pStyle" or "rStyle" => ElementCategories.Style,
        "settings" => ElementCategories.Settings,
        _ => ElementCategories.WordprocessingElement
    };

    private static string ClassifyForeignElement(XElement element)
    {
        var localName = element.Name.LocalName;
        var namespaceUri = element.Name.NamespaceName;

        if (localName is "sizeRelH" or "sizeRelV" or "pctWidth" or "pctHeight")
        {
            return ElementCategories.DrawingRelativeSize;
        }

        if (localName.Contains("grpSp", StringComparison.OrdinalIgnoreCase) ||
            namespaceUri.Contains("wordprocessingGroup", StringComparison.OrdinalIgnoreCase))
        {
            return ElementCategories.GroupedShape;
        }

        if (localName is "chart" or "chartSpace" ||
            namespaceUri.Contains("/chart", StringComparison.OrdinalIgnoreCase))
        {
            return ElementCategories.Chart;
        }

        if (namespaceUri.Contains("diagram", StringComparison.OrdinalIgnoreCase))
        {
            return ElementCategories.SmartArt;
        }

        if (localName.Equals("svgBlip", StringComparison.OrdinalIgnoreCase) ||
            namespaceUri.Contains("/svg", StringComparison.OrdinalIgnoreCase))
        {
            return ElementCategories.SvgImage;
        }

        if (localName.Contains("oleObject", StringComparison.OrdinalIgnoreCase) ||
            namespaceUri.Contains(":office:office", StringComparison.OrdinalIgnoreCase))
        {
            return ElementCategories.EmbeddedObject;
        }

        if (namespaceUri.Contains("wordprocessingShape", StringComparison.OrdinalIgnoreCase) ||
            namespaceUri.Contains("wordprocessingCanvas", StringComparison.OrdinalIgnoreCase))
        {
            return ElementCategories.DrawingMl;
        }

        return IsKnownOfficeNamespace(namespaceUri)
            ? ElementCategories.XmlElement
            : ElementCategories.UnknownNamespace;
    }

    private static bool IsKnownOfficeNamespace(string namespaceUri) =>
        namespaceUri.Length == 0 ||
        namespaceUri.Contains("openxmlformats.org", StringComparison.OrdinalIgnoreCase) ||
        namespaceUri.Contains("purl.oclc.org/ooxml", StringComparison.OrdinalIgnoreCase) ||
        namespaceUri.Contains("schemas.microsoft.com/office", StringComparison.OrdinalIgnoreCase) ||
        namespaceUri.StartsWith("urn:schemas-microsoft-com:", StringComparison.OrdinalIgnoreCase) ||
        namespaceUri.Contains("purl.org/dc/", StringComparison.OrdinalIgnoreCase) ||
        namespaceUri.Contains("www.w3.org/", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

public static class ElementCategories
{
    public const string Document = "Document";
    public const string Body = "Body";
    public const string Paragraph = "Paragraph";
    public const string ParagraphProperties = "ParagraphProperties";
    public const string Run = "Run";
    public const string RunProperties = "RunProperties";
    public const string Text = "Text";
    public const string Table = "Table";
    public const string TableRow = "TableRow";
    public const string TableRowProperties = "TableRowProperties";
    public const string TableCell = "TableCell";
    public const string TableCellProperties = "TableCellProperties";
    public const string TableProperties = "TableProperties";
    public const string TableGrid = "TableGrid";
    public const string TableMerge = "TableMerge";
    public const string Drawing = "Drawing";
    public const string InlineDrawing = "InlineDrawing";
    public const string AnchoredDrawing = "AnchoredDrawing";
    public const string DrawingPosition = "DrawingPosition";
    public const string DrawingWrapping = "DrawingWrapping";
    public const string DrawingLayout = "DrawingLayout";
    public const string DrawingMl = "DrawingMl";
    public const string DrawingGraphic = "DrawingGraphic";
    public const string DrawingTransform = "DrawingTransform";
    public const string DrawingRelativeSize = "DrawingRelativeSize";
    public const string ImageReference = "ImageReference";
    public const string ImageCrop = "ImageCrop";
    public const string GroupedShape = "GroupedShape";
    public const string SvgImage = "SvgImage";
    public const string Chart = "Chart";
    public const string SmartArt = "SmartArt";
    public const string TextBoxContent = "TextBoxContent";
    public const string EmbeddedObject = "EmbeddedObject";
    public const string LegacyDrawingContainer = "LegacyDrawingContainer";
    public const string LegacyVml = "LegacyVml";
    public const string Compatibility = "Compatibility";
    public const string Hyperlink = "Hyperlink";
    public const string Bookmark = "Bookmark";
    public const string ContentControl = "ContentControl";
    public const string Field = "Field";
    public const string Revision = "Revision";
    public const string SectionProperties = "SectionProperties";
    public const string SectionSetting = "SectionSetting";
    public const string DocumentSetting = "DocumentSetting";
    public const string HeaderReference = "HeaderReference";
    public const string FooterReference = "FooterReference";
    public const string Header = "Header";
    public const string Footer = "Footer";
    public const string Footnote = "Footnote";
    public const string Endnote = "Endnote";
    public const string Comment = "Comment";
    public const string Numbering = "Numbering";
    public const string Style = "Style";
    public const string Settings = "Settings";
    public const string Relationships = "Relationships";
    public const string ContentTypes = "ContentTypes";
    public const string WordprocessingElement = "WordprocessingElement";
    public const string XmlElement = "XmlElement";
    public const string UnknownNamespace = "UnknownNamespace";
}
