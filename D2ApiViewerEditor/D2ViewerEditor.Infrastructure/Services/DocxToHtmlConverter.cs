using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Conversion;
using D2ViewerEditor.Infrastructure.DocxModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using A = DocumentFormat.OpenXml.Drawing;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;
using Wpg = DocumentFormat.OpenXml.Office2010.Word.DrawingGroup;
using Wps = DocumentFormat.OpenXml.Office2010.Word.DrawingShape;
using DomainFootnote = D2ViewerEditor.Domain.Models.Footnote;
using WpFootnote = DocumentFormat.OpenXml.Wordprocessing.Footnote;
using DomainEndnote = D2ViewerEditor.Domain.Models.Endnote;
using WpEndnote = DocumentFormat.OpenXml.Wordprocessing.Endnote;

namespace D2ViewerEditor.Infrastructure.Services;

internal sealed record PreservedRelEntry(string ct, string data);

public class DocxToHtmlConverter : IDocxToHtmlConverter
{
    private readonly Dictionary<string, DocumentImage> _images = new();
    private readonly Dictionary<string, string> _styles = new();
    private readonly Dictionary<string, Style> _rawStyles = new();
    private int _defaultTabStopTwips = 708;
    private readonly List<DocumentStyle> _documentStyles = new();
    private readonly Dictionary<int, string> _picBulletDataUris = new();
    private int _imageCounter = 0;
    private NumberingDefinitionsPart? _numberingPart;
    private ThemePart? _themePart;
    private string? _defaultFontFamily;
    private double? _defaultFontSizePt;
    private string? _defaultSpacingBeforeTw;
    private string? _defaultSpacingAfterTw;
    private string? _defaultSpacingLine;
    private string? _defaultSpacingLineRule;
    private string? _defaultParagraphStyleId;
    private ColumnLayout? _baseSectionColumns;

    private long? _availableContentWidthTwips;
    private string _defaultParagraphSpacingCss = "";
    private string? _tableParagraphDefaultCss;
    private string? _themeMajorLatin;
    private string? _themeMinorLatin;
    private string? _themeMajorEastAsia;
    private string? _themeMinorEastAsia;
    private string? _themeMajorComplexScript;
    private string? _themeMinorComplexScript;
    private bool _flexTabs;

    private long? _pageWidthTwips;
    private long? _pageHeightTwips;
    private long _marginLeftTwips;
    private long _marginTopTwips;
    private long _marginRightTwips;
    private long _marginBottomTwips;
    private long _headerDistanceTwips;
    private long _footerDistanceTwips;

    private enum HfBand { None, Header, Footer }
    private HfBand _anchorBand = HfBand.None;

    private readonly Dictionary<(int abstractNumId, int level), int> _listCounters = new();
    private readonly HashSet<int> _appliedStartOverrides = new();

    private readonly DocumentDefaultsOptions _defaults;
    private readonly IGraphicConversionService _graphics;
    private readonly ILogger<DocxToHtmlConverter> _log;

    public DocxToHtmlConverter()
    {
        _defaults = new DocumentDefaultsOptions();
        _graphics = new GraphicConversionService();
        _log = NullLogger<DocxToHtmlConverter>.Instance;
    }

    public DocxToHtmlConverter(IOptions<DocumentDefaultsOptions> defaults, IGraphicConversionService? graphics = null,
        ILogger<DocxToHtmlConverter>? logger = null)
    {
        _defaults = defaults?.Value ?? new DocumentDefaultsOptions();
        _graphics = graphics ?? new GraphicConversionService();
        _log = logger ?? NullLogger<DocxToHtmlConverter>.Instance;
    }

    private (string dataUrl, bool isBlank)? WebGraphicForLegacy(byte[] bytes, string? contentType, long widthEmu, long heightEmu, string? sourcePath = null)
    {
        var kind = _graphics.Detect(bytes, contentType);
        if (kind is not (GraphicKind.Emf or GraphicKind.Wmf or GraphicKind.Tiff or GraphicKind.Unknown))
            return null;
        var result = _graphics.ConvertForEditor(new GraphicSource
        {
            Data = bytes,
            ContentType = contentType,
            SourcePath = sourcePath,
            Origin = GraphicOrigin.LegacyDocxPart,
            TargetWidthEmu = widthEmu > 0 ? widthEmu : null,
            TargetHeightEmu = heightEmu > 0 ? heightEmu : null
        });
        var diag = result.Diagnostics;
        var isBlank = result.Web?.IsBlankFallback == true;
        var failed = diag.Status is GraphicConversionStatus.Fallback
            or GraphicConversionStatus.Unsupported or GraphicConversionStatus.Rejected;

        var suspicious = diag.Warnings.Any(w => w.Contains("PODEJRZANE", StringComparison.Ordinal));
        if (failed || isBlank || diag.Warnings.Count > 0 || diag.LostProperties.Count > 0)
        {
            var level = failed || isBlank || suspicious
                ? Microsoft.Extensions.Logging.LogLevel.Warning
                : Microsoft.Extensions.Logging.LogLevel.Debug;
            _log.Log(level,
                "Grafika legacy: part={SourcePath} declaredType={ContentType} detected={Kind} size={Size}B " +
                "wymiaryEMU={WEmu}x{HEmu} status={Status} blank={Blank} strategie=[{Strategies}] powód={Reason} " +
                "ostrzeżenia=[{Warnings}] straty=[{Lost}]",
                sourcePath, contentType, diag.InputKind, bytes.Length,
                widthEmu, heightEmu, diag.Status, isBlank,
                string.Join(",", diag.AttemptedStrategies), diag.FailureReason,
                string.Join(" | ", diag.Warnings), string.Join(" | ", diag.LostProperties));
        }
        return result.Web != null ? (result.Web.ToDataUrl(), result.Web.IsBlankFallback) : null;
    }


    public DocumentContent Convert(Stream docxStream)
    {
        _images.Clear();
        _styles.Clear();
        _rawStyles.Clear();
        _documentStyles.Clear();
        _picBulletDataUris.Clear();
        _listCounters.Clear();
        _appliedStartOverrides.Clear();
        _imageCounter = 0;
        _numberingPart = null;
        _themePart = null;
        _defaultFontFamily = null;
        _defaultFontSizePt = null;
        _defaultSpacingBeforeTw = _defaultSpacingAfterTw = null;
        _defaultSpacingLine = _defaultSpacingLineRule = null;
        _defaultParagraphStyleId = null;
        _defaultParagraphSpacingCss = "";
        _tableParagraphDefaultCss = null;
        _themeMajorLatin = _themeMinorLatin = null;
        _themeMajorEastAsia = _themeMinorEastAsia = null;
        _themeMajorComplexScript = _themeMinorComplexScript = null;
        _pageWidthTwips = _pageHeightTwips = null;
        _marginLeftTwips = _marginTopTwips = _marginRightTwips = _marginBottomTwips = 0;
        _pendingTextBoxes.Clear();
        _openFieldFrames.Clear();
        _footnoteDisplayNumbers.Clear();
        _footnoteRefOrder.Clear();
        _endnoteDisplayNumbers.Clear();
        _endnoteRefOrder.Clear();

        using var document = WordprocessingDocument.Open(docxStream, false);

        AcceptTrackedRevisions(document);

        _numberingPart = document.MainDocumentPart?.NumberingDefinitionsPart;
        _themePart = document.MainDocumentPart?.ThemePart;
        LoadThemeFonts();
        LoadNumberingPictureBullets();
        LoadPageGeometry(document);
        var defaultTab = document.MainDocumentPart?.DocumentSettingsPart?.Settings?
            .GetFirstChild<DefaultTabStop>()?.Val?.Value;
        _defaultTabStopTwips = defaultTab is > 0 ? defaultTab.Value : 708;
        
        var stylesLoaded = ExtractDocumentStyles(document);
        
        var content = new DocumentContent
        {
            Metadata = ExtractMetadata(document),
            Html = ConvertBodyToHtml(document),
            Images = _images.Values.ToList(),
            Styles = stylesLoaded.Count > 0 ? stylesLoaded : DefaultWordStyles.GetDefaultStyles(),
            Header = ExtractHeader(document),
            Footer = ExtractFooter(document),
            Margins = ExtractPageMargins(document),
            PageSize = ExtractPageSize(document),
            SectionHeadersFooters = ExtractSectionHeadersFooters(document)
        };

        content.Footnotes = ExtractFootnotes(document);
        content.Endnotes = ExtractEndnotes(document);

        content.FootnoteNumberFormat = ReadNoteNumberFormat(document, endnote: false);
        content.EndnoteNumberFormat = ReadNoteNumberFormat(document, endnote: true);

        content.Columns = _baseSectionColumns;

        content.IsReadOnlyProtected =
            (document.MainDocumentPart != null && HasEnforcedEditProtection(document.MainDocumentPart))
            || IsMarkedAsFinal(document);

        return content;
    }

    private static List<SectionProperties> GetSectionPropertiesInDocumentOrder(Body? body)
    {
        var result = new List<SectionProperties>();
        if (body == null) return result;

        result.AddRange(body.Descendants<SectionProperties>()
            .Where(sp => sp.Parent is ParagraphProperties));

        var bodyLevel = body.Elements<SectionProperties>().FirstOrDefault();
        if (bodyLevel != null) result.Add(bodyLevel);
        return result;
    }

    private static SectionProperties? GetFirstSectionProperties(WordprocessingDocument document)
        => GetSectionPropertiesInDocumentOrder(document.MainDocumentPart?.Document?.Body).FirstOrDefault();

    private static string GetSectionBreakType(SectionProperties sectPr)
    {
        var type = sectPr.GetFirstChild<SectionType>()?.Val?.Value;
        if (type == null) return "nextPage";
        if (type == SectionMarkValues.Continuous) return "continuous";
        if (type == SectionMarkValues.OddPage) return "oddPage";
        if (type == SectionMarkValues.EvenPage) return "evenPage";
        if (type == SectionMarkValues.NextColumn) return "nextColumn";
        return "nextPage";
    }

    private static string BuildSectionBreakMarkerHtml(SectionProperties endedSection, List<SectionProperties> orderedSections)
    {
        var idx = orderedSections.IndexOf(endedSection);
        if (idx < 0 || idx + 1 >= orderedSections.Count) return string.Empty;

        var next = orderedSections[idx + 1];
        var breakType = GetSectionBreakType(next);
        var page = SectionPropertiesReader.ReadPageSettings(next);
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        if (breakType is "nextPage" or "oddPage" or "evenPage")
            sb.Append("<div class=\"page-break\"></div>");

        sb.Append("<div class=\"docx-section-break\" data-break-type=\"").Append(breakType).Append('"');

        if (page.PageWidthTwips is { } w && page.PageHeightTwips is { } h)
        {
            sb.Append(string.Format(inv, " data-page-width-cm=\"{0:0.##}\"", OoxmlUnits.TwipsToCm(w)));
            sb.Append(string.Format(inv, " data-page-height-cm=\"{0:0.##}\"", OoxmlUnits.TwipsToCm(h)));
            sb.Append(page.Orientation == PageOrientation.Landscape
                ? " data-orientation=\"landscape\""
                : " data-orientation=\"portrait\"");
        }

        if (page.HasPageMargin)
        {
            if (page.TopMarginTwips is { } t)
                sb.Append(string.Format(inv, " data-margin-top-cm=\"{0:0.##}\"", OoxmlUnits.TwipsToCm(Math.Abs(t))));
            if (page.BottomMarginTwips is { } b)
                sb.Append(string.Format(inv, " data-margin-bottom-cm=\"{0:0.##}\"", OoxmlUnits.TwipsToCm(Math.Abs(b))));
            if (page.LeftMarginTwips is { } l)
                sb.Append(string.Format(inv, " data-margin-left-cm=\"{0:0.##}\"", OoxmlUnits.TwipsToCm(l)));
            if (page.RightMarginTwips is { } r)
                sb.Append(string.Format(inv, " data-margin-right-cm=\"{0:0.##}\"", OoxmlUnits.TwipsToCm(r)));
            if (page.HeaderDistanceTwips is { } hd)
                sb.Append(string.Format(inv, " data-header-distance-cm=\"{0:0.##}\"", OoxmlUnits.TwipsToCm(hd)));
            if (page.FooterDistanceTwips is { } fd)
                sb.Append(string.Format(inv, " data-footer-distance-cm=\"{0:0.##}\"", OoxmlUnits.TwipsToCm(fd)));
        }

        AppendColumnDataAttributes(sb, page.Columns);

        sb.Append("></div>");
        return sb.ToString();
    }

    private static void AppendColumnDataAttributes(StringBuilder sb, ColumnLayout? cols)
    {
        if (cols == null || cols.Count <= 1) return;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append(string.Format(inv, " data-col-count=\"{0}\"", cols.Count));
        sb.Append(string.Format(inv, " data-col-space-tw=\"{0}\"", cols.SpaceTwips));
        sb.Append(" data-col-equal=\"").Append(cols.EqualWidth ? "1" : "0").Append('"');
        if (cols.Separator) sb.Append(" data-col-sep=\"1\"");

        if (!cols.EqualWidth && cols.Columns is { Count: > 0 } list)
        {
            sb.Append(" data-col-widths-tw=\"")
              .Append(string.Join(",", list.Select(c => c.WidthTwips.ToString(inv)))).Append('"');
            sb.Append(" data-col-spaces-tw=\"")
              .Append(string.Join(",", list.Select(c => c.SpaceTwips.ToString(inv)))).Append('"');
        }
    }

    private static Domain.Models.PageSize? ExtractPageSize(WordprocessingDocument document)
    {
        var sectionProps = GetFirstSectionProperties(document);
        var page = SectionPropertiesReader.ReadPageSettings(sectionProps);
        if (page.PageWidthTwips is not { } width || page.PageHeightTwips is not { } height)
            return null;

        return new Domain.Models.PageSize
        {
            WidthCm = Math.Round(OoxmlUnits.TwipsToCm(width), 2),
            HeightCm = Math.Round(OoxmlUnits.TwipsToCm(height), 2),
            Orientation = page.Orientation == PageOrientation.Landscape ? "landscape" : "portrait"
        };
    }

    private static PageMargins? ExtractPageMargins(WordprocessingDocument document)
    {
        var sectionProps = GetFirstSectionProperties(document);
        var page = SectionPropertiesReader.ReadPageSettings(sectionProps);
        if (!page.HasPageMargin) return null;

        return new PageMargins
        {
            Top    = page.TopMarginTwips    is { } t ? Math.Round(OoxmlUnits.TwipsToCm(Math.Abs(t)), 2) : 2.5,
            Bottom = page.BottomMarginTwips is { } b ? Math.Round(OoxmlUnits.TwipsToCm(Math.Abs(b)), 2) : 2.5,
            Left   = page.LeftMarginTwips   is { } l ? Math.Round(OoxmlUnits.TwipsToCm(l),            2) : 2.5,
            Right  = page.RightMarginTwips  is { } r ? Math.Round(OoxmlUnits.TwipsToCm(r),            2) : 2.5,
        };
    }

    private void LoadPageGeometry(WordprocessingDocument document)
    {
        var page = SectionPropertiesReader.ReadPageSettings(GetFirstSectionProperties(document));
        _pageWidthTwips = page.PageWidthTwips;
        _pageHeightTwips = page.PageHeightTwips;
        _marginLeftTwips = page.LeftMarginTwips is { } l ? l : 1440;
        _marginRightTwips = page.RightMarginTwips is { } r ? r : 1440;
        _marginTopTwips = page.TopMarginTwips is { } t ? Math.Abs(t) : 1440;
        _marginBottomTwips = page.BottomMarginTwips is { } b ? Math.Abs(b) : 1440;
        _headerDistanceTwips = page.HeaderDistanceTwips ?? 720;
        _footerDistanceTwips = page.FooterDistanceTwips ?? 720;
    }

    private (long xEmu, long yEmu) ResolveAnchorPosition(
        DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor anchor, long widthEmu, long heightEmu)
    {
        long pageW = _pageWidthTwips is { } pw ? OoxmlUnits.TwipsToEmu(pw) : 0;
        long pageH = _pageHeightTwips is { } ph ? OoxmlUnits.TwipsToEmu(ph) : 0;
        long mLeft = OoxmlUnits.TwipsToEmu(_marginLeftTwips);
        long mTop = OoxmlUnits.TwipsToEmu(_marginTopTwips);
        long mRight = OoxmlUnits.TwipsToEmu(_marginRightTwips);
        long mBottom = OoxmlUnits.TwipsToEmu(_marginBottomTwips);

        var posH = anchor.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.HorizontalPosition>();
        var posV = anchor.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalPosition>();

        long xPage = ResolveAxis(
            offsetText: posH?.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.PositionOffset>()?.Text,
            alignText: posH?.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.HorizontalAlignment>()?.Text,
            relFrom: posH?.RelativeFrom?.InnerText,
            objectSize: widthEmu, pageSize: pageW, marginStart: mLeft, marginEnd: mRight, horizontal: true);

        long? bandParagraphBase = _anchorBand switch
        {
            HfBand.Header => OoxmlUnits.TwipsToEmu(_headerDistanceTwips),
            HfBand.Footer when pageH > 0 => pageH - mBottom,
            _ => null,
        };
        long yPage = ResolveAxis(
            offsetText: posV?.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.PositionOffset>()?.Text,
            alignText: posV?.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalAlignment>()?.Text,
            relFrom: posV?.RelativeFrom?.InnerText,
            objectSize: heightEmu, pageSize: pageH, marginStart: mTop, marginEnd: mBottom, horizontal: false,
            bandParagraphBase: bandParagraphBase);

        return (xPage, yPage - mTop);
    }

    private static long ResolveAxis(string? offsetText, string? alignText, string? relFrom,
        long objectSize, long pageSize, long marginStart, long marginEnd, bool horizontal,
        long? bandParagraphBase = null)
    {
        long baseStart;
        long extent;
        switch (relFrom)
        {
            case "leftMargin":
            case "topMargin":
            case "insideMargin":
                baseStart = 0;
                extent = marginStart;
                break;
            case "rightMargin":
            case "bottomMargin":
            case "outsideMargin":
                baseStart = pageSize > 0 ? pageSize - marginEnd : marginStart;
                extent = marginEnd;
                break;
            case "page":
                baseStart = 0;
                extent = pageSize;
                break;
            default:
                if (!horizontal && bandParagraphBase is { } bandBase
                    && relFrom is null or "paragraph" or "line")
                {
                    baseStart = bandBase;
                    extent = 0;
                    break;
                }
                baseStart = marginStart;
                extent = pageSize > 0 ? pageSize - marginStart - marginEnd : 0;
                break;
        }

        if (long.TryParse(offsetText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var offset))
            return baseStart + offset;

        if (extent > 0 && !string.IsNullOrEmpty(alignText))
        {
            switch (alignText)
            {
                case "right":
                case "bottom":
                case "outside":
                    return baseStart + Math.Max(0, extent - objectSize);
                case "center":
                    return baseStart + Math.Max(0, (extent - objectSize) / 2);
                case "left":
                case "top":
                case "inside":
                default:
                    return baseStart;
            }
        }

        return baseStart;
    }

    private static double ComputeBandHeightCm(int? marginTwips, int? distanceTwips)
    {
        var margin = marginTwips is { } m ? Math.Abs(m) : 0;
        var distance = distanceTwips ?? 720;
        var band = margin > distance ? margin - distance : margin;
        return OoxmlUnits.TwipsToCm(band);
    }

    private HeaderFooterContent? ExtractHeader(WordprocessingDocument document)
    {
        var mainPart = document.MainDocumentPart;
        if (mainPart == null) return null;

        var sections = GetSectionPropertiesInDocumentOrder(mainPart.Document?.Body);

        var sectionProps = sections.FirstOrDefault(s =>
            ResolveHeaderPart(mainPart, s, HeaderFooterValues.Default) != null) ?? sections.FirstOrDefault();

        var headerPart = ResolveHeaderPart(mainPart, sectionProps, HeaderFooterValues.Default);
        if (headerPart == null && !SectionDeclaresAnyHeaderReference(sectionProps))
            headerPart = mainPart.HeaderParts.FirstOrDefault();

        var html = headerPart?.Header != null ? ConvertHeaderPartToHtml(headerPart, document) : null;
        if (string.IsNullOrWhiteSpace(html)) html = null;

        string? firstPageHtml = null;
        var differentFirstPage = false;
        if (HasTitlePage(sectionProps))
        {
            differentFirstPage = true;
            var firstPart = ResolveHeaderPart(mainPart, sectionProps, HeaderFooterValues.First);
            var fph = firstPart?.Header != null ? ConvertHeaderPartToHtml(firstPart, document) : null;
            firstPageHtml = string.IsNullOrWhiteSpace(fph) ? string.Empty : fph;
        }

        string? evenHtml = null;
        var differentOddEven = false;
        if (HasEvenAndOddHeaders(mainPart))
        {
            differentOddEven = true;
            var evenPart = ResolveHeaderPart(mainPart, sectionProps, HeaderFooterValues.Even);
            var eh = evenPart?.Header != null ? ConvertHeaderPartToHtml(evenPart, document) : null;
            evenHtml = string.IsNullOrWhiteSpace(eh) ? string.Empty : eh;
        }

        if (html == null && string.IsNullOrEmpty(firstPageHtml) && string.IsNullOrEmpty(evenHtml)) return null;

        var page = SectionPropertiesReader.ReadPageSettings(sections.FirstOrDefault());
        double headerHeight = page.HasPageMargin
            ? ComputeBandHeightCm(page.TopMarginTwips, page.HeaderDistanceTwips)
            : 1.5;

        return new HeaderFooterContent
        {
            Html = html ?? string.Empty,
            Height = Math.Max(0.8, Math.Min(8, headerHeight)),
            DifferentFirstPage = differentFirstPage,
            FirstPageHtml = firstPageHtml,
            DifferentOddEven = differentOddEven,
            EvenHtml = evenHtml
        };
    }

    private HeaderFooterContent? ExtractFooter(WordprocessingDocument document)
    {
        var mainPart = document.MainDocumentPart;
        if (mainPart == null) return null;

        var sections = GetSectionPropertiesInDocumentOrder(mainPart.Document?.Body);

        var sectionProps = sections.FirstOrDefault(s =>
            ResolveFooterPart(mainPart, s, HeaderFooterValues.Default) != null) ?? sections.FirstOrDefault();

        var footerPart = ResolveFooterPart(mainPart, sectionProps, HeaderFooterValues.Default);
        if (footerPart == null && !SectionDeclaresAnyFooterReference(sectionProps))
            footerPart = mainPart.FooterParts.FirstOrDefault();

        var html = footerPart?.Footer != null ? ConvertFooterPartToHtml(footerPart, document) : null;
        if (string.IsNullOrWhiteSpace(html)) html = null;

        string? firstPageHtml = null;
        var differentFirstPage = false;
        if (HasTitlePage(sectionProps))
        {
            differentFirstPage = true;
            var firstPart = ResolveFooterPart(mainPart, sectionProps, HeaderFooterValues.First);
            var fph = firstPart?.Footer != null ? ConvertFooterPartToHtml(firstPart, document) : null;
            firstPageHtml = string.IsNullOrWhiteSpace(fph) ? string.Empty : fph;
        }

        string? evenHtml = null;
        var differentOddEven = false;
        if (HasEvenAndOddHeaders(mainPart))
        {
            differentOddEven = true;
            var evenPart = ResolveFooterPart(mainPart, sectionProps, HeaderFooterValues.Even);
            var eh = evenPart?.Footer != null ? ConvertFooterPartToHtml(evenPart, document) : null;
            evenHtml = string.IsNullOrWhiteSpace(eh) ? string.Empty : eh;
        }

        if (html == null && string.IsNullOrEmpty(firstPageHtml) && string.IsNullOrEmpty(evenHtml)) return null;

        var page = SectionPropertiesReader.ReadPageSettings(sections.FirstOrDefault());
        double footerHeight = page.HasPageMargin
            ? ComputeBandHeightCm(page.BottomMarginTwips, page.FooterDistanceTwips)
            : 1.5;

        return new HeaderFooterContent
        {
            Html = html ?? string.Empty,
            Height = Math.Max(0.8, Math.Min(8, footerHeight)),
            DifferentFirstPage = differentFirstPage,
            FirstPageHtml = firstPageHtml,
            DifferentOddEven = differentOddEven,
            EvenHtml = evenHtml
        };
    }

    private List<SectionHeaderFooter>? ExtractSectionHeadersFooters(WordprocessingDocument document)
    {
        var mainPart = document.MainDocumentPart;
        if (mainPart == null) return null;

        var sections = GetSectionPropertiesInDocumentOrder(mainPart.Document?.Body);
        if (sections.Count < 2) return null;

        var result = new List<SectionHeaderFooter>();
        for (int i = 1; i < sections.Count; i++)
        {
            var header = ExtractHeaderOwnedBySection(mainPart, document, sections[i]);
            var footer = ExtractFooterOwnedBySection(mainPart, document, sections[i]);
            if (header != null || footer != null)
                result.Add(new SectionHeaderFooter { SectionIndex = i, Header = header, Footer = footer });
        }
        return result.Count > 0 ? result : null;
    }

    private HeaderFooterContent? ExtractHeaderOwnedBySection(MainDocumentPart mainPart, WordprocessingDocument document, SectionProperties sectionProps)
    {
        var headerPart = ResolveHeaderPart(mainPart, sectionProps, HeaderFooterValues.Default);
        var html = headerPart?.Header != null ? ConvertHeaderPartToHtml(headerPart, document) : null;
        if (string.IsNullOrWhiteSpace(html)) html = null;

        string? firstPageHtml = null;
        var differentFirstPage = false;
        if (HasTitlePage(sectionProps))
        {
            differentFirstPage = true;
            if (ResolveHeaderPart(mainPart, sectionProps, HeaderFooterValues.First) is { Header: not null } firstPart)
            {
                var fph = ConvertHeaderPartToHtml(firstPart, document);
                firstPageHtml = string.IsNullOrWhiteSpace(fph) ? string.Empty : fph;
            }
        }

        string? evenHtml = null;
        var differentOddEven = false;
        if (HasEvenAndOddHeaders(mainPart))
        {
            differentOddEven = true;
            if (ResolveHeaderPart(mainPart, sectionProps, HeaderFooterValues.Even) is { Header: not null } evenPart)
            {
                var eh = ConvertHeaderPartToHtml(evenPart, document);
                evenHtml = string.IsNullOrWhiteSpace(eh) ? string.Empty : eh;
            }
        }

        if (html == null && firstPageHtml == null && evenHtml == null && !differentFirstPage) return null;

        var page = SectionPropertiesReader.ReadPageSettings(sectionProps);
        var height = page.HasPageMargin ? ComputeBandHeightCm(page.TopMarginTwips, page.HeaderDistanceTwips) : 1.5;

        return new HeaderFooterContent
        {
            Html = html ?? string.Empty,
            Height = Math.Max(0.8, Math.Min(8, height)),
            DifferentFirstPage = differentFirstPage,
            FirstPageHtml = firstPageHtml,
            DifferentOddEven = differentOddEven,
            EvenHtml = evenHtml
        };
    }

    private HeaderFooterContent? ExtractFooterOwnedBySection(MainDocumentPart mainPart, WordprocessingDocument document, SectionProperties sectionProps)
    {
        var footerPart = ResolveFooterPart(mainPart, sectionProps, HeaderFooterValues.Default);
        var html = footerPart?.Footer != null ? ConvertFooterPartToHtml(footerPart, document) : null;
        if (string.IsNullOrWhiteSpace(html)) html = null;

        string? firstPageHtml = null;
        var differentFirstPage = false;
        if (HasTitlePage(sectionProps))
        {
            differentFirstPage = true;
            if (ResolveFooterPart(mainPart, sectionProps, HeaderFooterValues.First) is { Footer: not null } firstPart)
            {
                var fph = ConvertFooterPartToHtml(firstPart, document);
                firstPageHtml = string.IsNullOrWhiteSpace(fph) ? string.Empty : fph;
            }
        }

        string? evenHtml = null;
        var differentOddEven = false;
        if (HasEvenAndOddHeaders(mainPart))
        {
            differentOddEven = true;
            if (ResolveFooterPart(mainPart, sectionProps, HeaderFooterValues.Even) is { Footer: not null } evenPart)
            {
                var eh = ConvertFooterPartToHtml(evenPart, document);
                evenHtml = string.IsNullOrWhiteSpace(eh) ? string.Empty : eh;
            }
        }

        if (html == null && firstPageHtml == null && evenHtml == null && !differentFirstPage) return null;

        var page = SectionPropertiesReader.ReadPageSettings(sectionProps);
        var height = page.HasPageMargin ? ComputeBandHeightCm(page.BottomMarginTwips, page.FooterDistanceTwips) : 1.5;

        return new HeaderFooterContent
        {
            Html = html ?? string.Empty,
            Height = Math.Max(0.8, Math.Min(8, height)),
            DifferentFirstPage = differentFirstPage,
            FirstPageHtml = firstPageHtml,
            DifferentOddEven = differentOddEven,
            EvenHtml = evenHtml
        };
    }

    private static HeaderPart? ResolveHeaderPart(MainDocumentPart mainPart, SectionProperties? sectionProps, HeaderFooterValues type)
    {
        var reference = sectionProps?.Elements<HeaderReference>()
            .FirstOrDefault(r => r.Type != null && r.Type.Value == type);
        if (reference?.Id?.Value == null) return null;
        return mainPart.GetPartById(reference.Id.Value) as HeaderPart;
    }

    private static FooterPart? ResolveFooterPart(MainDocumentPart mainPart, SectionProperties? sectionProps, HeaderFooterValues type)
    {
        var reference = sectionProps?.Elements<FooterReference>()
            .FirstOrDefault(r => r.Type != null && r.Type.Value == type);
        if (reference?.Id?.Value == null) return null;
        return mainPart.GetPartById(reference.Id.Value) as FooterPart;
    }

    private static bool HasTitlePage(SectionProperties? sectionProps)
    {
        var titlePg = sectionProps?.GetFirstChild<TitlePage>();
        return titlePg != null && (titlePg.Val == null || titlePg.Val.Value);
    }

    private static bool SectionDeclaresAnyHeaderReference(SectionProperties? sectionProps)
        => sectionProps?.Elements<HeaderReference>().Any() == true;

    private static bool SectionDeclaresAnyFooterReference(SectionProperties? sectionProps)
        => sectionProps?.Elements<FooterReference>().Any() == true;

    private static bool HasEvenAndOddHeaders(MainDocumentPart mainPart)
    {
        var setting = mainPart.DocumentSettingsPart?.Settings?.GetFirstChild<EvenAndOddHeaders>();
        return setting != null && (setting.Val == null || setting.Val.Value);
    }

    private static bool HasEnforcedEditProtection(MainDocumentPart mainPart)
    {
        var settings = mainPart.DocumentSettingsPart?.Settings;
        if (settings == null) return false;

        var protection = settings.GetFirstChild<DocumentProtection>();
        if (protection != null
            && protection.Enforcement?.Value == true
            && protection.Edit != null
            && protection.Edit.Value != DocumentProtectionValues.None)
        {
            return true;
        }

        var writeProtection = settings.GetFirstChild<WriteProtection>();
        return writeProtection != null
            && (writeProtection.Recommended?.Value == true
                || !string.IsNullOrEmpty(writeProtection.Hash?.Value)
                || !string.IsNullOrEmpty(writeProtection.HashValue?.Value));
    }

    private static bool IsMarkedAsFinal(WordprocessingDocument document)
    {
        var properties = document.CustomFilePropertiesPart?.Properties;
        if (properties == null) return false;

        foreach (var property in properties.Elements<DocumentFormat.OpenXml.CustomProperties.CustomDocumentProperty>())
        {
            if (property.Name?.Value == "_MarkAsFinal")
            {
                var value = property.VTBool?.Text;
                return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
            }
        }

        return false;
    }

    private string ConvertHeaderPartToHtml(HeaderPart part, WordprocessingDocument document)
    {
        foreach (var imagePart in part.ImageParts)
        {
            LoadImageFromPart(part, imagePart);
        }
        _anchorBand = HfBand.Header;
        var prevAvail = _availableContentWidthTwips;
        _availableContentWidthTwips = FullContentWidthTwips();
        try { return ConvertHeaderFooterToHtml(part.Header, part, document); }
        finally { _anchorBand = HfBand.None; _availableContentWidthTwips = prevAvail; }
    }

    private string ConvertFooterPartToHtml(FooterPart part, WordprocessingDocument document)
    {
        foreach (var imagePart in part.ImageParts)
        {
            LoadImageFromPart(part, imagePart);
        }
        _anchorBand = HfBand.Footer;
        var prevAvail = _availableContentWidthTwips;
        _availableContentWidthTwips = FullContentWidthTwips();
        try { return ConvertHeaderFooterToHtml(part.Footer, part, document); }
        finally { _anchorBand = HfBand.None; _availableContentWidthTwips = prevAvail; }
    }

    private long? FullContentWidthTwips()
    {
        if (_pageWidthTwips is not { } w || w <= 0) return null;
        var content = w - _marginLeftTwips - _marginRightTwips;
        return content > 0 ? content : null;
    }

    private string ConvertHeaderFooterToHtml(OpenXmlCompositeElement headerFooter, OpenXmlPart part, WordprocessingDocument document)
    {
        var inner = new StringBuilder();

        foreach (var element in headerFooter.Elements())
        {
            if (element is Paragraph para)
            {
                inner.Append(ConvertParagraphToHtml(para, document, part));
            }
            else if (element is Table table)
            {
                inner.Append(ConvertTableToHtml(table, document, part));
            }
            else if (element is SdtBlock sdt)
            {
                inner.Append(ConvertSdtBlockToHtml(sdt, document, part));
            }
        }

        if (inner.Length == 0) return string.Empty;

        var css = BuildDefaultContainerCss();
        var openTag = css.Length > 0
            ? $"<div class=\"header-footer-content\" style=\"{css}\">"
            : "<div class=\"header-footer-content\">";
        return openTag + inner + "</div>";
    }

    private string BuildDefaultContainerCss()
    {
        var css = new StringBuilder();
        var effectiveFontFamily = !string.IsNullOrEmpty(_defaultFontFamily)
            ? _defaultFontFamily
            : (!string.IsNullOrWhiteSpace(_defaults.FontFamily) ? _defaults.FontFamily : null);
        if (!string.IsNullOrEmpty(effectiveFontFamily))
            css.Append(FontFamilyCss(effectiveFontFamily));
        var effectiveFontSizePt = _defaultFontSizePt ?? (_defaults.FontSizePt > 0 ? _defaults.FontSizePt : (double?)null);
        if (effectiveFontSizePt.HasValue)
            css.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "font-size:{0:0.##}pt;", effectiveFontSizePt.Value));
        return css.ToString();
    }

    private DocumentMetadata ExtractMetadata(WordprocessingDocument document)
    {
        var metadata = new DocumentMetadata();

        var coreProps = document.PackageProperties;
        if (coreProps != null)
        {
            metadata.Title = coreProps.Title;
            metadata.Author = coreProps.Creator;
            metadata.Subject = coreProps.Subject;
            metadata.Keywords = coreProps.Keywords;
            metadata.Description = coreProps.Description;
            metadata.Category = coreProps.Category;
            metadata.ContentStatus = coreProps.ContentStatus;
            metadata.LastModifiedBy = coreProps.LastModifiedBy;
            metadata.Revision = coreProps.Revision;
            metadata.Version = coreProps.Version;
            metadata.Created = coreProps.Created;
            metadata.Modified = coreProps.Modified;
        }

        var extPropsPart = document.ExtendedFilePropertiesPart;
        if (extPropsPart?.Properties != null)
        {
            metadata.Company = extPropsPart.Properties.Company?.Text;
            metadata.Manager = extPropsPart.Properties.Manager?.Text;
        }

        var body = document.MainDocumentPart?.Document?.Body;
        if (body != null)
        {
            var text = body.InnerText;
            metadata.WordCount = text.Split(new[] { ' ', '\t', '\n', '\r' }, 
                StringSplitOptions.RemoveEmptyEntries).Length;
        }

        metadata.Signatures = ExtractSignatures(document);

        return metadata;
    }

    private List<DigitalSignatureInfo> ExtractSignatures(WordprocessingDocument document)
    {
        var signatures = new List<DigitalSignatureInfo>();
        var sigNs = XNamespace.Get("http://schemas.D2ViewerEditor.app/digitalsignatures");

        if (document.MainDocumentPart == null) return signatures;

        foreach (var xmlPart in document.MainDocumentPart.CustomXmlParts)
        {
            try
            {
                using var stream = xmlPart.GetStream(FileMode.Open, FileAccess.Read);
                var doc = XDocument.Load(stream);

                if (doc.Root?.Name.Namespace == sigNs && doc.Root.Name.LocalName == "DigitalSignatures")
                {
                    foreach (var sigEl in doc.Root.Elements(sigNs + "Signature"))
                    {
                        signatures.Add(new DigitalSignatureInfo
                        {
                            SignerName = sigEl.Element(sigNs + "SignerName")?.Value ?? "",
                            SignerTitle = sigEl.Element(sigNs + "SignerTitle")?.Value,
                            SignerEmail = sigEl.Element(sigNs + "SignerEmail")?.Value,
                            Reason = sigEl.Element(sigNs + "Reason")?.Value,
                            CertificateSubject = sigEl.Element(sigNs + "CertificateSubject")?.Value ?? "",
                            CertificateIssuer = sigEl.Element(sigNs + "CertificateIssuer")?.Value ?? "",
                            CertificateSerialNumber = sigEl.Element(sigNs + "CertificateSerial")?.Value ?? "",
                            SignedAt = DateTime.TryParse(sigEl.Element(sigNs + "SignedAt")?.Value, out var signedAt) ? signedAt : DateTime.MinValue,
                            CertificateValidFrom = DateTime.TryParse(sigEl.Element(sigNs + "CertificateValidFrom")?.Value, out var from) ? from : DateTime.MinValue,
                            CertificateValidTo = DateTime.TryParse(sigEl.Element(sigNs + "CertificateValidTo")?.Value, out var to) ? to : DateTime.MaxValue,
                            IsValid = true,
                            ValidationMessage = "Podpis odczytany — pełna weryfikacja wymaga dedykowanego zapytania."
                        });
                    }
                }
            }
            catch {  }
        }

        return signatures;
    }

    private string ConvertBodyToHtml(WordprocessingDocument document)
    {
        var body = document.MainDocumentPart?.Document?.Body;
        if (body == null)
            return "<div></div>";

        LoadDocumentStyles(document);
        LoadDocumentImages(document);

        var html = new StringBuilder();
        var containerCss = BuildDefaultContainerCss();

        var containerAttrs = new StringBuilder();
        containerAttrs.Append($" data-default-before-tw=\"{_defaultSpacingBeforeTw ?? "0"}\"");
        containerAttrs.Append($" data-default-after-tw=\"{_defaultSpacingAfterTw ?? "0"}\"");
        if (_defaultSpacingLine != null)
            containerAttrs.Append($" data-default-line=\"{_defaultSpacingLine}\"" +
                $" data-default-line-rule=\"{_defaultSpacingLineRule}\"");

        _baseSectionColumns = SectionPropertiesReader.ReadPageSettings(GetFirstSectionProperties(document)).Columns;
        AppendColumnDataAttributes(containerAttrs, _baseSectionColumns);
        var containerLineHeight = ExtractCssProperty(_defaultParagraphSpacingCss, "line-height");
        var bodyContainerCss = containerLineHeight != null
            ? containerCss + $"line-height:{containerLineHeight};"
            : containerCss;

        if (bodyContainerCss.Length > 0 || containerAttrs.Length > 0)
            html.Append($"<div class=\"document-content\"{containerAttrs} style=\"{bodyContainerCss}\">");
        else
            html.Append("<div class=\"document-content\">");

        var elements = body.Elements().ToList();
        var orderedSections = GetSectionPropertiesInDocumentOrder(body);
        _availableContentWidthTwips = SectionColumnWidthTwips(orderedSections.FirstOrDefault());
        int i = 0;
        while (i < elements.Count)
        {
            var element = elements[i];

            if (element is Paragraph p && IsListParagraph(p))
            {
                html.Append(ConvertConsecutiveListItems(elements, ref i, document));
            }
            else
            {
                var endedSection = (element as Paragraph)?.ParagraphProperties
                    ?.GetFirstChild<SectionProperties>();
                var isBareSectionMark = endedSection != null
                    && element is Paragraph sectMarkPara
                    && !ParagraphHasVisibleContent(sectMarkPara);
                if (!isBareSectionMark)
                {
                    html.Append(ConvertElementToHtml(element, document));
                }

                if (endedSection != null)
                {
                    html.Append(BuildSectionBreakMarkerHtml(endedSection, orderedSections));
                    var endedIdx = orderedSections.IndexOf(endedSection);
                    if (endedIdx >= 0 && endedIdx + 1 < orderedSections.Count)
                        _availableContentWidthTwips = SectionColumnWidthTwips(orderedSections[endedIdx + 1]);
                }
                i++;
            }
        }

        html.Append("</div>");
        return html.ToString();
    }

    private long? SectionColumnWidthTwips(SectionProperties? section)
    {
        var page = section != null ? SectionPropertiesReader.ReadPageSettings(section) : null;
        var pageW = page?.PageWidthTwips ?? _pageWidthTwips;
        if (pageW is not > 0) return null;
        var mL = page?.LeftMarginTwips ?? _marginLeftTwips;
        var mR = page?.RightMarginTwips ?? _marginRightTwips;
        var content = pageW.Value - mL - mR;
        if (content <= 0) return null;
        var cols = page?.Columns;
        if (cols is { Count: > 1 })
        {
            if (!cols.EqualWidth && cols.Columns is { Count: > 0 })
            {
                content = Math.Max(1, cols.Columns.Max(c => (long)c.WidthTwips));
            }
            else
            {
                var space = (long)cols.SpaceTwips * (cols.Count - 1);
                content = Math.Max(1, (content - space) / cols.Count);
            }
        }
        return content;
    }

    private static bool ParagraphHasVisibleContent(Paragraph paragraph)
    {
        if (!string.IsNullOrEmpty(paragraph.InnerText)) return true;
        return paragraph.Descendants().Any(d =>
            d is Drawing or Picture or EmbeddedObject or Break or TabChar or SymbolChar
            or FootnoteReference or EndnoteReference);
    }

    private bool IsListParagraph(Paragraph paragraph)
    {
        var numPr = paragraph.ParagraphProperties?.NumberingProperties;
        if (numPr?.NumberingId?.Val?.Value != null && numPr.NumberingId.Val.Value > 0)
            return true;

        if (numPr?.NumberingId?.Val?.Value == 0)
            return false;

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var styleNumPr = ResolveStyleNumbering(styleId);
        return styleNumPr != null;
    }

    private NumberingProperties? ResolveStyleNumbering(string? styleId, HashSet<string>? visited = null)
    {
        if (styleId == null) return null;
        visited ??= new HashSet<string>();
        if (visited.Contains(styleId)) return null;
        visited.Add(styleId);

        if (!_rawStyles.TryGetValue(styleId, out var style)) return null;

        var spProps = style.StyleParagraphProperties;
        if (spProps != null)
        {
            var numPr = spProps.GetFirstChild<NumberingProperties>();
            if (numPr?.NumberingId?.Val?.Value != null && numPr.NumberingId.Val.Value > 0)
                return numPr;
        }

        var basedOn = style.BasedOn?.Val?.Value;
        if (basedOn != null)
            return ResolveStyleNumbering(basedOn, visited);

        return null;
    }

    private (int numId, int level) GetEffectiveNumberingInfo(Paragraph paragraph)
    {
        var numPr = paragraph.ParagraphProperties?.NumberingProperties;
        if (numPr?.NumberingId?.Val?.Value != null && numPr.NumberingId.Val.Value > 0)
        {
            return (numPr.NumberingId.Val.Value, numPr.NumberingLevelReference?.Val?.Value ?? 0);
        }

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var styleNumPr = ResolveStyleNumbering(styleId);
        if (styleNumPr != null)
        {
            return (
                styleNumPr.NumberingId?.Val?.Value ?? 0,
                styleNumPr.NumberingLevelReference?.Val?.Value ?? 0
            );
        }

        return (0, 0);
    }

    private NumberingProperties? GetEffectiveNumberingProps(Paragraph paragraph)
    {
        var numPr = paragraph.ParagraphProperties?.NumberingProperties;
        if (numPr?.NumberingId?.Val?.Value != null && numPr.NumberingId.Val.Value > 0)
            return numPr;

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        return ResolveStyleNumbering(styleId);
    }

    private (int leftPx, int hangingPx) GetNumberingLevelIndentation(NumberingProperties? numPr, int levelOverride = -1)
    {
        if (numPr == null || _numberingPart?.Numbering == null) return (0, 0);

        var numId = numPr.NumberingId?.Val?.Value;
        if (numId == null) return (0, 0);

        var level = levelOverride >= 0 ? levelOverride : (numPr.NumberingLevelReference?.Val?.Value ?? 0);

        var (levelDef, _, _) = FindLevelDefinition(numId.Value, level);
        var indent = levelDef?.PreviousParagraphProperties?.GetFirstChild<Indentation>();

        int leftTwips = 0, hangingTwips = 0;
        if (indent?.Left?.Value != null) int.TryParse(indent.Left.Value, out leftTwips);
        if (indent?.Hanging?.Value != null) int.TryParse(indent.Hanging.Value, out hangingTwips);

        return (TwipsToPx(leftTwips), TwipsToPx(hangingTwips));
    }

    private static string StripIndentationCss(string css)
    {
        if (string.IsNullOrEmpty(css)) return css;
        
        css = Regex.Replace(css, @"margin-left:\s*[^;]+;?", "");
        css = Regex.Replace(css, @"text-indent:\s*[^;]+;?", "");
        css = Regex.Replace(css, @"padding-left:\s*[^;]+;?", "");
        
        return css;
    }

    private string ConvertConsecutiveListItems(List<OpenXmlElement> elements, ref int index, WordprocessingDocument document, int parentIndentPx = 0)
    {
        var html = new StringBuilder();
        
        var firstPara = (Paragraph)elements[index];
        var firstNumProps = GetEffectiveNumberingProps(firstPara);
        var (firstNumId, _) = GetEffectiveNumberingInfo(firstPara);
        var firstLevel = GetListLevel(firstPara);
        var firstInfo = GetListLevelInfo(firstNumProps, firstLevel);
        var listType = firstInfo.Tag;

        var (levelIndentPx, _) = GetNumberingLevelIndentation(firstNumProps, firstLevel);
        var listPadding = levelIndentPx > parentIndentPx ? levelIndentPx - parentIndentPx
            : levelIndentPx > 0 ? 0
            : 36;

        var hangingPx = int.TryParse(firstInfo.IndHangingTw, out var indHangTw) && indHangTw > 0
            ? (int?)TwipsToPx(indHangTw)
            : null;
        var hangingCss = hangingPx is { } hp ? $"--ind-hanging:{hp}px;" : string.Empty;
        var markerColorVarCss = firstInfo.MarkerColorCss != null
            ? $"--marker-color:{firstInfo.MarkerColorCss};"
            : string.Empty;
        var markerSizeVarCss = MarkerSizeCssVar(firstInfo.MarkerSizeHalfPoints);
        var listStyleCss = $"margin:0;padding-left:{listPadding}px;list-style-type:{firstInfo.ListStyleType};{hangingCss}{markerColorVarCss}{markerSizeVarCss}";

        var startNumber = listType == "ol" ? PeekNextListNumber(firstNumId, firstLevel) : 1;
        var startAttr = (listType == "ol" && startNumber > 1) ? $" start=\"{startNumber}\"" : "";

        var identityAttrs = new StringBuilder();
        identityAttrs.Append($" data-num-id=\"{firstNumId}\"");
        var abstractId = ResolveAbstractNumId(firstNumId);
        if (abstractId >= 0) identityAttrs.Append($" data-abstract-num-id=\"{abstractId}\"");
        identityAttrs.Append($" data-ilvl=\"{firstLevel}\"");
        identityAttrs.Append($" data-num-fmt=\"{firstInfo.FmtToken}\"");
        if (firstInfo.Start > 1) identityAttrs.Append($" data-start=\"{firstInfo.Start}\"");
        if (firstInfo.LvlText != null)
            identityAttrs.Append($" data-lvl-text=\"{System.Net.WebUtility.HtmlEncode(firstInfo.LvlText)}\"");
        if (!string.IsNullOrEmpty(firstInfo.BulletFont))
            identityAttrs.Append($" data-bullet-font=\"{System.Net.WebUtility.HtmlEncode(firstInfo.BulletFont)}\"");
        if (firstInfo.StartOverride > 0)
            identityAttrs.Append($" data-start-override=\"{firstInfo.StartOverride}\"");
        if (firstInfo.SuffixToken != null)
            identityAttrs.Append($" data-suffix=\"{firstInfo.SuffixToken}\"");
        if (firstInfo.IsLegal)
            identityAttrs.Append(" data-is-legal=\"1\"");
        if (firstInfo.LvlRestart >= 0)
            identityAttrs.Append($" data-lvl-restart=\"{firstInfo.LvlRestart}\"");
        if (firstInfo.PicBulletId >= 0)
            identityAttrs.Append(" data-pic-bullet=\"1\"");
        if (firstInfo.IndLeftTw != null)
            identityAttrs.Append($" data-ind-left-tw=\"{firstInfo.IndLeftTw}\"");
        if (firstInfo.IndHangingTw != null)
            identityAttrs.Append($" data-ind-hanging-tw=\"{firstInfo.IndHangingTw}\"");
        if (firstInfo.IndFirstLineTw != null)
            identityAttrs.Append($" data-ind-first-line-tw=\"{firstInfo.IndFirstLineTw}\"");
        if (firstInfo.FromInstanceOverride)
            identityAttrs.Append(" data-lvl-override=\"1\"");
        if (firstInfo.MarkerColorHex != null)
            identityAttrs.Append($" data-marker-color=\"{firstInfo.MarkerColorHex}\"");
        if (firstInfo.MarkerSizeHalfPoints != null)
            identityAttrs.Append($" data-marker-size=\"{firstInfo.MarkerSizeHalfPoints}\"");

        html.Append($"<{listType}{startAttr}{identityAttrs} style=\"{listStyleCss}\">");

        while (index < elements.Count)
        {
            if (elements[index] is not Paragraph p || !IsListParagraph(p))
                break;

            var (currentNumId, _) = GetEffectiveNumberingInfo(p);
            var currentLevel = GetListLevel(p);

            if (currentNumId != firstNumId && currentLevel <= firstLevel)
                break;

            if (currentLevel > firstLevel)
            {
                var lastLi = "</li>";
                html.Length -= lastLi.Length;
                html.Append(ConvertConsecutiveListItems(elements, ref index, document, levelIndentPx));
                html.Append("</li>");
            }
            else if (currentLevel < firstLevel)
            {
                break;
            }
            else
            {
                NextListNumber(currentNumId, currentLevel);

                var cssStyle = GetParagraphStyle(p.ParagraphProperties);
                var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                if (styleId != null && _styles.TryGetValue(styleId, out var styleCss))
                {
                    cssStyle = styleCss + cssStyle;
                }
                if (!string.IsNullOrEmpty(_tableParagraphDefaultCss) && p.Ancestors<TableCell>().Any())
                {
                    cssStyle = _tableParagraphDefaultCss + cssStyle;
                }
                cssStyle = DeduplicateCss(StripIndentationCss(cssStyle));

                if (cssStyle.Contains("--w-contextual-spacing"))
                {
                    var myStyleId = EffectiveParagraphStyleId(p);
                    if (p.PreviousSibling() is Paragraph prevPara
                        && EffectiveParagraphStyleId(prevPara) == myStyleId)
                    {
                        cssStyle = SetCssProperty(cssStyle, "margin-top", "0");
                    }
                    if (p.NextSibling() is Paragraph nextPara
                        && EffectiveParagraphStyleId(nextPara) == myStyleId)
                    {
                        cssStyle = SetCssProperty(cssStyle, "margin-bottom", "0");
                        cssStyle = SetCssProperty(cssStyle, "padding-bottom", "0");
                    }
                }

                var itemHangingPx = hangingPx;
                var itemAttrs = string.Empty;
                var directInd = p.ParagraphProperties?.GetFirstChild<Indentation>();
                if (directInd != null)
                {
                    var indCss = new StringBuilder();
                    var indAttrs = new StringBuilder();
                    var directLeftRaw = directInd.Left?.Value ?? directInd.Start?.Value;
                    if (int.TryParse(directLeftRaw, out var directLeftTw))
                    {
                        var deltaPx = TwipsToPx(directLeftTw) - (parentIndentPx + listPadding);
                        if (deltaPx != 0) indCss.Append($"margin-left:{deltaPx}px;");
                        indAttrs.Append($" data-ind-left-tw=\"{directLeftTw}\"");
                    }
                    if (int.TryParse(directInd.Hanging?.Value, out var directHangTw))
                    {
                        itemHangingPx = directHangTw > 0 ? TwipsToPx(directHangTw) : null;
                        if (itemHangingPx != hangingPx)
                            indCss.Append($"--ind-hanging:{itemHangingPx ?? 0}px;");
                        indAttrs.Append($" data-ind-hanging-tw=\"{directHangTw}\"");
                    }
                    else if (int.TryParse(directInd.FirstLine?.Value, out var directFirstTw))
                    {
                        if (directFirstTw > 0)
                            indCss.Append($"text-indent:{TwipsToPx(directFirstTw)}px;");
                        indAttrs.Append($" data-ind-first-line-tw=\"{directFirstTw}\"");
                    }
                    cssStyle += indCss.ToString();
                    itemAttrs = indAttrs.ToString();
                }

                string? itemMarkerColorCss = null;
                if (firstInfo.MarkerColorCss == null)
                {
                    var markColor = p.ParagraphProperties
                        ?.GetFirstChild<ParagraphMarkRunProperties>()
                        ?.GetFirstChild<Color>();
                    itemMarkerColorCss = ResolveRunColorCss(markColor);
                    if (itemMarkerColorCss != null)
                    {
                        cssStyle += $"--marker-color:{itemMarkerColorCss};";
                        itemAttrs += $" data-mark-color=\"{itemMarkerColorCss.TrimStart('#')}\"";
                    }
                }

                string? itemMarkerSizeHalf = null;
                if (firstInfo.MarkerSizeHalfPoints == null)
                {
                    itemMarkerSizeHalf = p.ParagraphProperties
                        ?.GetFirstChild<ParagraphMarkRunProperties>()
                        ?.GetFirstChild<FontSize>()?.Val?.Value;
                    if (itemMarkerSizeHalf != null)
                        itemAttrs += $" data-mark-size=\"{itemMarkerSizeHalf}\"";
                    else
                        itemMarkerSizeHalf = p.Descendants<Run>()
                            .FirstOrDefault(r => r.GetFirstChild<Text>() != null)
                            ?.RunProperties?.FontSize?.Val?.Value;
                    var itemSizeVar = MarkerSizeCssVar(itemMarkerSizeHalf);
                    if (itemSizeVar.Length > 0) cssStyle += itemSizeVar;
                }

                html.Append($"<li{itemAttrs} style=\"{cssStyle}\">");

                var markerBoxCss = itemHangingPx is { } markerHang
                    ? $"display:inline-block;min-width:{markerHang}px;margin-right:0;"
                    : "display:inline-block;min-width:1.2em;margin-right:0.4em;";
                if (firstInfo.MarkerColorCss != null)
                    markerBoxCss += $"color:{firstInfo.MarkerColorCss};";
                else if (itemMarkerColorCss != null)
                    markerBoxCss += $"color:{itemMarkerColorCss};";
                markerBoxCss += MarkerSizeFontCss(firstInfo.MarkerSizeHalfPoints ?? itemMarkerSizeHalf);
                if (firstInfo.BulletImageDataUri != null)
                {
                    html.Append($"<span class=\"list-marker\" style=\"{markerBoxCss}\"><img src=\"{firstInfo.BulletImageDataUri}\" alt=\"\" style=\"height:1em;vertical-align:-0.125em;\"/></span>");
                }
                else if (firstInfo.BulletChar != null)
                {
                    var fontCss = !string.IsNullOrEmpty(firstInfo.BulletFont) &&
                        !firstInfo.BulletFont.ToLowerInvariant().Contains("wingdings") &&
                        !firstInfo.BulletFont.ToLowerInvariant().Contains("symbol")
                            ? $"font-family:'{firstInfo.BulletFont}';"
                            : "";
                    html.Append($"<span class=\"list-marker\" style=\"{markerBoxCss}{fontCss}\">{System.Net.WebUtility.HtmlEncode(firstInfo.BulletChar)}</span>");
                }
                
                foreach (var child in p.Elements())
                {
                    switch (child)
                    {
                        case Run run:
                            html.Append(ConvertRunToHtml(run, document));
                            break;
                        case Hyperlink hyperlink:
                            html.Append(ConvertHyperlinkToHtml(hyperlink, document));
                            break;
                        case SimpleField simpleField:
                            html.Append(ConvertSimpleFieldToHtml(simpleField));
                            break;
                        case SdtRun sdtRun:
                            html.Append(ConvertSdtRunToHtml(sdtRun, document));
                            break;
                    }
                }
                
                if (!p.Elements<Run>().Any() && !p.Elements<Hyperlink>().Any())
                {
                    html.Append("&nbsp;");
                }
                
                html.Append("</li>");
                index++;
            }
        }
        
        html.Append(listType == "ol" ? "</ol>" : "</ul>");
        return html.ToString();
    }

    private int GetListLevel(Paragraph paragraph)
    {
        var (_, level) = GetEffectiveNumberingInfo(paragraph);
        return level;
    }

    private void LoadDocumentStyles(WordprocessingDocument document)
    {
        var stylesPart = document.MainDocumentPart?.StyleDefinitionsPart;
        if (stylesPart?.Styles == null) return;

        LoadDocDefaults(stylesPart);

        foreach (var style in stylesPart.Styles.Elements<Style>())
        {
            if (style.StyleId?.Value != null)
            {
                _rawStyles[style.StyleId.Value] = style;
            }
        }

        ApplyDefaultParagraphStyleFont();

        foreach (var kvp in _rawStyles)
        {
            var css = ConvertStyleToCssWithInheritance(kvp.Value);
            _styles[kvp.Key] = css;
        }
    }

    private void ApplyDefaultParagraphStyleFont()
    {
        var defaultStyle = _rawStyles.Values.FirstOrDefault(s =>
            s.Type?.Value == StyleValues.Paragraph && s.Default?.Value == true);

        _defaultParagraphStyleId = defaultStyle?.StyleId?.Value;

        var styleSpacing = defaultStyle?.StyleParagraphProperties?.GetFirstChild<SpacingBetweenLines>();
        if (styleSpacing != null)
            CaptureDefaultParagraphSpacing(styleSpacing);
        _defaultParagraphSpacingCss = BuildDefaultParagraphSpacingCss();

        if (defaultStyle?.StyleRunProperties == null) return;

        var name = GetFontName(defaultStyle.StyleRunProperties.GetFirstChild<RunFonts>());
        if (!string.IsNullOrEmpty(name))
            _defaultFontFamily = name;

        var size = defaultStyle.StyleRunProperties.GetFirstChild<FontSize>();
        if (size?.Val?.Value != null &&
            double.TryParse(size.Val.Value, System.Globalization.CultureInfo.InvariantCulture, out var sz))
            _defaultFontSizePt = OoxmlUnits.HalfPointsToPoints(sz);
    }

    private void LoadDocDefaults(StyleDefinitionsPart stylesPart)
    {
        var docDefaults = stylesPart.Styles?.DocDefaults;

        var pPrDefault = docDefaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle;
        var defaultSpacing = pPrDefault?.GetFirstChild<SpacingBetweenLines>();
        if (defaultSpacing != null)
            CaptureDefaultParagraphSpacing(defaultSpacing);

        var rPrDefault = docDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
        if (rPrDefault == null) return;

        var fonts = rPrDefault.GetFirstChild<RunFonts>();
        var name = GetFontName(fonts);
        if (!string.IsNullOrEmpty(name))
            _defaultFontFamily = name;

        var size = rPrDefault.GetFirstChild<FontSize>();
        if (size?.Val?.Value != null &&
            double.TryParse(size.Val.Value, System.Globalization.CultureInfo.InvariantCulture, out var sz))
        {
            _defaultFontSizePt = OoxmlUnits.HalfPointsToPoints(sz);
        }
    }

    private void CaptureDefaultParagraphSpacing(SpacingBetweenLines spacing)
    {
        if (spacing.Before?.Value != null) _defaultSpacingBeforeTw = spacing.Before.Value;
        if (spacing.After?.Value != null) _defaultSpacingAfterTw = spacing.After.Value;
        if (spacing.Line?.Value != null)
        {
            _defaultSpacingLine = spacing.Line.Value;
            var rule = spacing.LineRule?.Value;
            _defaultSpacingLineRule = rule == LineSpacingRuleValues.Exact ? "exact"
                : rule == LineSpacingRuleValues.AtLeast ? "atLeast"
                : "auto";
        }
    }

    private string BuildDefaultParagraphSpacingCss()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var css = new StringBuilder();
        if (_defaultSpacingBeforeTw != null && int.TryParse(_defaultSpacingBeforeTw, out var beforeTw))
            css.Append(string.Format(inv, "margin-top:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(beforeTw)));
        if (_defaultSpacingAfterTw != null && int.TryParse(_defaultSpacingAfterTw, out var afterTw))
            css.Append(string.Format(inv, "padding-bottom:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(afterTw)));
        if (_defaultSpacingLine != null && int.TryParse(_defaultSpacingLine, out var lineTw))
        {
            if (_defaultSpacingLineRule == "atLeast")
            {
                css.Append(string.Format(inv,
                    "line-height:max({0:0.##}pt, var(--w-line-single, 1.2em));",
                    OoxmlUnits.TwipsToPoints(lineTw)));
                css.Append("--w-line-rule:atLeast;");
            }
            else if (_defaultSpacingLineRule == "exact")
            {
                css.Append(string.Format(inv, "line-height:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(lineTw)));
            }
            else
            {
                css.Append(WordLineSpacing.AutoCss(lineTw, _defaultFontFamily));
            }
        }
        else
        {
            css.Append(WordLineSpacing.AutoCss((int)WordLineSpacing.LineUnitsPerSingle, _defaultFontFamily));
        }
        return css.ToString();
    }

    private string ConvertStyleToCssWithInheritance(Style style, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();
        
        var styleId = style.StyleId?.Value;
        if (styleId != null && visited.Contains(styleId))
            return string.Empty;
        
        if (styleId != null)
            visited.Add(styleId);

        var css = new StringBuilder();
        
        var basedOn = style.BasedOn?.Val?.Value;
        if (basedOn != null && _rawStyles.TryGetValue(basedOn, out var baseStyle))
        {
            css.Append(ConvertStyleToCssWithInheritance(baseStyle, visited));
        }
        
        var runProps = style.StyleRunProperties;
        if (runProps != null)
        {
            css.Append(ConvertRunPropertiesToCss(runProps));
        }

        var paraProps = style.StyleParagraphProperties;
        if (paraProps != null)
        {
            css.Append(ConvertParagraphPropertiesToCss(paraProps, GetStyleFontFamily(style)));
        }

        return DeduplicateCss(css.ToString());
    }

    private static string DeduplicateCss(string css)
    {
        if (string.IsNullOrEmpty(css)) return css;

        var order = new List<string>();
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in Regex.Matches(css, @"([\w-]+)\s*:\s*([^;]+);"))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            var val  = m.Groups[2].Value.Trim();
            if (!props.ContainsKey(name))
                order.Add(name);
            props[name] = val;
        }

        return string.Concat(order.Select(p => $"{p}:{props[p]};"));
    }

    private static string SetCssProperty(string css, string property, string value)
    {
        var pattern = $@"(?<![\w-]){Regex.Escape(property)}\s*:\s*[^;]+;";
        return Regex.IsMatch(css, pattern)
            ? Regex.Replace(css, pattern, $"{property}:{value};")
            : $"{css}{property}:{value};";
    }

    private string? EffectiveParagraphStyleId(Paragraph paragraph) =>
        paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? _defaultParagraphStyleId;

    private static string? ExtractCssProperty(string css, string property)
    {
        if (string.IsNullOrEmpty(css)) return null;
        string? value = null;
        foreach (Match m in Regex.Matches(css, $@"(?<![\w-]){Regex.Escape(property)}\s*:\s*([^;]+);"))
            value = m.Groups[1].Value.Trim();
        return value;
    }

    private List<DocumentStyle> ExtractDocumentStyles(WordprocessingDocument document)
    {
        var result = new List<DocumentStyle>();
        var stylesPart = document.MainDocumentPart?.StyleDefinitionsPart;
        if (stylesPart?.Styles == null) return result;

        foreach (var style in stylesPart.Styles.Elements<Style>())
        {
            if (style.Type?.Value != StyleValues.Paragraph) continue;
            if (style.StyleId?.Value == null) continue;

            var styleName = style.StyleName?.Val?.Value ?? style.StyleId.Value;
            
            var semiHidden = style.SemiHidden;
            if (semiHidden != null && semiHidden.Val == null)
                continue;

            var docStyle = new DocumentStyle
            {
                Id = style.StyleId.Value,
                Name = TranslateStyleName(styleName),
                Type = "paragraph",
                BasedOn = style.BasedOn?.Val?.Value,
                NextStyle = style.NextParagraphStyle?.Val?.Value
            };

            var runProps = style.StyleRunProperties;
            if (runProps != null)
            {
                var font = runProps.RunFonts;
                if (font != null)
                {
                    docStyle.FontFamily = GetFontName(font);
                }

                if (runProps.FontSize?.Val?.Value != null &&
                    double.TryParse(runProps.FontSize.Val.Value, out var fontSize))
                {
                    docStyle.FontSize = OoxmlUnits.HalfPointsToPoints(fontSize);
                }

                var styleColor = ResolveRunColorCss(runProps.Color);
                if (styleColor != null)
                    docStyle.Color = styleColor;

                docStyle.IsBold = runProps.Bold != null && 
                                  (runProps.Bold.Val == null || runProps.Bold.Val.Value);
                docStyle.IsItalic = runProps.Italic != null && 
                                    (runProps.Italic.Val == null || runProps.Italic.Val.Value);
                docStyle.IsUnderline = runProps.Underline != null && 
                                       runProps.Underline.Val?.Value != UnderlineValues.None;
            }

            var paraProps = style.StyleParagraphProperties;
            if (paraProps != null)
            {
                var justification = paraProps.Justification?.Val;
                if (justification != null && justification.HasValue)
                {
                    var justVal = justification.Value;
                    if (justVal == JustificationValues.Left) docStyle.Alignment = "left";
                    else if (justVal == JustificationValues.Center) docStyle.Alignment = "center";
                    else if (justVal == JustificationValues.Right) docStyle.Alignment = "right";
                    else if (justVal == JustificationValues.Both) docStyle.Alignment = "justify";
                }

                var spacing = paraProps.SpacingBetweenLines;
                if (spacing != null)
                {
                    if (spacing.Before?.Value != null &&
                        int.TryParse(spacing.Before.Value, out var before))
                    {
                        docStyle.SpaceBefore = OoxmlUnits.TwipsToPoints(before);
                    }
                    if (spacing.After?.Value != null &&
                        int.TryParse(spacing.After.Value, out var after))
                    {
                        docStyle.SpaceAfter = OoxmlUnits.TwipsToPoints(after);
                    }
                    if (spacing.Line?.Value != null &&
                        int.TryParse(spacing.Line.Value, out var lineVal))
                    {
                        docStyle.LineSpacing = lineVal / 240.0;
                    }
                }

                var indent = paraProps.Indentation;
                if (indent != null)
                {
                    if (indent.Left?.Value != null &&
                        int.TryParse(indent.Left.Value, out var left))
                    {
                        docStyle.LeftIndent = TwipsToCm(left);
                    }
                    if (indent.Right?.Value != null &&
                        int.TryParse(indent.Right.Value, out var right))
                    {
                        docStyle.RightIndent = TwipsToCm(right);
                    }
                    if (indent.FirstLine?.Value != null &&
                        int.TryParse(indent.FirstLine.Value, out var firstLine))
                    {
                        docStyle.FirstLineIndent = TwipsToCm(firstLine);
                    }
                }

                if (paraProps.OutlineLevel?.Val?.Value != null)
                {
                    docStyle.OutlineLevel = paraProps.OutlineLevel.Val.Value + 1;
                }
            }

            result.Add(docStyle);
        }

        return result.Count > 0 ? result : DefaultWordStyles.GetDefaultStyles();
    }

    private string TranslateStyleName(string name)
    {
        return name.ToLower() switch
        {
            "normal" => "Normalny",
            "heading 1" or "heading1" => "Nagłówek 1",
            "heading 2" or "heading2" => "Nagłówek 2",
            "heading 3" or "heading3" => "Nagłówek 3",
            "heading 4" or "heading4" => "Nagłówek 4",
            "heading 5" or "heading5" => "Nagłówek 5",
            "heading 6" or "heading6" => "Nagłówek 6",
            "title" => "Tytuł",
            "subtitle" => "Podtytuł",
            "quote" => "Cytat",
            "intense quote" => "Cytat intensywny",
            "list paragraph" => "Akapit listy",
            "no spacing" => "Bez odstępów",
            "toc heading" => "Nagłówek spisu treści",
            _ => name
        };
    }

    private double TwipsToCm(int twips) => Math.Round(OoxmlUnits.TwipsToCm(twips), 2);

    private void LoadDocumentImages(WordprocessingDocument document)
    {
        var mainPart = document.MainDocumentPart;
        if (mainPart == null) return;

        foreach (var imagePart in mainPart.ImageParts)
            LoadImageFromPart(mainPart, imagePart);

        foreach (var headerPart in mainPart.HeaderParts)
            foreach (var imagePart in headerPart.ImageParts)
                LoadImageFromPart(headerPart, imagePart);

        foreach (var footerPart in mainPart.FooterParts)
            foreach (var imagePart in footerPart.ImageParts)
                LoadImageFromPart(footerPart, imagePart);
    }

    private static string ImageCacheKey(OpenXmlPart part, string relationshipId)
        => $"{part.Uri}|{relationshipId}";

    private void LoadImageFromPart(OpenXmlPart part, ImagePart imagePart)
    {
        var relationshipId = part.GetIdOfPart(imagePart);
        var cacheKey = ImageCacheKey(part, relationshipId);
        if (_images.ContainsKey(cacheKey)) return;

        using var stream = imagePart.GetStream();
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);

        var rawBytes = memoryStream.ToArray();
        var contentType = NormalizeImageContentType(imagePart.ContentType);

        if (IsSvgContentType(contentType))
        {
            if (rawBytes.Length > MaxSvgBytes)
            {
                _log.LogWarning("SVG part pominięty (za duży): part={PartUri} size={Size}B limit={Limit}B",
                    imagePart.Uri, rawBytes.Length, MaxSvgBytes);
                return;
            }
            var sanitized = _graphics.SanitizeSvg(System.Text.Encoding.UTF8.GetString(rawBytes));
            if (sanitized == null)
            {
                _log.LogWarning("SVG part pominięty (niepoprawny/niebezpieczny): part={PartUri}", imagePart.Uri);
                return;
            }
            _images[cacheKey] = new DocumentImage
            {
                Id = relationshipId,
                ContentType = "image/svg+xml",
                Base64Data = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sanitized))
            };
            return;
        }

        if (IsNonBrowserNativeContentType(contentType))
        {
            var converted = _graphics.ConvertForEditor(new GraphicSource
            {
                Data = rawBytes,
                ContentType = contentType,
                SourcePath = imagePart.Uri?.ToString(),
                Origin = GraphicOrigin.LegacyDocxPart
            });
            if (converted.Web is { IsBlankFallback: false } w && w.MimeType != "image/svg+xml")
            {
                rawBytes = w.Data;
                contentType = w.MimeType;
            }
            else if (converted.Diagnostics.Status is GraphicConversionStatus.Fallback
                     or GraphicConversionStatus.Unsupported or GraphicConversionStatus.Rejected)
            {
                _log.LogWarning(
                    "Media part bez rastra web: part={PartUri} relId={RelId} declaredType={ContentType} " +
                    "size={Size}B status={Status} powód={Reason}",
                    imagePart.Uri, relationshipId, imagePart.ContentType, rawBytes.Length,
                    converted.Diagnostics.Status, converted.Diagnostics.FailureReason);
            }
        }

        _images[cacheKey] = new DocumentImage
        {
            Id = relationshipId,
            ContentType = contentType,
            Base64Data = System.Convert.ToBase64String(rawBytes)
        };
    }

    private const int MaxSvgBytes = 2 * 1024 * 1024;

    private static string NormalizeImageContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;
        var ct = contentType.Trim();
        if (ct.Equals("img/svg+xml", StringComparison.OrdinalIgnoreCase)
            || ct.Equals("image/svg", StringComparison.OrdinalIgnoreCase))
            return "image/svg+xml";
        return ct;
    }

    private static bool IsSvgContentType(string? contentType)
        => !string.IsNullOrEmpty(contentType)
           && contentType.Contains("svg", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonBrowserNativeContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var ct = contentType.ToLowerInvariant();
        return ct.Contains("emf") || ct.Contains("wmf") || ct.Contains("metafile")
            || ct.Contains("tiff") || ct.Contains("tif")
            || ct.Contains("emz") || ct.Contains("wmz");
    }

    private void LoadNumberingPictureBullets()
    {
        if (_numberingPart?.Numbering == null) return;

        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        foreach (var picBullet in _numberingPart.Numbering.Elements<NumberingPictureBullet>())
        {
            int id;
            string? relId = null;
            try
            {
                var xml = XElement.Parse(picBullet.OuterXml);
                var idAttr = xml.Attribute(w + "numPicBulletId")?.Value;
                if (!int.TryParse(idAttr, out id)) continue;

                relId = xml.Descendants()
                    .Select(e => e.Attribute(r + "id")?.Value
                                 ?? e.Attribute(r + "embed")?.Value
                                 ?? e.Attribute(r + "link")?.Value)
                    .FirstOrDefault(v => !string.IsNullOrEmpty(v));
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(relId)) continue;

            try
            {
                if (_numberingPart.GetPartById(relId) is ImagePart imagePart)
                {
                    using var stream = imagePart.GetStream();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    var bytes = ms.ToArray();
                    var contentType = imagePart.ContentType;
                    if (IsNonBrowserNativeContentType(contentType))
                    {
                        var conv = _graphics.ConvertForEditor(new GraphicSource
                        {
                            Data = bytes, ContentType = contentType, Origin = GraphicOrigin.LegacyDocxPart
                        });
                        if (conv.Web != null)
                        {
                            _picBulletDataUris[id] = conv.Web.ToDataUrl();
                            continue;
                        }
                    }
                    var b64 = System.Convert.ToBase64String(bytes);
                    _picBulletDataUris[id] = $"data:{contentType};base64,{b64}";
                }
            }
            catch
            {
            }
        }
    }

    private string ConvertElementToHtml(OpenXmlElement element, WordprocessingDocument document)
    {
        return element switch
        {
            Paragraph p => ConvertParagraphToHtml(p, document),
            Table t => ConvertTableToHtml(t, document),
            SdtBlock sdt => ConvertSdtBlockToHtml(sdt, document),
            _ => string.Empty
        };
    }

    private string ConvertParagraphToHtml(Paragraph paragraph, WordprocessingDocument document, OpenXmlPart? sourcePart = null)
    {
        if (IsPageBreakOnlyParagraph(paragraph))
            return "<div class=\"page-break\"></div>";

        var html = new StringBuilder();
        var paraProps = paragraph.ParagraphProperties;

        var styleId = paraProps?.ParagraphStyleId?.Val?.Value;
        var headingLevel = GetHeadingLevel(styleId);
        
        var isListItem = IsListParagraph(paragraph);
        
        var tag = headingLevel > 0 ? $"h{headingLevel}" : "p";

        var docClass = GetDocStyleClass(styleId);

        var isInTableCell = paragraph.Ancestors<TableCell>().Any();
        var cssBuilder = new StringBuilder();
        if (isInTableCell && !string.IsNullOrEmpty(_tableParagraphDefaultCss))
        {
            cssBuilder.Append(_tableParagraphDefaultCss);
        }
        if (styleId != null && _styles.TryGetValue(styleId, out var styleCss))
        {
            cssBuilder.Append(styleCss);
        }
        cssBuilder.Append(GetParagraphStyle(paraProps, ResolveParagraphLineFont(paragraph)));

        var borderCss = GetParagraphBorderCss(paraProps);
        if (!string.IsNullOrEmpty(borderCss))
        {
            cssBuilder.Append(borderCss);
        }
        
        var effectiveTabStops = GetEffectiveTabStops(paraProps);
        var hasComplexField = paragraph.Descendants<FieldChar>().Any();
        var hasTabChar = paragraph.Descendants<TabChar>().Any();
        var hasPositionalTab = paragraph.Descendants<PositionalTab>().Any();

        var useLeaderTabs = hasTabChar
            && effectiveTabStops.Any(s => s.Leader != null)
            && !isInTableCell;

        var lastTextAlign = Regex.Matches(cssBuilder.ToString(), @"text-align\s*:\s*([a-z]+)")
            .Select(m => m.Groups[1].Value).LastOrDefault();
        var centeredWithTabChar = hasTabChar && lastTextAlign is "center" or "right";

        var tabTextOverflowsStops = hasTabChar && !hasPositionalTab
            && PositionedTabTextOverflowsStops(paragraph, effectiveTabStops);

        var usePositionedTabs = !useLeaderTabs
            && (hasTabChar || hasPositionalTab)
            && !isInTableCell
            && !centeredWithTabChar
            && !tabTextOverflowsStops;

        var useFlexTabs = !useLeaderTabs && !usePositionedTabs
            && hasTabChar
            && ParagraphHasAlignmentTab(paraProps);
        if (useFlexTabs)
            cssBuilder.Append("display:flex;align-items:baseline;width:100%;");
        if (useLeaderTabs)
            cssBuilder.Append("display:flex;align-items:baseline;");
        if (usePositionedTabs)
            cssBuilder.Append("position:relative;");

        var tabStopsAttr = effectiveTabStops.Count > 0
            ? $" data-tab-stops=\"{SerializeTabStops(effectiveTabStops)}\""
            : string.Empty;

        if (HasPageBreakBefore(paraProps))
            cssBuilder.Append("page-break-before:always;");
        else if (paraProps?.GetFirstChild<PageBreakBefore>() != null)
            cssBuilder.Append("page-break-before:auto;");

        var cssStyle = DeduplicateCss(cssBuilder.ToString());

        if (!string.IsNullOrEmpty(borderCss)
            || cssStyle.Contains("background-color", StringComparison.OrdinalIgnoreCase))
        {
            cssStyle = Regex.Replace(cssStyle, @"(?<![\w-])padding-bottom\s*:", "margin-bottom:");
            if (!Regex.IsMatch(cssStyle, @"(?<![\w-])margin-bottom\s*:")
                && _defaultSpacingAfterTw != null && int.TryParse(_defaultSpacingAfterTw, out var defAfterTw))
            {
                cssStyle += string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "margin-bottom:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(defAfterTw));
            }
            cssStyle = SetCssProperty(cssStyle, "padding-bottom", "0");
        }

        if (cssStyle.Contains("--w-contextual-spacing"))
        {
            var myStyleId = EffectiveParagraphStyleId(paragraph);
            if (paragraph.PreviousSibling() is Paragraph prevPara
                && EffectiveParagraphStyleId(prevPara) == myStyleId)
            {
                cssStyle = SetCssProperty(cssStyle, "margin-top", "0");
            }
            if (paragraph.NextSibling() is Paragraph nextPara
                && EffectiveParagraphStyleId(nextPara) == myStyleId)
            {
                cssStyle = SetCssProperty(cssStyle, "margin-bottom", "0");
                cssStyle = SetCssProperty(cssStyle, "padding-bottom", "0");
            }
        }
        var classAttr = docClass != null ? $" class=\"{docClass}\"" : string.Empty;
        var dataStyleAttr = !string.IsNullOrEmpty(styleId) && (docClass != null || IsTocParagraphStyleId(styleId))
            ? $" data-style-id=\"{System.Net.WebUtility.HtmlEncode(styleId)}\""
            : string.Empty;

        var pendingTextBoxesBefore = _pendingTextBoxes.Count;
        var openTagIndex = html.Length;

        if (isListItem)
        {
            html.Append($"<li{classAttr}{dataStyleAttr}{tabStopsAttr} style=\"{cssStyle}\">");
        }
        else
        {
            html.Append($"<{tag}{classAttr}{dataStyleAttr}{tabStopsAttr} style=\"{cssStyle}\">");
        }

        var prevFlexTabs = _flexTabs;
        _flexTabs = useFlexTabs;

        if (useLeaderTabs)
        {
            html.Append(BuildLeaderTabContent(paragraph, effectiveTabStops, document, sourcePart));
        }
        else if (usePositionedTabs)
        {
            html.Append(BuildPositionedTabContent(paragraph, effectiveTabStops, document, sourcePart));
        }
        else if (hasComplexField)
        {
            html.Append(ConvertComplexFieldParagraphContent(paragraph, document, sourcePart));
        }
        else
        {
            foreach (var child in paragraph.Elements())
            {
                switch (child)
                {
                    case Run run:
                        html.Append(ConvertRunToHtml(run, document, sourcePart));
                        break;
                    case Hyperlink hyperlink:
                        html.Append(ConvertHyperlinkToHtml(hyperlink, document, sourcePart));
                        break;
                    case SimpleField simpleField:
                        html.Append(ConvertSimpleFieldToHtml(simpleField));
                        break;
                    case SdtRun sdtRun:
                        html.Append(ConvertSdtRunToHtml(sdtRun, document, sourcePart));
                        break;
                    case BookmarkStart bookmark:
                        html.Append(RenderBookmarkStart(bookmark));
                        break;
                }
            }
        }

        _flexTabs = prevFlexTabs;

        if (!paragraph.Elements<Run>().Any() && !paragraph.Elements<Hyperlink>().Any() && !paragraph.Elements<SimpleField>().Any())
        {
            html.Append("&nbsp;");
        }

        html.Append(isListItem ? "</li>" : $"</{tag}>");

        if (_pendingTextBoxes.Count > pendingTextBoxesBefore)
        {
            var hoisted = string.Concat(_pendingTextBoxes.Skip(pendingTextBoxesBefore));
            _pendingTextBoxes.RemoveRange(pendingTextBoxesBefore, _pendingTextBoxes.Count - pendingTextBoxesBefore);
            if (isListItem)
                html.Insert(html.Length - "</li>".Length, hoisted);
            else
                html.Insert(openTagIndex, hoisted);
        }

        return html.ToString();
    }

    private static bool IsPageBreakOnlyParagraph(Paragraph paragraph)
    {
        var hasPageBreak = paragraph.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page);
        if (!hasPageBreak) return false;

        var hasText = paragraph.Descendants<Text>().Any(t => !string.IsNullOrEmpty(t.Text));
        var hasGraphics = paragraph.Descendants<Drawing>().Any() || paragraph.Descendants<Picture>().Any();
        return !hasText && !hasGraphics;
    }

    private static bool HasPageBreakBefore(ParagraphProperties? paraProps)
    {
        var pbb = paraProps?.GetFirstChild<PageBreakBefore>();
        if (pbb == null) return false;
        return pbb.Val == null || pbb.Val.Value;
    }

    private static bool ParagraphHasAlignmentTab(OpenXmlElement? paraProps)
    {
        var tabs = paraProps?.GetFirstChild<Tabs>();
        if (tabs == null) return false;

        return tabs.Elements<TabStop>().Any(t =>
            t.Val?.Value == TabStopValues.Center ||
            t.Val?.Value == TabStopValues.Right ||
            t.Val?.Value == TabStopValues.End);
    }

    private sealed record TabStopInfo(int PositionTwips, string Alignment, string? Leader);

    private List<TabStopInfo> GetEffectiveTabStops(ParagraphProperties? paraProps)
    {
        var result = new List<TabStopInfo>();

        void Apply(Tabs? tabs)
        {
            if (tabs == null) return;
            foreach (var t in tabs.Elements<TabStop>())
            {
                if (t.Position?.Value is not { } pos) continue;
                result.RemoveAll(x => x.PositionTwips == pos);
                var val = t.Val?.Value;
                if (val == TabStopValues.Clear) continue;
                if (val == TabStopValues.Bar) continue;
                result.Add(new TabStopInfo(pos, MapTabAlignment(val), MapTabLeader(t.Leader?.Value)));
            }
        }

        foreach (var style in GetParagraphStyleChainRootFirst(paraProps?.ParagraphStyleId?.Val?.Value))
            Apply(style.StyleParagraphProperties?.GetFirstChild<Tabs>());
        Apply(paraProps?.GetFirstChild<Tabs>());

        result.Sort((a, b) => a.PositionTwips.CompareTo(b.PositionTwips));
        return result;
    }

    private IEnumerable<Style> GetParagraphStyleChainRootFirst(string? styleId)
    {
        var chain = new List<Style>();
        var visited = new HashSet<string>();
        while (styleId != null && visited.Add(styleId) && _rawStyles.TryGetValue(styleId, out var style))
        {
            chain.Add(style);
            styleId = style.BasedOn?.Val?.Value;
        }
        chain.Reverse();
        return chain;
    }

    private static string MapTabAlignment(TabStopValues? val)
    {
        if (val == TabStopValues.Center) return "center";
        if (val == TabStopValues.Right || val == TabStopValues.End) return "right";
        if (val == TabStopValues.Decimal) return "decimal";
        return "left";
    }

    private static string? MapTabLeader(TabStopLeaderCharValues? leader)
    {
        if (leader == null || leader == TabStopLeaderCharValues.None) return null;
        if (leader == TabStopLeaderCharValues.Dot) return "dot";
        if (leader == TabStopLeaderCharValues.Hyphen) return "hyphen";
        if (leader == TabStopLeaderCharValues.Underscore) return "underscore";
        if (leader == TabStopLeaderCharValues.MiddleDot) return "middleDot";
        if (leader == TabStopLeaderCharValues.Heavy) return "heavy";
        return null;
    }

    private static string SerializeTabStops(List<TabStopInfo> stops) =>
        string.Join(";", stops.Select(s => s.Leader == null
            ? $"{s.PositionTwips}:{s.Alignment}"
            : $"{s.PositionTwips}:{s.Alignment}:{s.Leader}"));

    private string BuildPositionedTabContent(Paragraph paragraph, List<TabStopInfo> stops,
        WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        var segmentElements = new List<List<OpenXmlElement>> { new() };
        var segmentStops = new List<TabStopInfo?> { null };
        var nextRealStop = 0;
        var lastStopTw = 0;
        TabStopInfo NextRealStop()
        {
            if (nextRealStop < stops.Count)
            {
                var s = stops[nextRealStop++];
                lastStopTw = Math.Max(lastStopTw, s.PositionTwips);
                return s;
            }
            lastStopTw = (lastStopTw / _defaultTabStopTwips + 1) * _defaultTabStopTwips;
            return new TabStopInfo(lastStopTw, "left", null);
        }

        foreach (var child in paragraph.Elements())
        {
            if (child is ParagraphProperties) continue;
            if (child is Run run && run.Elements().Any(rc => rc is TabChar or PositionalTab))
            {
                var current = CloneRunShell(run);
                void FlushSubRun()
                {
                    if (current.ChildElements.Count > (run.RunProperties != null ? 1 : 0))
                        segmentElements[^1].Add(current);
                    current = CloneRunShell(run);
                }
                foreach (var rc in run.Elements())
                {
                    if (rc is RunProperties) continue;
                    if (rc is TabChar)
                    {
                        FlushSubRun();
                        segmentElements.Add(new List<OpenXmlElement>());
                        segmentStops.Add(NextRealStop());
                    }
                    else if (rc is PositionalTab ptab)
                    {
                        FlushSubRun();
                        segmentElements.Add(new List<OpenXmlElement>());
                        segmentStops.Add(SyntheticStopForPositionalTab(ptab));
                    }
                    else
                    {
                        current.AppendChild(rc.CloneNode(true));
                    }
                }
                FlushSubRun();
            }
            else if (child is Hyperlink hyperlink
                && hyperlink.Descendants().Any(d => d is TabChar or PositionalTab))
            {
                var currentLink = (Hyperlink)hyperlink.CloneNode(false);
                void FlushLink()
                {
                    if (currentLink.HasChildren) segmentElements[^1].Add(currentLink);
                    currentLink = (Hyperlink)hyperlink.CloneNode(false);
                }
                foreach (var linkChild in hyperlink.Elements())
                {
                    if (linkChild is Run linkRun && linkRun.Elements().Any(rc => rc is TabChar or PositionalTab))
                    {
                        var subRun = CloneRunShell(linkRun);
                        void FlushLinkSubRun()
                        {
                            if (subRun.ChildElements.Count > (linkRun.RunProperties != null ? 1 : 0))
                                currentLink.AppendChild(subRun);
                            subRun = CloneRunShell(linkRun);
                        }
                        foreach (var rc in linkRun.Elements())
                        {
                            if (rc is RunProperties) continue;
                            if (rc is TabChar)
                            {
                                FlushLinkSubRun();
                                FlushLink();
                                segmentElements.Add(new List<OpenXmlElement>());
                                segmentStops.Add(NextRealStop());
                            }
                            else if (rc is PositionalTab linkPtab)
                            {
                                FlushLinkSubRun();
                                FlushLink();
                                segmentElements.Add(new List<OpenXmlElement>());
                                segmentStops.Add(SyntheticStopForPositionalTab(linkPtab));
                            }
                            else
                            {
                                subRun.AppendChild(rc.CloneNode(true));
                            }
                        }
                        FlushLinkSubRun();
                    }
                    else
                    {
                        currentLink.AppendChild(linkChild.CloneNode(true));
                    }
                }
                FlushLink();
            }
            else
            {
                segmentElements[^1].Add(child);
            }
        }

        var state = new ComplexFieldState();
        var segments = segmentElements.Select(els =>
        {
            var sb = new StringBuilder();
            AppendComplexFieldContent(els, sb, state, document, sourcePart);
            return sb;
        }).ToList();

        var html = new StringBuilder();
        html.Append(segments[0].Length > 0 ? segments[0].ToString() : "&#8203;");

        for (int k = 1; k < segments.Count; k++)
        {
            var stop = segmentStops[k];
            if (stop == null)
            {
                html.Append(segments[k]);
                continue;
            }
            var leftPx = TwipsToPx(stop.PositionTwips);
            var transform = stop.Alignment switch
            {
                "center" => "transform:translateX(-50%);",
                "right" => "transform:translateX(-100%);",
                _ => string.Empty
            };
            html.Append($"<span class=\"docx-tab-seg\" data-tab-align=\"{stop.Alignment}\" " +
                        $"style=\"position:absolute;left:{leftPx}px;{transform}white-space:pre;\">")
                .Append(segments[k])
                .Append("</span>");
        }

        return html.ToString();
    }

    private bool PositionedTabTextOverflowsStops(Paragraph paragraph, List<TabStopInfo> stops)
    {
        const double CharWidthEm = 0.5;
        var defaultPt = _defaultFontSizePt ?? (_defaults.FontSizePt > 0 ? _defaults.FontSizePt : 11);
        double PxPerChar(Run? run)
        {
            var pt = defaultPt;
            var sz = run?.RunProperties?.FontSize?.Val?.Value;
            if (sz != null && double.TryParse(sz, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var half) && half > 0)
                pt = half / 2.0;
            return pt * 96.0 / 72.0 * CharWidthEm;
        }

        var nextRealStop = 0;
        var lastStopTw = 0;
        TabStopInfo NextStop()
        {
            if (nextRealStop < stops.Count)
            {
                var s = stops[nextRealStop++];
                lastStopTw = Math.Max(lastStopTw, s.PositionTwips);
                return s;
            }
            lastStopTw = (lastStopTw / _defaultTabStopTwips + 1) * _defaultTabStopTwips;
            return new TabStopInfo(lastStopTw, "left", null);
        }

        double? segStartPx = 0;
        double segWidthPx = 0;
        var overflow = false;

        void OnRunContent(OpenXmlElement rc, Run run)
        {
            switch (rc)
            {
                case TabChar:
                    var stop = NextStop();
                    if (stop.Alignment == "left")
                    {
                        if (segStartPx.HasValue && segStartPx.Value + segWidthPx > TwipsToPx(stop.PositionTwips))
                            overflow = true;
                        segStartPx = TwipsToPx(stop.PositionTwips);
                    }
                    else
                    {
                        segStartPx = null;
                    }
                    segWidthPx = 0;
                    break;
                case PositionalTab:
                    segStartPx = null;
                    segWidthPx = 0;
                    break;
                case Text t:
                    segWidthPx += (t.Text?.Length ?? 0) * PxPerChar(run);
                    break;
            }
        }

        foreach (var child in paragraph.Elements())
        {
            switch (child)
            {
                case ParagraphProperties:
                    continue;
                case Run run:
                    foreach (var rc in run.Elements()) OnRunContent(rc, run);
                    break;
                case Hyperlink link:
                    foreach (var lc in link.Elements())
                    {
                        if (lc is Run linkRun)
                            foreach (var rc in linkRun.Elements()) OnRunContent(rc, linkRun);
                        else
                            segWidthPx += lc.InnerText.Length * PxPerChar(null);
                    }
                    break;
                default:
                    segWidthPx += child.InnerText.Length * PxPerChar(null);
                    break;
            }
            if (overflow) return true;
        }
        return overflow;
    }

    private static Run CloneRunShell(Run run)
    {
        var shell = new Run();
        if (run.RunProperties != null)
            shell.AppendChild(run.RunProperties.CloneNode(true));
        return shell;
    }

    private TabStopInfo SyntheticStopForPositionalTab(PositionalTab ptab)
    {
        var widthTw = _availableContentWidthTwips ?? FullContentWidthTwips() ?? 9072;
        var alignment = ptab.Alignment?.Value;
        if (alignment == AbsolutePositionTabAlignmentValues.Center)
            return new TabStopInfo((int)(widthTw / 2), "center", null);
        if (alignment == AbsolutePositionTabAlignmentValues.Right)
            return new TabStopInfo((int)widthTw, "right", null);
        return new TabStopInfo(0, "left", null);
    }

    private string BuildLeaderTabContent(Paragraph paragraph, List<TabStopInfo> stops,
        WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        var segments = new List<StringBuilder> { new() };
        var state = new ComplexFieldState();
        string? openAnchor = null;

        void AppendRun(Run run)
        {
            if (run.GetFirstChild<FieldChar>() != null
                || (state.InField && run.GetFirstChild<FieldCode>() != null)
                || (state.InField && state.Separated && state.ValueHandled))
            {
                AppendComplexFieldRun(run, segments[^1], state, document, sourcePart);
                return;
            }

            var flags = GetRunSemanticFlags(run.RunProperties);
            var (prefix, suffix) = BuildRunWrapper(run.RunProperties,
                flags.Bold, flags.Italic, flags.Underline, flags.Strike, flags.Sup, flags.Sub);
            var chunk = new StringBuilder();
            void FlushChunk()
            {
                if (chunk.Length > 0)
                {
                    segments[^1].Append(prefix).Append(chunk).Append(suffix);
                    chunk.Clear();
                }
            }
            foreach (var rc in run.Elements())
            {
                if (rc is TabChar)
                {
                    FlushChunk();
                    if (openAnchor != null) segments[^1].Append("</a>");
                    segments.Add(new StringBuilder());
                    if (openAnchor != null) segments[^1].Append(openAnchor);
                }
                else
                {
                    chunk.Append(ConvertRunChildToHtml(rc, document, sourcePart));
                }
            }
            FlushChunk();
        }

        void AppendElements(IEnumerable<OpenXmlElement> elements)
        {
            foreach (var child in elements)
            {
                switch (child)
                {
                    case Run run:
                        AppendRun(run);
                        break;
                    case Hyperlink hyperlink:
                        var openTag = BuildAnchorOpenTag(hyperlink, document);
                        segments[^1].Append(openTag);
                        openAnchor = openTag;
                        AppendElements(hyperlink.Elements());
                        segments[^1].Append("</a>");
                        openAnchor = null;
                        break;
                    case SimpleField simpleField:
                        segments[^1].Append(ConvertSimpleFieldToHtml(simpleField));
                        break;
                    case SdtRun sdtRun:
                        AppendComplexFieldSdtRun(sdtRun, segments[^1], state, document, sourcePart);
                        break;
                    case BookmarkStart bookmark:
                        segments[^1].Append(RenderBookmarkStart(bookmark));
                        break;
                }
            }
        }

        AppendElements(paragraph.Elements());

        var html = new StringBuilder();
        html.Append("<span class=\"docx-tab-text\" style=\"min-width:0;\">")
            .Append(segments[0].Length > 0 ? segments[0].ToString() : "&#8203;")
            .Append("</span>");

        var tabCount = segments.Count - 1;
        for (int k = 1; k < segments.Count; k++)
        {
            var stopIndex = stops.Count - tabCount + (k - 1);
            var stop = stopIndex >= 0 && stopIndex < stops.Count ? stops[stopIndex] : null;
            if (stop?.Leader != null)
            {
                html.Append($"<span class=\"docx-tab-leader\" data-leader=\"{stop.Leader}\" " +
                            "style=\"flex:1 1 0;min-width:8px;position:relative;overflow:hidden;\">\t</span>");
            }
            else
            {
                html.Append("<span style=\"display:inline-block;min-width:2em;white-space:pre;\">\t</span>");
            }
            html.Append("<span class=\"docx-tab-text\" style=\"white-space:pre;\">")
                .Append(segments[k])
                .Append("</span>");
        }

        return html.ToString();
    }

    private string BuildAnchorOpenTag(Hyperlink hyperlink, WordprocessingDocument document)
    {
        var relationshipId = hyperlink.Id?.Value;
        var relationshipUrl = relationshipId != null
            ? document.MainDocumentPart?.HyperlinkRelationships
                .FirstOrDefault(r => r.Id == relationshipId)?.Uri?.OriginalString
            : null;

        var anchor = hyperlink.Anchor?.Value;
        if (string.IsNullOrEmpty(anchor) && relationshipUrl?.StartsWith('#') == true)
            anchor = relationshipUrl.TrimStart('#');

        if (string.IsNullOrEmpty(anchor) && !string.IsNullOrEmpty(relationshipUrl))
            return $"<a href=\"{EscapeHtml(relationshipUrl)}\" target=\"_blank\" style=\"color:#0563C1;text-decoration:underline;\">";

        if (!string.IsNullOrEmpty(anchor))
        {
            var encoded = System.Net.WebUtility.HtmlEncode(anchor);
            return $"<a href=\"#{encoded}\" data-anchor=\"{encoded}\" " +
                   "title=\"Ctrl+klik, aby przejść do elementu\" " +
                   "style=\"color:inherit;text-decoration:inherit;\">";
        }

        return "<a href=\"#\" target=\"_blank\" style=\"color:#0563C1;text-decoration:underline;\">";
    }

    private static string RenderBookmarkStart(BookmarkStart bookmark)
    {
        var name = bookmark.Name?.Value;
        if (string.IsNullOrEmpty(name) || name == "_GoBack") return string.Empty;
        return $"<span class=\"docx-bookmark\" data-bm-name=\"{System.Net.WebUtility.HtmlEncode(name)}\" style=\"display:none;\"></span>";
    }

    private string ConvertComplexFieldParagraphContent(Paragraph paragraph, WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        var html = new StringBuilder();
        AppendComplexFieldContent(paragraph.Elements(), html, new ComplexFieldState(), document, sourcePart);
        return html.ToString();
    }

    private static bool IsRoundTrippedFieldInstruction(string instruction)
    {
        var instr = instruction.TrimStart();
        return instr.StartsWith("TOC", StringComparison.OrdinalIgnoreCase)
            || instr.StartsWith("PAGEREF", StringComparison.OrdinalIgnoreCase);
    }

    private static string FieldInstructionName(string instruction) =>
        instruction.TrimStart().Split(' ', '\t').FirstOrDefault() ?? string.Empty;

    private static bool IsAutoDateFieldInstruction(string instruction)
    {
        var name = FieldInstructionName(instruction).ToUpperInvariant();
        if (name != "DATE" && name != "TIME") return false;
        return !instruction.Contains("\\!");
    }

    private static string DateFormatFromInstruction(string instruction)
    {
        var m = Regex.Match(instruction, "\\\\@\\s+\"([^\"]+)\"");
        if (!m.Success) m = Regex.Match(instruction, "\\\\@\\s+([^\\s\\\\]+)");
        if (!m.Success) return "dd.MM.yyyy";
        return m.Groups[1].Value.Replace("AM/PM", "tt").Replace("am/pm", "tt");
    }

    private string FieldDateSpan(string instruction, Run? run)
    {
        string text;
        try
        {
            text = DateTime.Now.ToString(
                DateFormatFromInstruction(instruction),
                System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
        }
        catch (FormatException)
        {
            text = DateTime.Now.ToString("dd.MM.yyyy");
        }
        var style = run?.RunProperties != null ? GetRunStyleClean(run.RunProperties) : string.Empty;
        return $"<span class=\"field-date\" data-fld-instr=\"{System.Net.WebUtility.HtmlEncode(instruction.Trim())}\" " +
               $"contenteditable=\"false\" style=\"{style}\">{EscapeHtml(text)}</span>";
    }

    private static string FieldMarkerBegin(string instruction) =>
        $"<span class=\"docx-fld-marker\" data-fld=\"begin\" data-fld-instr=\"{System.Net.WebUtility.HtmlEncode(instruction.Trim())}\" style=\"display:none;\"></span>";

    private const string FieldMarkerEndHtml =
        "<span class=\"docx-fld-marker\" data-fld=\"end\" style=\"display:none;\"></span>";

    private readonly List<bool> _openFieldFrames = new();

    private sealed class ComplexFieldState
    {
        public bool InField;
        public string Instruction = string.Empty;
        public bool Separated;
        public bool ValueHandled;
        public bool Locked;
    }

    private void AppendComplexFieldContent(IEnumerable<OpenXmlElement> elements, StringBuilder html,
        ComplexFieldState state, WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        foreach (var child in elements)
        {
            switch (child)
            {
                case Run run:
                    AppendComplexFieldRun(run, html, state, document, sourcePart);
                    break;
                case Hyperlink hyperlink:
                    html.Append(ConvertHyperlinkToHtml(hyperlink, document, sourcePart, state));
                    break;
                case SimpleField simpleField:
                    html.Append(ConvertSimpleFieldToHtml(simpleField));
                    break;
                case SdtRun sdtRun:
                    AppendComplexFieldSdtRun(sdtRun, html, state, document, sourcePart);
                    break;
                case BookmarkStart bookmark:
                    html.Append(RenderBookmarkStart(bookmark));
                    break;
            }
        }
    }

    private void AppendComplexFieldSdtRun(SdtRun sdtRun, StringBuilder html,
        ComplexFieldState state, WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        var content = sdtRun.GetFirstChild<SdtContentRun>();
        if (content == null) return;

        html.Append($"<span class=\"sdt-inline\"{BuildSdtDataAttrs(sdtRun.SdtProperties)}>");
        var innerStart = html.Length;
        AppendComplexFieldContent(content.Elements(), html, state, document, sourcePart);
        if (html.Length == innerStart) html.Append("&nbsp;");
        html.Append("</span>");
    }

    private void AppendComplexFieldRun(Run run, StringBuilder html, ComplexFieldState state,
        WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        var fieldChar = run.GetFirstChild<FieldChar>();
        if (fieldChar != null)
        {
            var fctVal = fieldChar.FieldCharType?.Value;
            if (fctVal == FieldCharValues.Begin)
            {
                state.InField = true;
                state.Instruction = string.Empty;
                state.Separated = false;
                state.ValueHandled = false;
                state.Locked = fieldChar.FieldLock?.Value == true;
                _openFieldFrames.Add(false);
            }
            else if (fctVal == FieldCharValues.Separate)
            {
                state.Separated = true;
                state.ValueHandled = false;
                var instr = state.Instruction.Trim().ToUpperInvariant();
                if (instr.Contains("PAGE") && !instr.Contains("PAGEREF")
                    && !instr.Contains("NUMPAGES") && !instr.Contains("SECTIONPAGES"))
                {
                    html.Append(FieldSpan("field-page", "{page}", run));
                    state.ValueHandled = true;
                }
                else if (instr.Contains("NUMPAGES") || instr.Contains("SECTIONPAGES"))
                {
                    html.Append(FieldSpan("field-numpages", "{pages}", run));
                    state.ValueHandled = true;
                }
                else if (!state.Locked && IsAutoDateFieldInstruction(state.Instruction))
                {
                    html.Append(FieldDateSpan(state.Instruction, run));
                    state.ValueHandled = true;
                }
                else if (IsRoundTrippedFieldInstruction(state.Instruction))
                {
                    html.Append(FieldMarkerBegin(state.Instruction));
                    if (_openFieldFrames.Count > 0) _openFieldFrames[^1] = true;
                }
            }
            else if (fctVal == FieldCharValues.End)
            {
                if (!state.Separated)
                {
                    var instrEnd = state.Instruction.Trim().ToUpperInvariant();
                    if (instrEnd.Contains("PAGE") && !instrEnd.Contains("PAGEREF") && !instrEnd.Contains("NUMPAGES"))
                    {
                        html.Append(FieldSpan("field-page", "{page}", run));
                    }
                    else if (instrEnd.Contains("NUMPAGES") || instrEnd.Contains("SECTIONPAGES"))
                    {
                        html.Append(FieldSpan("field-numpages", "{pages}", run));
                    }
                    else if (!state.Locked && IsAutoDateFieldInstruction(state.Instruction))
                    {
                        html.Append(FieldDateSpan(state.Instruction, run));
                    }
                    else if (IsRoundTrippedFieldInstruction(state.Instruction))
                    {
                        html.Append(FieldMarkerBegin(state.Instruction));
                        if (_openFieldFrames.Count > 0) _openFieldFrames[^1] = true;
                    }
                }
                if (_openFieldFrames.Count > 0)
                {
                    var markerEmitted = _openFieldFrames[^1];
                    _openFieldFrames.RemoveAt(_openFieldFrames.Count - 1);
                    if (markerEmitted) html.Append(FieldMarkerEndHtml);
                }
                state.InField = false;
                state.Instruction = string.Empty;
                state.Separated = false;
            }
            return;
        }

        if (state.InField)
        {
            var fieldCode = run.GetFirstChild<FieldCode>();
            if (fieldCode != null)
            {
                state.Instruction += fieldCode.Text;
                return;
            }

            if (state.Separated && state.ValueHandled) return;
        }

        html.Append(ConvertRunToHtml(run, document, sourcePart));
    }

    private string GetParagraphBorderCss(ParagraphProperties? props)
    {
        if (props == null) return string.Empty;
        
        var borders = props.ParagraphBorders;
        if (borders == null) return string.Empty;
        
        var css = new StringBuilder();
        
        if (borders.TopBorder?.Val != null && borders.TopBorder.Val.Value != BorderValues.None && borders.TopBorder.Val.Value != BorderValues.Nil)
            css.Append($"border-top:{GetBorderCss(borders.TopBorder)};");
        
        if (borders.BottomBorder?.Val != null && borders.BottomBorder.Val.Value != BorderValues.None && borders.BottomBorder.Val.Value != BorderValues.Nil)
            css.Append($"border-bottom:{GetBorderCss(borders.BottomBorder)};");
        
        if (borders.LeftBorder?.Val != null && borders.LeftBorder.Val.Value != BorderValues.None && borders.LeftBorder.Val.Value != BorderValues.Nil)
            css.Append($"border-left:{GetBorderCss(borders.LeftBorder)};");
        
        if (borders.RightBorder?.Val != null && borders.RightBorder.Val.Value != BorderValues.None && borders.RightBorder.Val.Value != BorderValues.Nil)
            css.Append($"border-right:{GetBorderCss(borders.RightBorder)};");
        
        if (css.Length > 0)
            css.Append("padding:4px 8px;");
        
        return css.ToString();
    }

    private string GetBorderCss(BorderType border)
    {
        var borderVal = border.Val?.Value;

        if (borderVal == null || borderVal == BorderValues.None || borderVal == BorderValues.Nil)
            return "none";

        var size = border.Size?.Value ?? 4;
        var sizePx = Math.Max(0.5, size / 6.0);
        var color = border.Color?.Value;
        if ((color == null || color == "auto") && border.ThemeColor?.HasValue == true)
        {
            var themeHex = ResolveThemeColor(border.ThemeColor.Value)?.TrimStart('#');
            if (themeHex != null)
                color = ApplyTintShade(themeHex, border.ThemeTint?.Value, border.ThemeShade?.Value);
        }
        if (color == null || color == "auto") color = "000000";

        string style = "solid";
        if (borderVal == BorderValues.Single) style = "solid";
        else if (borderVal == BorderValues.Double) style = "double";
        else if (borderVal == BorderValues.Dotted) style = "dotted";
        else if (borderVal == BorderValues.Dashed) style = "dashed";
        else if (borderVal == BorderValues.DashSmallGap) style = "dashed";
        else if (borderVal == BorderValues.DotDash) style = "dashed";
        else if (borderVal == BorderValues.Triple) style = "double";
        else if (borderVal == BorderValues.Thick) style = "solid";
        else if (borderVal == BorderValues.ThickThinSmallGap) style = "double";
        else if (borderVal == BorderValues.ThinThickSmallGap) style = "double";

        if (style == "double")
            sizePx = Math.Max(3.0, sizePx * 3.0);

        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#}px {1} #{2}", sizePx, style, color);
    }

    private int GetHeadingLevel(string? styleId)
    {
        if (string.IsNullOrEmpty(styleId)) return 0;
        var match = Regex.Match(styleId, @"Heading(\d)|Nagwek(\d)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var level = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return int.Parse(level);
        }
        return 0;
    }

    private static bool IsTocParagraphStyleId(string styleId) =>
        Regex.IsMatch(styleId, @"^TOC(\d|Heading)$", RegexOptions.IgnoreCase)
        || styleId.StartsWith("Spistre", StringComparison.OrdinalIgnoreCase)
        || styleId.StartsWith("Nagwekspisutreci", StringComparison.OrdinalIgnoreCase);

    private static string? GetDocStyleClass(string? styleId)
    {
        if (string.IsNullOrEmpty(styleId)) return null;
        var s = styleId.Replace(" ", string.Empty);
        if (string.Equals(s, "Title", StringComparison.OrdinalIgnoreCase)) return "doc-title";
        if (string.Equals(s, "Subtitle", StringComparison.OrdinalIgnoreCase)) return "doc-subtitle";
        return null;
    }

    private string GetListType(NumberingProperties? numPr, WordprocessingDocument document)
    {
        var info = GetListLevelInfo(numPr);
        return info.Tag;
    }

    private int ResolveAbstractNumId(int numId)
    {
        if (_numberingPart?.Numbering == null) return -1;
        var visited = new HashSet<int>();
        while (visited.Add(numId))
        {
            var numInstance = _numberingPart.Numbering.Elements<NumberingInstance>()
                .FirstOrDefault(n => n.NumberID?.Value == numId);
            var absId = numInstance?.AbstractNumId?.Val?.Value;
            if (absId == null) return -1;

            var abs = _numberingPart.Numbering.Elements<AbstractNum>()
                .FirstOrDefault(a => a.AbstractNumberId?.Value == absId);
            var linkedStyleId = abs?.GetFirstChild<NumberingStyleLink>()?.Val?.Value;
            if (string.IsNullOrEmpty(linkedStyleId))
                return absId.Value;

            var linkedNumId = _rawStyles.TryGetValue(linkedStyleId, out var style)
                ? style.StyleParagraphProperties?.GetFirstChild<NumberingProperties>()?.NumberingId?.Val?.Value
                : null;
            if (linkedNumId == null) return absId.Value;
            numId = linkedNumId.Value;
        }
        return -1;
    }

    private (Level? levelDef, int startOverride, bool fromInstanceOverride) FindLevelDefinition(int numId, int level)
    {
        if (_numberingPart?.Numbering == null) return (null, -1, false);

        var numInstance = _numberingPart.Numbering.Elements<NumberingInstance>()
            .FirstOrDefault(n => n.NumberID?.Value == numId);
        if (numInstance == null) return (null, -1, false);

        var levelOverrideElem = numInstance.Elements<LevelOverride>()
            .FirstOrDefault(lo => lo.LevelIndex?.Value == level);
        int startOverride = levelOverrideElem?.StartOverrideNumberingValue?.Val?.Value ?? -1;

        Level? levelDef = levelOverrideElem?.GetFirstChild<Level>();
        var fromInstanceOverride = levelDef != null;
        if (levelDef == null)
        {
            var absId = ResolveAbstractNumId(numId);
            var abstractNum = _numberingPart.Numbering.Elements<AbstractNum>()
                .FirstOrDefault(a => a.AbstractNumberId?.Value == absId);
            levelDef = abstractNum?.Elements<Level>()
                .FirstOrDefault(l => l.LevelIndex?.Value == level);
        }
        return (levelDef, startOverride, fromInstanceOverride);
    }

    private int GetLevelStart(int numId, int level)
    {
        var (levelDef, startOverride, _) = FindLevelDefinition(numId, level);
        if (startOverride > 0) return startOverride;
        return levelDef?.StartNumberingValue?.Val?.Value ?? 1;
    }

    private int CounterKeyFor(int numId)
    {
        var absId = ResolveAbstractNumId(numId);
        return absId >= 0 ? absId : -numId;
    }

    private void ApplyStartOverridesOnFirstUse(int numId, int counterKey)
    {
        if (!_appliedStartOverrides.Add(numId)) return;
        if (_numberingPart?.Numbering == null) return;

        var numInstance = _numberingPart.Numbering.Elements<NumberingInstance>()
            .FirstOrDefault(n => n.NumberID?.Value == numId);
        if (numInstance == null) return;

        foreach (var lo in numInstance.Elements<LevelOverride>())
        {
            var lvl = lo.LevelIndex?.Value;
            var so = lo.StartOverrideNumberingValue?.Val?.Value;
            if (lvl != null && so != null)
                _listCounters[(counterKey, lvl.Value)] = so.Value - 1;
        }
    }

    private int PeekNextListNumber(int numId, int level)
    {
        var key = CounterKeyFor(numId);
        ApplyStartOverridesOnFirstUse(numId, key);
        return _listCounters.TryGetValue((key, level), out var last)
            ? last + 1
            : GetLevelStart(numId, level);
    }

    private int NextListNumber(int numId, int level)
    {
        var key = CounterKeyFor(numId);
        ApplyStartOverridesOnFirstUse(numId, key);

        var next = _listCounters.TryGetValue((key, level), out var last)
            ? last + 1
            : GetLevelStart(numId, level);
        _listCounters[(key, level)] = next;

        for (int deeper = level + 1; deeper <= 8; deeper++)
        {
            if (!_listCounters.ContainsKey((key, deeper))) continue;
            var (deeperDef, _, _) = FindLevelDefinition(numId, deeper);
            var lvlRestart = deeperDef?.LevelRestart?.Val?.Value;
            if (lvlRestart == 0) continue;
            _listCounters.Remove((key, deeper));
        }
        return next;
    }

    private static string NumFmtToken(Level? levelDef)
    {
        var fmt = levelDef?.NumberingFormat?.Val?.Value;
        if (fmt == NumberFormatValues.Decimal) return "decimal";
        if (fmt == NumberFormatValues.DecimalZero) return "decimalZero";
        if (fmt == NumberFormatValues.LowerLetter) return "lowerLetter";
        if (fmt == NumberFormatValues.UpperLetter) return "upperLetter";
        if (fmt == NumberFormatValues.LowerRoman) return "lowerRoman";
        if (fmt == NumberFormatValues.UpperRoman) return "upperRoman";
        if (fmt == NumberFormatValues.Bullet) return "bullet";
        if (fmt == NumberFormatValues.None) return "none";
        var raw = levelDef?.NumberingFormat?.Val?.InnerText;
        return string.IsNullOrEmpty(raw) ? "decimal" : raw;
    }

    private readonly struct ListLevelInfo
    {
        public string Tag { get; init; }
        public string ListStyleType { get; init; }
        public string? BulletChar { get; init; }
        public string? BulletFont { get; init; }
        public string? BulletImageDataUri { get; init; }
        public int Start { get; init; }
        public string FmtToken { get; init; }
        public string? LvlText { get; init; }
        public int StartOverride { get; init; }
        public string? SuffixToken { get; init; }
        public bool IsLegal { get; init; }
        public int LvlRestart { get; init; }
        public int PicBulletId { get; init; }
        public string? IndLeftTw { get; init; }
        public string? IndHangingTw { get; init; }
        public string? IndFirstLineTw { get; init; }
        public bool FromInstanceOverride { get; init; }
        public string? MarkerColorHex { get; init; }
        public string? MarkerColorCss { get; init; }
        public string? MarkerSizeHalfPoints { get; init; }
    }

    private ListLevelInfo GetListLevelInfo(NumberingProperties? numPr, int levelOverride = -1)
    {
        var fallback = new ListLevelInfo
        {
            Tag = "ul", ListStyleType = "disc", Start = 1, FmtToken = "bullet",
            StartOverride = -1, LvlRestart = -1, PicBulletId = -1
        };
        if (numPr == null || _numberingPart?.Numbering == null) return fallback;

        var numId = numPr.NumberingId?.Val?.Value;
        if (numId == null) return fallback;
        var level = levelOverride >= 0 ? levelOverride : (numPr.NumberingLevelReference?.Val?.Value ?? 0);

        var (levelDef, startOverride, fromInstanceOverride) = FindLevelDefinition(numId.Value, level);
        if (levelDef == null) return fallback;

        var numFmt = levelDef.NumberingFormat?.Val?.Value;
        var levelText = levelDef.LevelText?.Val?.Value ?? string.Empty;
        var bulletFontRun = levelDef.NumberingSymbolRunProperties?.GetFirstChild<RunFonts>();
        var bulletFont = bulletFontRun?.Ascii?.Value
                         ?? bulletFontRun?.HighAnsi?.Value
                         ?? bulletFontRun?.ComplexScript?.Value
                         ?? bulletFontRun?.EastAsia?.Value;
        var markerColor = levelDef.NumberingSymbolRunProperties?.GetFirstChild<Color>();
        var markerColorCss = ResolveRunColorCss(markerColor);
        var markerColorHex = markerColor?.Val?.Value ?? markerColorCss?.TrimStart('#');
        var markerSizeHalfPoints = levelDef.NumberingSymbolRunProperties?.GetFirstChild<FontSize>()?.Val?.Value;
        var start = levelDef.StartNumberingValue?.Val?.Value ?? 1;

        string? suffixToken = null;
        var suffix = levelDef.LevelSuffix?.Val;
        if (suffix != null && suffix == LevelSuffixValues.Space) suffixToken = "space";
        else if (suffix != null && suffix == LevelSuffixValues.Nothing) suffixToken = "nothing";

        var isLgl = levelDef.IsLegalNumberingStyle;
        var isLegal = isLgl != null && (isLgl.Val == null || isLgl.Val.Value);

        var lvlRestart = levelDef.LevelRestart?.Val?.Value ?? -1;

        var lvlInd = levelDef.PreviousParagraphProperties?.GetFirstChild<Indentation>();

        string? bulletImageDataUri = null;
        var picBulletId = levelDef.LevelPictureBulletId?.Val?.Value;
        if (picBulletId.HasValue && _picBulletDataUris.TryGetValue(picBulletId.Value, out var picUri))
        {
            bulletImageDataUri = picUri;
        }

        string tag = "ul";
        string listStyle = "disc";
        string? bulletChar = null;

        if (numFmt == NumberFormatValues.Decimal) { tag = "ol"; listStyle = "decimal"; }
        else if (numFmt == NumberFormatValues.DecimalZero) { tag = "ol"; listStyle = "decimal-leading-zero"; }
        else if (numFmt == NumberFormatValues.UpperLetter) { tag = "ol"; listStyle = "upper-alpha"; }
        else if (numFmt == NumberFormatValues.LowerLetter) { tag = "ol"; listStyle = "lower-alpha"; }
        else if (numFmt == NumberFormatValues.UpperRoman) { tag = "ol"; listStyle = "upper-roman"; }
        else if (numFmt == NumberFormatValues.LowerRoman) { tag = "ol"; listStyle = "lower-roman"; }
        else if (numFmt == NumberFormatValues.Bullet)
        {
            tag = "ul";
            int codePoint = 0;
            if (!string.IsNullOrEmpty(levelText))
            {
                codePoint = char.IsHighSurrogate(levelText[0]) && levelText.Length > 1
                    ? char.ConvertToUtf32(levelText[0], levelText[1])
                    : levelText[0];
            }

            var fontLower = (bulletFont ?? string.Empty).ToLowerInvariant();
            bool isSymbolicFont = fontLower.Contains("wingdings") || fontLower.Contains("symbol");
            int lookup = (isSymbolicFont || (codePoint >= 0xF000 && codePoint <= 0xF0FF))
                ? (codePoint & 0xFF)
                : codePoint;

            int firstCodePointLength = !string.IsNullOrEmpty(levelText)
                && char.IsHighSurrogate(levelText[0]) && levelText.Length > 1 ? 2 : 1;
            bool isSingleCodePoint = levelText.Length == firstCodePointLength;

            if (bulletImageDataUri != null)
            {
                listStyle = "none";
            }
            else if (isSymbolicFont)
            {
                listStyle = "none";
                bulletChar = MapBulletChar(lookup, bulletFont);
            }
            else if (!isSingleCodePoint && codePoint != 0)
            {
                listStyle = "none";
                bulletChar = levelText;
            }
            else
            {
                switch (lookup)
                {
                    case 0x2022: case 'l': listStyle = "disc"; break;
                    case 'o': case 0x25E6: listStyle = "circle"; break;
                    case 0x00A7: case 0x25AA: case 0x25FE: listStyle = "square"; break;
                    default:
                        if (codePoint != 0)
                        {
                            listStyle = "none";
                            bulletChar = MapBulletChar(codePoint, bulletFont);
                        }
                        else
                        {
                            listStyle = "disc";
                        }
                        break;
                }
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(levelText) && levelText.Contains("%"))
            { tag = "ol"; listStyle = "decimal"; }
        }

        return new ListLevelInfo
        {
            Tag = tag,
            ListStyleType = listStyle,
            BulletChar = bulletChar,
            BulletFont = bulletFont,
            BulletImageDataUri = bulletImageDataUri,
            Start = start,
            FmtToken = NumFmtToken(levelDef),
            LvlText = string.IsNullOrEmpty(levelText) ? null : levelText,
            StartOverride = startOverride > 0 ? startOverride : -1,
            SuffixToken = suffixToken,
            IsLegal = isLegal,
            LvlRestart = lvlRestart,
            PicBulletId = picBulletId ?? -1,
            IndLeftTw = lvlInd?.Left?.Value,
            IndHangingTw = lvlInd?.Hanging?.Value,
            IndFirstLineTw = lvlInd?.FirstLine?.Value,
            FromInstanceOverride = fromInstanceOverride,
            MarkerColorHex = markerColorHex,
            MarkerColorCss = markerColorCss,
            MarkerSizeHalfPoints = markerSizeHalfPoints
        };
    }

    private static string MarkerSizeCssVar(string? halfPoints)
    {
        var css = MarkerSizeFontCss(halfPoints);
        return css.Length > 0 ? $"--marker-font-size:{css["font-size:".Length..]}" : string.Empty;
    }

    private static string MarkerSizeFontCss(string? halfPoints)
    {
        if (halfPoints == null || !double.TryParse(halfPoints,
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var half))
            return string.Empty;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "font-size:{0:0.#}pt;", half / 2.0);
    }

    private static string MapBulletChar(int codePoint, string? font)
    {
        var f = (font ?? string.Empty).ToLowerInvariant();

        int low = codePoint & 0xFF;

        if (f.Contains("wingdings"))
        {
            return low switch
            {
                0xFE => "\u2611",
                0xA8 => "\u2610",
                0xFC => "\u2714",
                0xA7 => "\u25A0",
                0x6C => "\u2022",
                0xD8 => "\u2756",
                _ => "\u2022"
            };
        }
        if (f.Contains("symbol"))
        {
            return low switch
            {
                0xB7 => "\u2022",
                0xA8 => "\u25E6",
                _ => "\u2022"
            };
        }
        try { return char.ConvertFromUtf32(codePoint); }
        catch { return "\u2022"; }
    }

    private string GetParagraphStyle(ParagraphProperties? props, string? lineFontFamily = null)
    {
        if (props == null) return string.Empty;
        return ConvertParagraphPropertiesToCss(props, lineFontFamily);
    }

    private string? ResolveParagraphLineFont(Paragraph paragraph)
    {
        foreach (var run in paragraph.Elements<Run>())
        {
            var runFont = GetFontName(run.RunProperties?.GetFirstChild<RunFonts>());
            if (!string.IsNullOrEmpty(runFont)) return runFont;
            if (run.Elements<Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text))) break;
        }

        var markFont = GetFontName(
            paragraph.ParagraphProperties?.ParagraphMarkRunProperties?.GetFirstChild<RunFonts>());
        if (!string.IsNullOrEmpty(markFont)) return markFont;

        var styleId = EffectiveParagraphStyleId(paragraph);
        if (styleId != null && _rawStyles.TryGetValue(styleId, out var style))
            return GetStyleFontFamily(style);
        return null;
    }

    private string? GetStyleFontFamily(Style style, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();
        var id = style.StyleId?.Value;
        if (id != null && !visited.Add(id)) return null;

        var name = GetFontName(style.StyleRunProperties?.GetFirstChild<RunFonts>());
        if (!string.IsNullOrEmpty(name)) return name;

        var basedOn = style.BasedOn?.Val?.Value;
        return basedOn != null && _rawStyles.TryGetValue(basedOn, out var parent)
            ? GetStyleFontFamily(parent, visited)
            : null;
    }

    private string ConvertParagraphPropertiesToCss(OpenXmlElement props, string? lineFontFamily = null)
    {
        var css = new StringBuilder();

        var justification = props.Descendants<Justification>().FirstOrDefault();
        if (justification?.Val != null)
        {
            css.Append($"text-align:{GetJustificationAlignment(justification.Val.Value)};");
        }

        var indentation = props.Descendants<Indentation>().FirstOrDefault();
        if (indentation != null)
        {
            if (indentation.Left?.Value != null && int.TryParse(indentation.Left.Value, out var leftVal))
                css.Append($"margin-left:{TwipsToPx(leftVal)}px;");
            if (indentation.Right?.Value != null && int.TryParse(indentation.Right.Value, out var rightVal))
                css.Append($"margin-right:{TwipsToPx(rightVal)}px;");
            if (indentation.FirstLine?.Value != null && int.TryParse(indentation.FirstLine.Value, out var firstLineVal))
                css.Append($"text-indent:{TwipsToPx(firstLineVal)}px;");
            if (indentation.Hanging?.Value != null && int.TryParse(indentation.Hanging.Value, out var hangingVal))
            {
                css.Append($"text-indent:-{TwipsToPx(hangingVal)}px;");
            }
        }

        var spacing = props.Descendants<SpacingBetweenLines>().FirstOrDefault();
        if (spacing != null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            var beforeAuto = spacing.BeforeAutoSpacing?.Value == true;
            var afterAuto = spacing.AfterAutoSpacing?.Value == true;

            if (!beforeAuto)
            {
                if (spacing.Before?.Value != null && int.TryParse(spacing.Before.Value, out var beforeVal))
                    css.Append(string.Format(inv, "margin-top:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(beforeVal)));
                else if (spacing.BeforeLines?.Value != null)
                {
                    var pt = spacing.BeforeLines.Value / 100.0 * (_defaultFontSizePt ?? 11);
                    css.Append(string.Format(inv, "margin-top:{0:0.##}pt;", pt));
                }
            }

            if (!afterAuto)
            {
                if (spacing.After?.Value != null && int.TryParse(spacing.After.Value, out var afterVal))
                    css.Append(string.Format(inv, "padding-bottom:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(afterVal)));
                else if (spacing.AfterLines?.Value != null)
                {
                    var pt = spacing.AfterLines.Value / 100.0 * (_defaultFontSizePt ?? 11);
                    css.Append(string.Format(inv, "padding-bottom:{0:0.##}pt;", pt));
                }
            }

            if (spacing.Line?.Value != null && int.TryParse(spacing.Line.Value, out var lineVal))
            {
                var lineRule = spacing.LineRule?.Value;
                if (lineRule == LineSpacingRuleValues.AtLeast)
                {
                    css.Append(string.Format(inv,
                        "line-height:max({0:0.##}pt, var(--w-line-single, 1.2em));",
                        OoxmlUnits.TwipsToPoints(lineVal)));
                    css.Append("--w-line-rule:atLeast;");
                }
                else if (lineRule == LineSpacingRuleValues.Exact)
                {
                    css.Append(string.Format(inv, "line-height:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(lineVal)));
                }
                else
                {
                    css.Append(WordLineSpacing.AutoCss(lineVal, lineFontFamily ?? _defaultFontFamily));
                }
            }
        }

        var stylePageBreak = props.Descendants<PageBreakBefore>().FirstOrDefault();
        if (stylePageBreak != null && (stylePageBreak.Val == null || stylePageBreak.Val.Value))
            css.Append("page-break-before:always;");

        var contextualSpacing = props.Descendants<ContextualSpacing>().FirstOrDefault();
        if (contextualSpacing != null && (contextualSpacing.Val == null || contextualSpacing.Val.Value))
        {
            css.Append("--w-contextual-spacing:1;");
        }

        var shading = props.Descendants<Shading>().FirstOrDefault();
        if (shading?.Fill?.Value != null && shading.Fill.Value != "auto" && shading.Fill.Value.ToUpper() != "FFFFFF")
        {
            css.Append($"background-color:#{shading.Fill.Value};");
        }

        return css.ToString();
    }

    private string ConvertRunToHtml(Run run, WordprocessingDocument document, OpenXmlPart? sourcePart = null)
    {
        var html = new StringBuilder();
        var runProps = run.RunProperties;

        if (_flexTabs && run.Elements<TabChar>().Any()
            && !run.Elements<Text>().Any() && !run.Elements<Drawing>().Any()
            && !run.Elements<Picture>().Any() && !run.Elements<Break>().Any())
        {
            return "<span style=\"flex:1 1 0;\">\t</span>";
        }

        bool needsBold = false, needsItalic = false, needsUnderline = false, needsStrike = false, needsSup = false, needsSub = false;
        if (runProps != null)
        {
            needsBold = runProps.Bold != null && (runProps.Bold.Val == null || runProps.Bold.Val.Value);
            needsItalic = runProps.Italic != null && (runProps.Italic.Val == null || runProps.Italic.Val.Value);
            needsUnderline = runProps.Underline != null && runProps.Underline.Val?.Value != UnderlineValues.None;
            needsStrike = (runProps.Strike != null && (runProps.Strike.Val == null || runProps.Strike.Val.Value)) ||
                          (runProps.DoubleStrike != null && (runProps.DoubleStrike.Val == null || runProps.DoubleStrike.Val.Value));
            var vertAlign = runProps.VerticalTextAlignment;
            if (vertAlign?.Val != null)
            {
                needsSup = vertAlign.Val.Value == VerticalPositionValues.Superscript;
                needsSub = vertAlign.Val.Value == VerticalPositionValues.Subscript;
            }
        }

        var (prefix, suffix) = BuildRunWrapper(runProps, needsBold, needsItalic, needsUnderline, needsStrike, needsSup, needsSub);
        html.Append(prefix);
        foreach (var child in run.Elements())
        {
            html.Append(ConvertRunChildToHtml(child, document, sourcePart));
        }
        html.Append(suffix);

        return html.ToString();
    }

    private (string Prefix, string Suffix) BuildRunWrapper(RunProperties? runProps,
        bool needsBold, bool needsItalic, bool needsUnderline, bool needsStrike, bool needsSup, bool needsSub)
    {
        var cleanCss = GetRunStyleClean(runProps);

        var rStyleId = runProps?.RunStyle?.Val?.Value;
        var rStyleCss = rStyleId != null && _styles.TryGetValue(rStyleId, out var rsCss)
            ? rsCss
            : string.Empty;

        var prefix = new StringBuilder();
        prefix.Append($"<span style=\"{rStyleCss}{cleanCss}\">");
        if (needsBold) prefix.Append("<strong>");
        if (needsItalic) prefix.Append("<em>");
        if (needsUnderline) prefix.Append("<u>");
        if (needsStrike) prefix.Append("<s>");
        if (needsSup) prefix.Append("<sup>");
        if (needsSub) prefix.Append("<sub>");

        var suffix = new StringBuilder();
        if (needsSub) suffix.Append("</sub>");
        if (needsSup) suffix.Append("</sup>");
        if (needsStrike) suffix.Append("</s>");
        if (needsUnderline) suffix.Append("</u>");
        if (needsItalic) suffix.Append("</em>");
        if (needsBold) suffix.Append("</strong>");
        suffix.Append("</span>");

        return (prefix.ToString(), suffix.ToString());
    }

    private static (bool Bold, bool Italic, bool Underline, bool Strike, bool Sup, bool Sub) GetRunSemanticFlags(RunProperties? runProps)
    {
        if (runProps == null) return default;
        var bold = runProps.Bold != null && (runProps.Bold.Val == null || runProps.Bold.Val.Value);
        var italic = runProps.Italic != null && (runProps.Italic.Val == null || runProps.Italic.Val.Value);
        var underline = runProps.Underline != null && runProps.Underline.Val?.Value != UnderlineValues.None;
        var strike = (runProps.Strike != null && (runProps.Strike.Val == null || runProps.Strike.Val.Value)) ||
                     (runProps.DoubleStrike != null && (runProps.DoubleStrike.Val == null || runProps.DoubleStrike.Val.Value));
        var vertAlign = runProps.VerticalTextAlignment;
        var sup = vertAlign?.Val != null && vertAlign.Val.Value == VerticalPositionValues.Superscript;
        var sub = vertAlign?.Val != null && vertAlign.Val.Value == VerticalPositionValues.Subscript;
        return (bold, italic, underline, strike, sup, sub);
    }

    private string ConvertRunChildToHtml(OpenXmlElement child, WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        switch (child)
        {
            case Text text:
                return EscapeHtml(MapSymbolicTextRun(text));
            case Break br:
                if (br.Type?.Value == BreakValues.Page) return "<div class=\"page-break\"></div>";
                if (br.Type?.Value == BreakValues.Column) return "<div class=\"docx-column-break\"></div>";
                return "<br/>";
            case TabChar _:
                return "<span style=\"display:inline-block;min-width:2em;\">\t</span>";
            case Drawing drawing:
                return ConvertDrawingToHtml(drawing, document, sourcePart);
            case Picture picture:
                return ConvertPictureToHtml(picture, document, sourcePart);
            case EmbeddedObject embedded:
                return ConvertEmbeddedObjectToHtml(embedded, document, sourcePart);
            case AlternateContent alternate:
                return ConvertAlternateContentToHtml(alternate, document, sourcePart);
            case FootnoteReference footnoteRef:
                return RenderFootnoteReference(footnoteRef);
            case FootnoteReferenceMark _:
                return string.Empty;
            case EndnoteReference endnoteRef:
                return RenderEndnoteReference(endnoteRef);
            case EndnoteReferenceMark _:
                return string.Empty;
            case NoBreakHyphen _:
                return "&#8209;";
            case SoftHyphen _:
                return "&shy;";
            case SymbolChar sym:
                return ConvertSymbolCharToHtml(sym);
            default:
                return string.Empty;
        }
    }

    private string ConvertSymbolCharToHtml(SymbolChar sym)
    {
        var hex = sym.Char?.Value;
        if (string.IsNullOrEmpty(hex) ||
            !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var codePoint))
        {
            return string.Empty;
        }

        var font = sym.Font?.Value;
        if (TryMapSymbolicChar(codePoint, font, out var mapped))
            return EscapeHtml(mapped);

        var style = string.IsNullOrEmpty(font)
            ? string.Empty
            : $" style=\"font-family:'{EscapeHtml(font)}';\"";
        return $"<span{style}>&#x{codePoint:X};</span>";
    }

    private string MapSymbolicTextRun(Text text)
    {
        var value = text.Text ?? string.Empty;
        if (value.Length == 0) return value;

        var hasPua = false;
        foreach (var c in value)
        {
            if (c >= '\uF000' && c <= '\uF0FF') { hasPua = true; break; }
        }

        var runFonts = (text.Parent as Run)?.RunProperties?.RunFonts;
        if (!hasPua && runFonts == null) return value;

        var font = GetFontName(runFonts);
        var symbolicFont = NormalizeSymbolFontName(font) != null;
        if (!hasPua && !symbolicFont) return value;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var isPua = c >= '\uF000' && c <= '\uF0FF';
            if ((isPua || symbolicFont) && TryMapSymbolicChar(c, font, out var mapped))
                sb.Append(mapped);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? NormalizeSymbolFontName(string? font)
    {
        var f = font?.Trim().ToLowerInvariant();
        return f is "symbol" or "wingdings" or "wingdings 2" or "wingdings 3" or "webdings"
            ? f
            : null;
    }

    private static bool TryMapSymbolicChar(int codePoint, string? font, out string mapped)
    {
        mapped = string.Empty;
        var canonical = NormalizeSymbolFontName(font);
        var isPua = codePoint is >= 0xF000 and <= 0xF0FF;
        var lookup = isPua ? codePoint & 0xFF : codePoint;

        if (canonical == null)
        {
            if (isPua) return false;
            try { mapped = char.ConvertFromUtf32(codePoint); return true; }
            catch { return false; }
        }

        var table = canonical switch
        {
            "symbol" => SymbolFontMap,
            "wingdings" => WingdingsFontMap,
            _ => null,
        };
        if (table != null && table.TryGetValue(lookup, out var s))
        {
            mapped = s;
            return true;
        }
        return false;
    }

    private static readonly Dictionary<int, string> SymbolFontMap = new()
    {
        [0x22] = "∀", [0x24] = "∃", [0x27] = "∍", [0x40] = "≅",
        [0x41] = "Α", [0x42] = "Β", [0x43] = "Χ", [0x44] = "Δ",
        [0x45] = "Ε", [0x46] = "Φ", [0x47] = "Γ", [0x48] = "Η",
        [0x49] = "Ι", [0x4A] = "ϑ", [0x4B] = "Κ", [0x4C] = "Λ",
        [0x4D] = "Μ", [0x4E] = "Ν", [0x4F] = "Ο", [0x50] = "Π",
        [0x51] = "Θ", [0x52] = "Ρ", [0x53] = "Σ", [0x54] = "Τ",
        [0x55] = "Υ", [0x56] = "ς", [0x57] = "Ω", [0x58] = "Ξ",
        [0x59] = "Ψ", [0x5A] = "Ζ", [0x5E] = "⊥",
        [0x61] = "α", [0x62] = "β", [0x63] = "χ", [0x64] = "δ",
        [0x65] = "ε", [0x66] = "φ", [0x67] = "γ", [0x68] = "η",
        [0x69] = "ι", [0x6A] = "ϕ", [0x6B] = "κ", [0x6C] = "λ",
        [0x6D] = "μ", [0x6E] = "ν", [0x6F] = "ο", [0x70] = "π",
        [0x71] = "θ", [0x72] = "ρ", [0x73] = "σ", [0x74] = "τ",
        [0x75] = "υ", [0x76] = "ϖ", [0x77] = "ω", [0x78] = "ξ",
        [0x79] = "ψ", [0x7A] = "ζ",
        [0xA2] = "′", [0xA3] = "≤", [0xA4] = "⁄", [0xA5] = "∞",
        [0xA6] = "ƒ", [0xA7] = "♣", [0xA8] = "♦", [0xA9] = "♥",
        [0xAA] = "♠", [0xAB] = "↔", [0xAC] = "←", [0xAD] = "↑",
        [0xAE] = "→", [0xAF] = "↓",
        [0xB0] = "°", [0xB1] = "±", [0xB2] = "″", [0xB3] = "≥",
        [0xB4] = "×", [0xB5] = "∝", [0xB6] = "∂", [0xB7] = "•",
        [0xB8] = "÷", [0xB9] = "≠", [0xBA] = "≡", [0xBB] = "≈",
        [0xBC] = "…",
        [0xC0] = "ℵ", [0xC1] = "ℑ", [0xC2] = "ℜ", [0xC3] = "℘",
        [0xC4] = "⊗", [0xC5] = "⊕", [0xC6] = "∅", [0xC7] = "∩",
        [0xC8] = "∪", [0xC9] = "⊃", [0xCA] = "⊇", [0xCB] = "⊄",
        [0xCC] = "⊂", [0xCD] = "⊆", [0xCE] = "∈", [0xCF] = "∉",
        [0xD0] = "∠", [0xD1] = "∇", [0xD5] = "∏", [0xD6] = "√",
        [0xD7] = "⋅", [0xD8] = "¬", [0xD9] = "∧", [0xDA] = "∨",
        [0xDB] = "⇔", [0xDC] = "⇐", [0xDD] = "⇑", [0xDE] = "⇒",
        [0xDF] = "⇓", [0xE5] = "∑", [0xF2] = "∫",
    };

    private static readonly Dictionary<int, string> WingdingsFontMap = new()
    {
        [0x4A] = "☺", [0x4C] = "☹",
        [0x6C] = "●", [0x6E] = "■", [0x6F] = "□", [0x75] = "◆",
        [0xA7] = "■", [0xA8] = "☐",
        [0xD8] = "❖",
        [0xE8] = "➔",
        [0xEF] = "⇦", [0xF0] = "⇨", [0xF1] = "⇧", [0xF2] = "⇩",
        [0xF3] = "⬄", [0xF4] = "⇳",
        [0xFB] = "✗", [0xFC] = "✔", [0xFD] = "☒", [0xFE] = "☑",
    };

    private string ConvertAlternateContentToHtml(AlternateContent alternate, WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        string? placeholderOnly = null;
        foreach (var branch in alternate.ChildElements)
        {
            if (branch is not (AlternateContentChoice or AlternateContentFallback)) continue;

            var pendingBefore = _pendingTextBoxes.Count;
            var html = new StringBuilder();
            foreach (var drawing in branch.Descendants<Drawing>())
                html.Append(ConvertDrawingToHtml(drawing, document, sourcePart));
            if (html.Length == 0 && _pendingTextBoxes.Count == pendingBefore)
            {
                foreach (var pict in branch.Descendants<Picture>())
                    html.Append(ConvertPictureToHtml(pict, document, sourcePart));
            }
            if (_pendingTextBoxes.Count > pendingBefore) return html.ToString();
            if (html.Length == 0) continue;

            if (IsPreservedPlaceholderOnly(html.ToString()))
            {
                placeholderOnly ??= html.ToString();
                continue;
            }
            return placeholderOnly != null
                ? SwapPreservedAttrsForAlternate(html.ToString(), alternate, sourcePart, document)
                : html.ToString();
        }
        foreach (var branch in alternate.ChildElements)
        {
            if (branch is not (AlternateContentChoice or AlternateContentFallback)) continue;
            var textBox = RenderTextBoxContent(branch, document, sourcePart);
            if (!string.IsNullOrEmpty(textBox)) return HoistTextBox(textBox);
        }

        var (acW, acH) = alternate.Descendants<Drawing>().Select(DrawingExtentPx).FirstOrDefault();
        var acPart = sourcePart ?? (OpenXmlPart?)document.MainDocumentPart;
        var preservedAc = RenderPreservedPlaceholder(alternate, acPart, acW, acH, "alternate");
        if (!string.IsNullOrEmpty(preservedAc)) return preservedAc;
        if (placeholderOnly != null) return placeholderOnly;

        _log.LogDebug("mc:AlternateContent bez konwertowalnego obrazu ani pola tekstowego — element pominięty.");
        return string.Empty;
    }

    private static bool IsPreservedPlaceholderOnly(string html)
    {
        if (!html.Contains("docx-preserved", StringComparison.Ordinal)) return false;
        var stripped = Regex.Replace(html, "<span class=\"docx-preserved\"[^>]*></span>", string.Empty);
        return string.IsNullOrWhiteSpace(stripped);
    }

    private string SwapPreservedAttrsForAlternate(string html, AlternateContent alternate,
        OpenXmlPart? sourcePart, WordprocessingDocument document)
    {
        var acPart = sourcePart ?? (OpenXmlPart?)document.MainDocumentPart;
        var acAttrs = BuildPreservedXmlAttrs(alternate, acPart);
        if (acAttrs.Length == 0) return html;

        const string markerPattern = "data-docx-xml=\"[^\"]*\"( data-docx-rels=\"[^\"]*\")?";
        if (Regex.Matches(html, markerPattern).Count != 1) return html;
        return Regex.Replace(html, markerPattern, acAttrs.TrimStart().Replace("$", "$$"));
    }

    private string RenderTextBoxContent(OpenXmlElement container, WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        var txbx = container.Descendants<TextBoxContent>().FirstOrDefault();
        if (txbx == null) return string.Empty;

        var inner = new StringBuilder();
        foreach (var child in txbx.Elements())
        {
            switch (child)
            {
                case Paragraph para:
                    inner.Append(ConvertParagraphToHtml(para, document, sourcePart));
                    break;
                case Table table:
                    inner.Append(ConvertTableToHtml(table, document, sourcePart));
                    break;
            }
        }
        if (inner.Length == 0) return string.Empty;

        var layout = BuildTextBoxLayoutCss(container);

        var extent = container.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
        var widthEmu = extent?.Cx?.Value ?? 0;
        var heightEmu = extent?.Cy?.Value ?? 0;
        var attrs = new StringBuilder();
        if (widthEmu > 0) attrs.Append($" data-width-emu=\"{widthEmu}\"");
        if (heightEmu > 0) attrs.Append($" data-height-emu=\"{heightEmu}\"");

        var anchor = container.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor>().FirstOrDefault();
        if (anchor != null)
        {
            var behind = anchor.BehindDoc?.Value == true;
            var (xEmu, yEmu) = ResolveAnchorPosition(anchor, widthEmu, heightEmu);
            attrs.Append($" data-pos-mode=\"{(behind ? "behind" : "front")}\"");
            attrs.Append($" data-x-emu=\"{xEmu}\" data-y-emu=\"{yEmu}\"");
            var wrap = ReadAnchorWrapMode(anchor);
            if (wrap != null) attrs.Append($" data-wrap=\"{wrap}\"");
        }

        var borderCss = string.Empty;
        var shapeOutline = container.Descendants<DocumentFormat.OpenXml.Drawing.Outline>()
            .FirstOrDefault(o => !o.Ancestors<TextBoxContent>().Any());
        if (shapeOutline != null && shapeOutline.GetFirstChild<DocumentFormat.OpenXml.Drawing.NoFill>() == null)
        {
            var hex = HexColorOrNull(shapeOutline.Descendants<DocumentFormat.OpenXml.Drawing.RgbColorModelHex>()
                .FirstOrDefault()?.Val?.Value);
            if (hex != null)
            {
                var px = shapeOutline.Width?.Value is { } w && w > 0
                    ? Math.Max(1, (int)Math.Round(OoxmlUnits.EmuToPixels(w)))
                    : 1;
                var dash = shapeOutline.GetFirstChild<DocumentFormat.OpenXml.Drawing.PresetDash>()?.Val?.Value;
                var borderStyle = "solid";
                if (dash != null && dash == DocumentFormat.OpenXml.Drawing.PresetLineDashValues.Dash) borderStyle = "dashed";
                else if (dash != null && dash == DocumentFormat.OpenXml.Drawing.PresetLineDashValues.Dot) borderStyle = "dotted";
                borderCss = $"border:{px}px {borderStyle} #{hex};";
                attrs.Append($" data-border-width=\"{px}\" data-border-color=\"#{hex}\" data-border-style=\"{borderStyle}\"");
            }
        }

        var bodyPr = container.Descendants<Wps.TextBodyProperties>().FirstOrDefault();
        var (paddingCss, anchorCss) = BuildTextBoxBodyCss(bodyPr, attrs);

        return $"<div class=\"docx-textbox\" data-textbox=\"1\"{attrs} style=\"{layout}"
             + borderCss
             + paddingCss + anchorCss
             + "box-sizing:border-box;\">"
             + inner + "</div>";
    }

    private const long WordDefaultTextBoxHorizontalInsetEmu = 91440;
    private const long WordDefaultTextBoxVerticalInsetEmu = 45720;

    private static (string PaddingCss, string AnchorCss) BuildTextBoxBodyCss(
        Wps.TextBodyProperties? bodyPr, StringBuilder attrs)
    {
        var lIns = (long?)bodyPr?.LeftInset?.Value ?? WordDefaultTextBoxHorizontalInsetEmu;
        var rIns = (long?)bodyPr?.RightInset?.Value ?? WordDefaultTextBoxHorizontalInsetEmu;
        var tIns = (long?)bodyPr?.TopInset?.Value ?? WordDefaultTextBoxVerticalInsetEmu;
        var bIns = (long?)bodyPr?.BottomInset?.Value ?? WordDefaultTextBoxVerticalInsetEmu;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var paddingCss = string.Format(inv, "padding:{0:0.#}px {1:0.#}px {2:0.#}px {3:0.#}px;",
            OoxmlUnits.EmuToPixels(tIns), OoxmlUnits.EmuToPixels(rIns),
            OoxmlUnits.EmuToPixels(bIns), OoxmlUnits.EmuToPixels(lIns));
        if (bodyPr?.LeftInset?.Value != null || bodyPr?.TopInset?.Value != null
            || bodyPr?.RightInset?.Value != null || bodyPr?.BottomInset?.Value != null)
        {
            attrs.Append($" data-tb-ins=\"{lIns} {tIns} {rIns} {bIns}\"");
        }

        var anchorCss = string.Empty;
        var anchorVal = bodyPr?.Anchor?.Value;
        if (anchorVal != null && anchorVal != A.TextAnchoringTypeValues.Top)
        {
            var justify = anchorVal == A.TextAnchoringTypeValues.Bottom ? "flex-end" : "center";
            anchorCss = $"display:flex;flex-direction:column;justify-content:{justify};";
            attrs.Append($" data-tb-anchor=\"{(anchorVal == A.TextAnchoringTypeValues.Bottom ? "b" : "ctr")}\"");
        }

        return (paddingCss, anchorCss);
    }

    private static string? ReadAnchorWrapMode(DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor anchor)
    {
        if (anchor.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.WrapSquare>() != null) return "square";
        if (anchor.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.WrapTight>() != null) return "tight";
        if (anchor.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.WrapThrough>() != null) return "through";
        if (anchor.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.WrapTopBottom>() != null) return "topAndBottom";
        return null;
    }

    private string HoistTextBox(string textBoxHtml)
    {
        if (string.IsNullOrEmpty(textBoxHtml)) return string.Empty;
        _pendingTextBoxes.Add(textBoxHtml);
        return string.Empty;
    }

    private readonly List<string> _pendingTextBoxes = new();

    private readonly Dictionary<long, int> _footnoteDisplayNumbers = new();
    private readonly List<long> _footnoteRefOrder = new();

    private static string FootnoteHtmlId(long ooxmlId) => $"fn-{ooxmlId}";

    private static string? ReadNoteNumberFormat(WordprocessingDocument document, bool endnote)
    {
        var firstSect = GetSectionPropertiesInDocumentOrder(document.MainDocumentPart?.Document?.Body)
            .FirstOrDefault();
        var sectionNumFmt = endnote
            ? firstSect?.GetFirstChild<EndnoteProperties>()?.GetFirstChild<NumberingFormat>()?.Val?.Value
            : firstSect?.GetFirstChild<FootnoteProperties>()?.GetFirstChild<NumberingFormat>()?.Val?.Value;

        var settings = document.MainDocumentPart?.DocumentSettingsPart?.Settings;
        var settingsNumFmt = endnote
            ? settings?.GetFirstChild<EndnoteDocumentWideProperties>()?.GetFirstChild<NumberingFormat>()?.Val?.Value
            : settings?.GetFirstChild<FootnoteDocumentWideProperties>()?.GetFirstChild<NumberingFormat>()?.Val?.Value;

        var numFmt = sectionNumFmt ?? settingsNumFmt;
        if (numFmt == null)
            return null;

        if (numFmt == NumberFormatValues.Decimal) return "decimal";
        if (numFmt == NumberFormatValues.LowerRoman) return "lowerRoman";
        if (numFmt == NumberFormatValues.UpperRoman) return "upperRoman";
        if (numFmt == NumberFormatValues.LowerLetter) return "lowerLetter";
        if (numFmt == NumberFormatValues.UpperLetter) return "upperLetter";
        return null;
    }

    private string RenderFootnoteReference(FootnoteReference footnoteRef)
    {
        if (footnoteRef.Id?.Value is not long ooxmlId)
            return string.Empty;

        if (!_footnoteDisplayNumbers.TryGetValue(ooxmlId, out var number))
        {
            number = _footnoteRefOrder.Count + 1;
            _footnoteDisplayNumbers[ooxmlId] = number;
            _footnoteRefOrder.Add(ooxmlId);
        }

        var htmlId = FootnoteHtmlId(ooxmlId);
        return $"<sup class=\"footnote-ref\" data-footnote-id=\"{htmlId}\" " +
               $"aria-label=\"Przypis {number}\">{number}</sup>";
    }

    private List<DomainFootnote>? ExtractFootnotes(WordprocessingDocument document)
    {
        if (_footnoteRefOrder.Count == 0)
            return null;

        var footnotesPart = document.MainDocumentPart?.FootnotesPart;
        var contentById = new Dictionary<long, string>();
        if (footnotesPart?.Footnotes != null)
        {
            foreach (var footnote in footnotesPart.Footnotes.Elements<WpFootnote>())
            {
                var type = footnote.Type?.Value;
                if (type == FootnoteEndnoteValues.Separator ||
                    type == FootnoteEndnoteValues.ContinuationSeparator ||
                    type == FootnoteEndnoteValues.ContinuationNotice)
                    continue;

                if (footnote.Id?.Value is not long id)
                    continue;

                try
                {
                    contentById[id] = ConvertFootnoteContent(footnote, document, footnotesPart);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Nie udało się skonwertować treści przypisu o id {FootnoteId}.", id);
                    contentById[id] = string.Empty;
                }
            }
        }

        var result = new List<DomainFootnote>(_footnoteRefOrder.Count);
        foreach (var ooxmlId in _footnoteRefOrder)
        {
            if (!contentById.TryGetValue(ooxmlId, out var html))
            {
                _log.LogWarning("Odwołanie do przypisu {FootnoteId} nie ma treści w footnotes.xml.", ooxmlId);
                html = string.Empty;
            }
            result.Add(new DomainFootnote { Id = FootnoteHtmlId(ooxmlId), Html = html });
        }

        return result;
    }

    private string ConvertFootnoteContent(WpFootnote footnote, WordprocessingDocument document, OpenXmlPart sourcePart)
    {
        var html = new StringBuilder();
        foreach (var block in footnote.Elements())
        {
            switch (block)
            {
                case Paragraph paragraph:
                    html.Append(ConvertParagraphToHtml(paragraph, document, sourcePart));
                    break;
                case Table table:
                    html.Append(ConvertTableToHtml(table, document, sourcePart));
                    break;
            }
        }
        return html.ToString();
    }

    private readonly Dictionary<long, int> _endnoteDisplayNumbers = new();
    private readonly List<long> _endnoteRefOrder = new();

    private static string EndnoteHtmlId(long ooxmlId) => $"en-{ooxmlId}";

    private string RenderEndnoteReference(EndnoteReference endnoteRef)
    {
        if (endnoteRef.Id?.Value is not long ooxmlId)
            return string.Empty;

        if (!_endnoteDisplayNumbers.TryGetValue(ooxmlId, out var number))
        {
            number = _endnoteRefOrder.Count + 1;
            _endnoteDisplayNumbers[ooxmlId] = number;
            _endnoteRefOrder.Add(ooxmlId);
        }

        var htmlId = EndnoteHtmlId(ooxmlId);
        return $"<sup class=\"endnote-ref\" data-endnote-id=\"{htmlId}\" " +
               $"aria-label=\"Przypis końcowy {number}\">{number}</sup>";
    }

    private List<DomainEndnote>? ExtractEndnotes(WordprocessingDocument document)
    {
        if (_endnoteRefOrder.Count == 0)
            return null;

        var endnotesPart = document.MainDocumentPart?.EndnotesPart;
        var contentById = new Dictionary<long, string>();
        if (endnotesPart?.Endnotes != null)
        {
            foreach (var endnote in endnotesPart.Endnotes.Elements<WpEndnote>())
            {
                var type = endnote.Type?.Value;
                if (type == FootnoteEndnoteValues.Separator ||
                    type == FootnoteEndnoteValues.ContinuationSeparator ||
                    type == FootnoteEndnoteValues.ContinuationNotice)
                    continue;

                if (endnote.Id?.Value is not long id)
                    continue;

                try
                {
                    contentById[id] = ConvertEndnoteContent(endnote, document, endnotesPart);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Nie udało się skonwertować treści przypisu końcowego o id {EndnoteId}.", id);
                    contentById[id] = string.Empty;
                }
            }
        }

        var result = new List<DomainEndnote>(_endnoteRefOrder.Count);
        foreach (var ooxmlId in _endnoteRefOrder)
        {
            if (!contentById.TryGetValue(ooxmlId, out var html))
            {
                _log.LogWarning("Odwołanie do przypisu końcowego {EndnoteId} nie ma treści w endnotes.xml.", ooxmlId);
                html = string.Empty;
            }
            result.Add(new DomainEndnote { Id = EndnoteHtmlId(ooxmlId), Html = html });
        }

        return result;
    }

    private string ConvertEndnoteContent(WpEndnote endnote, WordprocessingDocument document, OpenXmlPart sourcePart)
    {
        var html = new StringBuilder();
        foreach (var block in endnote.Elements())
        {
            switch (block)
            {
                case Paragraph paragraph:
                    html.Append(ConvertParagraphToHtml(paragraph, document, sourcePart));
                    break;
                case Table table:
                    html.Append(ConvertTableToHtml(table, document, sourcePart));
                    break;
            }
        }
        return html.ToString();
    }

    private string RenderVectorShapeAsHtml(Drawing drawing, string preservedAttrs = "")
    {
        var custom = drawing.Descendants<DocumentFormat.OpenXml.Drawing.CustomGeometry>().FirstOrDefault();
        var presetGeom = drawing.Descendants<DocumentFormat.OpenXml.Drawing.PresetGeometry>().FirstOrDefault();
        var preset = presetGeom?.Preset?.Value;

        var extent = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
        var widthPx = extent?.Cx != null ? (int)OoxmlUnits.EmuToPixels(extent.Cx.Value) : 0;
        var heightPx = extent?.Cy != null ? (int)OoxmlUnits.EmuToPixels(extent.Cy.Value) : 0;

        var outline = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Outline>().FirstOrDefault();
        var lineColor = HexColorOrNull(outline?.Descendants<DocumentFormat.OpenXml.Drawing.RgbColorModelHex>().FirstOrDefault()?.Val?.Value)
                        ?? "000000";
        var lineWidthPx = outline?.Width != null && outline.Width.Value > 0
            ? Math.Max(1, (int)Math.Round(OoxmlUnits.EmuToPixels(outline.Width.Value)))
            : 1;

        var isLine = preset == DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Line
                     || preset == DocumentFormat.OpenXml.Drawing.ShapeTypeValues.StraightConnector1;

        var pos = BuildTextBoxLayoutCss(drawing);

        var transformCss = BuildShapeTransformCss(((OpenXmlElement?)custom ?? presetGeom)?.Parent);
        const string editGuard = " contenteditable=\"false\"";

        if (isLine)
        {
            var w = widthPx > 0 ? $"{widthPx}px" : "100%";
            return $"<div class=\"docx-shape docx-line\" data-shape=\"line\"{editGuard}{preservedAttrs} "
                 + $"style=\"{StripSize(pos)}width:{w};height:{lineWidthPx}px;"
                 + $"background:#{lineColor};margin:2px 0;{transformCss}\"></div>";
        }

        var noFill = ShapeHasExplicitNoFill(drawing, custom);
        var fillHex = noFill ? null : GetShapeFillHex(drawing, custom);
        var strokeHex = outline?.Elements<DocumentFormat.OpenXml.Drawing.NoFill>().Any() == true ? null
            : HexColorOrNull(outline?.Elements<DocumentFormat.OpenXml.Drawing.SolidFill>()
                  .FirstOrDefault()?.RgbColorModelHex?.Val?.Value)
              ?? LineReferenceHex(drawing.Descendants<DocumentFormat.OpenXml.Drawing.LineReference>().FirstOrDefault());
        var gradient = noFill ? null : GetShapeGradient(((OpenXmlElement?)custom ?? presetGeom)?.Parent);

        var cgW = widthPx;
        var cgH = heightPx;
        if (custom != null && strokeHex != null && (cgW <= 0 ^ cgH <= 0))
        {
            cgW = Math.Max(cgW, lineWidthPx);
            cgH = Math.Max(cgH, lineWidthPx);
        }
        if (custom != null && cgW > 0 && cgH > 0)
        {
            var svg = BuildCustomGeometrySvg(custom, cgW, cgH, fillHex, strokeHex, lineWidthPx, noFill, gradient);
            if (!string.IsNullOrEmpty(svg))
                return $"<div class=\"docx-shape docx-custgeom\" data-shape=\"custom\"{editGuard}{preservedAttrs} "
                     + $"style=\"{StripSize(pos)}width:{cgW}px;height:{cgH}px;{transformCss}\">{svg}</div>";
        }

        var isBlock = preset == DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle
                      || preset == DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Ellipse
                      || preset == DocumentFormat.OpenXml.Drawing.ShapeTypeValues.RoundRectangle;
        if (isBlock && widthPx > 0 && heightPx > 0)
        {
            var bg = gradient != null
                ? $"background:{BuildCssLinearGradient(gradient.Value)};"
                : fillHex != null ? $"background:#{fillHex};" : string.Empty;
            var border = strokeHex != null ? $"border:{lineWidthPx}px solid #{strokeHex};" : string.Empty;
            var radius = preset == DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Ellipse
                ? "border-radius:50%;"
                : preset == DocumentFormat.OpenXml.Drawing.ShapeTypeValues.RoundRectangle
                    ? RoundRectRadiusCss(presetGeom, widthPx, heightPx)
                    : string.Empty;
            var shapeName = preset == DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Ellipse ? "ellipse" : "rect";
            return $"<div class=\"docx-shape docx-{shapeName}\" data-shape=\"{shapeName}\"{editGuard}{preservedAttrs} "
                 + $"style=\"{pos}{bg}{border}{radius}box-sizing:border-box;{transformCss}\"></div>";
        }

        if (presetGeom?.Preset?.InnerText is string presetName && widthPx > 0 && heightPx > 0)
        {
            var svg = BuildPresetGeometrySvg(presetName, widthPx, heightPx, fillHex, strokeHex, lineWidthPx, noFill, gradient);
            if (!string.IsNullOrEmpty(svg))
                return $"<div class=\"docx-shape docx-preset\" data-shape=\"{EscapeHtml(presetName)}\"{editGuard}{preservedAttrs} "
                     + $"style=\"{StripSize(pos)}width:{widthPx}px;height:{heightPx}px;{transformCss}\">{svg}</div>";
        }

        return string.Empty;
    }

    private static string BuildShapeTransformCss(OpenXmlElement? spPr)
        => BuildShapeTransformCssFromXfrm(spPr?.GetFirstChild<A.Transform2D>());

    private static string BuildShapeTransformCssFromXfrm(A.Transform2D? xfrm)
        => xfrm == null
            ? string.Empty
            : BuildTransformCss(xfrm.Rotation?.Value, xfrm.HorizontalFlip?.Value, xfrm.VerticalFlip?.Value);

    private static string BuildTransformCss(int? rotation, bool? flipH, bool? flipV)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var parts = new List<string>();
        if (rotation is int rot && rot != 0)
            parts.Add(string.Format(inv, "rotate({0:0.##}deg)", rot / 60000.0));
        if (flipH == true) parts.Add("scaleX(-1)");
        if (flipV == true) parts.Add("scaleY(-1)");
        return parts.Count == 0
            ? string.Empty
            : $"transform:{string.Join(' ', parts)};transform-origin:center center;";
    }

    private (List<(double pos, string hex)> stops, double angleDeg)? GetShapeGradient(OpenXmlElement? spPr)
    {
        var grad = spPr?.Elements<A.GradientFill>().FirstOrDefault();
        if (grad == null) return null;

        var stops = new List<(double pos, string hex)>();
        foreach (var gs in grad.Descendants<A.GradientStop>())
        {
            var hex = ResolvedDrawingColorHex(gs.GetFirstChild<A.RgbColorModelHex>(),
                gs.GetFirstChild<A.SchemeColor>());
            if (hex == null) continue;
            stops.Add(((gs.Position?.Value ?? 0) / 1000.0, hex));
        }
        if (stops.Count < 2) return null;
        stops.Sort((a, b) => a.pos.CompareTo(b.pos));

        var angle = (grad.GetFirstChild<A.LinearGradientFill>()?.Angle?.Value ?? 0) / 60000.0;
        return (stops, angle);
    }

    private static string BuildCssLinearGradient((List<(double pos, string hex)> stops, double angleDeg) gradient)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var cssDeg = (gradient.angleDeg + 90.0) % 360.0;
        var stopsCss = string.Join(", ",
            gradient.stops.Select(s => string.Format(inv, "#{0} {1:0.##}%", s.hex, s.pos)));
        return string.Format(inv, "linear-gradient({0:0.##}deg, {1})", cssDeg, stopsCss);
    }

    private static (string defs, string fillRef) BuildSvgLinearGradient(
        (List<(double pos, string hex)> stops, double angleDeg) gradient)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rad = gradient.angleDeg * Math.PI / 180.0;
        var dx = Math.Cos(rad) / 2.0;
        var dy = Math.Sin(rad) / 2.0;
        string F(double v) => v.ToString("0.###", inv);

        var key = string.Join("|", gradient.stops.Select(s => $"{s.pos:0.##}:{s.hex}")) + "@" + gradient.angleDeg.ToString("0.##", inv);
        var id = "dg" + (uint)key.GetHashCode();

        var stopsXml = string.Join(string.Empty, gradient.stops.Select(s =>
            string.Format(inv, "<stop offset=\"{0:0.##}%\" stop-color=\"#{1}\"/>", s.pos, s.hex)));
        var defs = $"<defs><linearGradient id=\"{id}\" x1=\"{F(0.5 - dx)}\" y1=\"{F(0.5 - dy)}\" "
                 + $"x2=\"{F(0.5 + dx)}\" y2=\"{F(0.5 + dy)}\">{stopsXml}</linearGradient></defs>";
        return (defs, $"url(#{id})");
    }

    private static string RoundRectRadiusCss(A.PresetGeometry? presetGeom, int widthPx, int heightPx)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double adj = 16667;
        var gd = presetGeom?.AdjustValueList?.Elements<A.ShapeGuide>()
            .FirstOrDefault(g => g.Name?.Value == "adj");
        if (gd?.Formula?.Value is string f && f.StartsWith("val ", StringComparison.Ordinal)
            && double.TryParse(f[4..], System.Globalization.NumberStyles.Float, inv, out var v))
        {
            adj = v;
        }
        var radius = Math.Max(0, adj) / 100000.0 * Math.Min(widthPx, heightPx);
        return string.Format(inv, "border-radius:{0:0.##}px;", radius);
    }

    private static string BuildPresetGeometrySvg(string presetName, int w, int h,
        string? fillHex, string? strokeHex, int strokeWidthPx, bool noFill,
        (List<(double pos, string hex)> stops, double angleDeg)? gradient)
    {
        if (w <= 0 || h <= 0) return string.Empty;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("0.##", inv);

        double hd = Math.Min(w, h) / 2.0;
        double pt3 = Math.Min(w, h) / 3.0;

        string Pts(params (double x, double y)[] p)
            => string.Join(" ", p.Select(q => $"{F(q.x)},{F(q.y)}"));

        string? star5Points()
        {
            var pts = new List<(double x, double y)>();
            double cx = w / 2.0, cy = h / 2.0, rx = w / 2.0, ry = h / 2.0;
            for (int k = 0; k < 10; k++)
            {
                var r = k % 2 == 0 ? 1.0 : 0.382;
                var ang = -Math.PI / 2 + k * Math.PI / 5;
                pts.Add((cx + rx * r * Math.Cos(ang), cy + ry * r * Math.Sin(ang)));
            }
            return Pts(pts.ToArray());
        }

        string? inner = presetName switch
        {
            "triangle" => $"<polygon points=\"{Pts((w / 2.0, 0), (w, h), (0, h))}\"/>",
            "rtTriangle" => $"<polygon points=\"{Pts((0, 0), (0, h), (w, h))}\"/>",
            "diamond" => $"<polygon points=\"{Pts((w / 2.0, 0), (w, h / 2.0), (w / 2.0, h), (0, h / 2.0))}\"/>",
            "parallelogram" => $"<polygon points=\"{Pts((w * 0.25, 0), (w, 0), (w * 0.75, h), (0, h))}\"/>",
            "trapezoid" => $"<polygon points=\"{Pts((w * 0.25, 0), (w * 0.75, 0), (w, h), (0, h))}\"/>",
            "pentagon" => $"<polygon points=\"{Pts((w / 2.0, 0), (w, h * 0.38), (w * 0.81, h), (w * 0.19, h), (0, h * 0.38))}\"/>",
            "hexagon" => $"<polygon points=\"{Pts((w * 0.25, 0), (w * 0.75, 0), (w, h / 2.0), (w * 0.75, h), (w * 0.25, h), (0, h / 2.0))}\"/>",
            "octagon" => $"<polygon points=\"{Pts((w * 0.29, 0), (w * 0.71, 0), (w, h * 0.29), (w, h * 0.71), (w * 0.71, h), (w * 0.29, h), (0, h * 0.71), (0, h * 0.29))}\"/>",
            "star5" => $"<polygon points=\"{star5Points()}\"/>",
            "rightArrow" => $"<polygon points=\"{Pts((0, h * 0.25), (w - hd, h * 0.25), (w - hd, 0), (w, h / 2.0), (w - hd, h), (w - hd, h * 0.75), (0, h * 0.75))}\"/>",
            "leftArrow" => $"<polygon points=\"{Pts((w, h * 0.25), (hd, h * 0.25), (hd, 0), (0, h / 2.0), (hd, h), (hd, h * 0.75), (w, h * 0.75))}\"/>",
            "upArrow" => $"<polygon points=\"{Pts((w * 0.25, h), (w * 0.25, hd), (0, hd), (w / 2.0, 0), (w, hd), (w * 0.75, hd), (w * 0.75, h))}\"/>",
            "downArrow" => $"<polygon points=\"{Pts((w * 0.25, 0), (w * 0.25, h - hd), (0, h - hd), (w / 2.0, h), (w, h - hd), (w * 0.75, h - hd), (w * 0.75, 0))}\"/>",
            "leftRightArrow" => $"<polygon points=\"{Pts((hd, 0), (hd, h * 0.25), (w - hd, h * 0.25), (w - hd, 0), (w, h / 2.0), (w - hd, h), (w - hd, h * 0.75), (hd, h * 0.75), (hd, h), (0, h / 2.0))}\"/>",
            "chevron" => $"<polygon points=\"{Pts((0, 0), (w - hd, 0), (w, h / 2.0), (w - hd, h), (0, h), (hd, h / 2.0))}\"/>",
            "homePlate" => $"<polygon points=\"{Pts((0, 0), (w - hd, 0), (w, h / 2.0), (w - hd, h), (0, h))}\"/>",
            "plus" => $"<polygon points=\"{Pts(((w - pt3) / 2, 0), ((w + pt3) / 2, 0), ((w + pt3) / 2, (h - pt3) / 2), (w, (h - pt3) / 2), (w, (h + pt3) / 2), ((w + pt3) / 2, (h + pt3) / 2), ((w + pt3) / 2, h), ((w - pt3) / 2, h), ((w - pt3) / 2, (h + pt3) / 2), (0, (h + pt3) / 2), (0, (h - pt3) / 2), ((w - pt3) / 2, (h - pt3) / 2))}\"/>",
            "rect" => $"<rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\"/>",
            "roundRect" => $"<rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" rx=\"{F(Math.Min(w, h) * 0.16667)}\"/>",
            "ellipse" => $"<ellipse cx=\"{F(w / 2.0)}\" cy=\"{F(h / 2.0)}\" rx=\"{F(w / 2.0)}\" ry=\"{F(h / 2.0)}\"/>",
            "line" or "straightConnector1" => $"<line x1=\"0\" y1=\"0\" x2=\"{w}\" y2=\"{h}\"/>",
            _ => null
        };
        if (inner == null) return string.Empty;

        var defs = string.Empty;
        string fill;
        if (noFill) fill = "none";
        else if (gradient != null)
        {
            var (d, fillRef) = BuildSvgLinearGradient(gradient.Value);
            defs = d;
            fill = fillRef;
        }
        else fill = fillHex != null ? $"#{fillHex}" : "currentColor";

        var isLinePrimitive = inner.StartsWith("<line", StringComparison.Ordinal);
        var stroke = strokeHex != null || isLinePrimitive
            ? $" stroke=\"#{strokeHex ?? "000000"}\" stroke-width=\"{Math.Max(1, strokeWidthPx)}\""
            : string.Empty;

        inner = inner.Insert(inner.Length - 2, $" fill=\"{(isLinePrimitive ? "none" : fill)}\"{stroke}");
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\" "
             + $"width=\"{w}\" height=\"{h}\" preserveAspectRatio=\"none\" "
             + $"style=\"display:block;\">{defs}{inner}</svg>";
    }

    private string RenderGroupDrawingAsHtml(Drawing drawing, WordprocessingDocument document, OpenXmlPart? sourcePart)
    {
        var group = drawing.Descendants<Wpg.WordprocessingGroup>().FirstOrDefault();
        if (group == null) return string.Empty;

        var (contW, contH) = DrawingExtentPx(drawing);
        var xfrmGrp = group.GetFirstChild<Wpg.GroupShapeProperties>()?.TransformGroup;
        if (contW <= 0 && contH <= 0)
        {
            contW = xfrmGrp?.Extents?.Cx != null ? (int)OoxmlUnits.EmuToPixels(xfrmGrp.Extents.Cx.Value) : 0;
            contH = xfrmGrp?.Extents?.Cy != null ? (int)OoxmlUnits.EmuToPixels(xfrmGrp.Extents.Cy.Value) : 0;
        }
        if (contW <= 0 && contH <= 0) return string.Empty;
        contW = Math.Max(contW, 1);
        contH = Math.Max(contH, 1);

        long extCx = xfrmGrp?.Extents?.Cx ?? 0;
        long extCy = xfrmGrp?.Extents?.Cy ?? 0;
        long chCx = xfrmGrp?.ChildExtents?.Cx ?? extCx;
        long chCy = xfrmGrp?.ChildExtents?.Cy ?? extCy;
        long chOffX = xfrmGrp?.ChildOffset?.X ?? 0;
        long chOffY = xfrmGrp?.ChildOffset?.Y ?? 0;
        var scaleX = chCx > 0 && extCx > 0 ? (double)extCx / chCx : 1.0;
        var scaleY = chCy > 0 && extCy > 0 ? (double)extCy / chCy : 1.0;

        var groupFillHex = GroupOwnFillHex(group.GetFirstChild<Wpg.GroupShapeProperties>(), null);
        var inner = RenderGroupChildrenHtml(group, document, sourcePart,
            scaleX, scaleY, chOffX, chOffY, groupFillHex, depth: 0);
        if (string.IsNullOrEmpty(inner)) return string.Empty;

        var preservedAttrs = BuildPreservedXmlAttrs(drawing, sourcePart);
        var transformCss = BuildTransformCss(xfrmGrp?.Rotation?.Value,
            xfrmGrp?.HorizontalFlip?.Value, xfrmGrp?.VerticalFlip?.Value);
        var posCss = StripSize(BuildTextBoxLayoutCss(drawing));
        if (!posCss.Contains("position:absolute"))
            posCss += "position:relative;display:inline-block;";
        return $"<div class=\"docx-shape docx-group\" data-shape=\"group\" contenteditable=\"false\"{preservedAttrs} "
             + $"style=\"{posCss}width:{contW}px;height:{contH}px;overflow:visible;{transformCss}\">{inner}</div>";
    }

    private string? RenderGroupChildrenHtml(OpenXmlElement group, WordprocessingDocument document,
        OpenXmlPart? sourcePart, double scaleX, double scaleY, long chOffX, long chOffY,
        string? groupFillHex, int depth)
    {
        var inner = new StringBuilder();
        foreach (var child in group.ChildElements)
        {
            string? html;
            switch (child)
            {
                case Wps.WordprocessingShape wsp:
                    html = RenderGroupChildShape(wsp, document, sourcePart,
                        scaleX, scaleY, chOffX, chOffY, groupFillHex);
                    break;
                case Pic.Picture pic:
                    html = RenderGroupChildPicture(pic, document, sourcePart, scaleX, scaleY, chOffX, chOffY);
                    break;
                case Wpg.GroupShape nested:
                    html = RenderNestedGroupShape(nested, document, sourcePart,
                        scaleX, scaleY, chOffX, chOffY, groupFillHex, depth);
                    break;
                case Wpg.GraphicFrame:
                    html = null;
                    break;
                default:
                    continue;
            }
            if (html == null)
            {
                _log.LogInformation(
                    "Grupa kształtów: dziecko {Child} nieodwzorowalne — cała grupa w pass-through.",
                    child.LocalName);
                return null;
            }
            inner.Append(html);
        }
        return inner.ToString();
    }

    private const int MaxNestedGroupDepth = 8;

    private string? RenderNestedGroupShape(Wpg.GroupShape nested, WordprocessingDocument document,
        OpenXmlPart? sourcePart, double scaleX, double scaleY, long chOffX, long chOffY,
        string? inheritedFillHex, int depth)
    {
        if (depth >= MaxNestedGroupDepth) return null;
        var grpSpPr = nested.GetFirstChild<Wpg.GroupShapeProperties>();
        var xfrm = grpSpPr?.TransformGroup;
        if (xfrm?.Offset?.X == null || xfrm.Offset.Y == null
            || xfrm.Extents?.Cx == null || xfrm.Extents.Cy == null)
        {
            return null;
        }

        var leftPx = OoxmlUnits.EmuToPixels((xfrm.Offset.X.Value - chOffX) * scaleX);
        var topPx = OoxmlUnits.EmuToPixels((xfrm.Offset.Y.Value - chOffY) * scaleY);
        var wPx = OoxmlUnits.EmuToPixels(xfrm.Extents.Cx.Value * scaleX);
        var hPx = OoxmlUnits.EmuToPixels(xfrm.Extents.Cy.Value * scaleY);
        if (wPx <= 0 && hPx <= 0) return string.Empty;
        wPx = Math.Max(wPx, 1);
        hPx = Math.Max(hPx, 1);

        long extCx = xfrm.Extents.Cx.Value;
        long extCy = xfrm.Extents.Cy.Value;
        long chCx = xfrm.ChildExtents?.Cx ?? extCx;
        long chCy = xfrm.ChildExtents?.Cy ?? extCy;
        var innerScaleX = chCx > 0 ? scaleX * extCx / chCx : scaleX;
        var innerScaleY = chCy > 0 ? scaleY * extCy / chCy : scaleY;

        var fillHex = GroupOwnFillHex(grpSpPr, inheritedFillHex);
        var inner = RenderGroupChildrenHtml(nested, document, sourcePart,
            innerScaleX, innerScaleY, xfrm.ChildOffset?.X ?? 0, xfrm.ChildOffset?.Y ?? 0,
            fillHex, depth + 1);
        if (inner == null) return null;
        if (inner.Length == 0) return string.Empty;

        var transformCss = BuildTransformCss(xfrm.Rotation?.Value,
            xfrm.HorizontalFlip?.Value, xfrm.VerticalFlip?.Value);
        return $"<div style=\"position:absolute;left:{Px(leftPx)}px;top:{Px(topPx)}px;"
             + $"width:{Px(wPx)}px;height:{Px(hPx)}px;overflow:visible;{transformCss}\">{inner}</div>";
    }

    private static string Px(double v)
        => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private string? GroupOwnFillHex(OpenXmlElement? grpSpPr, string? inherited)
        => SolidFillHex(grpSpPr?.Elements<A.SolidFill>().FirstOrDefault()) ?? inherited;

    private string? RenderGroupChildShape(Wps.WordprocessingShape wsp, WordprocessingDocument document,
        OpenXmlPart? sourcePart, double scaleX, double scaleY, long chOffX, long chOffY, string? groupFillHex)
    {
        var spPr = wsp.GetFirstChild<Wps.ShapeProperties>();
        var xfrm = spPr?.GetFirstChild<A.Transform2D>();
        if (xfrm?.Offset?.X == null || xfrm.Offset.Y == null
            || xfrm.Extents?.Cx == null || xfrm.Extents.Cy == null)
        {
            return null;
        }

        var leftPx = OoxmlUnits.EmuToPixels((xfrm.Offset.X.Value - chOffX) * scaleX);
        var topPx = OoxmlUnits.EmuToPixels((xfrm.Offset.Y.Value - chOffY) * scaleY);
        var wPx = OoxmlUnits.EmuToPixels(xfrm.Extents.Cx.Value * scaleX);
        var hPx = OoxmlUnits.EmuToPixels(xfrm.Extents.Cy.Value * scaleY);
        if (wPx < 0 || hPx < 0) return null;

        var blipRel = spPr!.GetFirstChild<A.BlipFill>()?.Blip?.Embed?.Value;
        if (blipRel != null)
        {
            if (wPx <= 0 || hPx <= 0) return string.Empty;
            var part = sourcePart ?? (OpenXmlPart?)document.MainDocumentPart;
            if (part == null) return null;
            var src = TryResolveImageDataUrl(part, blipRel,
                OoxmlUnits.PixelsToEmu(wPx), OoxmlUnits.PixelsToEmu(hPx));
            if (src == null) return null;
            return $"<img src=\"{src}\" style=\"position:absolute;left:{Px(leftPx)}px;top:{Px(topPx)}px;"
                 + $"width:{Px(wPx)}px;height:{Px(hPx)}px;max-width:none;{BuildShapeTransformCssFromXfrm(xfrm)}\" />";
        }

        var noFill = spPr.Elements<A.NoFill>().Any();
        string? fillHex;
        if (spPr.Elements<A.GroupFill>().Any())
        {
            fillHex = groupFillHex;
        }
        else
        {
            fillHex = noFill ? null
                : SolidFillHex(spPr.Elements<A.SolidFill>().FirstOrDefault())
                  ?? FillReferenceHex(wsp.Descendants<A.FillReference>().FirstOrDefault());
        }
        var outline = spPr.GetFirstChild<A.Outline>();
        var strokeHex = outline?.Elements<A.NoFill>().Any() == true ? null
            : HexColorOrNull(outline?.Elements<A.SolidFill>()
                  .FirstOrDefault()?.RgbColorModelHex?.Val?.Value)
              ?? LineReferenceHex(wsp.Descendants<A.LineReference>().FirstOrDefault());
        var strokeW = outline?.Width != null && outline.Width.Value > 0
            ? Math.Max(1, (int)Math.Round(OoxmlUnits.EmuToPixels(outline.Width.Value)))
            : 1;
        var gradient = noFill ? null : GetShapeGradient(spPr);

        if (wPx <= 0 || hPx <= 0)
        {
            var hasStroke = strokeHex != null;
            if (!hasStroke || (wPx <= 0 && hPx <= 0)) return string.Empty;
            wPx = Math.Max(wPx, strokeW);
            hPx = Math.Max(hPx, strokeW);
        }

        string svg;
        var custom = spPr.GetFirstChild<A.CustomGeometry>();
        if (custom != null)
        {
            svg = BuildCustomGeometrySvg(custom, wPx, hPx, fillHex, strokeHex, strokeW, noFill, gradient);
        }
        else
        {
            var presetName = spPr.GetFirstChild<A.PresetGeometry>()?.Preset?.InnerText;
            svg = presetName != null
                ? BuildPresetGeometrySvg(presetName, (int)Math.Round(wPx), (int)Math.Round(hPx),
                    fillHex, strokeHex, strokeW, noFill, gradient)
                : string.Empty;
        }
        if (string.IsNullOrEmpty(svg)) return null;

        var transformCss = BuildShapeTransformCssFromXfrm(xfrm);
        return $"<div style=\"position:absolute;left:{Px(leftPx)}px;top:{Px(topPx)}px;"
             + $"width:{Px(wPx)}px;height:{Px(hPx)}px;{transformCss}\">{svg}</div>";
    }

    private string? RenderGroupChildPicture(Pic.Picture pic, WordprocessingDocument document,
        OpenXmlPart? sourcePart, double scaleX, double scaleY, long chOffX, long chOffY)
    {
        var xfrm = pic.ShapeProperties?.Transform2D;
        if (xfrm?.Offset?.X == null || xfrm.Offset.Y == null
            || xfrm.Extents?.Cx == null || xfrm.Extents.Cy == null)
        {
            return null;
        }

        var leftPx = OoxmlUnits.EmuToPixels((xfrm.Offset.X.Value - chOffX) * scaleX);
        var topPx = OoxmlUnits.EmuToPixels((xfrm.Offset.Y.Value - chOffY) * scaleY);
        var wPx = OoxmlUnits.EmuToPixels(xfrm.Extents.Cx.Value * scaleX);
        var hPx = OoxmlUnits.EmuToPixels(xfrm.Extents.Cy.Value * scaleY);
        if (wPx <= 0 || hPx <= 0) return string.Empty;

        var relId = pic.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
        if (relId == null) return null;
        var effectivePart = sourcePart ?? (OpenXmlPart?)document.MainDocumentPart;
        if (effectivePart == null) return null;

        var src = TryResolveImageDataUrl(effectivePart, relId,
            OoxmlUnits.PixelsToEmu(wPx), OoxmlUnits.PixelsToEmu(hPx));
        if (src == null) return null;

        var transformCss = BuildShapeTransformCssFromXfrm(xfrm);
        return $"<img src=\"{src}\" style=\"position:absolute;left:{Px(leftPx)}px;top:{Px(topPx)}px;"
             + $"width:{Px(wPx)}px;height:{Px(hPx)}px;max-width:none;{transformCss}\" />";
    }

    private string? TryResolveImageDataUrl(OpenXmlPart effectivePart, string relationshipId,
        long widthEmu, long heightEmu)
    {
        string? base64Data = null;
        string? contentType = null;
        if (_images.TryGetValue(ImageCacheKey(effectivePart, relationshipId), out var cached))
        {
            base64Data = cached.Base64Data;
            contentType = cached.ContentType;
        }
        else
        {
            try
            {
                if (effectivePart.GetPartById(relationshipId) is ImagePart imagePart)
                {
                    LoadImageFromPart(effectivePart, imagePart);
                    if (_images.TryGetValue(ImageCacheKey(effectivePart, relationshipId), out var lazy))
                    {
                        base64Data = lazy.Base64Data;
                        contentType = lazy.ContentType;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Nie udało się rozwiązać relacji obrazu {RelId} w części {PartUri}: {Error}",
                    relationshipId, effectivePart.Uri, ex.Message);
            }
        }
        if (base64Data == null || contentType == null) return null;

        var legacy = WebGraphicForLegacy(System.Convert.FromBase64String(base64Data), contentType, widthEmu, heightEmu);
        return legacy?.dataUrl ?? $"data:{contentType};base64,{base64Data}";
    }

    private string? GetShapeFillHex(Drawing drawing, DocumentFormat.OpenXml.Drawing.CustomGeometry? custom)
    {
        var geom = (OpenXmlElement?)custom
            ?? drawing.Descendants<DocumentFormat.OpenXml.Drawing.PresetGeometry>().FirstOrDefault();
        var spPr = geom?.Parent;

        var solid = spPr?.Elements<DocumentFormat.OpenXml.Drawing.SolidFill>().FirstOrDefault()
                    ?? drawing.Descendants<DocumentFormat.OpenXml.Drawing.SolidFill>()
                        .FirstOrDefault(f => f.Parent is not DocumentFormat.OpenXml.Drawing.Outline);
        var hex = SolidFillHex(solid);
        if (hex != null) return hex;

        var fillRef = spPr?.Parent?.Descendants<DocumentFormat.OpenXml.Drawing.FillReference>().FirstOrDefault();
        return FillReferenceHex(fillRef);
    }

    private static bool ShapeHasExplicitNoFill(Drawing drawing, DocumentFormat.OpenXml.Drawing.CustomGeometry? custom)
    {
        var geom = (OpenXmlElement?)custom
            ?? drawing.Descendants<DocumentFormat.OpenXml.Drawing.PresetGeometry>().FirstOrDefault();
        return geom?.Parent?.Elements<DocumentFormat.OpenXml.Drawing.NoFill>().Any() == true;
    }

    private string? SolidFillHex(DocumentFormat.OpenXml.Drawing.SolidFill? fill)
        => fill == null ? null
            : ResolvedDrawingColorHex(fill.RgbColorModelHex, fill.SchemeColor);

    private string? FillReferenceHex(DocumentFormat.OpenXml.Drawing.FillReference? fillRef)
        => fillRef == null ? null
            : ResolvedDrawingColorHex(fillRef.RgbColorModelHex, fillRef.SchemeColor);

    private string? LineReferenceHex(DocumentFormat.OpenXml.Drawing.LineReference? lnRef)
    {
        if (lnRef == null) return null;
        var hex = ResolvedDrawingColorHex(lnRef.RgbColorModelHex, lnRef.SchemeColor);
        return hex is { Length: 8 } && hex.EndsWith("00", StringComparison.Ordinal) ? null : hex;
    }

    private string? ResolvedDrawingColorHex(
        DocumentFormat.OpenXml.Drawing.RgbColorModelHex? srgb,
        DocumentFormat.OpenXml.Drawing.SchemeColor? scheme)
    {
        if (HexColorOrNull(srgb?.Val?.Value) is string hex)
            return ApplyDrawingColorTransforms(hex, srgb!);
        var baseHex = ResolveDrawingSchemeColor(scheme?.Val?.Value);
        return baseHex == null || scheme == null ? baseHex
            : ApplyDrawingColorTransforms(baseHex, scheme);
    }

    private static string ApplyDrawingColorTransforms(string hex, OpenXmlElement colorElement)
    {
        if (!colorElement.HasChildren) return hex;

        double r = System.Convert.ToInt32(hex[..2], 16) / 255.0;
        double g = System.Convert.ToInt32(hex[2..4], 16) / 255.0;
        double b = System.Convert.ToInt32(hex[4..6], 16) / 255.0;
        double alpha = 1.0;
        static double Pct(Int32Value? v) => Math.Clamp((v?.Value ?? 100000) / 100000.0, 0.0, 1.0);

        foreach (var t in colorElement.ChildElements)
        {
            switch (t)
            {
                case DocumentFormat.OpenXml.Drawing.Tint tint:
                {
                    var f = Pct(tint.Val);
                    r = r * f + (1 - f); g = g * f + (1 - f); b = b * f + (1 - f);
                    break;
                }
                case DocumentFormat.OpenXml.Drawing.Shade shade:
                {
                    var f = Pct(shade.Val);
                    r *= f; g *= f; b *= f;
                    break;
                }
                case DocumentFormat.OpenXml.Drawing.SaturationModulation satMod:
                {
                    var (h, s, l) = RgbToHsl(r, g, b);
                    (r, g, b) = HslToRgb(h, Math.Clamp(s * ((satMod.Val?.Value ?? 100000) / 100000.0), 0, 1), l);
                    break;
                }
                case DocumentFormat.OpenXml.Drawing.LuminanceModulation lumMod:
                {
                    var (h, s, l) = RgbToHsl(r, g, b);
                    (r, g, b) = HslToRgb(h, s, Math.Clamp(l * ((lumMod.Val?.Value ?? 100000) / 100000.0), 0, 1));
                    break;
                }
                case DocumentFormat.OpenXml.Drawing.LuminanceOffset lumOff:
                {
                    var (h, s, l) = RgbToHsl(r, g, b);
                    (r, g, b) = HslToRgb(h, s, Math.Clamp(l + (lumOff.Val?.Value ?? 0) / 100000.0, 0, 1));
                    break;
                }
                case DocumentFormat.OpenXml.Drawing.Alpha a:
                    alpha = Pct(a.Val);
                    break;
            }
        }

        static int Byte255(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        var rgbHex = $"{Byte255(r):X2}{Byte255(g):X2}{Byte255(b):X2}";
        return alpha < 1.0 ? rgbHex + $"{Byte255(alpha):X2}" : rgbHex;
    }

    private static (double h, double s, double l) RgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h / 6.0, s, l);
    }

    private static (double r, double g, double b) HslToRgb(double h, double s, double l)
    {
        if (s == 0) return (l, l, l);
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return (Hue(p, q, h + 1.0 / 3), Hue(p, q, h), Hue(p, q, h - 1.0 / 3));
    }

    private string? ResolveDrawingSchemeColor(DocumentFormat.OpenXml.Drawing.SchemeColorValues? scheme)
    {
        if (scheme == null || _themePart?.Theme?.ThemeElements?.ColorScheme == null) return null;
        var cs = _themePart.Theme.ThemeElements.ColorScheme;
        var s = scheme.Value;

        DocumentFormat.OpenXml.Drawing.Color2Type? c2 = null;
        if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Dark1 || s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Text1) c2 = cs.Dark1Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Light1 || s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Background1) c2 = cs.Light1Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Dark2 || s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Text2) c2 = cs.Dark2Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Light2 || s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Background2) c2 = cs.Light2Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Accent1) c2 = cs.Accent1Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Accent2) c2 = cs.Accent2Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Accent3) c2 = cs.Accent3Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Accent4) c2 = cs.Accent4Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Accent5) c2 = cs.Accent5Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Accent6) c2 = cs.Accent6Color;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.Hyperlink) c2 = cs.Hyperlink;
        else if (s == DocumentFormat.OpenXml.Drawing.SchemeColorValues.FollowedHyperlink) c2 = cs.FollowedHyperlinkColor;
        if (c2 == null) return null;

        var srgb = c2.GetFirstChild<DocumentFormat.OpenXml.Drawing.RgbColorModelHex>();
        if (srgb?.Val?.Value != null) return HexColorOrNull(srgb.Val.Value);
        var sys = c2.GetFirstChild<DocumentFormat.OpenXml.Drawing.SystemColor>();
        return HexColorOrNull(sys?.LastColor?.Value);
    }

    private static string BuildCustomGeometrySvg(DocumentFormat.OpenXml.Drawing.CustomGeometry custom,
        double widthPx, double heightPx, string? fillHex, string? strokeHex, int strokeWidthPx, bool noFill = false,
        (List<(double pos, string hex)> stops, double angleDeg)? gradient = null)
    {
        var pathList = custom.GetFirstChild<DocumentFormat.OpenXml.Drawing.PathList>();
        var paths = pathList?.Elements<DocumentFormat.OpenXml.Drawing.Path>().ToList();
        if (paths == null || paths.Count == 0) return string.Empty;

        var inv = System.Globalization.CultureInfo.InvariantCulture;

        long emuW = Math.Max(1L, OoxmlUnits.PixelsToEmu(widthPx));
        long emuH = Math.Max(1L, OoxmlUnits.PixelsToEmu(heightPx));
        long PathW(DocumentFormat.OpenXml.Drawing.Path p) => p.Width?.Value is long w && w > 0 ? w : emuW;
        long PathH(DocumentFormat.OpenXml.Drawing.Path p) => p.Height?.Value is long h && h > 0 ? h : emuH;
        long spaceW = paths.Max(PathW);
        long spaceH = paths.Max(PathH);

        var guides = EvaluateGeometryGuides(custom, emuW, emuH);

        bool TryVal(string? token, out double result)
        {
            result = 0;
            if (string.IsNullOrEmpty(token)) return false;
            return double.TryParse(token, System.Globalization.NumberStyles.Float, inv, out result)
                || guides.TryGetValue(token, out result);
        }
        string F(double v) => v.ToString("0.###", inv);

        var defs = string.Empty;
        string fill;
        if (noFill) fill = "none";
        else if (gradient != null)
        {
            var (gDefs, gRef) = BuildSvgLinearGradient(gradient.Value);
            defs = gDefs;
            fill = gRef;
        }
        else fill = fillHex != null ? $"#{fillHex}" : "currentColor";
        var stroke = strokeHex != null
            ? $" stroke=\"#{strokeHex}\" stroke-width=\"{Math.Max(1, strokeWidthPx)}\" vector-effect=\"non-scaling-stroke\""
            : string.Empty;

        var svgPaths = new StringBuilder();
        foreach (var path in paths)
        {
            var sx = (double)spaceW / PathW(path);
            var sy = (double)spaceH / PathH(path);
            var d = new StringBuilder();
            double curX = 0, curY = 0;

            bool TryPt(DocumentFormat.OpenXml.Drawing.Point? p, out double x, out double y)
            {
                x = y = 0;
                if (!TryVal(p?.X?.Value, out x) || !TryVal(p?.Y?.Value, out y)) return false;
                x *= sx;
                y *= sy;
                return true;
            }

            foreach (var cmd in path.ChildElements)
            {
                switch (cmd)
                {
                    case DocumentFormat.OpenXml.Drawing.MoveTo mv when TryPt(mv.Point, out var x, out var y):
                        d.Append('M').Append(F(x)).Append(' ').Append(F(y)).Append(' ');
                        curX = x; curY = y;
                        break;
                    case DocumentFormat.OpenXml.Drawing.LineTo ln when TryPt(ln.Point, out var x, out var y):
                        d.Append('L').Append(F(x)).Append(' ').Append(F(y)).Append(' ');
                        curX = x; curY = y;
                        break;
                    case DocumentFormat.OpenXml.Drawing.CubicBezierCurveTo cb:
                    {
                        var p = cb.Elements<DocumentFormat.OpenXml.Drawing.Point>().ToList();
                        if (p.Count == 3 && TryPt(p[0], out var x1, out var y1)
                            && TryPt(p[1], out var x2, out var y2) && TryPt(p[2], out var ex, out var ey))
                        {
                            d.Append('C').Append(F(x1)).Append(' ').Append(F(y1)).Append(' ')
                                .Append(F(x2)).Append(' ').Append(F(y2)).Append(' ')
                                .Append(F(ex)).Append(' ').Append(F(ey)).Append(' ');
                            curX = ex; curY = ey;
                        }
                        break;
                    }
                    case DocumentFormat.OpenXml.Drawing.QuadraticBezierCurveTo qb:
                    {
                        var p = qb.Elements<DocumentFormat.OpenXml.Drawing.Point>().ToList();
                        if (p.Count == 2 && TryPt(p[0], out var x1, out var y1)
                            && TryPt(p[1], out var ex, out var ey))
                        {
                            d.Append('Q').Append(F(x1)).Append(' ').Append(F(y1)).Append(' ')
                                .Append(F(ex)).Append(' ').Append(F(ey)).Append(' ');
                            curX = ex; curY = ey;
                        }
                        break;
                    }
                    case DocumentFormat.OpenXml.Drawing.ArcTo arc:
                    {
                        if (TryVal(arc.WidthRadius?.Value, out var wr) && TryVal(arc.HeightRadius?.Value, out var hr)
                            && TryVal(arc.StartAngle?.Value, out var stRaw) && TryVal(arc.SwingAngle?.Value, out var swRaw)
                            && wr > 0 && hr > 0)
                        {
                            wr *= sx;
                            hr *= sy;
                            var theta1 = stRaw / 60000.0 * Math.PI / 180.0;
                            var delta = swRaw / 60000.0 * Math.PI / 180.0;
                            var cx = curX - wr * Math.Cos(theta1);
                            var cy = curY - hr * Math.Sin(theta1);
                            var ex = cx + wr * Math.Cos(theta1 + delta);
                            var ey = cy + hr * Math.Sin(theta1 + delta);
                            var largeArc = Math.Abs(delta) > Math.PI ? 1 : 0;
                            var sweep = delta >= 0 ? 1 : 0;
                            d.Append('A').Append(F(wr)).Append(' ').Append(F(hr)).Append(" 0 ")
                                .Append(largeArc).Append(' ').Append(sweep).Append(' ')
                                .Append(F(ex)).Append(' ').Append(F(ey)).Append(' ');
                            curX = ex; curY = ey;
                        }
                        break;
                    }
                    case DocumentFormat.OpenXml.Drawing.CloseShapePath:
                        d.Append("Z ");
                        break;
                }
            }
            if (d.Length == 0) continue;

            var pathNoFill = path.Fill != null
                && path.Fill.Value == DocumentFormat.OpenXml.Drawing.PathFillModeValues.None;
            var pathStroke = path.Stroke?.Value ?? true;
            svgPaths.Append($"<path d=\"{d.ToString().Trim()}\" fill=\"{(pathNoFill ? "none" : fill)}\""
                + $"{(pathStroke ? stroke : string.Empty)}/>");
        }
        if (svgPaths.Length == 0 || spaceW <= 0 || spaceH <= 0) return string.Empty;

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {spaceW} {spaceH}\" "
             + $"width=\"{F(widthPx)}\" height=\"{F(heightPx)}\" preserveAspectRatio=\"none\" "
             + $"style=\"display:block;\">{defs}{svgPaths}</svg>";
    }

    private static Dictionary<string, double> EvaluateGeometryGuides(
        DocumentFormat.OpenXml.Drawing.CustomGeometry custom, double w, double h)
    {
        var ss = Math.Min(w, h);
        var guides = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["w"] = w, ["h"] = h, ["l"] = 0, ["t"] = 0, ["r"] = w, ["b"] = h,
            ["hc"] = w / 2, ["vc"] = h / 2, ["ss"] = ss, ["ls"] = Math.Max(w, h),
            ["wd2"] = w / 2, ["wd3"] = w / 3, ["wd4"] = w / 4, ["wd5"] = w / 5,
            ["wd6"] = w / 6, ["wd8"] = w / 8, ["wd10"] = w / 10,
            ["hd2"] = h / 2, ["hd3"] = h / 3, ["hd4"] = h / 4, ["hd5"] = h / 5,
            ["hd6"] = h / 6, ["hd8"] = h / 8, ["hd10"] = h / 10,
            ["ssd2"] = ss / 2, ["ssd4"] = ss / 4, ["ssd6"] = ss / 6, ["ssd8"] = ss / 8,
            ["ssd16"] = ss / 16, ["ssd32"] = ss / 32,
            ["cd2"] = 10800000, ["cd4"] = 5400000, ["cd8"] = 2700000,
            ["3cd4"] = 16200000, ["3cd8"] = 8100000, ["5cd8"] = 13500000, ["7cd8"] = 18900000,
        };
        foreach (var list in new OpenXmlElement?[] { custom.AdjustValueList, custom.ShapeGuideList })
        {
            if (list == null) continue;
            foreach (var gd in list.Elements<DocumentFormat.OpenXml.Drawing.ShapeGuide>())
            {
                if (gd.Name?.Value is string name && gd.Formula?.Value is string fmla
                    && TryEvalGuideFormula(fmla, guides, out var val))
                {
                    guides[name] = val;
                }
            }
        }
        return guides;
    }

    private static bool TryEvalGuideFormula(string formula,
        IReadOnlyDictionary<string, double> guides, out double result)
    {
        result = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tok = formula.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0) return false;

        var args = new double[tok.Length - 1];
        for (var i = 1; i < tok.Length; i++)
        {
            if (!double.TryParse(tok[i], System.Globalization.NumberStyles.Float, inv, out args[i - 1])
                && !guides.TryGetValue(tok[i], out args[i - 1]))
            {
                return false;
            }
        }
        static double Rad(double deg60k) => deg60k / 60000.0 * Math.PI / 180.0;
        static double Deg60k(double rad) => rad * 180.0 / Math.PI * 60000.0;

        switch (tok[0])
        {
            case "val" when args.Length == 1: result = args[0]; return true;
            case "*/" when args.Length == 3 && args[2] != 0: result = args[0] * args[1] / args[2]; return true;
            case "+-" when args.Length == 3: result = args[0] + args[1] - args[2]; return true;
            case "+/" when args.Length == 3 && args[2] != 0: result = (args[0] + args[1]) / args[2]; return true;
            case "?:" when args.Length == 3: result = args[0] > 0 ? args[1] : args[2]; return true;
            case "abs" when args.Length == 1: result = Math.Abs(args[0]); return true;
            case "max" when args.Length == 2: result = Math.Max(args[0], args[1]); return true;
            case "min" when args.Length == 2: result = Math.Min(args[0], args[1]); return true;
            case "mod" when args.Length == 3:
                result = Math.Sqrt(args[0] * args[0] + args[1] * args[1] + args[2] * args[2]); return true;
            case "pin" when args.Length == 3:
                result = Math.Clamp(args[1], Math.Min(args[0], args[2]), Math.Max(args[0], args[2])); return true;
            case "sqrt" when args.Length == 1 && args[0] >= 0: result = Math.Sqrt(args[0]); return true;
            case "sin" when args.Length == 2: result = args[0] * Math.Sin(Rad(args[1])); return true;
            case "cos" when args.Length == 2: result = args[0] * Math.Cos(Rad(args[1])); return true;
            case "tan" when args.Length == 2: result = args[0] * Math.Tan(Rad(args[1])); return true;
            case "at2" when args.Length == 2: result = Deg60k(Math.Atan2(args[1], args[0])); return true;
            case "cat2" when args.Length == 3:
                result = args[0] * Math.Cos(Math.Atan2(args[2], args[1])); return true;
            case "sat2" when args.Length == 3:
                result = args[0] * Math.Sin(Math.Atan2(args[2], args[1])); return true;
            default: return false;
        }
    }

    private static string? HexColorOrNull(string? value)
        => !string.IsNullOrEmpty(value) && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$")
            ? value : null;

    private static string StripSize(string css)
        => System.Text.RegularExpressions.Regex.Replace(css, @"(?:min-height|width):[^;]+;", string.Empty);

    private string BuildTextBoxLayoutCss(OpenXmlElement container)
    {
        var extent = container.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
        var widthEmu = extent?.Cx?.Value ?? 0;
        var heightEmu = extent?.Cy?.Value ?? 0;
        var widthPx = widthEmu > 0 ? (int)OoxmlUnits.EmuToPixels(widthEmu) : 0;
        var heightPx = heightEmu > 0 ? (int)OoxmlUnits.EmuToPixels(heightEmu) : 0;

        var sizeCss = new StringBuilder();
        if (widthPx > 0) sizeCss.Append($"width:{widthPx}px;");
        if (heightPx > 0) sizeCss.Append($"min-height:{heightPx}px;");

        var anchor = container.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor>().FirstOrDefault();
        if (anchor == null)
            return "display:inline-block;max-width:100%;vertical-align:top;margin:4px 0;" + sizeCss;

        var (xEmu, yEmu) = ResolveAnchorPosition(anchor, widthEmu, heightEmu);
        var leftPx = (int)OoxmlUnits.EmuToPixels(xEmu);
        var topPx = (int)OoxmlUnits.EmuToPixels(yEmu);
        var zIndex = anchor.BehindDoc?.Value == true ? "z-index:0;" : "z-index:1;";

        return $"position:absolute;left:{leftPx}px;top:{topPx}px;{zIndex}" + sizeCss;
    }

    private string GetRunStyleClean(RunProperties? props)
    {
        if (props == null) return string.Empty;
        var css = new StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        var fontSize = props.Descendants<FontSize>().FirstOrDefault();
        if (fontSize?.Val != null &&
            double.TryParse(fontSize.Val.Value, System.Globalization.NumberStyles.Float, inv, out var sz))
        {
            css.Append(string.Format(inv, "font-size:{0:0.##}pt;", OoxmlUnits.HalfPointsToPoints(sz)));
        }

        var fontFamily = props.Descendants<RunFonts>().FirstOrDefault();
        var fontName = GetFontName(fontFamily);
        if (fontName != null)
            css.Append(FontFamilyCss(fontName));

        var colorCss = ResolveRunColorCss(props.Descendants<Color>().FirstOrDefault());
        if (colorCss != null)
            css.Append($"color:{colorCss};");

        var highlight = props.Descendants<Highlight>().FirstOrDefault();
        if (highlight?.Val != null)
            css.Append($"background-color:{GetHighlightColor(highlight.Val.Value)};");

        var shading = props.Descendants<Shading>().FirstOrDefault();
        if (shading?.Fill?.Value != null && shading.Fill.Value != "auto")
            css.Append($"background-color:#{shading.Fill.Value};");

        var spacing = props.Descendants<Spacing>().FirstOrDefault();
        if (spacing?.Val != null)
            css.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "letter-spacing:{0:0.#}pt;", OoxmlUnits.TwipsToPoints(spacing.Val.Value)));

        var caps = props.Descendants<Caps>().FirstOrDefault();
        if (caps != null && (caps.Val == null || caps.Val.Value))
            css.Append("text-transform:uppercase;");
        var smallCaps = props.Descendants<SmallCaps>().FirstOrDefault();
        if (smallCaps != null && (smallCaps.Val == null || smallCaps.Val.Value))
            css.Append("font-variant:small-caps;");

        return css.ToString();
    }

    private string ConvertRunPropertiesToCss(OpenXmlElement props)
    {
        var css = new StringBuilder();

        var bold = props.Descendants<Bold>().FirstOrDefault();
        if (bold != null && (bold.Val == null || bold.Val.Value))
            css.Append("font-weight:bold;");

        var italic = props.Descendants<Italic>().FirstOrDefault();
        if (italic != null && (italic.Val == null || italic.Val.Value))
            css.Append("font-style:italic;");

        var decorations = new List<string>();
        var underline = props.Descendants<Underline>().FirstOrDefault();
        if (underline?.Val != null && underline.Val.Value != UnderlineValues.None)
            decorations.Add("underline");
        var strike = props.Descendants<Strike>().FirstOrDefault();
        if (strike != null && (strike.Val == null || strike.Val.Value))
            decorations.Add("line-through");
        var dStrike = props.Descendants<DoubleStrike>().FirstOrDefault();
        if (dStrike != null && (dStrike.Val == null || dStrike.Val.Value))
            decorations.Add("line-through");
        if (decorations.Count > 0)
            css.Append($"text-decoration:{string.Join(" ", decorations)};");

        var fontSize = props.Descendants<FontSize>().FirstOrDefault();
        if (fontSize?.Val != null &&
            double.TryParse(fontSize.Val.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var size))
        {
            css.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "font-size:{0:0.##}pt;", OoxmlUnits.HalfPointsToPoints(size)));
        }

        var fontFamily = props.Descendants<RunFonts>().FirstOrDefault();
        var fontName = GetFontName(fontFamily);
        if (fontName != null)
            css.Append(FontFamilyCss(fontName));

        var styleColorCss = ResolveRunColorCss(props.Descendants<Color>().FirstOrDefault());
        if (styleColorCss != null)
            css.Append($"color:{styleColorCss};");

        var highlight = props.Descendants<Highlight>().FirstOrDefault();
        if (highlight?.Val != null)
            css.Append($"background-color:{GetHighlightColor(highlight.Val.Value)};");

        var shading = props.Descendants<Shading>().FirstOrDefault();
        if (shading?.Fill?.Value != null && shading.Fill.Value != "auto")
            css.Append($"background-color:#{shading.Fill.Value};");

        var vertAlign = props.Descendants<VerticalTextAlignment>().FirstOrDefault();
        if (vertAlign?.Val != null)
        {
            if (vertAlign.Val.Value == VerticalPositionValues.Superscript)
                css.Append("vertical-align:super;font-size:smaller;");
            else if (vertAlign.Val.Value == VerticalPositionValues.Subscript)
                css.Append("vertical-align:sub;font-size:smaller;");
        }

        var spacing = props.Descendants<Spacing>().FirstOrDefault();
        if (spacing?.Val != null)
            css.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "letter-spacing:{0:0.#}pt;", OoxmlUnits.TwipsToPoints(spacing.Val.Value)));

        var capsProp = props.Descendants<Caps>().FirstOrDefault();
        if (capsProp != null && (capsProp.Val == null || capsProp.Val.Value))
            css.Append("text-transform:uppercase;");
        var smallCapsProp = props.Descendants<SmallCaps>().FirstOrDefault();
        if (smallCapsProp != null && (smallCapsProp.Val == null || smallCapsProp.Val.Value))
            css.Append("font-variant:small-caps;");

        return css.ToString();
    }

    private void LoadThemeFonts()
    {
        var fontScheme = _themePart?.Theme?.ThemeElements?.FontScheme;
        if (fontScheme == null) return;

        var major = fontScheme.MajorFont;
        if (major != null)
        {
            _themeMajorLatin = major.LatinFont?.Typeface?.Value;
            _themeMajorEastAsia = major.EastAsianFont?.Typeface?.Value;
            _themeMajorComplexScript = major.ComplexScriptFont?.Typeface?.Value;
        }

        var minor = fontScheme.MinorFont;
        if (minor != null)
        {
            _themeMinorLatin = minor.LatinFont?.Typeface?.Value;
            _themeMinorEastAsia = minor.EastAsianFont?.Typeface?.Value;
            _themeMinorComplexScript = minor.ComplexScriptFont?.Typeface?.Value;
        }
    }

    private string? ResolveThemeFont(ThemeFontValues theme)
    {
        if (theme == ThemeFontValues.MajorAscii || theme == ThemeFontValues.MajorHighAnsi) return _themeMajorLatin;
        if (theme == ThemeFontValues.MinorAscii || theme == ThemeFontValues.MinorHighAnsi) return _themeMinorLatin;
        if (theme == ThemeFontValues.MajorEastAsia) return _themeMajorEastAsia;
        if (theme == ThemeFontValues.MinorEastAsia) return _themeMinorEastAsia;
        if (theme == ThemeFontValues.MajorBidi) return _themeMajorComplexScript;
        if (theme == ThemeFontValues.MinorBidi) return _themeMinorComplexScript;
        return null;
    }

    private static string FontFamilyCss(string fontName) =>
        $"font-family:'{fontName}',{GenericFontFallback(fontName)};";

    private static string GenericFontFallback(string fontName)
    {
        var f = fontName.ToLowerInvariant();
        if (f.Contains("times") || f.Contains("cambria") || f.Contains("georgia") || f.Contains("garamond")
            || f.Contains("minion") || f.Contains("book antiqua") || f.Contains("palatino")
            || (f.Contains("serif") && !f.Contains("sans")))
            return "serif";
        if (f.Contains("courier") || f.Contains("consolas") || f.Contains("mono"))
            return "monospace";
        return "sans-serif";
    }

    private string? GetFontName(RunFonts? fonts)
    {
        if (fonts == null) return null;


        if (!string.IsNullOrEmpty(fonts.Ascii?.Value)) return fonts.Ascii!.Value;

        if (fonts.AsciiTheme?.Value != null)
        {
            var resolved = ResolveThemeFont(fonts.AsciiTheme.Value);
            if (!string.IsNullOrEmpty(resolved)) return resolved;
        }

        if (!string.IsNullOrEmpty(fonts.HighAnsi?.Value)) return fonts.HighAnsi!.Value;

        if (fonts.HighAnsiTheme?.Value != null)
        {
            var resolved = ResolveThemeFont(fonts.HighAnsiTheme.Value);
            if (!string.IsNullOrEmpty(resolved)) return resolved;
        }

        return null;
    }

    private string? ResolveRunColorCss(Color? color)
    {
        if (color == null) return null;

        var val = color.Val?.Value;
        if (!string.IsNullOrEmpty(val) && val != "auto")
            return "#" + val;

        if (color.ThemeColor?.Value != null)
        {
            var themeHex = ResolveThemeColor(color.ThemeColor.Value)?.TrimStart('#');
            if (themeHex != null)
                return "#" + ApplyTintShade(themeHex, color.ThemeTint?.Value, color.ThemeShade?.Value);
        }

        return val == "auto" ? "#000000" : null;
    }

    private string? ResolveThemeColor(ThemeColorValues themeColor)
    {
        if (_themePart?.Theme?.ThemeElements?.ColorScheme == null) return null;
        
        var cs = _themePart.Theme.ThemeElements.ColorScheme;
        
        DocumentFormat.OpenXml.Drawing.Color2Type? c2 = null;
        if (themeColor == ThemeColorValues.Dark1) c2 = cs.Dark1Color;
        else if (themeColor == ThemeColorValues.Light1) c2 = cs.Light1Color;
        else if (themeColor == ThemeColorValues.Dark2) c2 = cs.Dark2Color;
        else if (themeColor == ThemeColorValues.Light2) c2 = cs.Light2Color;
        else if (themeColor == ThemeColorValues.Accent1) c2 = cs.Accent1Color;
        else if (themeColor == ThemeColorValues.Accent2) c2 = cs.Accent2Color;
        else if (themeColor == ThemeColorValues.Accent3) c2 = cs.Accent3Color;
        else if (themeColor == ThemeColorValues.Accent4) c2 = cs.Accent4Color;
        else if (themeColor == ThemeColorValues.Accent5) c2 = cs.Accent5Color;
        else if (themeColor == ThemeColorValues.Accent6) c2 = cs.Accent6Color;
        else if (themeColor == ThemeColorValues.Hyperlink) c2 = cs.Hyperlink;
        else if (themeColor == ThemeColorValues.FollowedHyperlink) c2 = cs.FollowedHyperlinkColor;
        else if (themeColor == ThemeColorValues.Text1) c2 = cs.Dark1Color;
        else if (themeColor == ThemeColorValues.Text2) c2 = cs.Dark2Color;
        else if (themeColor == ThemeColorValues.Background1) c2 = cs.Light1Color;
        else if (themeColor == ThemeColorValues.Background2) c2 = cs.Light2Color;

        if (c2 == null) return null;
        
        var srgb = c2.GetFirstChild<DocumentFormat.OpenXml.Drawing.RgbColorModelHex>();
        if (srgb?.Val?.Value != null) return "#" + srgb.Val.Value;
        
        var sysColor = c2.GetFirstChild<DocumentFormat.OpenXml.Drawing.SystemColor>();
        if (sysColor?.LastColor?.Value != null) return "#" + sysColor.LastColor.Value;
        
        return null;
    }

    private static void AcceptTrackedRevisions(WordprocessingDocument document)
    {
        var main = document.MainDocumentPart;
        if (main == null) return;

        var roots = new List<OpenXmlElement?> { main.Document?.Body };
        foreach (var headerPart in main.HeaderParts) roots.Add(headerPart.Header);
        foreach (var footerPart in main.FooterParts) roots.Add(footerPart.Footer);
        roots.Add(main.FootnotesPart?.Footnotes);
        roots.Add(main.EndnotesPart?.Endnotes);

        foreach (var root in roots)
        {
            if (root == null) continue;

            foreach (var row in root.Descendants<TableRow>().ToList())
            {
                var trPr = row.TableRowProperties;
                if (trPr?.GetFirstChild<Deleted>() != null) row.Remove();
                else trPr?.GetFirstChild<Inserted>()?.Remove();
            }

            foreach (var deleted in root.Descendants<DeletedRun>().ToList()) deleted.Remove();
            foreach (var moveFrom in root.Descendants<MoveFromRun>().ToList()) moveFrom.Remove();
            foreach (var inserted in root.Descendants<InsertedRun>().ToList()) UnwrapRevisionContainer(inserted);
            foreach (var moveTo in root.Descendants<MoveToRun>().ToList()) UnwrapRevisionContainer(moveTo);

            foreach (var mark in root.Descendants<Inserted>().ToList()) mark.Remove();
            foreach (var mark in root.Descendants<Deleted>().ToList()) mark.Remove();
            foreach (var change in root.Descendants<ParagraphPropertiesChange>().ToList()) change.Remove();
            foreach (var change in root.Descendants<RunPropertiesChange>().ToList()) change.Remove();
        }
    }

    private static void UnwrapRevisionContainer(OpenXmlElement wrapper)
    {
        var parent = wrapper.Parent;
        if (parent == null) return;
        OpenXmlElement anchor = wrapper;
        foreach (var child in wrapper.ChildElements.ToList())
        {
            child.Remove();
            parent.InsertAfter(child, anchor);
            anchor = child;
        }
        wrapper.Remove();
    }

    private string GetHighlightColor(HighlightColorValues value)
    {
        if (value == HighlightColorValues.Yellow) return "#ffff00";
        if (value == HighlightColorValues.Green) return "#00ff00";
        if (value == HighlightColorValues.Cyan) return "#00ffff";
        if (value == HighlightColorValues.Magenta) return "#ff00ff";
        if (value == HighlightColorValues.Blue) return "#0000ff";
        if (value == HighlightColorValues.Red) return "#ff0000";
        if (value == HighlightColorValues.DarkBlue) return "#000080";
        if (value == HighlightColorValues.DarkCyan) return "#008080";
        if (value == HighlightColorValues.DarkGreen) return "#008000";
        if (value == HighlightColorValues.DarkMagenta) return "#800080";
        if (value == HighlightColorValues.DarkRed) return "#800000";
        if (value == HighlightColorValues.DarkYellow) return "#808000";
        if (value == HighlightColorValues.DarkGray) return "#808080";
        if (value == HighlightColorValues.LightGray) return "#c0c0c0";
        if (value == HighlightColorValues.Black) return "#000000";
        return "transparent";
    }

    private string ConvertHyperlinkToHtml(Hyperlink hyperlink, WordprocessingDocument document,
        OpenXmlPart? sourcePart = null, ComplexFieldState? state = null)
    {
        var html = new StringBuilder();
        html.Append(BuildAnchorOpenTag(hyperlink, document));
        AppendComplexFieldContent(hyperlink.Elements(), html, state ?? new ComplexFieldState(), document, sourcePart);
        html.Append("</a>");
        return html.ToString();
    }

    private string ConvertSimpleFieldToHtml(SimpleField simpleField)
    {
        var instruction = simpleField.Instruction?.Value?.Trim().ToUpperInvariant() ?? "";
        var fieldRun = simpleField.Descendants<Run>().FirstOrDefault();

        if (instruction.Contains("PAGE") && !instruction.Contains("NUMPAGES") && !instruction.Contains("SECTIONPAGES"))
            return FieldSpan("field-page", "{page}", fieldRun);
        if (instruction.Contains("NUMPAGES") || instruction.Contains("SECTIONPAGES"))
            return FieldSpan("field-numpages", "{pages}", fieldRun);

        var rawInstruction = simpleField.Instruction?.Value ?? string.Empty;
        if (simpleField.FieldLock?.Value != true && IsAutoDateFieldInstruction(rawInstruction))
            return FieldDateSpan(rawInstruction, fieldRun);

        var text = string.Join("", simpleField.Descendants<Text>().Select(t => t.Text));
        if (!string.IsNullOrEmpty(text))
            return EscapeHtml(text);
        if (instruction.Contains("DATE") || instruction.Contains("TIME"))
            return FieldSpan("field-date", DateTime.Now.ToString("dd.MM.yyyy"), fieldRun);
        return "";
    }

    private string FieldSpan(string cssClass, string placeholder, Run? run)
    {
        var style = run?.RunProperties != null ? GetRunStyleClean(run.RunProperties) : string.Empty;
        return $"<span class=\"{cssClass}\" style=\"{style}\">{placeholder}</span>";
    }

    private string ConvertDrawingToHtml(Drawing drawing, WordprocessingDocument document, OpenXmlPart? sourcePart = null)
    {
        var dispatchPart = sourcePart ?? (OpenXmlPart?)document.MainDocumentPart;

        var graphicUri = drawing.Descendants<A.GraphicData>().FirstOrDefault()?.Uri?.Value ?? string.Empty;
        var (extWpx, extHpx) = DrawingExtentPx(drawing);
        if (graphicUri.EndsWith("/wordprocessingGroup", StringComparison.OrdinalIgnoreCase)
            || graphicUri.EndsWith("/wordprocessingCanvas", StringComparison.OrdinalIgnoreCase))
        {
            var group = RenderGroupDrawingAsHtml(drawing, document, dispatchPart);
            if (!string.IsNullOrEmpty(group)) return group;
            return RenderPreservedPlaceholder(drawing, dispatchPart, extWpx, extHpx, "group");
        }
        if (graphicUri.EndsWith("/chart", StringComparison.OrdinalIgnoreCase)
            || graphicUri.Contains("/diagram", StringComparison.OrdinalIgnoreCase))
        {
            return RenderPreservedPlaceholder(drawing, dispatchPart, extWpx, extHpx, "chart");
        }

        var blip = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault();
        if (blip?.Embed?.Value == null)
        {
            var textBox = RenderTextBoxContent(drawing, document, sourcePart);
            if (!string.IsNullOrEmpty(textBox)) return HoistTextBox(textBox);

            var shape = RenderVectorShapeAsHtml(drawing, BuildPreservedXmlAttrs(drawing, dispatchPart));
            if (!string.IsNullOrEmpty(shape)) return shape;

            if (blip?.Link?.Value != null)
                _log.LogWarning("Pominięto obraz z relacją zewnętrzną r:link={RelId} (obrazy linkowane nie są osadzone w pakiecie).",
                    blip.Link.Value);

            return RenderPreservedPlaceholder(drawing, dispatchPart, extWpx, extHpx, "drawing");
        }

        var relationshipId = blip.Embed.Value;

        var effectivePart = sourcePart ?? (OpenXmlPart?)document.MainDocumentPart;
        if (effectivePart == null) return string.Empty;

        var extent = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
        var width = extent?.Cx != null ? EmuToPx(extent.Cx.Value) : 200;
        var height = extent?.Cy != null ? EmuToPx(extent.Cy.Value) : 200;
        var widthEmu = extent?.Cx?.Value ?? OoxmlUnits.PixelsToEmu(width);
        var heightEmu = extent?.Cy?.Value ?? OoxmlUnits.PixelsToEmu(height);

        string? base64Data = null;
        string? contentType = null;

        if (_images.TryGetValue(ImageCacheKey(effectivePart, relationshipId), out var image))
        {
            base64Data = image.Base64Data;
            contentType = image.ContentType;
        }
        else
        {
            try
            {
                var imagePart = effectivePart.GetPartById(relationshipId) as ImagePart;
                if (imagePart != null)
                {
                    LoadImageFromPart(effectivePart, imagePart);
                    if (_images.TryGetValue(ImageCacheKey(effectivePart, relationshipId), out var lazy))
                    {
                        base64Data = lazy.Base64Data;
                        contentType = lazy.ContentType;
                    }
                }
                else
                {
                    _log.LogWarning("Relacja obrazu {RelId} w części {PartUri} nie wskazuje na ImagePart — obraz pominięty.",
                        relationshipId, effectivePart.Uri);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Nie udało się rozwiązać relacji obrazu {RelId} w części {PartUri}: {Error}",
                    relationshipId, effectivePart.Uri, ex.Message);
            }
        }

        if (base64Data == null || contentType == null) return string.Empty;

        if (width <= 0 || height <= 0)
        {
            var probe = _graphics.ConvertForEditor(new GraphicSource
            {
                Data = System.Convert.FromBase64String(base64Data),
                ContentType = contentType,
                Origin = GraphicOrigin.LegacyDocxPart
            });
            width = probe.Web is { WidthPx: > 0 } pw ? pw.WidthPx : 200;
            height = probe.Web is { HeightPx: > 0 } ph ? ph.HeightPx : 200;
            widthEmu = OoxmlUnits.PixelsToEmu(width);
            heightEmu = OoxmlUnits.PixelsToEmu(height);
        }

        var legacySrc = WebGraphicForLegacy(System.Convert.FromBase64String(base64Data), contentType, widthEmu, heightEmu);
        var drawingSrc = legacySrc?.dataUrl ?? $"data:{contentType};base64,{base64Data}";

        var anchor = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor>().FirstOrDefault();
        var posAttrs = string.Empty;
        if (anchor != null)
        {
            var behind = anchor.BehindDoc?.Value == true;
            var (xEmu, yEmu) = ResolveAnchorPosition(anchor, widthEmu, heightEmu);
            posAttrs = $" data-pos-mode=\"{(behind ? "behind" : "front")}\""
                + $" data-x-emu=\"{xEmu}\" data-y-emu=\"{yEmu}\"";
            var wrap = ReadAnchorWrapMode(anchor);
            if (wrap != null) posAttrs += $" data-wrap=\"{wrap}\"";
        }

        var borderAttrs = string.Empty;
        var outline = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Outline>().FirstOrDefault();
        if (outline != null && outline.Width != null && outline.Width.Value > 0)
        {
            var borderWidthPx = Math.Max(1, (int)Math.Round(OoxmlUnits.EmuToPixels(outline.Width.Value)));
            var srgb = outline.Descendants<DocumentFormat.OpenXml.Drawing.RgbColorModelHex>().FirstOrDefault();
            var color = srgb?.Val?.Value;
            if (!string.IsNullOrEmpty(color) && System.Text.RegularExpressions.Regex.IsMatch(color, "^[0-9A-Fa-f]{6}$"))
            {
                var dash = outline.GetFirstChild<DocumentFormat.OpenXml.Drawing.PresetDash>();
                var style = "solid";
                if (dash?.Val?.Value == DocumentFormat.OpenXml.Drawing.PresetLineDashValues.Dash) style = "dashed";
                else if (dash?.Val?.Value == DocumentFormat.OpenXml.Drawing.PresetLineDashValues.Dot) style = "dotted";
                borderAttrs = $" data-border-width=\"{borderWidthPx}\" data-border-color=\"#{color}\" data-border-style=\"{style}\"";
            }
        }

        var cropAttrs = string.Empty;
        var srcRect = drawing.Descendants<DocumentFormat.OpenXml.Drawing.SourceRectangle>().FirstOrDefault();
        if (srcRect != null)
        {
            var l = (srcRect.Left?.Value ?? 0) / 1000;
            var r = (srcRect.Right?.Value ?? 0) / 1000;
            var t = (srcRect.Top?.Value ?? 0) / 1000;
            var b = (srcRect.Bottom?.Value ?? 0) / 1000;
            if (l > 0 || r > 0 || t > 0 || b > 0)
            {
                cropAttrs = $" data-crop-l=\"{l}\" data-crop-r=\"{r}\" data-crop-t=\"{t}\" data-crop-b=\"{b}\"";
            }
        }

        var docPr = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().FirstOrDefault();
        var alt = docPr?.Description?.Value ?? docPr?.Title?.Value;
        var altAttr = !string.IsNullOrEmpty(alt) ? $" alt=\"{EscapeHtml(alt)}\"" : string.Empty;

        var legacyAttr = legacySrc?.isBlank == true ? " data-legacy-graphic=\"blank\"" : string.Empty;
        var originalAttr = legacySrc != null
            ? $" data-original-src=\"data:{contentType};base64,{base64Data}\""
            : string.Empty;
        return $"<img src=\"{drawingSrc}\" " +
               $"style=\"max-width:100%;width:{width}px;height:{height}px;\" " +
               $"data-image-id=\"{relationshipId}\" " +
               $"data-width-emu=\"{widthEmu}\" data-height-emu=\"{heightEmu}\"" +
               $"{altAttr}{posAttrs}{borderAttrs}{cropAttrs}{legacyAttr}{originalAttr} />";
    }


    internal const int MaxPreservedXmlBytes = 1024 * 1024;
    internal const int MaxPreservedRelsBytes = 4 * 1024 * 1024;
    internal const string OoxmlRelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private string BuildPreservedXmlAttrs(OpenXmlElement element, OpenXmlPart? sourcePart)
    {
        try
        {
            var xmlBytes = Encoding.UTF8.GetBytes(element.OuterXml);
            if (xmlBytes.Length > MaxPreservedXmlBytes)
            {
                _log.LogWarning("Fragment grafiki XML {Name} przekracza limit {Limit} B — bez pass-through.",
                    element.LocalName, MaxPreservedXmlBytes);
                return string.Empty;
            }

            var relIds = CollectRelationshipIds(element);
            var relsAttr = string.Empty;
            if (relIds.Count > 0)
            {
                if (sourcePart == null) return string.Empty;
                var map = new Dictionary<string, PreservedRelEntry>();
                long total = 0;
                foreach (var rid in relIds)
                {
                    OpenXmlPart target;
                    try { target = sourcePart.GetPartById(rid); }
                    catch
                    {
                        _log.LogInformation("Grafika XML {Name}: relacja {RelId} nierozwiązywalna — bez pass-through.",
                            element.LocalName, rid);
                        return string.Empty;
                    }
                    using var partStream = target.GetStream(FileMode.Open, FileAccess.Read);
                    using var buffer = new MemoryStream();
                    partStream.CopyTo(buffer);
                    total += buffer.Length;
                    if (total > MaxPreservedRelsBytes)
                    {
                        _log.LogWarning("Grafika XML {Name}: części relacji przekraczają limit {Limit} B — bez pass-through.",
                            element.LocalName, MaxPreservedRelsBytes);
                        return string.Empty;
                    }
                    map[rid] = new PreservedRelEntry(target.ContentType, System.Convert.ToBase64String(buffer.ToArray()));
                }
                var json = System.Text.Json.JsonSerializer.Serialize(map);
                relsAttr = $" data-docx-rels=\"{System.Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}\"";
            }

            return $" data-docx-xml=\"{System.Convert.ToBase64String(xmlBytes)}\"{relsAttr}";
        }
        catch (Exception ex)
        {
            _log.LogWarning("Nie udało się zbudować pass-through grafiki XML {Name}: {Error}",
                element.LocalName, ex.Message);
            return string.Empty;
        }
    }

    internal static List<string> CollectRelationshipIds(OpenXmlElement element)
    {
        var ids = new List<string>();
        void Scan(OpenXmlElement el)
        {
            foreach (var attr in el.GetAttributes())
            {
                if (attr.NamespaceUri == OoxmlRelationshipNs
                    && !string.IsNullOrEmpty(attr.Value) && !ids.Contains(attr.Value))
                {
                    ids.Add(attr.Value!);
                }
            }
            foreach (var child in el.ChildElements) Scan(child);
        }
        Scan(element);
        return ids;
    }

    private string RenderPreservedPlaceholder(OpenXmlElement element, OpenXmlPart? sourcePart,
        int widthPx, int heightPx, string kind)
    {
        var attrs = BuildPreservedXmlAttrs(element, sourcePart);
        if (attrs.Length == 0)
        {
            _log.LogWarning("Grafika XML {Kind} ({Name}) pominięta BEZ zachowania (fragment nieprzenośny).",
                kind, element.LocalName);
            return string.Empty;
        }
        _log.LogInformation("Grafika XML {Kind} ({Name}) zachowana pass-through jako niewidoczny placeholder {W}x{H}px.",
            kind, element.LocalName, widthPx, heightPx);
        var size = widthPx > 0 && heightPx > 0 && !IsFloatingGraphicElement(element)
            ? $"width:{widthPx}px;height:{heightPx}px;"
            : "width:0;height:0;";
        return $"<span class=\"docx-preserved\" data-preserved=\"{kind}\" contenteditable=\"false\"" +
               $"{attrs} style=\"display:inline-block;overflow:hidden;vertical-align:baseline;{size}\"></span>";
    }

    private static bool IsFloatingGraphicElement(OpenXmlElement element)
    {
        if (element is A.Wordprocessing.Anchor || element.Descendants<A.Wordprocessing.Anchor>().Any())
            return true;
        return element.Descendants()
            .Any(d => d.NamespaceUri == "urn:schemas-microsoft-com:vml"
                && d.GetAttributes().Any(a => a.LocalName == "style"
                    && a.Value?.Contains("position:absolute", StringComparison.OrdinalIgnoreCase) == true));
    }

    private static (int w, int h) DrawingExtentPx(Drawing drawing)
    {
        var extent = drawing.Descendants<A.Wordprocessing.Extent>().FirstOrDefault();
        var w = extent?.Cx != null ? (int)OoxmlUnits.EmuToPixels(extent.Cx.Value) : 0;
        var h = extent?.Cy != null ? (int)OoxmlUnits.EmuToPixels(extent.Cy.Value) : 0;
        return (w, h);
    }

    private static (int w, int h) VmlStyleSizePx(OpenXmlElement container)
    {
        foreach (var el in container.Descendants())
        {
            string style;
            try { style = el.GetAttribute("style", "").Value ?? ""; } catch { continue; }
            if (string.IsNullOrEmpty(style)) continue;
            var wm = Regex.Match(style, @"width:\s*([\d.]+)pt");
            var hm = Regex.Match(style, @"height:\s*([\d.]+)pt");
            if (!wm.Success && !hm.Success) continue;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var w = wm.Success ? (int)(double.Parse(wm.Groups[1].Value, inv) * 96 / 72) : 0;
            var h = hm.Success ? (int)(double.Parse(hm.Groups[1].Value, inv) * 96 / 72) : 0;
            return (w, h);
        }
        return (0, 0);
    }

    private const string OfficeVmlNamespace = "urn:schemas-microsoft-com:office:office";

    private static string GetOfficeVmlAttribute(OpenXmlElement el, string localName)
    {
        try { return el.GetAttribute(localName, OfficeVmlNamespace).Value ?? string.Empty; }
        catch { return string.Empty; }
    }

    private string ConvertVmlHorizontalRuleToHtml(DocumentFormat.OpenXml.Vml.Rectangle rect)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var align = GetOfficeVmlAttribute(rect, "hralign");
        var pctRaw = GetOfficeVmlAttribute(rect, "hrpct");
        var noshade = GetOfficeVmlAttribute(rect, "hrnoshade");
        var std = GetOfficeVmlAttribute(rect, "hrstd");
        var fill = rect.FillColor?.Value;

        var styleAttr = string.Empty;
        try { styleAttr = rect.GetAttribute("style", string.Empty).Value ?? string.Empty; } catch { }
        var hm = Regex.Match(styleAttr, @"height:\s*([\d.]+)pt");
        var heightPt = hm.Success ? double.Parse(hm.Groups[1].Value, inv) : 0;
        var heightPx = Math.Max(1, (int)Math.Round(heightPt * 96.0 / 72.0));

        var css = new StringBuilder("display:block;border:none;");
        if (double.TryParse(pctRaw, System.Globalization.NumberStyles.Any, inv, out var pct)
            && pct > 0 && pct < 100)
        {
            css.Append(string.Format(inv, "width:{0:0.##}%;", pct));
            css.Append(align switch
            {
                "left" => "margin-left:0;margin-right:auto;",
                "right" => "margin-left:auto;margin-right:0;",
                _ => "margin-left:auto;margin-right:auto;",
            });
        }
        css.Append(string.Format(inv, "height:{0}px;background:{1};",
            heightPx, string.IsNullOrEmpty(fill) ? "#a0a0a0" : fill));

        var attrs = new StringBuilder(" data-docx-hr=\"1\"");
        if (!string.IsNullOrEmpty(align)) attrs.Append($" data-hr-align=\"{System.Net.WebUtility.HtmlEncode(align)}\"");
        if (!string.IsNullOrEmpty(pctRaw)) attrs.Append($" data-hr-pct=\"{System.Net.WebUtility.HtmlEncode(pctRaw)}\"");
        if (!string.IsNullOrEmpty(noshade)) attrs.Append($" data-hr-noshade=\"{System.Net.WebUtility.HtmlEncode(noshade)}\"");
        if (!string.IsNullOrEmpty(std)) attrs.Append($" data-hr-std=\"{System.Net.WebUtility.HtmlEncode(std)}\"");
        if (!string.IsNullOrEmpty(fill)) attrs.Append($" data-hr-fill=\"{System.Net.WebUtility.HtmlEncode(fill)}\"");
        if (heightPt > 0) attrs.Append(string.Format(inv, " data-hr-height-pt=\"{0:0.##}\"", heightPt));

        return $"<span class=\"docx-hr\"{attrs} style=\"{css}\"></span>";
    }

    private string ConvertPictureToHtml(Picture picture, WordprocessingDocument document, OpenXmlPart? sourcePart = null)
    {
        var hrRect = picture.Descendants<DocumentFormat.OpenXml.Vml.Rectangle>()
            .FirstOrDefault(r => GetOfficeVmlAttribute(r, "hr") is "t" or "true");
        if (hrRect != null) return ConvertVmlHorizontalRuleToHtml(hrRect);

        var imageData = picture.Descendants<DocumentFormat.OpenXml.Vml.ImageData>().FirstOrDefault();
        if (imageData?.RelationshipId?.Value == null)
        {
            var vmlPart = sourcePart ?? (OpenXmlPart?)document.MainDocumentPart;

            var vmlTextBox = RenderTextBoxContent(picture, document, sourcePart);
            if (!string.IsNullOrEmpty(vmlTextBox)) return HoistTextBox(vmlTextBox);

            foreach (var vmlChild in picture.ChildElements)
            {
                if (vmlChild.NamespaceUri != "urn:schemas-microsoft-com:vml") continue;
                var converted = _graphics.ConvertVmlShapeForEditor(vmlChild.OuterXml);
                if (converted?.Web == null) continue;
                var preservedVml = BuildPreservedXmlAttrs(picture, vmlPart);
                var wPx = converted.Web.WidthPx > 0 ? converted.Web.WidthPx : 200;
                var hPx = converted.Web.HeightPx > 0 ? converted.Web.HeightPx : 150;
                _log.LogInformation("Kształt VML {Name} odwzorowany jako SVG {W}x{H}px (pass-through: {Preserved}).",
                    vmlChild.LocalName, wPx, hPx, preservedVml.Length > 0);
                return $"<img src=\"{converted.Web.ToDataUrl()}\" style=\"width:{wPx}px;height:{hPx}px;\" "
                     + $"data-vml-shape=\"{EscapeHtml(vmlChild.LocalName)}\" contenteditable=\"false\"{preservedVml} />";
            }

            var (vmlW, vmlH) = VmlStyleSizePx(picture);
            return RenderPreservedPlaceholder(picture, vmlPart, vmlW, vmlH, "vml");
        }

        var relationshipId = imageData.RelationshipId.Value;

        var shape = picture.Descendants<DocumentFormat.OpenXml.Vml.Shape>().FirstOrDefault();
        var styleAttr = "";
        try { styleAttr = shape?.GetAttribute("style", "").Value ?? ""; } catch { }
        
        int vmlWidth = 200, vmlHeight = 150;
        var wm = Regex.Match(styleAttr, @"width:\s*([\d.]+)pt");
        var hm = Regex.Match(styleAttr, @"height:\s*([\d.]+)pt");
        if (wm.Success)
            vmlWidth = (int)(double.Parse(wm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 96 / 72);
        if (hm.Success)
            vmlHeight = (int)(double.Parse(hm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 96 / 72);

        string? base64Data = null;
        string? contentType = null;

        var effectivePart = sourcePart ?? (OpenXmlPart?)document.MainDocumentPart;
        if (effectivePart == null) return string.Empty;

        if (_images.TryGetValue(ImageCacheKey(effectivePart, relationshipId), out var image))
        {
            base64Data = image.Base64Data;
            contentType = image.ContentType;
        }
        else
        {
            try
            {
                if (effectivePart.GetPartById(relationshipId) is ImagePart imagePart)
                {
                    LoadImageFromPart(effectivePart, imagePart);
                    if (_images.TryGetValue(ImageCacheKey(effectivePart, relationshipId), out var lazy))
                    {
                        base64Data = lazy.Base64Data;
                        contentType = lazy.ContentType;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Nie udało się rozwiązać relacji VML v:imagedata {RelId} w części {PartUri}: {Error}",
                    relationshipId, effectivePart.Uri, ex.Message);
            }
        }

        if (base64Data == null || contentType == null) return string.Empty;

        var legacyVml = WebGraphicForLegacy(
            System.Convert.FromBase64String(base64Data), contentType,
            (long)(vmlWidth * 9525.0), (long)(vmlHeight * 9525.0));
        var vmlSrc = legacyVml?.dataUrl ?? $"data:{contentType};base64,{base64Data}";
        var vmlLegacyAttr = legacyVml?.isBlank == true ? " data-legacy-graphic=\"blank\"" : string.Empty;
        var vmlOriginalAttr = legacyVml != null
            ? $" data-original-src=\"data:{contentType};base64,{base64Data}\""
            : string.Empty;

        return $"<img src=\"{vmlSrc}\" " +
               $"style=\"max-width:100%;width:{vmlWidth}px;height:{vmlHeight}px;\" " +
               $"data-image-id=\"{relationshipId}\"{vmlLegacyAttr}{vmlOriginalAttr} />";
    }

    private string ConvertEmbeddedObjectToHtml(EmbeddedObject embedded, WordprocessingDocument document,
        OpenXmlPart? sourcePart)
    {
        var effectivePart = sourcePart ?? (OpenXmlPart?)document.MainDocumentPart;
        var preserved = BuildPreservedXmlAttrs(embedded, effectivePart);

        var relId = embedded.Descendants<DocumentFormat.OpenXml.Vml.ImageData>()
            .FirstOrDefault()?.RelationshipId?.Value;
        if (relId != null && effectivePart != null)
        {
            var (w, h) = VmlStyleSizePx(embedded);
            if (w <= 0) w = 200;
            if (h <= 0) h = 150;
            var src = TryResolveImageDataUrl(effectivePart, relId,
                OoxmlUnits.PixelsToEmu(w), OoxmlUnits.PixelsToEmu(h));
            if (src != null)
            {
                _log.LogInformation("w:object (OLE) — podgląd v:imagedata {W}x{H}px, pass-through: {Preserved}.",
                    w, h, preserved.Length > 0);
                return $"<img src=\"{src}\" style=\"width:{w}px;height:{h}px;\" "
                     + $"data-ole-preview=\"1\" contenteditable=\"false\"{preserved} />";
            }
        }

        var (pw, ph) = VmlStyleSizePx(embedded);
        return RenderPreservedPlaceholder(embedded, effectivePart, pw, ph, "object");
    }

    private string ConvertTableToHtml(Table table, WordprocessingDocument document, OpenXmlPart? sourcePart = null)
    {
        var html = new StringBuilder();
        var tableProps = table.GetFirstChild<TableProperties>();

        var styleCtx = ResolveTableStyleContext(tableProps);

        var tableWidth = "auto";
        var hasExplicitWidth = false;
        if (tableProps?.TableWidth?.Width?.Value != null)
        {
            var w = tableProps.TableWidth;
            if (w.Type?.Value == TableWidthUnitValues.Pct
                && double.TryParse(w.Width.Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct50) && pct50 > 0)
            {
                tableWidth = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##}%", pct50 / 50.0);
                hasExplicitWidth = true;
            }
            else if (w.Type?.Value == TableWidthUnitValues.Dxa && int.TryParse(w.Width.Value, out var wtw) && wtw > 0)
            {
                tableWidth = $"{TwipsToPx(wtw)}px";
                hasExplicitWidth = true;
            }
        }

        var gridColumnsPx = ReadTableGridColumnsPx(table);
        var isFixedLayout = tableProps?.TableLayout?.Type?.Value == TableLayoutValues.Fixed;

        var gridHasAllWidths = gridColumnsPx.Count > 0 && gridColumnsPx.All(c => c.Px > 0);
        var useFixedLayout = isFixedLayout || hasExplicitWidth || gridHasAllWidths;

        var indentTw = tableProps?.TableIndentation?.Width?.Value ?? 0;
        var availTw = _availableContentWidthTwips is { } a && a > 0
            ? a - Math.Max(0, indentTw)
            : (long?)null;
        if (availTw is > 0 && gridColumnsPx.Count > 0)
        {
            var availPx = TwipsToPx((int)availTw.Value);
            var totalPx = gridColumnsPx.Sum(c => c.Px);
            if (availPx > 0 && totalPx > availPx)
            {
                var f = (double)availPx / totalPx;
                gridColumnsPx = gridColumnsPx
                    .Select(c => (Px: Math.Max(1, (int)Math.Round(c.Px * f)), c.Tw))
                    .ToList();
                if (hasExplicitWidth && tableWidth.EndsWith("px", StringComparison.Ordinal))
                    tableWidth = $"{Math.Min(availPx, gridColumnsPx.Sum(c => c.Px))}px";
            }
        }

        if (useFixedLayout && tableWidth == "auto" && gridColumnsPx.Count > 0)
            tableWidth = $"{gridColumnsPx.Sum(c => c.Px)}px";

        var layoutCss = useFixedLayout ? "table-layout:fixed;" : string.Empty;
        var colgroupHtml = BuildColgroupHtml(gridColumnsPx);

        var tableAlign = "";
        if (tableProps?.TableJustification?.Val != null)
        {
            var tblAlignVal = tableProps.TableJustification.Val.Value;
            if (tblAlignVal == TableRowAlignmentValues.Center) tableAlign = "margin-left:auto;margin-right:auto;";
            else if (tblAlignVal == TableRowAlignmentValues.Right) tableAlign = "margin-left:auto;margin-right:0;";
        }

        var tableIndent = "";
        if (tableProps?.TableIndentation?.Width?.Value != null)
        {
            tableIndent = $"margin-left:{TwipsToPx(tableProps.TableIndentation.Width.Value)}px;";
        }

        var collapseCss = "border-collapse:collapse;";
        var cellSpacingAttr = string.Empty;
        var cellSpacingTw = GetTwipsValue(tableProps?.GetFirstChild<TableCellSpacing>());
        if (cellSpacingTw is > 0)
        {
            collapseCss = $"border-collapse:separate;border-spacing:{TwipsToPx(cellSpacingTw.Value)}px;";
            cellSpacingAttr = $" data-cell-spacing-tw=\"{cellSpacingTw.Value}\"";
        }

        var tblBordersMarker = styleCtx.Borders.IsEmpty ? " data-no-borders=\"1\"" : "";

        var widthSemanticsAttrs = string.Empty;
        if (!hasExplicitWidth && tableWidth != "auto")
            widthSemanticsAttrs += " data-tbl-w=\"auto\"";
        if (!isFixedLayout && useFixedLayout)
            widthSemanticsAttrs += " data-tbl-layout=\"autofit\"";

        var styleAttrs = string.Empty;
        if (!string.IsNullOrEmpty(styleCtx.StyleId))
            styleAttrs = $" data-tbl-style=\"{System.Net.WebUtility.HtmlEncode(styleCtx.StyleId)}\" data-tbl-look=\"{styleCtx.LookHex}\"";

        var defaultPadding = styleCtx.DefaultCellPaddingCss;

        var rows = table.Elements<TableRow>().ToList();
        var renderCtx = new TableRenderContext(
            styleCtx,
            defaultPadding,
            rows.Count,
            CountGridColumns(table, rows));

        html.Append($"<table{tblBordersMarker}{styleAttrs}{cellSpacingAttr}{widthSemanticsAttrs} style=\"{collapseCss}width:{tableWidth};margin:4px 0;{layoutCss}{tableAlign}{tableIndent}\">");
        html.Append(colgroupHtml);

        var prevTableParagraphDefaults = _tableParagraphDefaultCss;
        _tableParagraphDefaultCss = renderCtx.Style.ParagraphDefaultCss;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var trPr = row.TableRowProperties;

            var rowStyle = "";
            var rowAttrs = new StringBuilder();
            var trHeight = trPr?.Elements<TableRowHeight>().FirstOrDefault();
            if (trHeight?.Val?.Value != null)
            {
                var hRule = trHeight.HeightType?.Value ?? HeightRuleValues.AtLeast;
                if (hRule != HeightRuleValues.Auto)
                {
                    var hPx = TwipsToPx((int)trHeight.Val.Value);
                    rowStyle = $" style=\"height:{hPx}px;\"";
                    rowAttrs.Append($" data-row-height-tw=\"{trHeight.Val.Value}\"");
                    if (hRule == HeightRuleValues.Exact)
                        rowAttrs.Append(" data-row-hrule=\"exact\"");
                }
            }

            if (trPr?.Elements<TableHeader>().Any() == true)
                rowAttrs.Append(" data-tbl-header=\"1\"");
            if (trPr?.Elements<CantSplit>().Any() == true)
                rowAttrs.Append(" data-cant-split=\"1\"");

            html.Append($"<tr{rowAttrs}{rowStyle}>");

            var rowCells = FlattenRowCells(row).ToList();

            var gridBefore = trPr?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
            var gridAfter = trPr?.GetFirstChild<GridAfter>()?.Val?.Value ?? 0;

            var rowGridTotal = rowCells.Sum(GetGridSpan) + gridBefore + gridAfter;
            var deficit = renderCtx.GridColumnCount - rowGridTotal;

            var renderPlan = BuildRowRenderPlan(rowCells);

            var gridCursor = 0;
            if (gridBefore > 0)
            {
                html.Append(BuildGridSpacerCellHtml("before", gridBefore));
                gridCursor += gridBefore;
            }
            for (var ci = 0; ci < renderPlan.Count; ci++)
            {
                var extraColspan = renderPlan[ci].HMergeExtraSpan
                    + ((deficit > 0 && ci == renderPlan.Count - 1) ? deficit : 0);
                AppendTableCellHtml(html, table, rows, rowIndex, renderPlan[ci].Cell, renderCtx,
                    ref gridCursor, extraColspan, document, sourcePart);
            }
            if (gridAfter > 0)
                html.Append(BuildGridSpacerCellHtml("after", gridAfter));

            html.Append("</tr>");
        }

        _tableParagraphDefaultCss = prevTableParagraphDefaults;

        html.Append("</table>");
        return html.ToString();
    }

    private static string BuildGridSpacerCellHtml(string side, int span)
    {
        var colspan = span > 1 ? $" colspan=\"{span}\"" : string.Empty;
        return $"<td{colspan} data-grid-spacer=\"{side}\" style=\"border:none;padding:0;\"></td>";
    }

    private static int CountGridColumns(Table table, List<TableRow> rows)
    {
        var grid = table.GetFirstChild<TableGrid>();
        var fromGrid = grid?.Elements<GridColumn>().Count() ?? 0;
        if (fromGrid > 0) return fromGrid;

        var max = 0;
        foreach (var row in rows)
        {
            var trPr = row.TableRowProperties;
            var count = row.Elements<TableCell>().Sum(GetGridSpan)
                + (trPr?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0)
                + (trPr?.GetFirstChild<GridAfter>()?.Val?.Value ?? 0);
            max = Math.Max(max, count);
        }
        return max;
    }

    private List<(int Px, int Tw)> ReadTableGridColumnsPx(Table table)
    {
        var result = new List<(int Px, int Tw)>();
        var grid = table.GetFirstChild<TableGrid>();
        if (grid == null) return result;

        foreach (var col in grid.Elements<GridColumn>())
        {
            if (col.Width?.Value != null && int.TryParse(col.Width.Value, out var twips))
                result.Add((TwipsToPx(twips), twips));
            else
                result.Add((0, 0));
        }
        return result;
    }

    private static string BuildColgroupHtml(List<(int Px, int Tw)> columns)
    {
        if (columns.Count == 0) return string.Empty;

        var sb = new StringBuilder("<colgroup>");
        foreach (var (px, tw) in columns)
            sb.Append(px > 0 ? $"<col style=\"width:{px}px;\" data-w-tw=\"{tw}\" />" : "<col />");
        sb.Append("</colgroup>");
        return sb.ToString();
    }

    private int? GetTwipsValue(TableWidthType? element)
    {
        if (element?.Width?.Value == null) return null;
        return int.TryParse(element.Width.Value, out var v) ? v : null;
    }

    private int? GetDxaValue(TableWidthDxaNilType? element)
    {
        if (element?.Width?.Value == null) return null;
        return (int)element.Width.Value;
    }
    
    private int CountRowSpan(Table table, TableRow startRow, TableCell startCell)
    {
        var rows = table.Elements<TableRow>().ToList();
        var startRowIndex = rows.IndexOf(startRow);

        var startColumn = GetCellStartColumn(startRow, startCell);

        var rowSpan = 1;
        for (int i = startRowIndex + 1; i < rows.Count; i++)
        {
            var cell = FindCellAtColumn(rows[i], startColumn);
            var vMerge = cell?.TableCellProperties?.VerticalMerge;
            if (vMerge != null && (vMerge.Val == null || vMerge.Val.Value == MergedCellValues.Continue))
                rowSpan++;
            else
                break;
        }

        return rowSpan;
    }

    private static int GetGridSpan(TableCell cell)
    {
        var gs = CellPropertyElement<GridSpan>(cell)?.Val?.Value;
        return gs is > 0 ? gs.Value : 1;
    }

    private static T? CellPropertyElement<T>(TableCell cell) where T : OpenXmlElement =>
        cell.Elements<TableCellProperties>()
            .Select(p => p.GetFirstChild<T>())
            .FirstOrDefault(e => e != null);

    private static TableCellProperties? EffectiveCellProps(TableCell cell)
    {
        var all = cell.Elements<TableCellProperties>().ToList();
        if (all.Count <= 1) return all.Count == 1 ? all[0] : null;
        var merged = (TableCellProperties)all[0].CloneNode(true);
        foreach (var extra in all.Skip(1))
            foreach (var child in extra.ChildElements)
                if (merged.ChildElements.All(c => c.GetType() != child.GetType()))
                    merged.AppendChild(child.CloneNode(true));
        return merged;
    }

    private static List<(TableCell Cell, int HMergeExtraSpan)> BuildRowRenderPlan(List<TableCell> rowCells)
    {
        var plan = new List<(TableCell, int)>(rowCells.Count);
        foreach (var cell in rowCells)
        {
            if (plan.Count > 0 && GetHMerge(cell) == MergedCellValues.Continue)
            {
                var (prev, extra) = plan[^1];
                plan[^1] = (prev, extra + GetGridSpan(cell));
                continue;
            }
            plan.Add((cell, 0));
        }
        return plan;
    }

    private static MergedCellValues? GetHMerge(TableCell cell)
    {
        var hMerge = CellPropertyElement<HorizontalMerge>(cell);
        if (hMerge == null) return null;
        return hMerge.Val?.Value ?? MergedCellValues.Continue;
    }

    private static IEnumerable<TableCell> FlattenRowCells(TableRow row)
    {
        foreach (var cellLike in row.Elements())
        {
            if (cellLike is TableCell cell)
                yield return cell;
            else if (cellLike is SdtCell sdtCell
                     && sdtCell.GetFirstChild<SdtContentCell>() is { } sdtContent)
                foreach (var innerCell in sdtContent.Elements<TableCell>())
                    yield return innerCell;
        }
    }

    private static int GetCellStartColumn(TableRow row, TableCell target)
    {
        var column = 0;
        foreach (var cell in row.Elements<TableCell>())
        {
            if (ReferenceEquals(cell, target)) return column;
            column += GetGridSpan(cell);
        }
        return column;
    }

    private static TableCell? FindCellAtColumn(TableRow row, int targetColumn)
    {
        var column = 0;
        foreach (var cell in row.Elements<TableCell>())
        {
            if (column == targetColumn) return cell;
            column += GetGridSpan(cell);
        }
        return null;
    }

    private string GetTableCellStyleDetailed(
        TableCell cell,
        TableRenderContext ctx,
        int rowIndex,
        int gridColStart,
        int gridSpan,
        int rowSpan)
    {
        var css = new StringBuilder();
        var props = EffectiveCellProps(cell);

        var regions = ComputeConditionalRegions(ctx, rowIndex, gridColStart, gridSpan, rowSpan);

        var isFirstRow = rowIndex == 0;
        var isLastRow = rowIndex + rowSpan >= ctx.RowCount;
        var isFirstCol = gridColStart == 0;
        var isLastCol = ctx.GridColumnCount <= 0 || gridColStart + gridSpan >= ctx.GridColumnCount;

        var cb = props?.TableCellBorders;
        css.Append($"border-top:{ResolveCellBorderSide(cb?.TopBorder, regions, TableCellEdge.Top, isFirstRow ? ctx.Style.Borders.Top : ctx.Style.Borders.InsideH, ctx)};");
        css.Append($"border-bottom:{ResolveCellBorderSide(cb?.BottomBorder, regions, TableCellEdge.Bottom, isLastRow ? ctx.Style.Borders.Bottom : ctx.Style.Borders.InsideH, ctx)};");
        css.Append($"border-left:{ResolveCellBorderSide(cb?.LeftBorder, regions, TableCellEdge.Left, isFirstCol ? ctx.Style.Borders.Left : ctx.Style.Borders.InsideV, ctx)};");
        css.Append($"border-right:{ResolveCellBorderSide(cb?.RightBorder, regions, TableCellEdge.Right, isLastCol ? ctx.Style.Borders.Right : ctx.Style.Borders.InsideV, ctx)};");

        var cm = props?.TableCellMargin;
        if (cm != null)
        {
            var top = GetTwipsValue(cm.TopMargin) ?? ctx.Style.DefaultCellPadTopTw;
            var bottom = GetTwipsValue(cm.BottomMargin) ?? ctx.Style.DefaultCellPadBottomTw;
            var left = cm.LeftMargin?.Width?.Value != null
                ? int.Parse(cm.LeftMargin.Width.Value) : ctx.Style.DefaultCellPadLeftTw;
            var right = cm.RightMargin?.Width?.Value != null
                ? int.Parse(cm.RightMargin.Width.Value) : ctx.Style.DefaultCellPadRightTw;
            css.Append($"padding:{TwipsToPx(top)}px {TwipsToPx(right)}px {TwipsToPx(bottom)}px {TwipsToPx(left)}px;");
        }
        else
        {
            css.Append($"padding:{ctx.DefaultPadding};");
        }

        var vAlignVal = props?.TableCellVerticalAlignment?.Val?.Value
            ?? regions
                .Select(r => r.GetFirstChild<TableStyleConditionalFormattingTableCellProperties>()
                    ?.GetFirstChild<TableCellVerticalAlignment>()?.Val?.Value)
                .FirstOrDefault(v => v != null)
            ?? ctx.Style.WholeTableCellVerticalAlignment;
        var vAlign = vAlignVal != null ? GetTableVerticalAlignment(vAlignVal.Value) : "top";
        css.Append($"vertical-align:{vAlign};");

        var shadingHex = ResolveShadingHex(props?.Shading);
        if (shadingHex == null)
        {
            foreach (var region in regions)
            {
                shadingHex = ResolveShadingHex(region.GetFirstChild<TableStyleConditionalFormattingTableCellProperties>()?.GetFirstChild<Shading>());
                if (shadingHex != null) break;
            }
        }
        shadingHex ??= ResolveShadingHex(ctx.Style.WholeTableCellShading);
        shadingHex ??= ResolveShadingHex(ctx.Style.TableShading);
        if (shadingHex != null)
            css.Append($"background-color:#{shadingHex};");

        if (props != null)
        {
            var w = props.TableCellWidth;
            if (w?.Width?.Value != null)
            {
                if (w.Type?.Value == TableWidthUnitValues.Pct
                    && double.TryParse(w.Width.Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct50) && pct50 > 0)
                    css.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "width:{0:0.##}%;", pct50 / 50.0));
                else if ((w.Type == null || w.Type.Value == TableWidthUnitValues.Dxa)
                    && int.TryParse(w.Width.Value, out var wtw) && wtw > 0)
                    css.Append($"width:{TwipsToPx(wtw)}px;");
            }

            if (props.TextDirection?.Val != null)
            {
                var tdVal = props.TextDirection.Val.Value;
                if (tdVal == TextDirectionValues.TopToBottomRightToLeft) css.Append("writing-mode:vertical-rl;");
                else if (tdVal == TextDirectionValues.BottomToTopLeftToRight) css.Append("writing-mode:vertical-lr;");
            }

            if (props.NoWrap != null)
                css.Append("white-space:nowrap;");
        }

        return css.ToString();
    }

    private static bool HasNoVisibleBorders(string cellStyle) =>
        cellStyle.Contains("border-top:none;") &&
        cellStyle.Contains("border-bottom:none;") &&
        cellStyle.Contains("border-left:none;") &&
        cellStyle.Contains("border-right:none;");

    private enum TableCellEdge { Top, Bottom, Left, Right }

    private string ResolveCellBorderSide(
        BorderType? directBorder,
        List<TableStyleProperties> regions,
        TableCellEdge edge,
        BorderType? tableFallback,
        TableRenderContext ctx)
    {
        if (directBorder != null)
        {
            var v = directBorder.Val?.Value;
            if (v == null || v == BorderValues.None || v == BorderValues.Nil) return "none";
            return GetBorderCss(directBorder);
        }

        foreach (var region in regions)
        {
            var tcB = region.GetFirstChild<TableStyleConditionalFormattingTableCellProperties>()?.GetFirstChild<TableCellBorders>();
            var b = PickEdge(tcB, edge);
            if (b == null)
            {
                var tblB = region.GetFirstChild<TableStyleConditionalFormattingTableProperties>()?.GetFirstChild<TableBorders>();
                b = PickEdge(tblB, edge);
            }
            if (b != null)
            {
                var v = b.Val?.Value;
                if (v == null || v == BorderValues.None || v == BorderValues.Nil) return "none";
                return GetBorderCss(b);
            }
        }

        var whole = PickEdge(ctx.Style.WholeTableCellBorders, edge);
        if (whole != null)
        {
            var v = whole.Val?.Value;
            if (v == null || v == BorderValues.None || v == BorderValues.Nil) return "none";
            return GetBorderCss(whole);
        }

        if (tableFallback == null) return "none";
        var fv = tableFallback.Val?.Value;
        if (fv == null || fv == BorderValues.None || fv == BorderValues.Nil) return "none";
        return GetBorderCss(tableFallback);
    }

    private static BorderType? PickEdge(TableCellBorders? b, TableCellEdge edge) => edge switch
    {
        TableCellEdge.Top => b?.TopBorder,
        TableCellEdge.Bottom => b?.BottomBorder,
        TableCellEdge.Left => b?.LeftBorder,
        TableCellEdge.Right => b?.RightBorder,
        _ => null
    };

    private static BorderType? PickEdge(TableBorders? b, TableCellEdge edge) => edge switch
    {
        TableCellEdge.Top => b?.TopBorder,
        TableCellEdge.Bottom => b?.BottomBorder,
        TableCellEdge.Left => b?.LeftBorder,
        TableCellEdge.Right => b?.RightBorder,
        _ => null
    };

    private static bool IsTableBordersEmpty(TableBorders? tb)
    {
        if (tb == null) return true;
        bool IsBlank(BorderType? b)
        {
            if (b == null) return true;
            var v = b.Val?.Value;
            return v == null || v == BorderValues.None || v == BorderValues.Nil;
        }
        return IsBlank(tb.TopBorder)
            && IsBlank(tb.BottomBorder)
            && IsBlank(tb.LeftBorder)
            && IsBlank(tb.RightBorder)
            && IsBlank(tb.InsideHorizontalBorder)
            && IsBlank(tb.InsideVerticalBorder);
    }

    #region Rozwiązywanie stylu tabeli (w:tblStyle / w:tblLook / w:tblStylePr)

    private sealed class EffectiveTableBorders
    {
        public BorderType? Top, Bottom, Left, Right, InsideH, InsideV;

        public bool IsEmpty
        {
            get
            {
                static bool Blank(BorderType? b)
                {
                    if (b == null) return true;
                    var v = b.Val?.Value;
                    return v == null || v == BorderValues.None || v == BorderValues.Nil;
                }
                return Blank(Top) && Blank(Bottom) && Blank(Left) && Blank(Right) && Blank(InsideH) && Blank(InsideV);
            }
        }
    }

    private sealed class TableStyleContext
    {
        public string? StyleId;
        public string LookHex = "04A0";
        public bool FirstRow, LastRow, FirstColumn, LastColumn, RowBands = true, ColumnBands;
        public int RowBandSize = 1, ColBandSize = 1;
        public EffectiveTableBorders Borders = new();
        public Shading? TableShading;
        public Shading? WholeTableCellShading;
        public TableCellBorders? WholeTableCellBorders;
        public TableVerticalAlignmentValues? WholeTableCellVerticalAlignment;
        public string DefaultCellPaddingCss = "";
        public int DefaultCellPadTopTw, DefaultCellPadBottomTw, DefaultCellPadLeftTw, DefaultCellPadRightTw;
        public string ParagraphDefaultCss = "";
        public Dictionary<TableStyleOverrideValues, TableStyleProperties> Conditional = new();
    }

    private sealed class TableRenderContext
    {
        public TableStyleContext Style { get; }
        public string DefaultPadding { get; }
        public int RowCount { get; }
        public int GridColumnCount { get; }

        public TableRenderContext(TableStyleContext style, string defaultPadding, int rowCount, int gridColumnCount)
        {
            Style = style;
            DefaultPadding = defaultPadding;
            RowCount = rowCount;
            GridColumnCount = gridColumnCount;
        }
    }

    private TableStyleContext ResolveTableStyleContext(TableProperties? tblPr)
    {
        var ctx = new TableStyleContext();

        var chain = new List<Style>();
        var styleId = tblPr?.TableStyle?.Val?.Value;
        ctx.StyleId = styleId;
        var guard = 0;
        while (!string.IsNullOrEmpty(styleId) && guard++ < 12 && _rawStyles.TryGetValue(styleId!, out var st))
        {
            chain.Add(st);
            styleId = st.BasedOn?.Val?.Value;
        }

        var look = tblPr?.GetFirstChild<TableLook>();
        int mask = 0x0020 | 0x0080 | 0x0400;
        if (look?.Val?.Value is string lookVal
            && int.TryParse(lookVal, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedMask))
            mask = parsedMask;
        ctx.FirstRow = look?.FirstRow?.Value ?? (mask & 0x0020) != 0;
        ctx.LastRow = look?.LastRow?.Value ?? (mask & 0x0040) != 0;
        ctx.FirstColumn = look?.FirstColumn?.Value ?? (mask & 0x0080) != 0;
        ctx.LastColumn = look?.LastColumn?.Value ?? (mask & 0x0100) != 0;
        ctx.RowBands = !(look?.NoHorizontalBand?.Value ?? (mask & 0x0200) != 0);
        ctx.ColumnBands = !(look?.NoVerticalBand?.Value ?? (mask & 0x0400) != 0);
        ctx.LookHex = ((ctx.FirstRow ? 0x0020 : 0) | (ctx.LastRow ? 0x0040 : 0)
            | (ctx.FirstColumn ? 0x0080 : 0) | (ctx.LastColumn ? 0x0100 : 0)
            | (ctx.RowBands ? 0 : 0x0200) | (ctx.ColumnBands ? 0 : 0x0400))
            .ToString("X4", System.Globalization.CultureInfo.InvariantCulture);

        BorderType? Side(Func<TableBorders, BorderType?> pick)
        {
            if (tblPr?.TableBorders is TableBorders direct && pick(direct) is BorderType d) return d;
            foreach (var st in chain)
            {
                var sb = st.StyleTableProperties?.GetFirstChild<TableBorders>();
                if (sb != null && pick(sb) is BorderType b) return b;
            }
            return null;
        }
        ctx.Borders.Top = Side(b => b.TopBorder);
        ctx.Borders.Bottom = Side(b => b.BottomBorder);
        ctx.Borders.Left = Side(b => b.LeftBorder);
        ctx.Borders.Right = Side(b => b.RightBorder);
        ctx.Borders.InsideH = Side(b => b.InsideHorizontalBorder);
        ctx.Borders.InsideV = Side(b => b.InsideVerticalBorder);

        ctx.TableShading = tblPr?.GetFirstChild<Shading>()
            ?? chain.Select(s => s.StyleTableProperties?.GetFirstChild<Shading>()).FirstOrDefault(s => s != null);
        ctx.WholeTableCellShading = chain
            .Select(s => s.StyleTableCellProperties?.GetFirstChild<Shading>())
            .FirstOrDefault(s => s != null);
        ctx.WholeTableCellBorders = chain
            .Select(s => s.StyleTableCellProperties?.GetFirstChild<TableCellBorders>())
            .FirstOrDefault(b => b != null);
        ctx.WholeTableCellVerticalAlignment = chain
            .Select(s => s.StyleTableCellProperties?.GetFirstChild<TableCellVerticalAlignment>()?.Val?.Value)
            .FirstOrDefault(v => v != null);

        ctx.RowBandSize = (int?)tblPr?.GetFirstChild<TableStyleRowBandSize>()?.Val?.Value
            ?? chain.Select(s => (int?)s.StyleTableProperties?.GetFirstChild<TableStyleRowBandSize>()?.Val?.Value)
                .FirstOrDefault(v => v != null) ?? 1;
        ctx.ColBandSize = (int?)tblPr?.GetFirstChild<TableStyleColumnBandSize>()?.Val?.Value
            ?? chain.Select(s => (int?)s.StyleTableProperties?.GetFirstChild<TableStyleColumnBandSize>()?.Val?.Value)
                .FirstOrDefault(v => v != null) ?? 1;

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            foreach (var tsp in chain[i].Elements<TableStyleProperties>())
            {
                if (tsp.Type?.Value is TableStyleOverrideValues t)
                    ctx.Conditional[t] = tsp;
            }
        }

        const int wordDefaultCellMarginTwips = 108;
        var cellMars = new List<TableCellMarginDefault>();
        if (tblPr?.TableCellMarginDefault != null) cellMars.Add(tblPr.TableCellMarginDefault);
        foreach (var st in chain)
        {
            var m = st.StyleTableProperties?.GetFirstChild<TableCellMarginDefault>();
            if (m != null) cellMars.Add(m);
        }
        int PadSide(Func<TableCellMarginDefault, int?> pick, int fallback)
        {
            foreach (var m in cellMars)
                if (pick(m) is int v) return v;
            return fallback;
        }
        var topPad = PadSide(m => GetTwipsValue(m.TopMargin), 0);
        var bottomPad = PadSide(m => GetTwipsValue(m.BottomMargin), 0);
        var leftPad = PadSide(m => GetDxaValue(m.TableCellLeftMargin), wordDefaultCellMarginTwips);
        var rightPad = PadSide(m => GetDxaValue(m.TableCellRightMargin), wordDefaultCellMarginTwips);
        ctx.DefaultCellPadTopTw = topPad;
        ctx.DefaultCellPadBottomTw = bottomPad;
        ctx.DefaultCellPadLeftTw = leftPad;
        ctx.DefaultCellPadRightTw = rightPad;
        ctx.DefaultCellPaddingCss = $"{TwipsToPx(topPad)}px {TwipsToPx(rightPad)}px {TwipsToPx(bottomPad)}px {TwipsToPx(leftPad)}px";

        var cellParagraphCss = new StringBuilder(_defaultParagraphSpacingCss);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var styleSpacing = chain[i].StyleParagraphProperties?.GetFirstChild<SpacingBetweenLines>();
            if (styleSpacing != null)
                cellParagraphCss.Append(SpacingCss(styleSpacing));
        }
        ctx.ParagraphDefaultCss = DeduplicateCss(cellParagraphCss.ToString());

        return ctx;
    }

    private string SpacingCss(SpacingBetweenLines spacing)
    {
        var css = new StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (spacing.Before?.Value != null && int.TryParse(spacing.Before.Value, out var beforeVal))
            css.Append(string.Format(inv, "margin-top:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(beforeVal)));
        if (spacing.After?.Value != null && int.TryParse(spacing.After.Value, out var afterVal))
            css.Append(string.Format(inv, "padding-bottom:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(afterVal)));
        if (spacing.Line?.Value != null && int.TryParse(spacing.Line.Value, out var lineVal))
        {
            var lineRule = spacing.LineRule?.Value;
            if (lineRule == LineSpacingRuleValues.Exact || lineRule == LineSpacingRuleValues.AtLeast)
            {
                css.Append(string.Format(inv, "line-height:{0:0.##}pt;", OoxmlUnits.TwipsToPoints(lineVal)));
                if (lineRule == LineSpacingRuleValues.AtLeast)
                    css.Append("--w-line-rule:atLeast;");
            }
            else
            {
                css.Append(WordLineSpacing.AutoCss(lineVal, _defaultFontFamily));
            }
        }
        return css.ToString();
    }

    private static List<TableStyleProperties> ComputeConditionalRegions(
        TableRenderContext ctx, int rowIndex, int gridColStart, int gridSpan, int rowSpan)
    {
        var s = ctx.Style;
        var result = new List<TableStyleProperties>();
        if (s.Conditional.Count == 0) return result;

        var isFirstRow = s.FirstRow && rowIndex == 0;
        var isLastRow = s.LastRow && rowIndex + rowSpan >= ctx.RowCount;
        var isFirstCol = s.FirstColumn && gridColStart == 0;
        var isLastCol = s.LastColumn && ctx.GridColumnCount > 0 && gridColStart + gridSpan >= ctx.GridColumnCount;

        void Add(TableStyleOverrideValues t)
        {
            if (s.Conditional.TryGetValue(t, out var p)) result.Add(p);
        }

        if (isFirstRow) Add(TableStyleOverrideValues.FirstRow);
        if (isLastRow) Add(TableStyleOverrideValues.LastRow);
        if (isFirstCol) Add(TableStyleOverrideValues.FirstColumn);
        if (isLastCol) Add(TableStyleOverrideValues.LastColumn);

        if (s.ColumnBands && !isFirstCol && !isLastCol)
        {
            var colForBand = gridColStart - (s.FirstColumn ? 1 : 0);
            if (colForBand >= 0)
                Add((colForBand / Math.Max(1, s.ColBandSize)) % 2 == 0
                    ? TableStyleOverrideValues.Band1Vertical
                    : TableStyleOverrideValues.Band2Vertical);
        }
        if (s.RowBands && !isFirstRow && !isLastRow)
        {
            var rowForBand = rowIndex - (s.FirstRow ? 1 : 0);
            if (rowForBand >= 0)
                Add((rowForBand / Math.Max(1, s.RowBandSize)) % 2 == 0
                    ? TableStyleOverrideValues.Band1Horizontal
                    : TableStyleOverrideValues.Band2Horizontal);
        }
        return result;
    }

    private string? ResolveShadingHex(Shading? shd)
    {
        if (shd == null) return null;

        string? fill = null;
        if (shd.Fill?.Value is string f && !string.Equals(f, "auto", StringComparison.OrdinalIgnoreCase))
            fill = f;
        else if (shd.ThemeFill?.HasValue == true)
        {
            var themeHex = ResolveThemeColor(shd.ThemeFill.Value)?.TrimStart('#');
            if (themeHex != null)
                fill = ApplyTintShade(themeHex, shd.ThemeFillTint?.Value, shd.ThemeFillShade?.Value);
        }

        var patternPct = GetShadingPatternPercent(shd.Val?.Value);
        if (patternPct is double pct && pct > 0)
        {
            string patternColor = "000000";
            if (shd.Color?.Value is string c && !string.Equals(c, "auto", StringComparison.OrdinalIgnoreCase))
                patternColor = c;
            else if (shd.ThemeColor?.HasValue == true)
            {
                var th = ResolveThemeColor(shd.ThemeColor.Value)?.TrimStart('#');
                if (th != null) patternColor = ApplyTintShade(th, shd.ThemeTint?.Value, shd.ThemeShade?.Value);
            }
            fill = BlendHex(patternColor, fill ?? "FFFFFF", pct / 100.0);
        }

        if (fill == null) return null;
        return Regex.IsMatch(fill, "^[0-9A-Fa-f]{6}$") ? fill.ToUpperInvariant() : null;
    }

    private static double? GetShadingPatternPercent(ShadingPatternValues? val)
    {
        if (val == null) return null;
        if (val == ShadingPatternValues.Solid) return 100;
        var literal = ((DocumentFormat.OpenXml.IEnumValue)val.Value).Value;
        var m = Regex.Match(literal, @"^pct(\d+)$");
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (n == 12 || n == 37 || n == 62 || n == 87) n += 0.5;
        return n;
    }

    private static string ApplyTintShade(string hex, string? tintHex, string? shadeHex)
    {
        if (!Regex.IsMatch(hex, "^[0-9A-Fa-f]{6}$")) return hex;
        double r = System.Convert.ToInt32(hex.Substring(0, 2), 16);
        double g = System.Convert.ToInt32(hex.Substring(2, 2), 16);
        double b = System.Convert.ToInt32(hex.Substring(4, 2), 16);

        if (tintHex != null && int.TryParse(tintHex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var tint))
        {
            var t = tint / 255.0;
            r = r * t + 255 * (1 - t);
            g = g * t + 255 * (1 - t);
            b = b * t + 255 * (1 - t);
        }
        if (shadeHex != null && int.TryParse(shadeHex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var shade))
        {
            var s = shade / 255.0;
            r *= s; g *= s; b *= s;
        }
        return $"{(int)Math.Round(r):X2}{(int)Math.Round(g):X2}{(int)Math.Round(b):X2}";
    }

    private static string BlendHex(string fgHex, string bgHex, double weight)
    {
        if (!Regex.IsMatch(fgHex, "^[0-9A-Fa-f]{6}$") || !Regex.IsMatch(bgHex, "^[0-9A-Fa-f]{6}$"))
            return fgHex;
        weight = Math.Clamp(weight, 0, 1);
        int Mix(int i)
        {
            var fg = System.Convert.ToInt32(fgHex.Substring(i, 2), 16);
            var bg = System.Convert.ToInt32(bgHex.Substring(i, 2), 16);
            return (int)Math.Round(fg * weight + bg * (1 - weight));
        }
        return $"{Mix(0):X2}{Mix(2):X2}{Mix(4):X2}";
    }

    #endregion

    private static string BuildSdtDataAttrs(SdtProperties? props)
    {
        if (props == null) return string.Empty;
        var sb = new StringBuilder();
        var tag = props.Elements<Tag>().FirstOrDefault()?.Val?.Value ?? "";
        var alias = props.Elements<SdtAlias>().FirstOrDefault()?.Val?.Value ?? "";
        if (!string.IsNullOrEmpty(tag)) sb.Append($" data-sdt-tag=\"{System.Net.WebUtility.HtmlEncode(tag)}\"");
        if (!string.IsNullOrEmpty(alias)) sb.Append($" data-sdt-alias=\"{System.Net.WebUtility.HtmlEncode(alias)}\"");

        var xml = props.OuterXml;
        if (!string.IsNullOrEmpty(xml))
        {
            var b64 = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xml));
            sb.Append($" data-sdt-props=\"{b64}\"");
        }
        return sb.ToString();
    }

    private string ConvertSdtBlockToHtml(SdtBlock sdtBlock, WordprocessingDocument document, OpenXmlPart? sourcePart = null)
    {
        var html = new StringBuilder();
        var content = sdtBlock.SdtContentBlock;
        if (content != null)
        {
            var props = sdtBlock.SdtProperties;
            html.Append($"<div class=\"sdt-block\"{BuildSdtDataAttrs(props)}>");

            var elems = content.Elements().ToList();
            int i = 0;
            while (i < elems.Count)
            {
                var el = elems[i];
                if (el is Paragraph p && IsListParagraph(p))
                {
                    html.Append(ConvertConsecutiveListItems(elems, ref i, document));
                }
                else
                {
                    switch (el)
                    {
                        case Paragraph para:
                            html.Append(ConvertParagraphToHtml(para, document, sourcePart));
                            break;
                        case Table table:
                            html.Append(ConvertTableToHtml(table, document, sourcePart));
                            break;
                        case SdtBlock nested:
                            html.Append(ConvertSdtBlockToHtml(nested, document, sourcePart));
                            break;
                        default:
                            html.Append(ConvertElementToHtml(el, document));
                            break;
                    }
                    i++;
                }
            }

            html.Append("</div>");
        }
        return html.ToString();
    }

    private string ConvertSdtRunToHtml(SdtRun sdtRun, WordprocessingDocument document, OpenXmlPart? sourcePart = null)
    {
        var content = sdtRun.GetFirstChild<SdtContentRun>();
        if (content == null) return string.Empty;

        var props = sdtRun.SdtProperties;
        var dataAttrs = BuildSdtDataAttrs(props);

        var inner = new StringBuilder();
        foreach (var el in content.Elements())
        {
            switch (el)
            {
                case Run run:
                    inner.Append(ConvertRunToHtml(run, document, sourcePart));
                    break;
                case Hyperlink hl:
                    inner.Append(ConvertHyperlinkToHtml(hl, document, sourcePart));
                    break;
                case SimpleField sf:
                    inner.Append(ConvertSimpleFieldToHtml(sf));
                    break;
                case SdtRun nested:
                    inner.Append(ConvertSdtRunToHtml(nested, document, sourcePart));
                    break;
            }
        }

        if (inner.Length == 0) inner.Append("&nbsp;");

        return $"<span class=\"sdt-inline\"{dataAttrs}>{inner}</span>";
    }

    private void AppendTableCellHtml(
        StringBuilder html,
        Table table,
        List<TableRow> rows,
        int rowIndex,
        TableCell cell,
        TableRenderContext ctx,
        ref int gridCursor,
        int extraColspan,
        WordprocessingDocument document,
        OpenXmlPart? sourcePart)
    {
        var cellProps = EffectiveCellProps(cell);
        var gridSpan = GetGridSpan(cell) + Math.Max(0, extraColspan);
        var gridColStart = gridCursor;
        gridCursor += gridSpan;

        var colspan = "";
        if (gridSpan > 1)
            colspan = $" colspan=\"{gridSpan}\"";

        var rowspan = "";
        var rowSpanCount = 1;
        var row = rows[rowIndex];
        var vMerge = cellProps?.VerticalMerge;
        if (vMerge != null && vMerge.Val?.Value == MergedCellValues.Restart)
        {
            rowSpanCount = CountRowSpan(table, row, cell);
            if (rowSpanCount > 1) rowspan = $" rowspan=\"{rowSpanCount}\"";
        }
        else if (vMerge != null && (vMerge.Val == null || vMerge.Val.Value == MergedCellValues.Continue))
        {
            return;
        }

        var cellStyle = GetTableCellStyleDetailed(cell, ctx, rowIndex, gridColStart, gridSpan, rowSpanCount);

        var borderlessClass = HasNoVisibleBorders(cellStyle) ? " class=\"docx-borderless-cell\"" : "";

        html.Append($"<td{colspan}{rowspan}{borderlessClass} style=\"{cellStyle}\">");

        var innerElements = cell.Elements().Cast<OpenXmlElement>().ToList();
        var innerIndex = 0;
        while (innerIndex < innerElements.Count)
        {
            var inner = innerElements[innerIndex];
            if (inner is Paragraph listPara && IsListParagraph(listPara))
            {
                html.Append(ConvertConsecutiveListItems(innerElements, ref innerIndex, document));
                continue;
            }

            switch (inner)
            {
                case Paragraph para:
                    html.Append(ConvertParagraphToHtml(para, document, sourcePart));
                    break;
                case Table nestedTable:
                    html.Append(ConvertTableToHtml(nestedTable, document, sourcePart));
                    break;
                case SdtBlock sdt:
                    html.Append(ConvertSdtBlockToHtml(sdt, document));
                    break;
            }
            innerIndex++;
        }

        html.Append("</td>");
    }

    private int TwipsToPx(int twips) => (int)OoxmlUnits.TwipsToPixels(twips);
    private int EmuToPx(long emu) => (int)OoxmlUnits.EmuToPixels(emu);
    private string EscapeHtml(string text) => System.Net.WebUtility.HtmlEncode(text);

    private static string GetJustificationAlignment(JustificationValues value)
    {
        if (value == JustificationValues.Left) return "left";
        if (value == JustificationValues.Center) return "center";
        if (value == JustificationValues.Right) return "right";
        if (value == JustificationValues.Both) return "justify";
        return "left";
    }

    private static string GetTableVerticalAlignment(TableVerticalAlignmentValues value)
    {
        if (value == TableVerticalAlignmentValues.Top) return "top";
        if (value == TableVerticalAlignmentValues.Center) return "middle";
        if (value == TableVerticalAlignmentValues.Bottom) return "bottom";
        return "top";
    }
}
