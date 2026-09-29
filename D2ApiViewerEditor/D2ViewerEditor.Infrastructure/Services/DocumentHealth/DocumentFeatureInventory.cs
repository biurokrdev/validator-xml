using System.Globalization;
using System.Xml.Linq;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>Stabilne klucze konstrukcji DOCX rozpoznawanych przez inwentarz (te same w rejestrze możliwości i w GUI).</summary>
public static class FeatureKeys
{
    public const string Tables = "tables";
    public const string NestedTables = "nested-tables";
    public const string FloatingTables = "floating-tables";
    public const string Lists = "lists";
    public const string Footnotes = "footnotes";
    public const string Endnotes = "endnotes";
    public const string Comments = "comments";
    public const string TrackedChanges = "tracked-changes";
    public const string ContentControls = "content-controls";
    public const string CustomXmlMarkup = "custom-xml-markup";
    public const string AltChunk = "alt-chunk";
    public const string InlineImages = "inline-images";
    public const string AnchoredImages = "anchored-images";
    public const string WrapTightThrough = "wrap-tight-through";
    public const string ImageRotation = "image-rotation";
    public const string ImageCrop = "image-crop";
    public const string DrawingShapes = "drawing-shapes";
    public const string DrawingGroups = "drawing-groups";
    public const string TextBoxes = "text-boxes";
    public const string VmlShapes = "vml-shapes";
    public const string OleObjects = "ole-objects";
    public const string Charts = "charts";
    public const string SmartArt = "smartart";
    public const string Math = "math";
    public const string MetafileImages = "metafile-images";
    public const string SvgImages = "svg-images";
    public const string ComplexFields = "complex-fields";
    public const string SimpleFields = "simple-fields";
    public const string TableOfContents = "toc";
    public const string Hyperlinks = "hyperlinks";
    public const string Bookmarks = "bookmarks";
    public const string TabStops = "tab-stops";
    public const string MultipleSections = "multiple-sections";
    public const string Columns = "columns";
    public const string HeaderFooter = "header-footer";
    public const string FirstPageHeader = "first-page-header";
    public const string EvenOddHeaders = "even-odd-headers";
    public const string PageNumberFormat = "page-number-format";
    public const string EmbeddedFonts = "embedded-fonts";
    public const string Symbols = "symbols";
    public const string PageBorders = "page-borders";
    public const string LineNumbering = "line-numbering";
    public const string Watermark = "watermark";
    public const string RtlBidi = "rtl-bidi";
    public const string HiddenText = "hidden-text";
    public const string FramesDropCaps = "frames-dropcaps";
    public const string LegacyFormFields = "legacy-form-fields";
    public const string DocumentProtection = "document-protection";
    public const string MailMerge = "mail-merge";
    public const string Glossary = "glossary";
    public const string CustomXmlParts = "custom-xml-parts";
    public const string VbaMacros = "vba-macros";
    public const string DigitalSignature = "digital-signature";
}

/// <summary>Wystąpienia jednej konstrukcji: liczba i lokalizacja pierwszego wystąpienia (część:linia — ścieżka).</summary>
public sealed record FeatureOccurrence(string Key, int Count, string? SampleLocation);

/// <summary>
/// Inwentarz konstrukcji DOCX użytych w dokumencie — liczony na surowym XML (System.Xml.Linq), więc
/// działa też tam, gdzie SDK odmawia. Ten sam skaner biegnie na dokumencie źródłowym i na wyniku
/// round-tripu edytora; różnica liczników mówi, co nasz pipeline zgubił. Liczniki są celowo
/// „konstrukcyjne" (ile w:tbl, ile w:footnoteReference), nie treściowe — treść porównuje
/// narzędzie „Porównanie dokumentów".
/// </summary>
public sealed class DocumentFeatureInventory
{
    private const string WpsNamespace = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const string WpgNamespace = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    private const string ChartNamespace = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string DiagramNamespace = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string MathNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private const string SvgNamespace = "http://schemas.microsoft.com/office/drawing/2016/SVG/main";

    private static readonly XNamespace Mc = OoxmlNamespaces.MarkupCompatibility;

    private static readonly HashSet<string> RevisionElements = new(StringComparer.Ordinal)
    {
        "ins", "del", "moveFrom", "moveTo", "rPrChange", "pPrChange", "tblPrChange", "trPrChange",
        "tcPrChange", "sectPrChange", "numberingChange", "cellIns", "cellDel", "cellMerge"
    };

    private static readonly HashSet<string> VmlShapeElements = new(StringComparer.Ordinal)
    {
        "shape", "group", "rect", "oval", "line", "roundrect", "polyline", "arc", "curve"
    };

    private static readonly string[] StoryContentTypeFragments =
    [
        "header+xml", "footer+xml", "footnotes+xml", "endnotes+xml", "comments+xml", "glossary+xml"
    ];

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _samples = new(StringComparer.Ordinal);

    private DocumentFeatureInventory()
    {
    }

    public static DocumentFeatureInventory Empty { get; } = new();

    public IReadOnlyCollection<string> Keys => _counts.Keys;

    public int Count(string key) => _counts.GetValueOrDefault(key);

    public FeatureOccurrence? Get(string key) =>
        _counts.TryGetValue(key, out var count) ? new FeatureOccurrence(key, count, _samples.GetValueOrDefault(key)) : null;

    public static DocumentFeatureInventory Scan(HealthPackageContext context, IReadOnlyDictionary<string, XDocument> parts)
    {
        var inventory = new DocumentFeatureInventory();

        foreach (var (path, document) in StoryParts(context, parts))
        {
            if (document.Root is not null)
            {
                inventory.ScanStory(context, path, document.Root);
            }
        }

        inventory.ScanPackage(context, parts);

        return inventory;
    }

    // ── Części treści ─────────────────────────────────────────────────────────

    private void ScanStory(HealthPackageContext context, string path, XElement root)
    {
        var isHeaderOrFooter = root.Name.LocalName is "hdr" or "ftr";

        foreach (var element in root.Descendants())
        {
            var ns = element.Name.NamespaceName;
            var name = element.Name.LocalName;

            if (OoxmlNamespaces.IsWordprocessing(ns))
            {
                ScanWordprocessingElement(path, element, name);
            }
            else if (OoxmlNamespaces.IsWordprocessingDrawing(ns))
            {
                ScanDrawingContainer(path, element, name);
            }
            else if (OoxmlNamespaces.IsDrawingMain(ns))
            {
                ScanDrawingMain(path, element, name);
            }
            else if (ns == WpsNamespace && name == "wsp")
            {
                Hit(FeatureKeys.DrawingShapes, path, element);
            }
            else if (ns == WpgNamespace && name == "wgp")
            {
                Hit(FeatureKeys.DrawingGroups, path, element);
            }
            else if (ns == MathNamespace && name == "oMath")
            {
                Hit(FeatureKeys.Math, path, element);
            }
            else if (ns == SvgNamespace && name == "svgBlip")
            {
                Hit(FeatureKeys.SvgImages, path, element);
            }
            else if (ns == OoxmlNamespaces.Vml && VmlShapeElements.Contains(name))
            {
                if (element.Ancestors().Any(ancestor => ancestor.Name == Mc + "Fallback"))
                {
                    continue;
                }

                Hit(FeatureKeys.VmlShapes, path, element);

                var id = (string?)element.Attribute("id");

                if (isHeaderOrFooter && id is not null && id.Contains("WaterMark", StringComparison.OrdinalIgnoreCase))
                {
                    Hit(FeatureKeys.Watermark, path, element);
                }
            }
        }
    }

    private void ScanWordprocessingElement(string path, XElement element, string name)
    {
        switch (name)
        {
            case "tbl":
                Hit(FeatureKeys.Tables, path, element);

                if (element.Ancestors().Any(ancestor => IsW(ancestor, "tc")))
                {
                    Hit(FeatureKeys.NestedTables, path, element);
                }

                break;
            case "tblpPr":
                Hit(FeatureKeys.FloatingTables, path, element);
                break;
            case "numPr":
                Hit(FeatureKeys.Lists, path, element);
                break;
            case "footnoteReference":
                Hit(FeatureKeys.Footnotes, path, element);
                break;
            case "endnoteReference":
                Hit(FeatureKeys.Endnotes, path, element);
                break;
            case "commentReference":
                Hit(FeatureKeys.Comments, path, element);
                break;
            case "sdt":
                Hit(FeatureKeys.ContentControls, path, element);
                break;
            case "customXml":
                Hit(FeatureKeys.CustomXmlMarkup, path, element);
                break;
            case "altChunk":
                Hit(FeatureKeys.AltChunk, path, element);
                break;
            case "fldChar":
                if (WAttr(element, "fldCharType") == "begin")
                {
                    Hit(FeatureKeys.ComplexFields, path, element);
                }

                break;
            case "instrText":
                if (IsTocInstruction(element.Value))
                {
                    Hit(FeatureKeys.TableOfContents, path, element);
                }

                break;
            case "fldSimple":
                Hit(FeatureKeys.SimpleFields, path, element);

                if (IsTocInstruction(WAttr(element, "instr")))
                {
                    Hit(FeatureKeys.TableOfContents, path, element);
                }

                break;
            case "hyperlink":
                Hit(FeatureKeys.Hyperlinks, path, element);
                break;
            case "bookmarkStart":
                if (WAttr(element, "name") is not "_GoBack")
                {
                    Hit(FeatureKeys.Bookmarks, path, element);
                }

                break;
            case "tab" when element.Parent is { } parent && IsW(parent, "r"):
            case "ptab":
                Hit(FeatureKeys.TabStops, path, element);
                break;
            case "sectPr":
                Hit(FeatureKeys.MultipleSections, path, element);
                break;
            case "cols":
                if (ParseInt(WAttr(element, "num")) > 1)
                {
                    Hit(FeatureKeys.Columns, path, element);
                }

                break;
            case "titlePg":
                Hit(FeatureKeys.FirstPageHeader, path, element);
                break;
            case "pgNumType":
                Hit(FeatureKeys.PageNumberFormat, path, element);
                break;
            case "pgBorders":
                Hit(FeatureKeys.PageBorders, path, element);
                break;
            case "lnNumType":
                Hit(FeatureKeys.LineNumbering, path, element);
                break;
            case "bidi":
            case "rtl":
                Hit(FeatureKeys.RtlBidi, path, element);
                break;
            case "vanish":
                Hit(FeatureKeys.HiddenText, path, element);
                break;
            case "framePr":
                Hit(FeatureKeys.FramesDropCaps, path, element);
                break;
            case "ffData":
                Hit(FeatureKeys.LegacyFormFields, path, element);
                break;
            case "sym":
                Hit(FeatureKeys.Symbols, path, element);
                break;
            case "object":
                Hit(FeatureKeys.OleObjects, path, element);
                break;
            case "txbxContent":
                Hit(FeatureKeys.TextBoxes, path, element);
                break;
            default:
                if (RevisionElements.Contains(name))
                {
                    Hit(FeatureKeys.TrackedChanges, path, element);
                }

                break;
        }
    }

    private void ScanDrawingContainer(string path, XElement element, string name)
    {
        switch (name)
        {
            case "inline" when ContainsPicture(element):
                Hit(FeatureKeys.InlineImages, path, element);
                break;
            case "anchor" when ContainsPicture(element):
                Hit(FeatureKeys.AnchoredImages, path, element);
                break;
            case "wrapTight":
            case "wrapThrough":
                Hit(FeatureKeys.WrapTightThrough, path, element);
                break;
        }
    }

    private void ScanDrawingMain(string path, XElement element, string name)
    {
        switch (name)
        {
            case "graphicData":
                var uri = (string?)element.Attribute("uri") ?? string.Empty;

                if (uri.Equals(ChartNamespace, StringComparison.OrdinalIgnoreCase))
                {
                    Hit(FeatureKeys.Charts, path, element);
                }
                else if (uri.Equals(DiagramNamespace, StringComparison.OrdinalIgnoreCase))
                {
                    Hit(FeatureKeys.SmartArt, path, element);
                }

                break;
            case "srcRect":
                if (element.Attributes().Any(attribute =>
                        attribute.Name.LocalName is "l" or "t" or "r" or "b" && ParseInt(attribute.Value) is > 0))
                {
                    Hit(FeatureKeys.ImageCrop, path, element);
                }

                break;
            case "xfrm":
                if (ParseInt((string?)element.Attribute("rot")) is not null and not 0 &&
                    element.Ancestors().Any(ancestor => OoxmlNamespaces.IsDrawingPicture(ancestor.Name.NamespaceName)))
                {
                    Hit(FeatureKeys.ImageRotation, path, element);
                }

                break;
        }
    }

    // ── Pakiet ────────────────────────────────────────────────────────────────

    private void ScanPackage(HealthPackageContext context, IReadOnlyDictionary<string, XDocument> parts)
    {
        var headerFooterParts = context.PartsOfType("header+xml").Count() + context.PartsOfType("footer+xml").Count();

        if (headerFooterParts > 0)
        {
            Hit(FeatureKeys.HeaderFooter, context.PartsOfType("header+xml").Concat(context.PartsOfType("footer+xml")).First(), null, headerFooterParts);
        }

        if (Count(FeatureKeys.Comments) == 0 && context.PartsOfType("comments+xml").FirstOrDefault() is { } commentsPath &&
            parts.TryGetValue(commentsPath, out var comments) && comments.Root is not null)
        {
            var count = comments.Root.Elements().Count(element => IsW(element, "comment"));

            if (count > 0)
            {
                Hit(FeatureKeys.Comments, commentsPath, null, count);
            }
        }

        if (context.PartsOfType("glossary+xml").FirstOrDefault() is { } glossaryPath &&
            parts.TryGetValue(glossaryPath, out var glossary) && glossary.Root is not null)
        {
            var docParts = glossary.Root.Descendants().Count(element => IsW(element, "docPart"));
            Hit(FeatureKeys.Glossary, glossaryPath, null, Math.Max(1, docParts));
        }

        foreach (var (path, contentType) in context.Opc.ContentTypes)
        {
            if (contentType.Contains("x-emf", StringComparison.OrdinalIgnoreCase) ||
                contentType.Contains("x-wmf", StringComparison.OrdinalIgnoreCase) ||
                contentType.Equals("image/emf", StringComparison.OrdinalIgnoreCase) ||
                contentType.Equals("image/wmf", StringComparison.OrdinalIgnoreCase))
            {
                Hit(FeatureKeys.MetafileImages, path, null);
            }
            else if (contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) && Count(FeatureKeys.SvgImages) == 0)
            {
                Hit(FeatureKeys.SvgImages, path, null);
            }
        }

        foreach (var entry in context.Package.Entries)
        {
            if (entry.Path.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase) &&
                entry.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                !entry.Path.Contains("/_rels/", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(entry.Path).StartsWith("itemProps", StringComparison.OrdinalIgnoreCase))
            {
                Hit(FeatureKeys.CustomXmlParts, entry.Path, null);
            }
            else if (entry.Path.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
            {
                Hit(FeatureKeys.VbaMacros, entry.Path, null);
            }
            else if (entry.Path.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase) && Count(FeatureKeys.DigitalSignature) == 0)
            {
                Hit(FeatureKeys.DigitalSignature, entry.Path, null);
            }
        }

        ScanSettings(context, parts);
        ScanFontTable(context, parts);
    }

    private void ScanSettings(HealthPackageContext context, IReadOnlyDictionary<string, XDocument> parts)
    {
        var target = context.Opc.Relationships.FindTargetByType(context.MainDocumentPartPath, "settings");

        if (target is null || !parts.TryGetValue(target, out var document) || document.Root is null)
        {
            return;
        }

        foreach (var element in document.Root.Elements())
        {
            if (IsW(element, "evenAndOddHeaders") && IsTrue(WAttr(element, "val")))
            {
                Hit(FeatureKeys.EvenOddHeaders, target, element);
            }
            else if (IsW(element, "mailMerge"))
            {
                Hit(FeatureKeys.MailMerge, target, element);
            }
            else if ((IsW(element, "documentProtection") && IsTrue(WAttr(element, "enforcement"))) || IsW(element, "writeProtection"))
            {
                Hit(FeatureKeys.DocumentProtection, target, element);
            }
        }
    }

    private void ScanFontTable(HealthPackageContext context, IReadOnlyDictionary<string, XDocument> parts)
    {
        var target = context.Opc.Relationships.FindTargetByType(context.MainDocumentPartPath, "fontTable");

        if (target is null || !parts.TryGetValue(target, out var document) || document.Root is null)
        {
            return;
        }

        foreach (var embed in document.Root.Descendants().Where(element =>
                     OoxmlNamespaces.IsWordprocessing(element.Name.NamespaceName) &&
                     element.Name.LocalName is "embedRegular" or "embedBold" or "embedItalic" or "embedBoldItalic"))
        {
            Hit(FeatureKeys.EmbeddedFonts, target, embed);
        }
    }

    // ── Pomocnicze ────────────────────────────────────────────────────────────

    private static IEnumerable<(string Path, XDocument Document)> StoryParts(
        HealthPackageContext context,
        IReadOnlyDictionary<string, XDocument> parts)
    {
        if (parts.TryGetValue(context.MainDocumentPartPath, out var main))
        {
            yield return (context.MainDocumentPartPath, main);
        }

        foreach (var fragment in StoryContentTypeFragments)
        {
            foreach (var path in context.PartsOfType(fragment).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (parts.TryGetValue(path, out var document) && !context.IsMainPart(path))
                {
                    yield return (path, document);
                }
            }
        }
    }

    private void Hit(string key, string path, XElement? element, int count = 1)
    {
        _counts[key] = _counts.GetValueOrDefault(key) + count;

        if (!_samples.ContainsKey(key))
        {
            _samples[key] = element is null ? path : HealthLocations.Of(path, element);
        }
    }

    private static bool ContainsPicture(XElement drawing) =>
        drawing.Descendants().Any(element =>
            OoxmlNamespaces.IsDrawingPicture(element.Name.NamespaceName) && element.Name.LocalName == "pic");

    private static bool IsTocInstruction(string? instruction) =>
        instruction is not null && instruction.TrimStart().StartsWith("TOC", StringComparison.Ordinal) &&
        (instruction.TrimStart().Length == 3 || char.IsWhiteSpace(instruction.TrimStart()[3]) || instruction.TrimStart()[3] == '\\');

    private static bool IsW(XElement element, string localName) =>
        element.Name.LocalName == localName && OoxmlNamespaces.IsWordprocessing(element.Name.NamespaceName);

    private static string? WAttr(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName == localName && OoxmlNamespaces.IsWordprocessing(attribute.Name.NamespaceName))?.Value;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool IsTrue(string? value) =>
        value is null || value is "1" or "true" or "on";
}
