using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.ExtendedProperties;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Conversion;
using HtmlAgilityPack;
using OoxmlPageSize = DocumentFormat.OpenXml.Wordprocessing.PageSize;
using Microsoft.Extensions.Options;
using A = DocumentFormat.OpenXml.Drawing;
using V = DocumentFormat.OpenXml.Vml;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Wps = DocumentFormat.OpenXml.Office2010.Word.DrawingShape;
using DomainFootnote = D2ViewerEditor.Domain.Models.Footnote;
using WpFootnote = DocumentFormat.OpenXml.Wordprocessing.Footnote;
using DomainEndnote = D2ViewerEditor.Domain.Models.Endnote;
using WpEndnote = DocumentFormat.OpenXml.Wordprocessing.Endnote;

namespace D2ViewerEditor.Infrastructure.Services;

public class HtmlToDocxConverter : IHtmlToDocxConverter
{
    private MainDocumentPart? _mainPart;
    private readonly Dictionary<string, string> _imageRelationships = new();
    private int _imageCounter = 0;
    private int _numberingId = 1;
    private NumberingDefinitionsPart? _numberingPart;
    private readonly Dictionary<int, int> _abstractNumIds = new();
    private readonly Dictionary<string, int> _numIdByHtmlList = new();
    private readonly Dictionary<string, int> _abstractIdByHtmlAbstract = new();
    private readonly Dictionary<int, HashSet<int>> _picBulletLevelsByAbstract = new();
    private readonly Dictionary<int, HashSet<int>> _picBulletLevelsByNum = new();
    private readonly Dictionary<int, HashSet<int>> _specLevelsByAbstract = new();
    private readonly Dictionary<string, int> _picBulletIdByDataUri = new();
    private int _picBulletId = 1;

    private readonly Dictionary<string, long> _footnoteOoxmlIdByHtmlId = new();
    private readonly HashSet<string> _referencedFootnoteHtmlIds = new();

    private readonly Dictionary<string, long> _endnoteOoxmlIdByHtmlId = new();
    private readonly HashSet<string> _referencedEndnoteHtmlIds = new();

    private readonly DocumentDefaultsOptions _defaults;

    public HtmlToDocxConverter()
    {
        _defaults = new DocumentDefaultsOptions();
    }

    public HtmlToDocxConverter(IOptions<DocumentDefaultsOptions> defaults)
    {
        _defaults = defaults?.Value ?? new DocumentDefaultsOptions();
    }


    private OpenXmlPart? _currentImageContainer;

    private bool _inHeaderFooter = false;

    private string? _currentSectionStyleId = null;

    private sealed class SectionGeometry
    {
        public Domain.Models.PageSize? PageSize { get; set; }
        public PageMargins? Margins { get; set; }
        public double? HeaderDistanceCm { get; set; }
        public double? FooterDistanceCm { get; set; }
        public string? BreakType { get; set; }
        public ColumnLayout? Columns { get; set; }
        public DocGridSettings? DocGrid { get; set; }
    }

    private sealed record DocGridSettings(string Type, int? LinePitchTwips, int? CharSpace);

    private SectionGeometry _currentSection = new();

    private SectionProperties? _firstSectionProps;

    private readonly List<SectionProperties> _emittedSectionProps = new();

    private bool _hasSectionMarkers;

    private double? _headerBandCm;
    private double? _footerBandCm;

    private string? _docDefaultFontFamily;
    private double? _docDefaultFontSizePt;
    private string? _docDefaultSpacingBeforeTw;
    private string? _docDefaultSpacingAfterTw;
    private string? _docDefaultSpacingLine;
    private string? _docDefaultSpacingLineRule;
    private bool _paragraphSpacingSum;
    private DocGridSettings? _docDefaultDocGrid;
    private ColumnLayout? _docDefaultColumns;
    private int _openFieldMarkerCount;
    private int _nextBookmarkId = 1;

    public byte[] Convert(string html, DocumentMetadata? metadata = null, HeaderFooterContent? header = null, HeaderFooterContent? footer = null, PageMargins? margins = null, Domain.Models.PageSize? pageSize = null, IReadOnlyList<SectionHeaderFooter>? sectionHeadersFooters = null, IReadOnlyList<DomainFootnote>? footnotes = null, IReadOnlyList<DomainEndnote>? endnotes = null, string? footnoteNumberFormat = null, string? endnoteNumberFormat = null)
    {
        using var memoryStream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(memoryStream, WordprocessingDocumentType.Document))
        {
            _mainPart = document.AddMainDocumentPart();
            _mainPart.Document = new Document();

            _currentSection = new SectionGeometry { PageSize = pageSize, Margins = margins };
            _firstSectionProps = null;
            _emittedSectionProps.Clear();
            _numIdByHtmlList.Clear();
            _abstractIdByHtmlAbstract.Clear();
            _picBulletLevelsByAbstract.Clear();
            _picBulletLevelsByNum.Clear();
            _specLevelsByAbstract.Clear();
            _picBulletIdByDataUri.Clear();
            _picBulletId = 1;
            _numberingId = 1;
            _numberingPart = null;
            _pendingTextBoxDrawings.Clear();
            _footnoteOoxmlIdByHtmlId.Clear();
            _referencedFootnoteHtmlIds.Clear();
            AssignFootnoteOoxmlIds(footnotes);
            _endnoteOoxmlIdByHtmlId.Clear();
            _referencedEndnoteHtmlIds.Clear();
            AssignEndnoteOoxmlIds(endnotes);
            _hasSectionMarkers = false;
            _headerBandCm = header?.Height;
            _footerBandCm = footer?.Height;
            _docDefaultFontFamily = null;
            _docDefaultFontSizePt = null;
            _docDefaultSpacingBeforeTw = _docDefaultSpacingAfterTw = null;
            _docDefaultSpacingLine = _docDefaultSpacingLineRule = null;
            _docDefaultColumns = null;
            _paragraphSpacingSum = false;
            _docDefaultDocGrid = null;
            _openFieldMarkerCount = 0;
            _nextBookmarkId = 1;

            var body = new Body();
            _mainPart.Document.Body = body;

            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);
            CaptureDocumentDefaults(htmlDoc);
            _currentSection.Columns = _docDefaultColumns;
            _currentSection.DocGrid = _docDefaultDocGrid;

            AddDocumentStyles(document);

            ConvertHtmlToBody(htmlDoc.DocumentNode, body);

            if (_openFieldMarkerCount > 0)
            {
                var lastPara = body.Elements<Paragraph>().LastOrDefault();
                if (lastPara == null)
                {
                    lastPara = new Paragraph();
                    body.Append(lastPara);
                }
                while (_openFieldMarkerCount > 0)
                {
                    lastPara.Append(new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
                    _openFieldMarkerCount--;
                }
            }

            if (metadata != null)
            {
                SetDocumentMetadata(document, metadata);
            }

            AddHeaderAndFooter(document, header, footer);

            AddPageSettings(body, header, footer, margins, pageSize);

            AddSectionHeadersFooters(document, sectionHeadersFooters);

            AddFootnotes(footnotes);

            AddEndnotes(endnotes);

            ApplyNoteNumberFormats(document, footnoteNumberFormat, endnoteNumberFormat);

            ApplyParagraphSpacingCompat(document);

            document.Save();
        }

        return memoryStream.ToArray();
    }

    private void ApplyParagraphSpacingCompat(WordprocessingDocument document)
    {
        if (!_paragraphSpacingSum) return;
        var mainPart = document.MainDocumentPart;
        if (mainPart == null) return;
        var settingsPart = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings ??= new Settings();
        var settings = settingsPart.Settings;
        var compat = settings.GetFirstChild<Compatibility>();
        if (compat == null)
        {
            compat = new Compatibility();
            settings.AppendChild(compat);
        }
        if (!compat.Elements<DoNotUseHTMLParagraphAutoSpacing>().Any())
            compat.PrependChild(new DoNotUseHTMLParagraphAutoSpacing());
        settings.Save();
    }

    private static void AppendBeforeCompat(Settings settings, OpenXmlElement element)
    {
        if (settings.GetFirstChild<Compatibility>() is { } compat)
            settings.InsertBefore(element, compat);
        else
            settings.AppendChild(element);
    }

    public byte[] ConvertPreservingPackage(string html, Stream? originalPackage,
        DocumentMetadata? metadata = null, HeaderFooterContent? header = null,
        HeaderFooterContent? footer = null, PageMargins? margins = null, Domain.Models.PageSize? pageSize = null,
        IReadOnlyList<SectionHeaderFooter>? sectionHeadersFooters = null,
        IReadOnlyList<DomainFootnote>? footnotes = null,
        IReadOnlyList<DomainEndnote>? endnotes = null,
        string? footnoteNumberFormat = null,
        string? endnoteNumberFormat = null)
    {
        var generated = Convert(html, metadata, header, footer, margins, pageSize, sectionHeadersFooters,
            footnotes, endnotes, footnoteNumberFormat, endnoteNumberFormat);

        if (originalPackage == null || !originalPackage.CanRead)
            return generated;

        try
        {
            return PreserveOriginalParts(generated, originalPackage);
        }
        catch
        {
            return generated;
        }
    }

    private static byte[] PreserveOriginalParts(byte[] generated, Stream originalPackage)
    {
        var ms = BinaryBuffers.ToExpandableStream(generated);

        if (originalPackage.CanSeek) originalPackage.Position = 0;

        using (var original = WordprocessingDocument.Open(originalPackage, false))
        using (var target = WordprocessingDocument.Open(ms, true))
        {
            var origMain = original.MainDocumentPart;
            var targetMain = target.MainDocumentPart;
            if (origMain == null || targetMain == null)
                return generated;

            if (origMain.StyleDefinitionsPart != null)
            {
                var styles = targetMain.StyleDefinitionsPart ?? targetMain.AddNewPart<StyleDefinitionsPart>();
                using var s = origMain.StyleDefinitionsPart.GetStream(FileMode.Open, FileAccess.Read);
                styles.FeedData(s);
            }

            if (origMain.ThemePart != null)
            {
                var theme = targetMain.ThemePart ?? targetMain.AddNewPart<ThemePart>();
                using var s = origMain.ThemePart.GetStream(FileMode.Open, FileAccess.Read);
                theme.FeedData(s);
            }

            if (origMain.FontTablePart != null)
            {
                var fonts = targetMain.FontTablePart ?? targetMain.AddNewPart<FontTablePart>();
                using var s = origMain.FontTablePart.GetStream(FileMode.Open, FileAccess.Read);
                fonts.FeedData(s);
            }

            PreserveNoteProperties(origMain, targetMain);
            PreserveEditProtection(origMain, targetMain, target);

            target.Save();
        }

        return ms.ToArray();
    }

    private static NumberFormatValues? MapNoteNumberFormat(string? token) => token switch
    {
        "decimal" => NumberFormatValues.Decimal,
        "lowerRoman" => NumberFormatValues.LowerRoman,
        "upperRoman" => NumberFormatValues.UpperRoman,
        "lowerLetter" => NumberFormatValues.LowerLetter,
        "upperLetter" => NumberFormatValues.UpperLetter,
        _ => null
    };

    private static void ApplyNoteNumberFormats(WordprocessingDocument document,
        string? footnoteNumberFormat, string? endnoteNumberFormat)
    {
        var footnoteFmt = MapNoteNumberFormat(footnoteNumberFormat);
        var endnoteFmt = MapNoteNumberFormat(endnoteNumberFormat);
        if (footnoteFmt == null && endnoteFmt == null) return;

        var mainPart = document.MainDocumentPart;
        if (mainPart == null) return;
        var settingsPart = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings ??= new Settings();
        var settings = settingsPart.Settings;

        settings.RemoveAllChildren<FootnoteDocumentWideProperties>();
        settings.RemoveAllChildren<EndnoteDocumentWideProperties>();
        if (footnoteFmt != null)
            settings.AppendChild(new FootnoteDocumentWideProperties(new NumberingFormat { Val = footnoteFmt }));
        if (endnoteFmt != null)
            settings.AppendChild(new EndnoteDocumentWideProperties(new NumberingFormat { Val = endnoteFmt }));
        settings.Save();
    }

    private static void PreserveEditProtection(MainDocumentPart origMain, MainDocumentPart targetMain, WordprocessingDocument target)
    {
        var origSettings = origMain.DocumentSettingsPart?.Settings;
        var protection = origSettings?.GetFirstChild<DocumentProtection>();
        var enforced = protection != null && protection.Enforcement?.Value == true
            && protection.Edit != null && protection.Edit.Value != DocumentProtectionValues.None;
        var writeProtection = origSettings?.GetFirstChild<WriteProtection>();
        var writeProtected = writeProtection != null
            && (writeProtection.Recommended?.Value == true
                || !string.IsNullOrEmpty(writeProtection.Hash?.Value)
                || !string.IsNullOrEmpty(writeProtection.HashValue?.Value));

        if (enforced || writeProtected)
        {
            var settingsPart = targetMain.DocumentSettingsPart ?? targetMain.AddNewPart<DocumentSettingsPart>();
            settingsPart.Settings ??= new Settings();
            var settings = settingsPart.Settings;
            if (writeProtected && settings.GetFirstChild<WriteProtection>() == null)
                settings.PrependChild((WriteProtection)writeProtection!.CloneNode(true));
            if (enforced && settings.GetFirstChild<DocumentProtection>() == null)
            {
                var clone = (DocumentProtection)protection!.CloneNode(true);
                OpenXmlElement? before = settings.GetFirstChild<DefaultTabStop>()
                    ?? (OpenXmlElement?)settings.GetFirstChild<CharacterSpacingControl>()
                    ?? settings.GetFirstChild<Compatibility>();
                if (before != null) settings.InsertBefore(clone, before);
                else settings.AppendChild(clone);
            }
            settings.Save();
        }

        var origCustom = (origMain.OpenXmlPackage as WordprocessingDocument)?.CustomFilePropertiesPart;
        var markedFinal = origCustom?.Properties?.Elements<DocumentFormat.OpenXml.CustomProperties.CustomDocumentProperty>()
            .Any(p => p.Name?.Value == "_MarkAsFinal"
                      && (string.Equals(p.VTBool?.Text, "true", StringComparison.OrdinalIgnoreCase) || p.VTBool?.Text == "1")) == true;
        if (markedFinal && target.CustomFilePropertiesPart == null)
        {
            var part = target.AddCustomFilePropertiesPart();
            using var src = origCustom!.GetStream(FileMode.Open, FileAccess.Read);
            part.FeedData(src);
        }
    }

    private static void PreserveNoteProperties(MainDocumentPart origMain, MainDocumentPart targetMain)
    {
        var origSettings = origMain.DocumentSettingsPart?.Settings;
        var origFootnotePr = origSettings?.GetFirstChild<FootnoteDocumentWideProperties>();
        var origEndnotePr = origSettings?.GetFirstChild<EndnoteDocumentWideProperties>();

        var origFirstSect = origMain.Document?.Body?.Descendants<SectionProperties>().FirstOrDefault();
        var sectFootnotePr = origFirstSect?.GetFirstChild<FootnoteProperties>();
        var sectEndnotePr = origFirstSect?.GetFirstChild<EndnoteProperties>();

        var footnotePr = sectFootnotePr != null
            ? new FootnoteDocumentWideProperties(sectFootnotePr.ChildElements.Select(c => c.CloneNode(true)))
            : (FootnoteDocumentWideProperties?)origFootnotePr?.CloneNode(true);
        var endnotePr = sectEndnotePr != null
            ? new EndnoteDocumentWideProperties(sectEndnotePr.ChildElements.Select(c => c.CloneNode(true)))
            : (EndnoteDocumentWideProperties?)origEndnotePr?.CloneNode(true);

        footnotePr?.RemoveAllChildren<FootnoteSpecialReference>();
        endnotePr?.RemoveAllChildren<EndnoteSpecialReference>();
        if (footnotePr is { HasChildren: false }) footnotePr = null;
        if (endnotePr is { HasChildren: false }) endnotePr = null;
        if (footnotePr == null && endnotePr == null) return;

        var settingsPart = targetMain.DocumentSettingsPart ?? targetMain.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings ??= new Settings();
        var settings = settingsPart.Settings;

        if (footnotePr != null)
        {
            if (settings.GetFirstChild<FootnoteDocumentWideProperties>() is { } generatedFootnotePr)
                MergeMissingNoteProperties(generatedFootnotePr, footnotePr);
            else if (settings.GetFirstChild<EndnoteDocumentWideProperties>() is { } existingEndnotePr)
                settings.InsertBefore(footnotePr, existingEndnotePr);
            else
                AppendBeforeCompat(settings, footnotePr);
        }
        if (endnotePr != null)
        {
            if (settings.GetFirstChild<EndnoteDocumentWideProperties>() is { } generatedEndnotePr)
                MergeMissingNoteProperties(generatedEndnotePr, endnotePr);
            else
                AppendBeforeCompat(settings, endnotePr);
        }
        settings.Save();
    }

    private static void MergeMissingNoteProperties(OpenXmlCompositeElement generated, OpenXmlCompositeElement original)
    {
        if (generated.GetFirstChild<FootnotePosition>() == null
            && generated.GetFirstChild<EndnotePosition>() == null)
        {
            var pos = original.GetFirstChild<FootnotePosition>()
                ?? (OpenXmlElement?)original.GetFirstChild<EndnotePosition>();
            if (pos != null) generated.InsertAt(pos.CloneNode(true), 0);
        }
        if (generated.GetFirstChild<NumberingStart>() == null
            && original.GetFirstChild<NumberingStart>() is { } numStart)
        {
            if (generated.GetFirstChild<NumberingRestart>() is { } existingRestart)
                generated.InsertBefore(numStart.CloneNode(true), existingRestart);
            else
                generated.AppendChild(numStart.CloneNode(true));
        }
        if (generated.GetFirstChild<NumberingRestart>() == null
            && original.GetFirstChild<NumberingRestart>() is { } numRestart)
        {
            generated.AppendChild(numRestart.CloneNode(true));
        }
    }

    private void AddHeaderAndFooter(WordprocessingDocument document, HeaderFooterContent? header, HeaderFooterContent? footer)
    {
        if (_mainPart == null) return;

        if (header != null)
        {
            if (!string.IsNullOrWhiteSpace(header.Html))
                WriteHeaderPart(header.Html, HeaderFooterValues.Default);

            if (header.DifferentFirstPage)
            {
                if (header.FirstPageHtml != null)
                    WriteHeaderPart(header.FirstPageHtml, HeaderFooterValues.First);
                EnsureTitlePage();
            }

            if (header.DifferentOddEven && header.EvenHtml != null)
            {
                WriteHeaderPart(header.EvenHtml, HeaderFooterValues.Even);
                EnsureEvenAndOddHeaders(document);
            }
        }

        if (footer != null)
        {
            if (!string.IsNullOrWhiteSpace(footer.Html))
                WriteFooterPart(footer.Html, HeaderFooterValues.Default);

            if (footer.DifferentFirstPage)
            {
                if (footer.FirstPageHtml != null)
                    WriteFooterPart(footer.FirstPageHtml, HeaderFooterValues.First);
                EnsureTitlePage();
            }

            if (footer.DifferentOddEven && footer.EvenHtml != null)
            {
                WriteFooterPart(footer.EvenHtml, HeaderFooterValues.Even);
                EnsureEvenAndOddHeaders(document);
            }
        }
    }

    private void WriteHeaderPart(string html, HeaderFooterValues type, SectionProperties? targetSection = null)
    {
        var headerPart = _mainPart!.AddNewPart<HeaderPart>();
        var headerElement = new Header();

        var prepared = html
            .Replace("{page}", "<span class=\"field-page\"></span>")
            .Replace("{pages}", "<span class=\"field-numpages\"></span>");
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(prepared);

        var prevContainer = _currentImageContainer;
        var prevInHF = _inHeaderFooter;
        var prevSection = _currentSectionStyleId;
        _currentImageContainer = headerPart;
        _inHeaderFooter = true;
        _currentSectionStyleId = "Header";
        try
        {
            ConvertHtmlToHeaderFooter(htmlDoc.DocumentNode, headerElement);
        }
        finally
        {
            _currentImageContainer = prevContainer;
            _inHeaderFooter = prevInHF;
            _currentSectionStyleId = prevSection;
        }

        if (!headerElement.HasChildren)
            headerElement.Append(new Paragraph());

        headerPart.Header = headerElement;
        headerPart.Header.Save();

        AddHeaderReference(_mainPart.GetIdOfPart(headerPart), type, targetSection);
    }

    private void WriteFooterPart(string html, HeaderFooterValues type, SectionProperties? targetSection = null)
    {
        var footerPart = _mainPart!.AddNewPart<FooterPart>();
        var footerElement = new Footer();

        var prepared = html
            .Replace("{page}", "<span class=\"field-page\"></span>")
            .Replace("{pages}", "<span class=\"field-numpages\"></span>");
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(prepared);

        var prevContainer = _currentImageContainer;
        var prevInHF = _inHeaderFooter;
        var prevSection = _currentSectionStyleId;
        _currentImageContainer = footerPart;
        _inHeaderFooter = true;
        _currentSectionStyleId = "Footer";
        try
        {
            ConvertHtmlToHeaderFooter(htmlDoc.DocumentNode, footerElement);
        }
        finally
        {
            _currentImageContainer = prevContainer;
            _inHeaderFooter = prevInHF;
            _currentSectionStyleId = prevSection;
        }

        if (!footerElement.HasChildren)
            footerElement.Append(new Paragraph());

        footerPart.Footer = footerElement;
        footerPart.Footer.Save();

        AddFooterReference(_mainPart.GetIdOfPart(footerPart), type, targetSection);
    }

    private void EnsureTitlePage(SectionProperties? targetSection = null)
    {
        var sectionProps = targetSection ?? GetReferenceSectionProps();
        if (sectionProps == null) return;
        if (sectionProps.Elements<TitlePage>().Any()) return;

        var tail = sectionProps.ChildElements.FirstOrDefault(c =>
            c is TextDirection or BiDi or GutterOnRight or DocGrid or PrinterSettingsReference);
        var titlePg = new TitlePage();
        if (tail != null) sectionProps.InsertBefore(titlePg, tail);
        else sectionProps.Append(titlePg);
    }

    private static void EnsureEvenAndOddHeaders(WordprocessingDocument document)
    {
        var mainPart = document.MainDocumentPart;
        if (mainPart == null) return;
        var settingsPart = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings ??= new Settings();
        if (!settingsPart.Settings.Elements<EvenAndOddHeaders>().Any())
        {
            settingsPart.Settings.AppendChild(new EvenAndOddHeaders());
        }
        settingsPart.Settings.Save();
    }

    private SectionProperties? GetOrCreateSectionProps()
    {
        var body = _mainPart?.Document?.Body;
        if (body == null) return null;
        var sectionProps = body.Elements<SectionProperties>().FirstOrDefault();
        if (sectionProps == null)
        {
            sectionProps = new SectionProperties();
            body.Append(sectionProps);
        }
        return sectionProps;
    }

    private SectionProperties? GetReferenceSectionProps() => _firstSectionProps ?? GetOrCreateSectionProps();

    private void ConvertHtmlToHeaderFooter(HtmlNode node, OpenXmlCompositeElement parent)
    {
        Paragraph? pendingTextParagraph = null;

        void FlushPending()
        {
            if (pendingTextParagraph != null)
            {
                if (!pendingTextParagraph.Elements<Run>().Any()
                    && !pendingTextParagraph.Elements<Hyperlink>().Any()
                    && !pendingTextParagraph.Elements<SimpleField>().Any())
                {
                }
                else
                {
                    parent.Append(pendingTextParagraph);
                }
                pendingTextParagraph = null;
            }
        }

        foreach (var child in node.ChildNodes)
        {
            var name = child.Name.ToLower();
            switch (name)
            {
                case "#text":
                {
                    var text = child.InnerText;
                    if (!string.IsNullOrEmpty(text) && !string.IsNullOrWhiteSpace(text))
                    {
                        pendingTextParagraph ??= new Paragraph();
                        pendingTextParagraph.Append(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
                    }
                    break;
                }
                case "span":
                case "strong":
                case "b":
                case "em":
                case "i":
                case "u":
                case "s":
                case "strike":
                case "sub":
                case "sup":
                case "a":
                {
                    pendingTextParagraph ??= new Paragraph();
                    if (child.HasClass("field-page") || child.HasClass("page-number"))
                    {
                        pendingTextParagraph.Append(BuildFieldRun(" PAGE ", child));
                    }
                    else if (child.HasClass("field-numpages"))
                    {
                        pendingTextParagraph.Append(BuildFieldRun(" NUMPAGES ", child));
                    }
                    else if (child.HasClass("field-date"))
                    {
                        pendingTextParagraph.Append(BuildDateFieldRun(child));
                    }
                    else if (name == "a")
                    {
                        pendingTextParagraph.Append(ConvertAnchorElement(child));
                    }
                    else
                    {
                        var parentStyle = child.GetAttributeValue("style", "");
                        RunProperties? base_ = null;
                        if (!string.IsNullOrEmpty(parentStyle))
                        {
                            base_ = new RunProperties();
                            ApplyRunStyle(base_, parentStyle);
                            if (!base_.HasChildren) base_ = null;
                        }
                        foreach (var run in CreateRunsFromNode(child, base_))
                        {
                            pendingTextParagraph.Append(run);
                        }
                    }
                    break;
                }
                case "br":
                {
                    pendingTextParagraph ??= new Paragraph();
                    pendingTextParagraph.Append(new Run(new Break()));
                    break;
                }
                case "img":
                {
                    pendingTextParagraph ??= new Paragraph();
                    if (child.GetAttributeValue("data-docx-xml", "") != ""
                        && TryRestorePreservedElement(child) is { } preservedHfImg)
                    {
                        pendingTextParagraph.Append(new Run(preservedHfImg));
                        break;
                    }
                    var imgRun = CreateImageRun(child);
                    if (imgRun != null) pendingTextParagraph.Append(imgRun);
                    break;
                }
                case "p":
                {
                    FlushPending();
                    parent.Append(ConvertParagraphElement(child));
                    break;
                }
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                {
                    FlushPending();
                    var level = int.Parse(name[1].ToString());
                    parent.Append(ConvertHeadingElement(child, level));
                    break;
                }
                case "ul":
                case "ol":
                {
                    FlushPending();
                    foreach (var listElement in ConvertListElement(child, name == "ol"))
                        parent.Append(listElement);
                    break;
                }
                case "table":
                {
                    FlushPending();
                    parent.Append(ConvertTableElement(child));
                    break;
                }
                case "blockquote":
                {
                    FlushPending();
                    parent.Append(ConvertBlockquoteElement(child));
                    break;
                }
                case "hr":
                {
                    FlushPending();
                    parent.Append(CreateHorizontalRule());
                    break;
                }
                case "div":
                case "section":
                case "article":
                case "header":
                case "footer":
                {
                    if (child.GetAttributeValue("data-docx-xml", "") != ""
                        && TryRestorePreservedElement(child) is { } preservedHfBlock)
                    {
                        FlushPending();
                        parent.Append(new Paragraph(new Run(preservedHfBlock)));
                        break;
                    }
                    if (IsTextBoxNode(child))
                    {
                        FlushPending();
                        BufferTextBoxDrawing(child);
                        break;
                    }
                    FlushPending();
                    ConvertHtmlToHeaderFooter(child, parent);
                    break;
                }
                default:
                {
                    pendingTextParagraph ??= new Paragraph();
                    foreach (var run in CreateRunsFromNode(child, null))
                    {
                        pendingTextParagraph.Append(run);
                    }
                    break;
                }
            }
        }

        FlushPending();

        FlushPendingTextBoxesInto(parent);

        if (!parent.Elements<Paragraph>().Any() && !parent.Elements<Table>().Any())
        {
            parent.Append(new Paragraph());
        }

        if (_currentSectionStyleId != null)
        {
            foreach (var p in parent.Elements<Paragraph>())
            {
                if (p.ParagraphProperties?.GetFirstChild<Tabs>() != null) continue;
                ApplyDefaultSectionStyle(p, _currentSectionStyleId);
            }
        }
    }

    private static void ApplyDefaultSectionStyle(Paragraph paragraph, string styleId)
    {
        var props = paragraph.ParagraphProperties;
        if (props == null)
        {
            props = new ParagraphProperties();
            paragraph.InsertAt(props, 0);
        }
        if (props.ParagraphStyleId == null)
        {
            props.InsertAt(new ParagraphStyleId { Val = styleId }, 0);
        }
    }

    private void AddHeaderReference(string headerPartId, HeaderFooterValues type, SectionProperties? targetSection = null)
    {
        var sectionProps = targetSection ?? GetReferenceSectionProps();
        if (sectionProps == null) return;
        sectionProps.InsertAt(new HeaderReference { Type = type, Id = headerPartId }, 0);
    }

    private void AddFooterReference(string footerPartId, HeaderFooterValues type, SectionProperties? targetSection = null)
    {
        var sectionProps = targetSection ?? GetReferenceSectionProps();
        if (sectionProps == null) return;
        sectionProps.InsertAt(new FooterReference { Type = type, Id = footerPartId }, 0);
    }

    private void AddSectionHeadersFooters(WordprocessingDocument document, IReadOnlyList<SectionHeaderFooter>? sections)
    {
        if (sections == null || sections.Count == 0) return;
        var body = _mainPart?.Document?.Body;
        if (body == null) return;
        var bodySectPr = body.Elements<SectionProperties>().FirstOrDefault();

        foreach (var entry in sections)
        {
            if (entry.SectionIndex < 1) continue;

            SectionProperties? target = null;
            if (entry.SectionIndex < _emittedSectionProps.Count)
                target = _emittedSectionProps[entry.SectionIndex];
            else if (entry.SectionIndex == _emittedSectionProps.Count)
                target = bodySectPr;
            if (target == null) continue;

            if (entry.Header is { } h)
            {
                if (!string.IsNullOrWhiteSpace(h.Html))
                    WriteHeaderPart(h.Html, HeaderFooterValues.Default, target);
                if (h.DifferentFirstPage)
                {
                    if (h.FirstPageHtml != null)
                        WriteHeaderPart(h.FirstPageHtml, HeaderFooterValues.First, target);
                    EnsureTitlePage(target);
                }
                if (h.DifferentOddEven && h.EvenHtml != null)
                {
                    WriteHeaderPart(h.EvenHtml, HeaderFooterValues.Even, target);
                    EnsureEvenAndOddHeaders(document);
                }
            }

            if (entry.Footer is { } f)
            {
                if (!string.IsNullOrWhiteSpace(f.Html))
                    WriteFooterPart(f.Html, HeaderFooterValues.Default, target);
                if (f.DifferentFirstPage)
                {
                    if (f.FirstPageHtml != null)
                        WriteFooterPart(f.FirstPageHtml, HeaderFooterValues.First, target);
                    EnsureTitlePage(target);
                }
                if (f.DifferentOddEven && f.EvenHtml != null)
                {
                    WriteFooterPart(f.EvenHtml, HeaderFooterValues.Even, target);
                    EnsureEvenAndOddHeaders(document);
                }
            }
        }
    }

    private void CaptureDocumentDefaults(HtmlDocument htmlDoc)
    {
        var container = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class,'document-content')]");
        if (container == null) return;

        var style = container.GetAttributeValue("style", "");
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        var fontMatch = Regex.Match(style, @"font-family:\s*'?([^;',]+)");
        if (fontMatch.Success && !string.IsNullOrWhiteSpace(fontMatch.Groups[1].Value))
            _docDefaultFontFamily = fontMatch.Groups[1].Value.Trim();

        var sizeMatch = Regex.Match(style, @"font-size:\s*([\d.]+)pt");
        if (sizeMatch.Success && double.TryParse(sizeMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var pt) && pt > 0)
            _docDefaultFontSizePt = pt;

        string? Attr(string name)
        {
            var v = container.GetAttributeValue(name, "");
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        if (Attr("data-default-before-tw") is { } beforeTw && int.TryParse(beforeTw, out _))
            _docDefaultSpacingBeforeTw = beforeTw;
        if (Attr("data-default-after-tw") is { } afterTw && int.TryParse(afterTw, out _))
            _docDefaultSpacingAfterTw = afterTw;
        if (Attr("data-default-line") is { } line && int.TryParse(line, out _))
        {
            _docDefaultSpacingLine = line;
            _docDefaultSpacingLineRule = Attr("data-default-line-rule");
        }

        _docDefaultColumns = ParseColumnDataAttributes(container);
        _paragraphSpacingSum = container.GetAttributeValue("data-para-spacing-sum", "") == "1";
        _docDefaultDocGrid = ParseDocGridDataAttributes(container);
    }

    private static DocGridSettings? ParseDocGridDataAttributes(HtmlNode node)
    {
        var type = node.GetAttributeValue("data-doc-grid-type", "");
        var pitchRaw = node.GetAttributeValue("data-doc-grid-pitch-tw", "");
        var charsRaw = node.GetAttributeValue("data-doc-grid-chars", "");
        if (string.IsNullOrEmpty(type) && string.IsNullOrEmpty(pitchRaw) && string.IsNullOrEmpty(charsRaw))
            return null;
        return new DocGridSettings(
            string.IsNullOrEmpty(type) ? "default" : type,
            int.TryParse(pitchRaw, out var pitch) ? pitch : null,
            int.TryParse(charsRaw, out var chars) ? chars : null);
    }

    private static void AppendDocGrid(SectionProperties sectionProps, DocGridSettings? grid)
    {
        if (grid == null || sectionProps.Elements<DocGrid>().Any()) return;
        var docGrid = new DocGrid();
        docGrid.Type = grid.Type switch
        {
            "lines" => DocGridValues.Lines,
            "linesAndChars" => DocGridValues.LinesAndChars,
            "snapToChars" => DocGridValues.SnapToChars,
            _ => null
        };
        if (grid.LinePitchTwips is { } pitch) docGrid.LinePitch = pitch;
        if (grid.CharSpace is { } chars) docGrid.CharacterSpace = chars;
        if (sectionProps.GetFirstChild<PrinterSettingsReference>() is { } printer)
            sectionProps.InsertBefore(docGrid, printer);
        else
            sectionProps.AppendChild(docGrid);
    }

    private static ColumnLayout? ParseColumnDataAttributes(HtmlNode node)
    {
        var countRaw = node.GetAttributeValue("data-col-count", "");
        if (!int.TryParse(countRaw, out var count) || count <= 1) return null;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int SpaceTw() => int.TryParse(node.GetAttributeValue("data-col-space-tw", ""), out var s) ? s : 720;
        var equal = node.GetAttributeValue("data-col-equal", "1") != "0";

        var layout = new ColumnLayout
        {
            Count = count,
            EqualWidth = equal,
            SpaceTwips = SpaceTw(),
            Separator = node.GetAttributeValue("data-col-sep", "0") == "1",
        };

        if (!equal)
        {
            var widths = node.GetAttributeValue("data-col-widths-tw", "");
            var spaces = node.GetAttributeValue("data-col-spaces-tw", "");
            if (!string.IsNullOrWhiteSpace(widths))
            {
                var w = widths.Split(',');
                var sp = spaces.Split(',');
                var cols = new List<SectionColumn>();
                for (int i = 0; i < w.Length; i++)
                {
                    cols.Add(new SectionColumn
                    {
                        WidthTwips = int.TryParse(w[i], System.Globalization.NumberStyles.Integer, inv, out var wv) ? wv : 0,
                        SpaceTwips = i < sp.Length && int.TryParse(sp[i], System.Globalization.NumberStyles.Integer, inv, out var sv) ? sv : 0,
                    });
                }
                layout.Columns = cols;
            }
        }

        return layout;
    }

    private SpacingBetweenLines BuildDefaultSpacing()
    {
        if (_docDefaultSpacingAfterTw == null && _docDefaultSpacingBeforeTw == null && _docDefaultSpacingLine == null)
            return new SpacingBetweenLines { After = "160", Line = "259", LineRule = LineSpacingRuleValues.Auto };

        var spacing = new SpacingBetweenLines();
        if (_docDefaultSpacingBeforeTw != null) spacing.Before = _docDefaultSpacingBeforeTw;
        if (_docDefaultSpacingAfterTw != null) spacing.After = _docDefaultSpacingAfterTw;
        if (_docDefaultSpacingLine != null)
        {
            spacing.Line = _docDefaultSpacingLine;
            spacing.LineRule = _docDefaultSpacingLineRule switch
            {
                "exact" => LineSpacingRuleValues.Exact,
                "atLeast" => LineSpacingRuleValues.AtLeast,
                _ => LineSpacingRuleValues.Auto
            };
        }
        return spacing;
    }

    private void AddDocumentStyles(WordprocessingDocument document)
    {
        var stylesPart = _mainPart!.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        var bodyFont = _docDefaultFontFamily
            ?? (string.IsNullOrWhiteSpace(_defaults.FontFamily) ? "Calibri" : _defaults.FontFamily);
        var headingFont = string.IsNullOrWhiteSpace(_defaults.HeadingFontFamily) ? bodyFont : _defaults.HeadingFontFamily;
        var fontSizePt = _docDefaultFontSizePt ?? _defaults.FontSizePt;
        var halfPt = ((int)Math.Round(fontSizePt * 2)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var docDefaults = new DocDefaults(
            new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = bodyFont, HighAnsi = bodyFont, EastAsia = bodyFont, ComplexScript = bodyFont },
                    new FontSize { Val = halfPt },
                    new FontSizeComplexScript { Val = halfPt },
                    new Languages { Val = "pl-PL", EastAsia = "pl-PL" }
                )
            ),
            new ParagraphPropertiesDefault(
                new ParagraphPropertiesBaseStyle(BuildDefaultSpacing())
            )
        );
        styles.Append(docDefaults);

        var normalStyle = new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true
        };
        normalStyle.Append(new StyleName { Val = "Normal" });
        normalStyle.Append(new PrimaryStyle());
        normalStyle.Append(new StyleParagraphProperties(BuildDefaultSpacing()));
        normalStyle.Append(new StyleRunProperties(
            new RunFonts { Ascii = bodyFont, HighAnsi = bodyFont },
            new FontSize { Val = halfPt }
        ));
        styles.Append(normalStyle);

        string[] headingColors = { "2F5496", "2F5496", "1F3763", "2F5496", "2F5496", "1F3763" };
        string[] headingSizes = { "32", "26", "24", "22", "22", "22" };
        bool[] headingBold = { true, true, true, true, true, false };
        bool[] headingItalic = { false, false, false, true, false, true };
        int[] headingSpaceBefore = { 240, 40, 40, 40, 40, 40 };

        for (int i = 1; i <= 6; i++)
        {
            var headingStyle = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = $"Heading{i}"
            };
            headingStyle.Append(new StyleName { Val = $"Heading {i}" });
            headingStyle.Append(new BasedOn { Val = "Normal" });
            headingStyle.Append(new NextParagraphStyle { Val = "Normal" });
            headingStyle.Append(new PrimaryStyle());
            
            var paraProps = new StyleParagraphProperties(
                new KeepNext(),
                new KeepLines(),
                new SpacingBetweenLines { Before = headingSpaceBefore[i - 1].ToString(), After = "0" },
                new OutlineLevel { Val = i - 1 }
            );
            headingStyle.Append(paraProps);
            
            var runPropsElements = new List<OpenXmlElement>
            {
                new RunFonts { Ascii = headingFont, HighAnsi = headingFont },
            };
            if (headingBold[i - 1]) runPropsElements.Add(new Bold());
            if (headingItalic[i - 1]) runPropsElements.Add(new Italic());
            runPropsElements.Add(new Color { Val = headingColors[i - 1] });
            runPropsElements.Add(new FontSize { Val = headingSizes[i - 1] });

            headingStyle.Append(new StyleRunProperties(runPropsElements.ToArray()));
            styles.Append(headingStyle);
        }

        var hyperlinkStyle = new Style
        {
            Type = StyleValues.Character,
            StyleId = "Hyperlink"
        };
        hyperlinkStyle.Append(new StyleName { Val = "Hyperlink" });
        hyperlinkStyle.Append(new StyleRunProperties(
            new Color { Val = "0563C1", ThemeColor = ThemeColorValues.Hyperlink },
            new Underline { Val = UnderlineValues.Single }
        ));
        styles.Append(hyperlinkStyle);

        var listParagraph = new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = "ListParagraph"
        };
        listParagraph.Append(new StyleName { Val = "List Paragraph" });
        listParagraph.Append(new BasedOn { Val = "Normal" });
        listParagraph.Append(new StyleParagraphProperties(
            new Indentation { Left = "720" }
        ));
        styles.Append(listParagraph);

        var headerStyle = new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = "Header"
        };
        headerStyle.Append(new StyleName { Val = "header" });
        headerStyle.Append(new BasedOn { Val = "Normal" });
        headerStyle.Append(new LinkedStyle { Val = "HeaderChar" });
        headerStyle.Append(new UIPriority { Val = 99 });
        headerStyle.Append(new UnhideWhenUsed());
        headerStyle.Append(new StyleParagraphProperties(
            new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }
        ));
        styles.Append(headerStyle);

        var headerCharStyle = new Style
        {
            Type = StyleValues.Character,
            StyleId = "HeaderChar",
            CustomStyle = true
        };
        headerCharStyle.Append(new StyleName { Val = "Nagłówek Znak" });
        headerCharStyle.Append(new BasedOn { Val = "DefaultParagraphFont" });
        headerCharStyle.Append(new LinkedStyle { Val = "Header" });
        headerCharStyle.Append(new UIPriority { Val = 99 });
        styles.Append(headerCharStyle);

        var footerStyle = new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = "Footer"
        };
        footerStyle.Append(new StyleName { Val = "footer" });
        footerStyle.Append(new BasedOn { Val = "Normal" });
        footerStyle.Append(new LinkedStyle { Val = "FooterChar" });
        footerStyle.Append(new UIPriority { Val = 99 });
        footerStyle.Append(new UnhideWhenUsed());
        footerStyle.Append(new StyleParagraphProperties(
            new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }
        ));
        styles.Append(footerStyle);

        var footerCharStyle = new Style
        {
            Type = StyleValues.Character,
            StyleId = "FooterChar",
            CustomStyle = true
        };
        footerCharStyle.Append(new StyleName { Val = "Stopka Znak" });
        footerCharStyle.Append(new BasedOn { Val = "DefaultParagraphFont" });
        footerCharStyle.Append(new LinkedStyle { Val = "Footer" });
        footerCharStyle.Append(new UIPriority { Val = 99 });
        styles.Append(footerCharStyle);

        var defaultParagraphFont = new Style
        {
            Type = StyleValues.Character,
            StyleId = "DefaultParagraphFont",
            Default = true
        };
        defaultParagraphFont.Append(new StyleName { Val = "Default Paragraph Font" });
        defaultParagraphFont.Append(new UIPriority { Val = 1 });
        defaultParagraphFont.Append(new SemiHidden());
        defaultParagraphFont.Append(new UnhideWhenUsed());
        styles.Append(defaultParagraphFont);

        stylesPart.Styles = styles;
    }

    private void ConvertHtmlToBody(HtmlNode node, Body body)
    {
        foreach (var child in node.ChildNodes)
        {
            var elements = ConvertHtmlNode(child);
            foreach (var element in elements)
            {
                body.Append(element);
            }
        }

        FlushPendingTextBoxesInto(body);

        if (!body.Elements<Paragraph>().Any() && !body.Elements<Table>().Any())
        {
            body.Append(new Paragraph());
        }
    }

    private List<OpenXmlElement> ConvertHtmlNode(HtmlNode node)
    {
        var elements = new List<OpenXmlElement>();

        switch (node.NodeType)
        {
            case HtmlNodeType.Text:
                var text = System.Net.WebUtility.HtmlDecode(node.InnerText);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    elements.Add(CreateParagraph(text));
                }
                break;

            case HtmlNodeType.Element:
                elements.AddRange(ConvertHtmlElement(node));
                break;
        }

        return elements;
    }

    private List<OpenXmlElement> ConvertHtmlElement(HtmlNode node)
    {
        var elements = new List<OpenXmlElement>();
        var tagName = node.Name.ToLower();

        if (node.NodeType == HtmlNodeType.Element
            && node.GetAttributeValue("data-docx-xml", "") != ""
            && TryRestorePreservedElement(node) is { } preservedElement)
        {
            elements.Add(new Paragraph(new Run(preservedElement)));
            return elements;
        }

        switch (tagName)
        {
            case "p":
                elements.Add(ConvertParagraphElement(node));
                break;

            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                var level = int.Parse(tagName[1].ToString());
                elements.Add(ConvertHeadingElement(node, level));
                break;

            case "div":
                if (IsTextBoxNode(node))
                {
                    BufferTextBoxDrawing(node);
                }
                else if (IsSectionBreakNode(node))
                {
                    elements.Add(CreateSectionBreakParagraph(node));
                }
                else if (node.HasClass("docx-column-break"))
                {
                    elements.Add(new Paragraph(new Run(new Break { Type = BreakValues.Column })));
                }
                else if (IsPageBreakNode(node))
                {
                    if (!NextElementSiblingIsSectionBreak(node))
                        elements.Add(CreatePageBreak());
                }
                else if (node.HasClass("sdt-block"))
                {
                    elements.Add(BuildSdtBlockFromHtml(node));
                }
                else if (node.HasClass("document-content"))
                {
                    foreach (var child in node.ChildNodes)
                        elements.AddRange(ConvertHtmlNode(child));
                }
                else
                {
                    foreach (var child in node.ChildNodes)
                        elements.AddRange(ConvertHtmlNode(child));
                }
                break;

            case "br":
                elements.Add(new Paragraph());
                break;

            case "ul":
            case "ol":
                elements.AddRange(ConvertListElement(node, tagName == "ol"));
                break;

            case "table":
                elements.Add(ConvertTableElement(node));
                break;

            case "img":
                var imgPara = ConvertImageElement(node);
                if (imgPara != null)
                    elements.Add(imgPara);
                break;

            case "a":
                elements.Add(ConvertAnchorElement(node));
                break;

            case "blockquote":
                elements.Add(ConvertBlockquoteElement(node));
                break;

            case "hr":
                elements.Add(CreateHorizontalRule());
                break;

            case "span": case "strong": case "b": case "em": case "i": case "u": case "s": case "strike": case "sub": case "sup":
                elements.Add(ConvertInlineElement(node));
                break;

            default:
                foreach (var child in node.ChildNodes)
                    elements.AddRange(ConvertHtmlNode(child));
                break;
        }

        return elements;
    }

    private Paragraph ConvertParagraphElement(HtmlNode node)
    {
        var paragraph = new Paragraph();
        var props = new ParagraphProperties();

        var style = node.GetAttributeValue("style", "");
        ApplyParagraphStyle(props, style);

        var styleId = node.GetAttributeValue("data-style-id", "");
        if (!string.IsNullOrEmpty(styleId))
        {
            props.Append(new ParagraphStyleId { Val = styleId });
        }

        var tabStopsAttr = node.GetAttributeValue("data-tab-stops", "");
        if (!string.IsNullOrEmpty(tabStopsAttr) && ParseTabStops(tabStopsAttr) is { } tabs)
        {
            props.Append(tabs);
        }

        NormalizeParagraphPropertiesOrder(props);

        if (props.HasChildren)
            paragraph.Append(props);

        AttachPendingTextBoxes(paragraph);

        if (!IsEmptyParagraphMarkup(node))
            AppendInlineContent(paragraph, node);

        return paragraph;
    }

    private static bool IsEmptyParagraphMarkup(HtmlNode node)
    {
        var elements = node.Descendants().Where(d => d.NodeType == HtmlNodeType.Element).ToList();
        var text = System.Net.WebUtility.HtmlDecode(node.InnerText);

        if (elements.Count == 1 && elements[0].Name.Equals("br", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(text);

        foreach (var el in elements)
        {
            if (!el.Name.Equals("span", StringComparison.OrdinalIgnoreCase)) return false;
            if (el.Attributes.Any(a => a.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase)
                                       || a.Name.Equals("class", StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return text == " ";
    }

    private Paragraph ConvertHeadingElement(HtmlNode node, int level)
    {
        var paragraph = new Paragraph();
        var props = new ParagraphProperties();
        props.Append(new ParagraphStyleId { Val = $"Heading{level}" });

        var style = node.GetAttributeValue("style", "");
        if (!string.IsNullOrEmpty(style))
        {
            ApplyParagraphStyle(props, style);
        }

        paragraph.Append(props);
        AttachPendingTextBoxes(paragraph);
        AppendInlineContent(paragraph, node);

        return paragraph;
    }

    private List<OpenXmlElement> ConvertListElement(HtmlNode node, bool ordered, int level = 0, int? parentNumId = null)
    {
        var elements = new List<OpenXmlElement>();

        level = ResolveListLevel(node, level);

        int numId;
        if (parentNumId.HasValue)
        {
            numId = parentNumId.Value;
        }
        else
        {
            EnsureNumberingPart();

            var htmlListId = node.GetAttributeValue("data-num-id", "");
            if (htmlListId.Length > 0 && _numIdByHtmlList.TryGetValue(htmlListId, out var existingNumId))
            {
                numId = existingNumId;
            }
            else
            {
                var levelFormats = new Dictionary<int, bool>();
                ScanListLevels(node, ordered, level, levelFormats);

                var pictureBulletLevels = new Dictionary<int, string?>();
                ScanPictureBulletLevels(node, level, pictureBulletLevels);

                var levelSpecs = new Dictionary<int, HtmlListLevelSpec>();
                ScanListLevelSpecs(node, level, levelSpecs);

                var overrideSpecs = levelSpecs
                    .Where(kv => kv.Value.IsLvlOverride)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var abstractSpecs = levelSpecs
                    .Where(kv => !kv.Value.IsLvlOverride)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var abstractPicLevels = pictureBulletLevels
                    .Where(kv => !overrideSpecs.ContainsKey(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var htmlAbstractId = node.GetAttributeValue("data-abstract-num-id", "");
                int abstractNumId;
                if (htmlAbstractId.Length > 0 && _abstractIdByHtmlAbstract.TryGetValue(htmlAbstractId, out var existingAbstract))
                {
                    abstractNumId = existingAbstract;
                    UpgradeSharedAbstractLevels(abstractNumId, levelFormats, abstractPicLevels, abstractSpecs);
                }
                else
                {
                    abstractNumId = CreateAbstractNumbering(levelFormats, abstractPicLevels, abstractSpecs);
                    if (htmlAbstractId.Length > 0)
                        _abstractIdByHtmlAbstract[htmlAbstractId] = abstractNumId;
                }

                var startOverrides = levelSpecs
                    .Where(kv => kv.Value.StartOverride > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.StartOverride);

                var instancePicLevels = new HashSet<int>();
                var fullLevelOverrides = new Dictionary<int, Level>();
                foreach (var (lvl, overrideSpec) in overrideSpecs)
                {
                    string? overridePicUri = null;
                    var hasOverridePic = pictureBulletLevels.TryGetValue(lvl, out overridePicUri);
                    fullLevelOverrides[lvl] = BuildAbstractLevel(
                        lvl, ResolveLevelOrdered(levelFormats, lvl),
                        hasOverridePic, overridePicUri, overrideSpec, instancePicLevels);
                }

                numId = CreateNumberingInstance(abstractNumId, startOverrides, fullLevelOverrides);

                var numPicLevels = new HashSet<int>(instancePicLevels);
                if (_picBulletLevelsByAbstract.TryGetValue(abstractNumId, out var picLevels))
                    numPicLevels.UnionWith(picLevels);
                if (numPicLevels.Count > 0)
                    _picBulletLevelsByNum[numId] = numPicLevels;
                if (htmlListId.Length > 0)
                    _numIdByHtmlList[htmlListId] = numId;
            }
        }

        foreach (var child in node.ChildNodes)
        {
            if (child.Name.ToLower() != "li") continue;
            
            var nestedLists = child.SelectNodes("./ul|./ol");
            
            var para = new Paragraph();
            var props = new ParagraphProperties();
            var liParagraphStyleId = child.GetAttributeValue("data-style-id", "");
            props.Append(new ParagraphStyleId
            {
                Val = string.IsNullOrEmpty(liParagraphStyleId)
                    ? "ListParagraph"
                    : System.Net.WebUtility.HtmlDecode(liParagraphStyleId)
            });
            props.Append(new NumberingProperties(
                new NumberingLevelReference { Val = level },
                new NumberingId { Val = numId }
            ));
            
            var liStyle = child.GetAttributeValue("style", "");
            if (!string.IsNullOrEmpty(liStyle))
            {
                ApplyParagraphStyle(props, liStyle, includeIndentation: false);
            }

            AppendListItemIndentation(props, child);

            var liTabStopsAttr = child.GetAttributeValue("data-tab-stops", "");
            if (!string.IsNullOrEmpty(liTabStopsAttr) && ParseTabStops(liTabStopsAttr) is { } liTabs)
            {
                props.Append(liTabs);
                NormalizeParagraphPropertiesOrder(props);
            }

            var markColorRaw = child.GetAttributeValue("data-mark-color", "");
            var markSizeRaw = child.GetAttributeValue("data-mark-size", "");
            var hasMarkColor = Regex.IsMatch(markColorRaw, "^[0-9A-Fa-f]{6}$");
            var hasMarkSize = Regex.IsMatch(markSizeRaw, @"^\d{1,4}$");
            if (hasMarkColor || hasMarkSize)
            {
                var markProps = new ParagraphMarkRunProperties();
                if (hasMarkColor) markProps.Append(new Color { Val = markColorRaw });
                if (hasMarkSize) markProps.Append(new FontSize { Val = markSizeRaw });
                props.Append(markProps);
                NormalizeParagraphPropertiesOrder(props);
            }

            para.Append(props);

            RunProperties? liBaseProps = null;
            if (!string.IsNullOrEmpty(liStyle))
            {
                liBaseProps = new RunProperties();
                ApplyRunStyle(liBaseProps, liStyle);
                if (!liBaseProps.HasChildren) liBaseProps = null;
            }

            foreach (var liChild in child.ChildNodes)
            {
                var liChildName = liChild.Name.ToLower();
                if (liChildName == "ul" || liChildName == "ol")
                    continue;

                if (liChildName == "span" && IsListMarkerSpan(liChild))
                {
                    var img = liChild.SelectSingleNode(".//img");
                    var levelHasPicBullet = _picBulletLevelsByNum.TryGetValue(numId, out var picBulletLevels)
                        && picBulletLevels.Contains(level);
                    if (img != null && !levelHasPicBullet)
                    {
                        foreach (var run in CreateRunsFromNode(img, liBaseProps))
                            para.Append(run);
                    }
                    continue;
                }

                var runs = CreateRunsFromNode(liChild, liBaseProps);
                foreach (var run in runs)
                    para.Append(run);
            }

            if (!para.Elements<Run>().Any() && !para.Elements<Hyperlink>().Any())
            {
                para.Append(new Run(new Text("") { Space = SpaceProcessingModeValues.Preserve }));
            }

            elements.Add(para);

            if (nestedLists != null)
            {
                foreach (var nestedList in nestedLists)
                {
                    var isOrdered = nestedList.Name.ToLower() == "ol";
                    elements.AddRange(ConvertListElement(nestedList, isOrdered, level + 1, numId));
                }
            }
        }

        return elements;
    }

    private static void AppendListItemIndentation(ParagraphProperties props, HtmlNode li)
    {
        var ind = new Indentation();
        var hasInd = false;
        if (int.TryParse(li.GetAttributeValue("data-ind-left-tw", ""), out var leftTw))
        {
            ind.Left = leftTw.ToString();
            hasInd = true;
        }
        if (int.TryParse(li.GetAttributeValue("data-ind-hanging-tw", ""), out var hangingTw))
        {
            ind.Hanging = hangingTw.ToString();
            hasInd = true;
        }
        else if (int.TryParse(li.GetAttributeValue("data-ind-first-line-tw", ""), out var firstLineTw))
        {
            ind.FirstLine = firstLineTw.ToString();
            hasInd = true;
        }
        if (!hasInd) return;
        props.Append(ind);
        NormalizeParagraphPropertiesOrder(props);
    }

    private readonly record struct HtmlListLevelSpec(
        string? Fmt, string? LvlText, int Start, string? BulletFont,
        int StartOverride, string? Suffix, bool IsLegal, int LvlRestart,
        string? IndLeftTw, string? IndHangingTw, string? IndFirstLineTw,
        bool IsLvlOverride, string? MarkerColor = null, string? MarkerSizeHalfPoints = null);

    private static void ScanListLevelSpecs(HtmlNode node, int level, Dictionary<int, HtmlListLevelSpec> specs)
    {
        if (!specs.ContainsKey(level))
        {
            var fmt = node.GetAttributeValue("data-num-fmt", "");
            var lvlText = node.GetAttributeValue("data-lvl-text", "");
            var startRaw = node.GetAttributeValue("data-start", "");
            var bulletFont = node.GetAttributeValue("data-bullet-font", "");
            var startOverrideRaw = node.GetAttributeValue("data-start-override", "");
            var suffix = node.GetAttributeValue("data-suffix", "");
            var isLegal = node.GetAttributeValue("data-is-legal", "") == "1";
            var lvlRestartRaw = node.GetAttributeValue("data-lvl-restart", "");
            var indLeft = node.GetAttributeValue("data-ind-left-tw", "");
            var indHanging = node.GetAttributeValue("data-ind-hanging-tw", "");
            var indFirstLine = node.GetAttributeValue("data-ind-first-line-tw", "");
            var isLvlOverride = node.GetAttributeValue("data-lvl-override", "") == "1";
            var markerColorRaw = node.GetAttributeValue("data-marker-color", "");
            var markerColor = Regex.IsMatch(markerColorRaw, "^([0-9A-Fa-f]{6}|auto)$") ? markerColorRaw : null;
            var markerSizeRaw = node.GetAttributeValue("data-marker-size", "");
            var markerSize = Regex.IsMatch(markerSizeRaw, @"^\d{1,4}$") ? markerSizeRaw : null;
            if (fmt.Length > 0 || lvlText.Length > 0 || startRaw.Length > 0
                || startOverrideRaw.Length > 0 || suffix.Length > 0 || isLegal
                || lvlRestartRaw.Length > 0 || indLeft.Length > 0 || markerColor != null
                || markerSize != null)
            {
                _ = int.TryParse(startRaw, out var start);
                var startOverride = int.TryParse(startOverrideRaw, out var so) && so > 0 ? so : -1;
                var lvlRestart = int.TryParse(lvlRestartRaw, out var lr) && lr >= 0 ? lr : -1;
                specs[level] = new HtmlListLevelSpec(
                    fmt.Length > 0 ? fmt : null,
                    lvlText.Length > 0 ? HtmlEntity.DeEntitize(lvlText) : null,
                    start > 0 ? start : 1,
                    bulletFont.Length > 0 ? HtmlEntity.DeEntitize(bulletFont) : null,
                    startOverride,
                    suffix is "space" or "nothing" ? suffix : null,
                    isLegal,
                    lvlRestart,
                    indLeft.Length > 0 ? indLeft : null,
                    indHanging.Length > 0 ? indHanging : null,
                    indFirstLine.Length > 0 ? indFirstLine : null,
                    isLvlOverride,
                    markerColor,
                    markerSize);
            }
        }

        foreach (var child in node.ChildNodes)
        {
            if (child.Name.ToLower() != "li") continue;
            var nested = child.SelectNodes("./ul|./ol");
            if (nested == null) continue;
            foreach (var nestedList in nested)
                ScanListLevelSpecs(nestedList, ResolveListLevel(nestedList, level + 1), specs);
        }
    }

    private static int ResolveListLevel(HtmlNode node, int fallback)
    {
        var raw = node.GetAttributeValue("data-ilvl", "");
        if (int.TryParse(raw, out var ilvl) && ilvl is >= 0 and <= 8)
            return ilvl;
        return Math.Clamp(fallback, 0, 8);
    }

    private static bool TryMapNumFmt(string token, out NumberFormatValues fmt)
    {
        switch (token)
        {
            case "decimal": fmt = NumberFormatValues.Decimal; return true;
            case "decimalZero": fmt = NumberFormatValues.DecimalZero; return true;
            case "lowerLetter": fmt = NumberFormatValues.LowerLetter; return true;
            case "upperLetter": fmt = NumberFormatValues.UpperLetter; return true;
            case "lowerRoman": fmt = NumberFormatValues.LowerRoman; return true;
            case "upperRoman": fmt = NumberFormatValues.UpperRoman; return true;
            case "bullet": fmt = NumberFormatValues.Bullet; return true;
            case "none": fmt = NumberFormatValues.None; return true;
            default:
                if (Regex.IsMatch(token, "^[a-zA-Z][a-zA-Z0-9]*$"))
                {
                    fmt = new NumberFormatValues(token);
                    return true;
                }
                fmt = NumberFormatValues.Decimal;
                return false;
        }
    }

    private void ScanListLevels(HtmlNode node, bool ordered, int level, Dictionary<int, bool> levelFormats)
    {
        if (!levelFormats.ContainsKey(level))
            levelFormats[level] = ordered;

        foreach (var child in node.ChildNodes)
        {
            if (child.Name.ToLower() != "li") continue;
            var nested = child.SelectNodes("./ul|./ol");
            if (nested == null) continue;
            foreach (var nestedList in nested)
            {
                var isOrdered = nestedList.Name.ToLower() == "ol";
                ScanListLevels(nestedList, isOrdered, ResolveListLevel(nestedList, level + 1), levelFormats);
            }
        }
    }

    private static void ScanPictureBulletLevels(HtmlNode node, int level, Dictionary<int, string?> pictureBulletLevels)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.Name.ToLower() != "li") continue;

            foreach (var liChild in child.ChildNodes)
            {
                if (liChild.Name.ToLower() != "span") continue;
                if (!IsListMarkerSpan(liChild)) continue;
                var img = liChild.SelectSingleNode(".//img");
                if (img != null)
                {
                    if (!pictureBulletLevels.ContainsKey(level))
                    {
                        var src = img.GetAttributeValue("src", "");
                        pictureBulletLevels[level] = src.StartsWith("data:") ? src : null;
                    }
                    break;
                }
            }

            var nested = child.SelectNodes("./ul|./ol");
            if (nested == null) continue;
            foreach (var nestedList in nested)
                ScanPictureBulletLevels(nestedList, ResolveListLevel(nestedList, level + 1), pictureBulletLevels);
        }
    }

    private static bool IsListMarkerSpan(HtmlNode node)
    {
        var cls = node.GetAttributeValue("class", "");
        if (string.IsNullOrEmpty(cls)) return false;
        foreach (var token in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("list-marker", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void EnsureNumberingPart()
    {
        if (_numberingPart != null) return;
        
        _numberingPart = _mainPart!.AddNewPart<NumberingDefinitionsPart>();
        _numberingPart.Numbering = new Numbering();
        _numberingPart.Numbering.Save();
    }

    private int CreateAbstractNumbering(
        Dictionary<int, bool> levelFormats,
        Dictionary<int, string?>? pictureBulletLevels = null,
        Dictionary<int, HtmlListLevelSpec>? levelSpecs = null)
    {
        var abstractNumId = _numberingId++;

        var abstractNum = new AbstractNum { AbstractNumberId = abstractNumId };

        var nsidValue = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
        abstractNum.Append(new Nsid { Val = nsidValue });
        abstractNum.Append(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel });

        var exportedPicBulletLevels = new HashSet<int>();
        var specLevels = new HashSet<int>();

        for (int lvl = 0; lvl < 9; lvl++)
        {
            var isOrdered = ResolveLevelOrdered(levelFormats, lvl);
            HtmlListLevelSpec? spec = levelSpecs != null && levelSpecs.TryGetValue(lvl, out var s) ? s : null;
            string? picBulletUri = null;
            var hasPicBulletMarker = pictureBulletLevels != null
                && pictureBulletLevels.TryGetValue(lvl, out picBulletUri);

            if (spec != null || hasPicBulletMarker) specLevels.Add(lvl);

            abstractNum.Append(BuildAbstractLevel(
                lvl, isOrdered, hasPicBulletMarker, picBulletUri, spec, exportedPicBulletLevels));
        }

        _specLevelsByAbstract[abstractNumId] = specLevels;
        if (exportedPicBulletLevels.Count > 0)
            _picBulletLevelsByAbstract[abstractNumId] = exportedPicBulletLevels;

        var firstInstance = _numberingPart!.Numbering.Elements<NumberingInstance>().FirstOrDefault();
        if (firstInstance != null)
            _numberingPart.Numbering.InsertBefore(abstractNum, firstInstance);
        else
            _numberingPart.Numbering.Append(abstractNum);

        _numberingPart.Numbering.Save();
        return abstractNumId;
    }

    private static bool ResolveLevelOrdered(Dictionary<int, bool> levelFormats, int lvl) =>
        levelFormats.TryGetValue(lvl, out var fmt)
            ? fmt
            : (levelFormats.TryGetValue(0, out var defaultFmt) && defaultFmt);

    private Level BuildAbstractLevel(int lvl, bool isOrdered, bool hasPicBulletMarker,
        string? picBulletUri, HtmlListLevelSpec? spec, HashSet<int> exportedPicBulletLevels)
    {
            var levelDef = new Level { LevelIndex = lvl };
            levelDef.Append(new StartNumberingValue { Val = spec?.Start ?? 1 });

            if (hasPicBulletMarker && picBulletUri != null
                && TryCreatePictureBullet(picBulletUri, out var numPicBulletId))
            {
                levelDef.Append(new NumberingFormat { Val = NumberFormatValues.Bullet });
                levelDef.Append(new LevelText { Val = "" });
                levelDef.Append(new LevelPictureBulletId { Val = numPicBulletId });
                exportedPicBulletLevels.Add(lvl);
            }
            else if (hasPicBulletMarker)
            {
                levelDef.Append(new NumberingFormat { Val = NumberFormatValues.None });
                levelDef.Append(new LevelText { Val = string.Empty });
            }
            else if (spec is { Fmt: not null } sp && TryMapNumFmt(sp.Fmt, out var mappedFmt))
            {
                if (mappedFmt == NumberFormatValues.Bullet)
                {
                    levelDef.Append(new NumberingFormat { Val = NumberFormatValues.Bullet });
                    levelDef.Append(new LevelText { Val = !string.IsNullOrEmpty(sp.LvlText) ? sp.LvlText : "•" });
                    var bulletFont = sp.BulletFont;
                    levelDef.Append(new NumberingSymbolRunProperties(string.IsNullOrEmpty(bulletFont)
                        ? new RunFonts { Ascii = "Symbol", HighAnsi = "Symbol", Hint = FontTypeHintValues.Default }
                        : new RunFonts { Ascii = bulletFont, HighAnsi = bulletFont, Hint = FontTypeHintValues.Default }));
                }
                else
                {
                    levelDef.Append(new NumberingFormat { Val = mappedFmt });
                    var lvlText = !string.IsNullOrEmpty(sp.LvlText) && sp.LvlText.Contains('%')
                        ? sp.LvlText
                        : $"%{lvl + 1}.";
                    levelDef.Append(new LevelText { Val = lvlText });
                    levelDef.Append(new NumberingSymbolRunProperties(
                        new RunFonts { Hint = FontTypeHintValues.Default }
                    ));
                }
            }
            else if (isOrdered)
            {
                var format = lvl switch
                {
                    0 => NumberFormatValues.Decimal,
                    1 => NumberFormatValues.LowerLetter,
                    2 => NumberFormatValues.LowerRoman,
                    3 => NumberFormatValues.Decimal,
                    4 => NumberFormatValues.LowerLetter,
                    5 => NumberFormatValues.LowerRoman,
                    _ => NumberFormatValues.Decimal
                };
                levelDef.Append(new NumberingFormat { Val = format });
                levelDef.Append(new LevelText { Val = $"%{lvl + 1}." });
                levelDef.Append(new NumberingSymbolRunProperties(
                    new RunFonts { Hint = FontTypeHintValues.Default }
                ));
            }
            else if (!isOrdered)
            {
                levelDef.Append(new NumberingFormat { Val = NumberFormatValues.Bullet });
                
                var bulletType = lvl % 3;
                switch (bulletType)
                {
                    case 0:
                        levelDef.Append(new LevelText { Val = "\uF0B7" });
                        levelDef.Append(new NumberingSymbolRunProperties(
                            new RunFonts { Ascii = "Symbol", HighAnsi = "Symbol", Hint = FontTypeHintValues.Default }
                        ));
                        break;
                    case 1:
                        levelDef.Append(new LevelText { Val = "o" });
                        levelDef.Append(new NumberingSymbolRunProperties(
                            new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New", ComplexScript = "Courier New", Hint = FontTypeHintValues.Default }
                        ));
                        break;
                    case 2:
                        levelDef.Append(new LevelText { Val = "\uF0A7" });
                        levelDef.Append(new NumberingSymbolRunProperties(
                            new RunFonts { Ascii = "Wingdings", HighAnsi = "Wingdings", Hint = FontTypeHintValues.Default }
                        ));
                        break;
                }
            }
            
            if (spec is { } specProps && levelDef.GetFirstChild<NumberingFormat>() is { } numFmtAnchor)
            {
                OpenXmlElement anchor = numFmtAnchor;
                if (specProps.LvlRestart >= 0)
                {
                    var lvlRestartEl = new LevelRestart { Val = specProps.LvlRestart };
                    levelDef.InsertAfter(lvlRestartEl, anchor);
                    anchor = lvlRestartEl;
                }
                if (specProps.IsLegal)
                {
                    var isLglEl = new IsLegalNumberingStyle();
                    levelDef.InsertAfter(isLglEl, anchor);
                    anchor = isLglEl;
                }
                if (specProps.Suffix is { } suffixToken)
                {
                    levelDef.InsertAfter(new LevelSuffix
                    {
                        Val = suffixToken == "space" ? LevelSuffixValues.Space : LevelSuffixValues.Nothing
                    }, anchor);
                }
            }

            var markerRunProps = levelDef.GetFirstChild<NumberingSymbolRunProperties>();
            markerRunProps?.Remove();

            if (spec is { MarkerColor: not null } specWithColor)
            {
                markerRunProps ??= new NumberingSymbolRunProperties();
                markerRunProps.Append(new Color { Val = specWithColor.MarkerColor });
            }
            if (spec is { MarkerSizeHalfPoints: not null } specWithSize)
            {
                markerRunProps ??= new NumberingSymbolRunProperties();
                markerRunProps.Append(new FontSize { Val = specWithSize.MarkerSizeHalfPoints });
            }

            levelDef.Append(new LevelJustification { Val = LevelJustificationValues.Left });

            var indentation = new Indentation();
            if (spec is { IndLeftTw: not null } or { IndHangingTw: not null } or { IndFirstLineTw: not null })
            {
                if (spec.Value.IndLeftTw != null) indentation.Left = spec.Value.IndLeftTw;
                if (spec.Value.IndHangingTw != null) indentation.Hanging = spec.Value.IndHangingTw;
                if (spec.Value.IndFirstLineTw != null) indentation.FirstLine = spec.Value.IndFirstLineTw;
            }
            else
            {
                indentation.Left = (720 * (lvl + 1)).ToString();
                indentation.Hanging = "360";
            }
            levelDef.Append(new PreviousParagraphProperties(indentation));

            if (markerRunProps != null)
                levelDef.Append(markerRunProps);

            return levelDef;
    }

    private void UpgradeSharedAbstractLevels(
        int abstractNumId,
        Dictionary<int, bool> levelFormats,
        Dictionary<int, string?> pictureBulletLevels,
        Dictionary<int, HtmlListLevelSpec> levelSpecs)
    {
        var abstractNum = _numberingPart?.Numbering.Elements<AbstractNum>()
            .FirstOrDefault(a => a.AbstractNumberId?.Value == abstractNumId);
        if (abstractNum == null) return;

        if (!_specLevelsByAbstract.TryGetValue(abstractNumId, out var specLevels))
            _specLevelsByAbstract[abstractNumId] = specLevels = new HashSet<int>();
        var exportedPic = _picBulletLevelsByAbstract.TryGetValue(abstractNumId, out var pic)
            ? pic
            : new HashSet<int>();

        var changed = false;
        foreach (var lvl in levelSpecs.Keys.Union(pictureBulletLevels.Keys).OrderBy(l => l))
        {
            if (lvl is < 0 or > 8 || specLevels.Contains(lvl)) continue;

            HtmlListLevelSpec? spec = levelSpecs.TryGetValue(lvl, out var s) ? s : null;
            string? picBulletUri = null;
            var hasPicBulletMarker = pictureBulletLevels.TryGetValue(lvl, out picBulletUri);
            var isOrdered = ResolveLevelOrdered(levelFormats, lvl);

            var newLevel = BuildAbstractLevel(
                lvl, isOrdered, hasPicBulletMarker, picBulletUri, spec, exportedPic);
            var oldLevel = abstractNum.Elements<Level>()
                .FirstOrDefault(l => l.LevelIndex?.Value == lvl);
            if (oldLevel != null)
                abstractNum.ReplaceChild(newLevel, oldLevel);
            else
                abstractNum.Append(newLevel);

            specLevels.Add(lvl);
            changed = true;
        }

        if (exportedPic.Count > 0)
            _picBulletLevelsByAbstract[abstractNumId] = exportedPic;
        if (changed)
            _numberingPart!.Numbering.Save();
    }

    private bool TryCreatePictureBullet(string dataUri, out int numPicBulletId)
    {
        if (_picBulletIdByDataUri.TryGetValue(dataUri, out numPicBulletId))
            return true;

        numPicBulletId = -1;
        var m = Regex.Match(dataUri, @"data:([^;]+);base64,(.+)");
        if (!m.Success) return false;
        var contentType = m.Groups[1].Value;
        if (contentType == "image/svg+xml") return false;
        if (!Regex.IsMatch(contentType, @"^image/[\w.+-]+$")) return false;

        byte[] bytes;
        try { bytes = System.Convert.FromBase64String(m.Groups[2].Value); }
        catch { return false; }

        PartTypeInfo? knownType = contentType switch
        {
            "image/png" => ImagePartType.Png,
            "image/jpeg" or "image/jpg" => ImagePartType.Jpeg,
            "image/gif" => ImagePartType.Gif,
            "image/bmp" => ImagePartType.Bmp,
            "image/tiff" or "image/tif" => ImagePartType.Tiff,
            _ => (PartTypeInfo?)null
        };

        EnsureNumberingPart();
        var imagePart = knownType is { } t
            ? _numberingPart!.AddImagePart(t)
            : _numberingPart!.AddImagePart(contentType);
        using (var stream = new MemoryStream(bytes))
        {
            imagePart.FeedData(stream);
        }
        var relId = _numberingPart!.GetIdOfPart(imagePart);

        numPicBulletId = _picBulletId++;
        var numPicBullet = new NumberingPictureBullet(
            new PictureBulletBase(
                new V.Shape(new V.ImageData { RelationshipId = relId })
                {
                    Id = $"picBullet{numPicBulletId}",
                    Style = "width:12pt;height:12pt"
                }))
        { NumberingPictureBulletId = numPicBulletId };

        var firstAbstract = _numberingPart.Numbering.Elements<AbstractNum>().FirstOrDefault();
        var firstNum = _numberingPart.Numbering.Elements<NumberingInstance>().FirstOrDefault();
        if (firstAbstract != null)
            _numberingPart.Numbering.InsertBefore(numPicBullet, firstAbstract);
        else if (firstNum != null)
            _numberingPart.Numbering.InsertBefore(numPicBullet, firstNum);
        else
            _numberingPart.Numbering.Append(numPicBullet);
        _numberingPart.Numbering.Save();

        _picBulletIdByDataUri[dataUri] = numPicBulletId;
        return true;
    }

    private int CreateNumberingInstance(
        int abstractNumId,
        Dictionary<int, int>? startOverrides = null,
        Dictionary<int, Level>? fullLevelOverrides = null)
    {
        var numId = _numberingId++;

        var numInstance = new NumberingInstance { NumberID = numId };
        numInstance.Append(new AbstractNumId { Val = abstractNumId });

        var overrideLevels = (startOverrides?.Keys ?? Enumerable.Empty<int>())
            .Union(fullLevelOverrides?.Keys ?? Enumerable.Empty<int>())
            .OrderBy(l => l);
        foreach (var lvl in overrideLevels)
        {
            var levelOverride = new LevelOverride { LevelIndex = lvl };
            if (startOverrides != null && startOverrides.TryGetValue(lvl, out var startValue))
                levelOverride.Append(new StartOverrideNumberingValue { Val = startValue });
            if (fullLevelOverrides != null && fullLevelOverrides.TryGetValue(lvl, out var fullLevel))
                levelOverride.Append(fullLevel);
            numInstance.Append(levelOverride);
        }

        _numberingPart!.Numbering.Append(numInstance);
        _numberingPart.Numbering.Save();

        return numId;
    }

    private Table ConvertTableElement(HtmlNode node)
    {
        var table = new Table();
        var tableProps = new TableProperties();

        var defaultBorders = new TableBorders(
            new TopBorder { Val = BorderValues.None, Size = 0 },
            new LeftBorder { Val = BorderValues.None, Size = 0 },
            new BottomBorder { Val = BorderValues.None, Size = 0 },
            new RightBorder { Val = BorderValues.None, Size = 0 },
            new InsideHorizontalBorder { Val = BorderValues.None, Size = 0 },
            new InsideVerticalBorder { Val = BorderValues.None, Size = 0 }
        );
        
        var tableStyle = node.GetAttributeValue("style", "");

        var tblStyleId = node.GetAttributeValue("data-tbl-style", "");
        if (!string.IsNullOrEmpty(tblStyleId))
            tableProps.Append(new TableStyle { Val = System.Net.WebUtility.HtmlDecode(tblStyleId) });

        if (node.GetAttributeValue("data-tblp", "") == "1")
        {
            var tblp = new TablePositionProperties();
            string? Attr(string n) { var v = node.GetAttributeValue("data-tblp-" + n, ""); return string.IsNullOrEmpty(v) ? null : v; }
            short? S(string n) => short.TryParse(Attr(n), out var s) ? s : null;
            int? I(string n) => int.TryParse(Attr(n), out var i) ? i : null;
            if (S("left-tw") is { } l) tblp.LeftFromText = l;
            if (S("right-tw") is { } r) tblp.RightFromText = r;
            if (S("top-tw") is { } t) tblp.TopFromText = t;
            if (S("bottom-tw") is { } b) tblp.BottomFromText = b;
            tblp.HorizontalAnchor = Attr("horz-anchor") switch
            {
                "page" => HorizontalAnchorValues.Page,
                "margin" => HorizontalAnchorValues.Margin,
                "text" => HorizontalAnchorValues.Text,
                _ => null
            };
            tblp.VerticalAnchor = Attr("vert-anchor") switch
            {
                "page" => VerticalAnchorValues.Page,
                "margin" => VerticalAnchorValues.Margin,
                "text" => VerticalAnchorValues.Text,
                _ => null
            };
            tblp.TablePositionXAlignment = Attr("xspec") switch
            {
                "left" => HorizontalAlignmentValues.Left,
                "center" => HorizontalAlignmentValues.Center,
                "right" => HorizontalAlignmentValues.Right,
                "inside" => HorizontalAlignmentValues.Inside,
                "outside" => HorizontalAlignmentValues.Outside,
                _ => null
            };
            tblp.TablePositionYAlignment = Attr("yspec") switch
            {
                "inline" => VerticalAlignmentValues.Inline,
                "top" => VerticalAlignmentValues.Top,
                "center" => VerticalAlignmentValues.Center,
                "bottom" => VerticalAlignmentValues.Bottom,
                "inside" => VerticalAlignmentValues.Inside,
                "outside" => VerticalAlignmentValues.Outside,
                _ => null
            };
            if (I("x-tw") is { } x) tblp.TablePositionX = x;
            if (I("y-tw") is { } y) tblp.TablePositionY = y;
            tableProps.Append(tblp);
        }

        var tableWidthIsAuto = node.GetAttributeValue("data-tbl-w", "") == "auto";
        var tableWidthMatch = Regex.Match(tableStyle, @"width:\s*([\d.]+)(px|%)?");
        if (tableWidthIsAuto)
        {
            tableProps.Append(new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto });
        }
        else if (int.TryParse(node.GetAttributeValue("data-tbl-w-tw", ""), out var tblWTw) && tblWTw > 0)
        {
            tableProps.Append(new TableWidth { Width = tblWTw.ToString(), Type = TableWidthUnitValues.Dxa });
        }
        else if (tableWidthMatch.Success)
        {
            var widthValue = double.Parse(tableWidthMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var unit = tableWidthMatch.Groups[2].Value;

            if (unit == "%")
            {
                tableProps.Append(new TableWidth { Width = ((int)Math.Round(widthValue * 50)).ToString(), Type = TableWidthUnitValues.Pct });
            }
            else if (unit == "px" || string.IsNullOrEmpty(unit))
            {
                tableProps.Append(new TableWidth { Width = ((int)Math.Round(widthValue * 15)).ToString(), Type = TableWidthUnitValues.Dxa });
            }
        }
        else
        {
            tableProps.Append(new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto });
        }

        if (tableStyle.Contains("margin-left:auto") && tableStyle.Contains("margin-right:auto"))
        {
            tableProps.Append(new TableJustification { Val = TableRowAlignmentValues.Center });
        }
        else if (tableStyle.Contains("margin-left:auto"))
        {
            tableProps.Append(new TableJustification { Val = TableRowAlignmentValues.Right });
        }
        else if (node.GetAttributeValue("data-tbl-jc", "") == "left")
        {
            tableProps.Append(new TableJustification { Val = TableRowAlignmentValues.Left });
        }

        var cellSpacingTwAttr = node.GetAttributeValue("data-cell-spacing-tw", "");
        if (int.TryParse(cellSpacingTwAttr, out var cellSpacingTw) && cellSpacingTw > 0)
        {
            tableProps.Append(new TableCellSpacing { Width = cellSpacingTw.ToString(), Type = TableWidthUnitValues.Dxa });
        }
        else
        {
            var spacingMatch = Regex.Match(tableStyle, @"border-spacing:\s*([\d.]+)px");
            if (spacingMatch.Success)
            {
                var spacingPx = double.Parse(spacingMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (spacingPx > 0)
                    tableProps.Append(new TableCellSpacing { Width = ((int)Math.Round(spacingPx * 15 / 2)).ToString(), Type = TableWidthUnitValues.Dxa });
            }
        }

        var indentMatch = Regex.Match(tableStyle, @"margin-left:\s*(-?[\d.]+)px");
        if (indentMatch.Success)
        {
            var indentPx = double.Parse(indentMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(indentPx) > 0.01)
                tableProps.Append(new TableIndentation { Width = (int)Math.Round(indentPx * 15), Type = TableWidthUnitValues.Dxa });
        }

        var borderMatch = Regex.Match(tableStyle, @"(?<![a-z-])border:\s*([\d.]+)px\s+(\w+)\s+#?([a-fA-F0-9]{3,6})");
        if (borderMatch.Success)
        {
            var bStyle = ParseBorderStyle(borderMatch.Groups[2].Value);
            var bSize = CssBorderWidthToEighthPoints(borderMatch.Groups[1].Value, bStyle);
            var bColor = NormalizeColor(borderMatch.Groups[3].Value);

            defaultBorders = new TableBorders(
                new TopBorder { Val = bStyle, Size = bSize, Color = bColor },
                new LeftBorder { Val = bStyle, Size = bSize, Color = bColor },
                new BottomBorder { Val = bStyle, Size = bSize, Color = bColor },
                new RightBorder { Val = bStyle, Size = bSize, Color = bColor },
                new InsideHorizontalBorder { Val = bStyle, Size = bSize, Color = bColor },
                new InsideVerticalBorder { Val = bStyle, Size = bSize, Color = bColor }
            );
        }

        var longhandBorders = false;
        if (!borderMatch.Success)
        {
            BorderType? Side<T>(string side) where T : BorderType, new()
            {
                var m = Regex.Match(tableStyle, @"(?<![a-z-])border-" + side + @":\s*([\d.]+)px\s+(\w+)\s+#?([a-fA-F0-9]{3,6})");
                if (!m.Success) return null;
                var st = ParseBorderStyle(m.Groups[2].Value);
                return new T { Val = st, Size = CssBorderWidthToEighthPoints(m.Groups[1].Value, st), Color = NormalizeColor(m.Groups[3].Value) };
            }
            var lt = Side<TopBorder>("top"); var ll = Side<LeftBorder>("left");
            var lb = Side<BottomBorder>("bottom"); var lr = Side<RightBorder>("right");
            if (lt != null || ll != null || lb != null || lr != null)
            {
                longhandBorders = true;
                defaultBorders = new TableBorders();
                if (lt != null) defaultBorders.Append(lt);
                if (ll != null) defaultBorders.Append(ll);
                if (lb != null) defaultBorders.Append(lb);
                if (lr != null) defaultBorders.Append(lr);
            }
        }

        var noBordersMarker = node.GetAttributeValue("data-no-borders", "") == "1";
        if (borderMatch.Success || longhandBorders || noBordersMarker || string.IsNullOrEmpty(tblStyleId))
            tableProps.Append(defaultBorders);

        var isFixedLayout = tableStyle.Contains("table-layout:fixed")
            && node.GetAttributeValue("data-tbl-layout", "") != "autofit";
        tableProps.Append(new TableLayout { Type = isFixedLayout ? TableLayoutValues.Fixed : TableLayoutValues.Autofit });
        
        var cellMarTw = node.GetAttributeValue("data-tbl-cell-mar-tw", "").Split(',');
        var hasCellMar = cellMarTw.Length == 4 && cellMarTw.All(v => int.TryParse(v, out _));
        var marTop = hasCellMar ? cellMarTw[0] : "0";
        var marLeft = hasCellMar ? short.Parse(cellMarTw[1]) : (short)108;
        var marBottom = hasCellMar ? cellMarTw[2] : "0";
        var marRight = hasCellMar ? short.Parse(cellMarTw[3]) : (short)108;
        tableProps.Append(new TableCellMarginDefault(
            new TopMargin { Width = marTop, Type = TableWidthUnitValues.Dxa },
            new TableCellLeftMargin { Width = marLeft, Type = TableWidthValues.Dxa },
            new BottomMargin { Width = marBottom, Type = TableWidthUnitValues.Dxa },
            new TableCellRightMargin { Width = marRight, Type = TableWidthValues.Dxa }
        ));

        var tblLookHex = node.GetAttributeValue("data-tbl-look", "");
        if (Regex.IsMatch(tblLookHex, "^[0-9A-Fa-f]{4}$"))
        {
            var lookMask = int.Parse(tblLookHex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
            tableProps.Append(new TableLook
            {
                Val = tblLookHex.ToUpperInvariant(),
                FirstRow = (lookMask & 0x0020) != 0,
                LastRow = (lookMask & 0x0040) != 0,
                FirstColumn = (lookMask & 0x0080) != 0,
                LastColumn = (lookMask & 0x0100) != 0,
                NoHorizontalBand = (lookMask & 0x0200) != 0,
                NoVerticalBand = (lookMask & 0x0400) != 0
            });
        }

        table.Append(tableProps);

        var maxCols = 0;
        var rowNodes = node.SelectNodes("./tr | ./thead/tr | ./tbody/tr | ./tfoot/tr");
        if (rowNodes != null)
        {
            foreach (var rowNode in rowNodes)
            {
                var cellCount = 0;
                var cells = rowNode.SelectNodes("./td|./th");
                if (cells != null)
                {
                    foreach (var cellNode in cells)
                    {
                        var cs = cellNode.GetAttributeValue("colspan", "1");
                        cellCount += int.TryParse(cs, out var csVal) ? csVal : 1;
                    }
                }
                maxCols = Math.Max(maxCols, cellCount);
            }
        }

        var colWidthsTwips = ReadColgroupWidthsTwips(node);
        var gridColCount = Math.Max(maxCols, colWidthsTwips.Count);
        if (gridColCount > 0)
        {
            var grid = new TableGrid();
            for (int i = 0; i < gridColCount; i++)
            {
                var col = new GridColumn();
                if (i < colWidthsTwips.Count && colWidthsTwips[i].Tw > 0)
                    col.Width = colWidthsTwips[i].Tw.ToString();
                grid.Append(col);
            }
            table.Append(grid);
        }

        var activeRowSpans = new Dictionary<int, (int RemainingRows, int ColSpan, TableCellProperties Origin)>();
        if (rowNodes != null)
        {
            foreach (var rowNode in rowNodes)
            {
                var row = new TableRow();
                var gridCursor = 0;
                var spansStartedThisRow = new HashSet<int>();

                void AppendPendingContinuations()
                {
                    while (activeRowSpans.TryGetValue(gridCursor, out var span))
                    {
                        row.Append(CreateVerticalMergeContinuationCell(span.ColSpan, span.Origin));
                        gridCursor += span.ColSpan;
                    }
                }

                var rowStyle = rowNode.GetAttributeValue("style", "");
                var rowProps = new TableRowProperties();

                var rowCellNodes = rowNode.SelectNodes("./td|./th");
                var gridBeforeSpan = GridSpacerSpan(rowCellNodes?.FirstOrDefault(), "before");
                var gridAfterSpan = GridSpacerSpan(rowCellNodes?.LastOrDefault(), "after");
                if (gridBeforeSpan > 0)
                    rowProps.Append(new GridBefore { Val = gridBeforeSpan });
                if (gridAfterSpan > 0)
                    rowProps.Append(new GridAfter { Val = gridAfterSpan });
                if (gridBeforeSpan > 0 && gridBeforeSpan <= colWidthsTwips.Count
                    && colWidthsTwips.Take(gridBeforeSpan).All(c => c.Exact && c.Tw > 0))
                    rowProps.Append(new WidthBeforeTableRow
                    {
                        Width = colWidthsTwips.Take(gridBeforeSpan).Sum(c => c.Tw).ToString(),
                        Type = TableWidthUnitValues.Dxa
                    });
                if (gridAfterSpan > 0 && gridAfterSpan <= colWidthsTwips.Count
                    && colWidthsTwips.TakeLast(gridAfterSpan).All(c => c.Exact && c.Tw > 0))
                    rowProps.Append(new WidthAfterTableRow
                    {
                        Width = colWidthsTwips.TakeLast(gridAfterSpan).Sum(c => c.Tw).ToString(),
                        Type = TableWidthUnitValues.Dxa
                    });

                if (rowNode.GetAttributeValue("data-cant-split", "") == "1")
                    rowProps.Append(new CantSplit());
                else if (rowNode.GetAttributeValue("data-cant-split", "") == "0")
                    rowProps.Append(new CantSplit { Val = OnOffOnlyValues.Off });

                var hRule = rowNode.GetAttributeValue("data-row-hrule", "") == "exact"
                    ? HeightRuleValues.Exact
                    : HeightRuleValues.AtLeast;
                var heightTwAttr = rowNode.GetAttributeValue("data-row-height-tw", "");
                if (uint.TryParse(heightTwAttr, out var heightTwFromAttr) && heightTwFromAttr > 0)
                {
                    rowProps.Append(new TableRowHeight { Val = heightTwFromAttr, HeightType = hRule });
                }
                else
                {
                    var rowHeightMatch = Regex.Match(rowStyle, @"(?:min-)?height:\s*([\d.]+)px");
                    if (rowHeightMatch.Success)
                    {
                        var heightPx = (int)Math.Round(double.Parse(rowHeightMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                        var heightTwips = PxToTwips(heightPx);
                        if (heightTwips > 0)
                            rowProps.Append(new TableRowHeight { Val = (uint)heightTwips, HeightType = hRule });
                    }
                }

                if (rowNode.GetAttributeValue("data-tbl-header", "") == "1")
                    rowProps.Append(new TableHeader());

                if (rowProps.HasChildren)
                    row.Append(rowProps);

                var cells = rowCellNodes;
                if (cells != null)
                {
                    foreach (var cellNode in cells)
                    {
                        if (cellNode.GetAttributeValue("data-grid-spacer", "") != "")
                        {
                            gridCursor += Math.Max(1, cellNode.GetAttributeValue("colspan", 1));
                            continue;
                        }

                        AppendPendingContinuations();

                        var cell = new TableCell();
                        var cellProps = new TableCellProperties();

                        var colspanAttr = cellNode.GetAttributeValue("colspan", "1");
                        if (!int.TryParse(colspanAttr, out var colspan) || colspan < 1) colspan = 1;
                        if (colspan > 1)
                            cellProps.Append(new GridSpan { Val = colspan });

                        var rowspanAttr = cellNode.GetAttributeValue("rowspan", "1");
                        if (int.TryParse(rowspanAttr, out var rowspan) && rowspan > 1)
                        {
                            cellProps.Append(new VerticalMerge { Val = MergedCellValues.Restart });
                            activeRowSpans[gridCursor] = (rowspan - 1, colspan, cellProps);
                            spansStartedThisRow.Add(gridCursor);
                        }
                        var cellStartColumn = gridCursor;
                        gridCursor += colspan;

                        var cellStyle = cellNode.GetAttributeValue("style", "");
                        ApplyCellStyle(cellProps, cellStyle);

                        var hasTcwMarker = ApplyCellSourceMarkers(cellProps, cellNode,
                            node.GetAttributeValue("data-tbl-cell-mar-tw", "") != "");

                        if (!hasTcwMarker && cellStartColumn + colspan <= colWidthsTwips.Count)
                        {
                            var spanned = colWidthsTwips.GetRange(cellStartColumn, colspan);
                            var tcW = cellProps.GetFirstChild<TableCellWidth>();
                            var isPct = tcW?.Type?.Value == TableWidthUnitValues.Pct;
                            if (!isPct && spanned.All(c => c.Exact && c.Tw > 0))
                            {
                                var exactWidth = spanned.Sum(c => c.Tw).ToString();
                                if (tcW != null)
                                {
                                    tcW.Width = exactWidth;
                                    tcW.Type = TableWidthUnitValues.Dxa;
                                }
                                else
                                {
                                    cellProps.Append(new TableCellWidth { Width = exactWidth, Type = TableWidthUnitValues.Dxa });
                                }
                            }
                        }
                        
                        ApplyCellBorders(cellProps, cellStyle);

                        NormalizeTableCellPropertiesOrder(cellProps);

                        cell.Append(cellProps);

                        var hasContent = false;
                        foreach (var childNode in cellNode.ChildNodes)
                        {
                            var childTag = childNode.Name.ToLower();
                            if (childTag == "p" || childTag == "div" || childTag == "br" ||
                                childTag == "h1" || childTag == "h2" || childTag == "h3" ||
                                childTag == "h4" || childTag == "h5" || childTag == "h6" ||
                                childTag == "ul" || childTag == "ol" || childTag == "table")
                            {
                                var els = ConvertHtmlNode(childNode);
                                foreach (var el in els)
                                {
                                    if (el is Paragraph || el is Table)
                                    {
                                        cell.Append(el);
                                        hasContent = true;
                                    }
                                }
                            }
                            else if (childNode.NodeType == HtmlNodeType.Text || 
                                     IsInlineTag(childTag))
                            {
                                if (!hasContent)
                                {
                                    var cellPara = new Paragraph();
                                    AppendInlineContent(cellPara, cellNode);
                                    cell.Append(cellPara);
                                    hasContent = true;
                                    break;
                                }
                            }
                        }
                        
                        if (!hasContent)
                            cell.Append(new Paragraph());

                        row.Append(cell);
                    }
                }

                AppendPendingContinuations();

                foreach (var col in activeRowSpans.Keys.ToList())
                {
                    if (spansStartedThisRow.Contains(col)) continue;
                    var (remaining, span, origin) = activeRowSpans[col];
                    if (remaining <= 1) activeRowSpans.Remove(col);
                    else activeRowSpans[col] = (remaining - 1, span, origin);
                }

                table.Append(row);
            }
        }

        return table;
    }

    private static bool ApplyCellSourceMarkers(TableCellProperties props, HtmlNode cellNode, bool tableHasCellMarMarker)
    {
        var hasTcw = false;
        var tcwMatch = Regex.Match(cellNode.GetAttributeValue("data-tcw", ""), @"^(\d+):(dxa|pct|auto|nil)$");
        if (tcwMatch.Success)
        {
            props.RemoveAllChildren<TableCellWidth>();
            props.Append(new TableCellWidth
            {
                Width = tcwMatch.Groups[1].Value,
                Type = tcwMatch.Groups[2].Value switch
                {
                    "pct" => TableWidthUnitValues.Pct,
                    "auto" => TableWidthUnitValues.Auto,
                    "nil" => TableWidthUnitValues.Nil,
                    _ => TableWidthUnitValues.Dxa
                }
            });
            hasTcw = true;
        }

        var marAttr = cellNode.GetAttributeValue("data-tcmar-tw", "");
        if (!string.IsNullOrEmpty(marAttr))
        {
            var sides = marAttr.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('='))
                .Where(kv => kv.Length == 2 && int.TryParse(kv[1], out _))
                .ToDictionary(kv => kv[0], kv => kv[1]);
            props.RemoveAllChildren<TableCellMargin>();
            var tcMar = new TableCellMargin();
            foreach (var side in new[] { "top", "start", "left", "bottom", "end", "right" })
            {
                if (!sides.TryGetValue(side, out var w)) continue;
                OpenXmlElement el = side switch
                {
                    "top" => new TopMargin { Width = w, Type = TableWidthUnitValues.Dxa },
                    "start" => new StartMargin { Width = w, Type = TableWidthUnitValues.Dxa },
                    "left" => new LeftMargin { Width = w, Type = TableWidthUnitValues.Dxa },
                    "bottom" => new BottomMargin { Width = w, Type = TableWidthUnitValues.Dxa },
                    "end" => new EndMargin { Width = w, Type = TableWidthUnitValues.Dxa },
                    _ => new RightMargin { Width = w, Type = TableWidthUnitValues.Dxa }
                };
                tcMar.Append(el);
            }
            if (tcMar.HasChildren) props.Append(tcMar);
        }
        else if (tableHasCellMarMarker)
        {
            props.RemoveAllChildren<TableCellMargin>();
        }

        if (cellNode.GetAttributeValue("data-hide-mark", "") == "1") props.Append(new HideMark());
        if (cellNode.GetAttributeValue("data-fit-text", "") == "1") props.Append(new TableCellFitText());
        return hasTcw;
    }

    private static void NormalizeTableCellPropertiesOrder(TableCellProperties props)
    {
        if (!props.HasChildren) return;

        static int Rank(OpenXmlElement el) => el switch
        {
            ConditionalFormatStyle => 0,
            TableCellWidth => 1,
            GridSpan => 2,
            HorizontalMerge => 3,
            VerticalMerge => 4,
            TableCellBorders => 5,
            Shading => 6,
            NoWrap => 7,
            TableCellMargin => 8,
            TextDirection => 9,
            TableCellFitText => 10,
            TableCellVerticalAlignment => 11,
            HideMark => 12,
            _ => 13
        };

        var ordered = props.ChildElements.OrderBy(Rank).ToList();
        props.RemoveAllChildren();
        foreach (var child in ordered)
            props.Append(child);
    }

    private static TableCell CreateVerticalMergeContinuationCell(int colSpan, TableCellProperties origin)
    {
        var props = new TableCellProperties();
        if (origin.TableCellWidth is { } w) props.Append((TableCellWidth)w.CloneNode(true));
        if (colSpan > 1)
            props.Append(new GridSpan { Val = colSpan });
        props.Append(new VerticalMerge());
        if (origin.TableCellMargin is { } m) props.Append((TableCellMargin)m.CloneNode(true));
        return new TableCell(props, new Paragraph());
    }

    private static int GridSpacerSpan(HtmlNode? cellNode, string side)
    {
        if (cellNode == null || cellNode.GetAttributeValue("data-grid-spacer", "") != side)
            return 0;
        return Math.Max(1, cellNode.GetAttributeValue("colspan", 1));
    }

    private static List<(int Tw, bool Exact)> ReadColgroupWidthsTwips(HtmlNode tableNode)
    {
        var result = new List<(int Tw, bool Exact)>();
        var cols = tableNode.SelectNodes("./colgroup/col");
        if (cols == null) return result;

        foreach (var col in cols)
        {
            var twAttr = col.GetAttributeValue("data-w-tw", "");
            if (int.TryParse(twAttr, out var exactTw) && exactTw > 0)
            {
                result.Add((exactTw, true));
                continue;
            }
            var style = col.GetAttributeValue("style", "");
            var m = Regex.Match(style, @"width:\s*([\d.]+)px");
            result.Add(m.Success
                ? ((int)Math.Round(OoxmlUnits.PixelsToTwips(
                    double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))), false)
                : (0, false));
        }
        return result;
    }

    private bool IsInlineTag(string tagName) => tagName switch
    {
        "span" or "strong" or "b" or "em" or "i" or "u" or "s" or "strike" or "sub" or "sup" or "a" => true,
        _ => false
    };

    private void ApplyCellStyle(TableCellProperties cellProps, string style)
    {
        if (string.IsNullOrEmpty(style)) return;
        
        var widthMatch = Regex.Match(style, @"(?<![a-z-])width:\s*([\d.]+)(px|%)?");
        if (widthMatch.Success)
        {
            var widthVal = double.Parse(widthMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var widthUnit = widthMatch.Groups[2].Value;

            if (widthUnit == "%")
                cellProps.Append(new TableCellWidth { Width = ((int)Math.Round(widthVal * 50)).ToString(), Type = TableWidthUnitValues.Pct });
            else
                cellProps.Append(new TableCellWidth { Width = ((int)Math.Round(widthVal * 15)).ToString(), Type = TableWidthUnitValues.Dxa });
        }
        else
        {
            cellProps.Append(new TableCellWidth { Type = TableWidthUnitValues.Auto });
        }
        
        var bgColor = ExtractColor(style, @"background(?:-color)?:\s*");
        if (bgColor != null)
        {
            cellProps.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = bgColor });
        }
        
        var vAlignMatch = Regex.Match(style, @"vertical-align:\s*(top|middle|bottom)");
        if (vAlignMatch.Success)
        {
            var vAlign = vAlignMatch.Groups[1].Value switch
            {
                "middle" or "center" => TableVerticalAlignmentValues.Center,
                "bottom" => TableVerticalAlignmentValues.Bottom,
                _ => TableVerticalAlignmentValues.Top
            };
            cellProps.Append(new TableCellVerticalAlignment { Val = vAlign });
        }
        
        var paddingMatch = Regex.Match(style, @"padding:\s*([\d.]+)px(?:\s+([\d.]+)px)?(?:\s+([\d.]+)px)?(?:\s+([\d.]+)px)?");
        if (paddingMatch.Success)
        {
            var top = int.Parse(paddingMatch.Groups[1].Value);
            var right = paddingMatch.Groups[2].Success ? int.Parse(paddingMatch.Groups[2].Value) : top;
            var bottom = paddingMatch.Groups[3].Success ? int.Parse(paddingMatch.Groups[3].Value) : top;
            var left = paddingMatch.Groups[4].Success ? int.Parse(paddingMatch.Groups[4].Value) : right;
            
            cellProps.Append(new TableCellMargin(
                new TopMargin { Width = PxToTwips(top).ToString(), Type = TableWidthUnitValues.Dxa },
                new LeftMargin { Width = PxToTwips(left).ToString(), Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = PxToTwips(bottom).ToString(), Type = TableWidthUnitValues.Dxa },
                new RightMargin { Width = PxToTwips(right).ToString(), Type = TableWidthUnitValues.Dxa }
            ));
        }

        if (style.Contains("white-space:nowrap") || style.Contains("white-space: nowrap"))
        {
            cellProps.Append(new NoWrap());
        }

        if (style.Contains("writing-mode:vertical-rl"))
        {
            cellProps.Append(new TextDirection { Val = TextDirectionValues.TopToBottomRightToLeft });
        }
        else if (style.Contains("writing-mode:vertical-lr"))
        {
            cellProps.Append(new TextDirection { Val = TextDirectionValues.BottomToTopLeftToRight });
        }
    }

    private void ApplyCellBorders(TableCellProperties cellProps, string style)
    {
        if (string.IsNullOrEmpty(style)) return;

        var sides = ResolveCssBorderSides(style);
        if (sides == null) return;

        var borders = new TableCellBorders();
        AppendCellBorderSide(borders, sides[0], () => new TopBorder());
        AppendCellBorderSide(borders, sides[3], () => new LeftBorder());
        AppendCellBorderSide(borders, sides[2], () => new BottomBorder());
        AppendCellBorderSide(borders, sides[1], () => new RightBorder());
        if (borders.HasChildren)
            cellProps.Append(borders);
    }

    private static void AppendCellBorderSide(TableCellBorders borders, CssBorderSide? side, Func<BorderType> create)
    {
        if (side is not { } s) return;
        var border = create();
        if (s.Val == BorderValues.None)
        {
            border.Val = BorderValues.Nil;
        }
        else
        {
            border.Val = s.Val;
            border.Size = s.Size;
            if (s.Color != null) border.Color = s.Color;
        }
        borders.Append(border);
    }

    private readonly record struct CssBorderSide(BorderValues Val, uint Size, string? Color);

    private CssBorderSide?[]? ResolveCssBorderSides(string style)
    {
        var sides = new CssBorderSide?[4];

        if (TryParseBorderSideValue(GetCssDeclarationValue(style, "border"), out var uniform))
        {
            for (var i = 0; i < 4; i++) sides[i] = uniform;
        }

        var styleTokens = ExpandCssBoxValues(GetCssDeclarationValue(style, "border-style"));
        if (styleTokens != null)
        {
            var widthTokens = ExpandCssBoxValues(GetCssDeclarationValue(style, "border-width"));
            var colorTokens = ExpandCssBoxValues(GetCssDeclarationValue(style, "border-color"));
            for (var i = 0; i < 4; i++)
                sides[i] = BuildCssBorderSide(styleTokens[i], widthTokens?[i], colorTokens?[i]) ?? sides[i];
        }

        string[] prefixes = ["border-top", "border-right", "border-bottom", "border-left"];
        for (var i = 0; i < 4; i++)
        {
            if (TryParseBorderSideValue(GetCssDeclarationValue(style, prefixes[i]), out var side))
                sides[i] = side;
        }

        return sides.Any(s => s != null) ? sides : null;
    }

    private bool TryParseBorderSideValue(string? value, out CssBorderSide side)
    {
        side = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (Regex.IsMatch(value, @"\b(none|hidden)\b", RegexOptions.IgnoreCase))
        {
            side = new CssBorderSide(BorderValues.None, 0, null);
            return true;
        }

        var m = Regex.Match(value, @"([\d.]+)px\s+(\w+)(?:\s+(.+))?");
        if (!m.Success) return false;

        var val = ParseBorderStyle(m.Groups[2].Value);
        var px = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (px <= 0 || IsInvisibleCssColor(m.Groups[3].Value))
        {
            side = new CssBorderSide(BorderValues.None, 0, null);
            return true;
        }

        side = new CssBorderSide(val, CssBorderWidthToEighthPoints(m.Groups[1].Value, val),
            NormalizeCssColorToken(m.Groups[3].Value) ?? "auto");
        return true;
    }

    private CssBorderSide? BuildCssBorderSide(string styleToken, string? widthToken, string? colorToken)
    {
        if (styleToken.Equals("none", StringComparison.OrdinalIgnoreCase)
            || styleToken.Equals("hidden", StringComparison.OrdinalIgnoreCase))
            return new CssBorderSide(BorderValues.None, 0, null);

        var val = ParseBorderStyle(styleToken);
        var widthMatch = widthToken != null ? Regex.Match(widthToken, @"([\d.]+)px") : Match.Empty;
        if (widthMatch.Success
            && double.Parse(widthMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) <= 0)
            return new CssBorderSide(BorderValues.None, 0, null);
        if (IsInvisibleCssColor(colorToken))
            return new CssBorderSide(BorderValues.None, 0, null);

        var size = widthMatch.Success ? CssBorderWidthToEighthPoints(widthMatch.Groups[1].Value, val) : 6u;
        return new CssBorderSide(val, size, NormalizeCssColorToken(colorToken) ?? "auto");
    }

    private static bool IsInvisibleCssColor(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();
        if (token.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return true;
        return Regex.IsMatch(token, @"^rgba\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*0(\.0+)?\s*\)$");
    }

    private static string[]? ExpandCssBoxValues(string? decl)
    {
        if (string.IsNullOrWhiteSpace(decl)) return null;
        var tokens = Regex.Matches(decl, @"rgba?\([^)]*\)|\S+").Select(m => m.Value).ToList();
        return tokens.Count switch
        {
            1 => [tokens[0], tokens[0], tokens[0], tokens[0]],
            2 => [tokens[0], tokens[1], tokens[0], tokens[1]],
            3 => [tokens[0], tokens[1], tokens[2], tokens[1]],
            4 => [tokens[0], tokens[1], tokens[2], tokens[3]],
            _ => null
        };
    }

    private static string? GetCssDeclarationValue(string style, string property)
    {
        var match = Regex.Match(style, $@"(?<![a-z-]){Regex.Escape(property)}\s*:\s*([^;]+)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private string? NormalizeCssColorToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        token = token.Trim();
        if (token.StartsWith("#"))
        {
            var hex = token.TrimStart('#');
            return Regex.IsMatch(hex, "^(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$") ? NormalizeColor(hex) : null;
        }
        var rgb = Regex.Match(token, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
        if (rgb.Success)
            return $"{int.Parse(rgb.Groups[1].Value):X2}{int.Parse(rgb.Groups[2].Value):X2}{int.Parse(rgb.Groups[3].Value):X2}";
        return null;
    }

    private static uint CssPxToBorderEighthPoints(string px)
    {
        var v = double.Parse(px, System.Globalization.CultureInfo.InvariantCulture);
        return (uint)Math.Max(2, Math.Round(v * 6));
    }

    private static uint CssBorderWidthToEighthPoints(string px, BorderValues style)
    {
        var v = double.Parse(px, System.Globalization.CultureInfo.InvariantCulture);
        if (style == BorderValues.Double) v /= 3.0;
        return (uint)Math.Max(2, Math.Round(v * 6));
    }

    private BorderValues ParseBorderStyle(string cssStyle) => cssStyle.ToLower() switch
    {
        "solid" => BorderValues.Single,
        "double" => BorderValues.Double,
        "dotted" => BorderValues.Dotted,
        "dashed" => BorderValues.Dashed,
        "none" => BorderValues.None,
        _ => BorderValues.Single
    };

    private string NormalizeColor(string color)
    {
        if (color.Length == 3)
            return $"{color[0]}{color[0]}{color[1]}{color[1]}{color[2]}{color[2]}";
        return color;
    }

    private string? ExtractColor(string style, string prefix)
    {
        var hexMatch = Regex.Match(style, $@"{prefix}#?([a-fA-F0-9]{{3,6}})");
        if (hexMatch.Success)
            return NormalizeColor(hexMatch.Groups[1].Value);
        
        var rgbMatch = Regex.Match(style, $@"{prefix}rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)");
        if (rgbMatch.Success)
        {
            var r = int.Parse(rgbMatch.Groups[1].Value);
            var g = int.Parse(rgbMatch.Groups[2].Value);
            var b = int.Parse(rgbMatch.Groups[3].Value);
            return $"{r:X2}{g:X2}{b:X2}";
        }
        
        var rgbaMatch = Regex.Match(style, $@"{prefix}rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*[\d.]+\s*\)");
        if (rgbaMatch.Success)
        {
            var r = int.Parse(rgbaMatch.Groups[1].Value);
            var g = int.Parse(rgbaMatch.Groups[2].Value);
            var b = int.Parse(rgbaMatch.Groups[3].Value);
            return $"{r:X2}{g:X2}{b:X2}";
        }
        
        return null;
    }

    private static string ResolveImageSrc(HtmlNode node)
    {
        var original = node.GetAttributeValue("data-original-src", "");
        if (!string.IsNullOrEmpty(original) && original.StartsWith("data:")) return original;
        return node.GetAttributeValue("src", "");
    }

    private Paragraph? ConvertImageElement(HtmlNode node)
    {
        var src = ResolveImageSrc(node);
        if (string.IsNullOrEmpty(src)) return null;

        if (!src.StartsWith("data:")) return null;

        var match = Regex.Match(src, @"data:([^;]+);base64,(.+)");
        if (!match.Success) return null;
        
        var contentType = match.Groups[1].Value;
        var base64 = match.Groups[2].Value;
        
        try
        {
            var imageBytes = System.Convert.FromBase64String(base64);
            return CreateImageParagraph(imageBytes, contentType, node);
        }
        catch
        {
            return null;
        }
    }

    private Paragraph CreateImageParagraph(byte[] imageBytes, string contentType, HtmlNode node)
    {
        var drawing = BuildImageDrawing(imageBytes, contentType, node);
        return drawing != null ? new Paragraph(new Run(drawing)) : new Paragraph();
    }

    private Run? CreateImageRun(HtmlNode node)
    {
        var src = ResolveImageSrc(node);
        if (string.IsNullOrEmpty(src) || !src.StartsWith("data:")) return null;
        var m = Regex.Match(src, @"data:([^;]+);base64,(.+)");
        if (!m.Success) return null;
        try
        {
            var bytes = System.Convert.FromBase64String(m.Groups[2].Value);
            var drawing = BuildImageDrawing(bytes, m.Groups[1].Value, node);
            return drawing != null ? new Run(drawing) : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? SniffImageContentType(byte[] b)
    {
        if (b.Length < 8) return null;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return "image/gif";
        if (b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
        if ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A && b[3] == 0x00) || (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x00 && b[3] == 0x2A)) return "image/tiff";
        if (b[0] == 0x01 && b[1] == 0x00 && b[2] == 0x00 && b[3] == 0x00 && b.Length > 44 && b[40] == 0x20 && b[41] == 0x45 && b[42] == 0x4D && b[43] == 0x46) return "image/x-emf";
        if (b[0] == 0xD7 && b[1] == 0xCD && b[2] == 0xC6 && b[3] == 0x9A) return "image/x-wmf";
        return null;
    }

    private Drawing? BuildImageDrawing(byte[] imageBytes, string contentType, HtmlNode node)
    {
        var container = _currentImageContainer ?? (OpenXmlPart?)_mainPart;
        if (container == null) return null;

        if (contentType == "image/svg+xml")
            return null;

        contentType = SniffImageContentType(imageBytes) ?? contentType;
        PartTypeInfo? knownType = contentType switch
        {
            "image/png" => ImagePartType.Png,
            "image/jpeg" or "image/jpg" => ImagePartType.Jpeg,
            "image/gif" => ImagePartType.Gif,
            "image/bmp" => ImagePartType.Bmp,
            "image/tiff" or "image/tif" => ImagePartType.Tiff,
            "image/x-icon" or "image/vnd.microsoft.icon" => ImagePartType.Icon,
            "image/x-emf" or "image/emf" => ImagePartType.Emf,
            "image/x-wmf" or "image/wmf" => ImagePartType.Wmf,
            _ => (PartTypeInfo?)null
        };
        var isPlainImageMime = Regex.IsMatch(contentType, @"^image/[\w.+-]+$");

        ImagePart imagePart = container switch
        {
            MainDocumentPart m => knownType is { } t1 ? m.AddImagePart(t1) : m.AddImagePart(isPlainImageMime ? contentType : "image/jpeg"),
            HeaderPart h => knownType is { } t2 ? h.AddImagePart(t2) : h.AddImagePart(isPlainImageMime ? contentType : "image/jpeg"),
            FooterPart f => knownType is { } t3 ? f.AddImagePart(t3) : f.AddImagePart(isPlainImageMime ? contentType : "image/jpeg"),
            _ => knownType is { } t4 ? _mainPart!.AddImagePart(t4) : _mainPart!.AddImagePart(isPlainImageMime ? contentType : "image/jpeg")
        };

        using (var stream = new MemoryStream(imageBytes))
        {
            imagePart.FeedData(stream);
        }

        var relationshipId = container.GetIdOfPart(imagePart);

        var altText = HtmlEntity.DeEntitize(node.GetAttributeValue("alt", "")) ?? string.Empty;

        long widthEmu, heightEmu;

        var emuWidthAttr = node.GetAttributeValue("data-width-emu", "");
        var emuHeightAttr = node.GetAttributeValue("data-height-emu", "");

        if (!string.IsNullOrEmpty(emuWidthAttr) && !string.IsNullOrEmpty(emuHeightAttr) &&
            long.TryParse(emuWidthAttr, out var origWidthEmu) && long.TryParse(emuHeightAttr, out var origHeightEmu))
        {
            widthEmu = origWidthEmu;
            heightEmu = origHeightEmu;
        }
        else
        {
            var style = node.GetAttributeValue("style", "");
            var widthMatch = Regex.Match(style, @"width:\s*([\d.]+)px");
            var heightMatch = Regex.Match(style, @"height:\s*([\d.]+)px");

            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var width = widthMatch.Success ? double.Parse(widthMatch.Groups[1].Value, ci) : 200;
            var height = heightMatch.Success ? double.Parse(heightMatch.Groups[1].Value, ci) : width * 0.75;

            if (!widthMatch.Success)
            {
                var wAttr = node.GetAttributeValue("width", "");
                if (!string.IsNullOrEmpty(wAttr) && double.TryParse(wAttr, System.Globalization.NumberStyles.Float, ci, out var wp))
                    width = wp;
            }
            if (!heightMatch.Success)
            {
                var hAttr = node.GetAttributeValue("height", "");
                if (!string.IsNullOrEmpty(hAttr) && double.TryParse(hAttr, System.Globalization.NumberStyles.Float, ci, out var hp))
                    height = hp;
            }

            widthEmu = OoxmlUnits.PixelsToEmu(width);
            heightEmu = OoxmlUnits.PixelsToEmu(height);
        }

        var maxWidthEmu = _inHeaderFooter ? 6_120_000L : 5_400_000L;
        if (widthEmu > maxWidthEmu)
        {
            var scale = (double)maxWidthEmu / widthEmu;
            widthEmu = maxWidthEmu;
            heightEmu = (long)(heightEmu * scale);
        }
        if (widthEmu < OoxmlUnits.EmuPerPixel) widthEmu = OoxmlUnits.EmuPerPixel;
        if (heightEmu < OoxmlUnits.EmuPerPixel) heightEmu = OoxmlUnits.EmuPerPixel;

        _imageCounter++;

        var posMode = node.GetAttributeValue("data-pos-mode", "");
        var isFloating = posMode == "front" || posMode == "behind";

        int.TryParse(node.GetAttributeValue("data-border-width", "0"), out var borderWidthPx);
        var borderColor = node.GetAttributeValue("data-border-color", "").TrimStart('#');
        var borderStyle = node.GetAttributeValue("data-border-style", "solid");

        int.TryParse(node.GetAttributeValue("data-crop-l", "0"), out var cropL);
        int.TryParse(node.GetAttributeValue("data-crop-r", "0"), out var cropR);
        int.TryParse(node.GetAttributeValue("data-crop-t", "0"), out var cropT);
        int.TryParse(node.GetAttributeValue("data-crop-b", "0"), out var cropB);
        var hasCrop = cropL > 0 || cropR > 0 || cropT > 0 || cropB > 0;

        var blip = new DocumentFormat.OpenXml.Drawing.Blip { Embed = relationshipId };
        var blipFill = new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(blip);
        if (hasCrop)
        {
            blipFill.Append(new DocumentFormat.OpenXml.Drawing.SourceRectangle
            {
                Left = cropL * 1000,
                Right = cropR * 1000,
                Top = cropT * 1000,
                Bottom = cropB * 1000
            });
        }
        blipFill.Append(new DocumentFormat.OpenXml.Drawing.Stretch(new DocumentFormat.OpenXml.Drawing.FillRectangle()));

        var shapeProps = new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(
            new DocumentFormat.OpenXml.Drawing.Transform2D(
                new DocumentFormat.OpenXml.Drawing.Offset { X = 0, Y = 0 },
                new DocumentFormat.OpenXml.Drawing.Extents { Cx = widthEmu, Cy = heightEmu }),
            new DocumentFormat.OpenXml.Drawing.PresetGeometry(
                new DocumentFormat.OpenXml.Drawing.AdjustValueList())
            { Preset = DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle });
        if (borderWidthPx > 0 && System.Text.RegularExpressions.Regex.IsMatch(borderColor, "^[0-9A-Fa-f]{6}$"))
        {
            var dashStyle = borderStyle switch
            {
                "dashed" => DocumentFormat.OpenXml.Drawing.PresetLineDashValues.Dash,
                "dotted" => DocumentFormat.OpenXml.Drawing.PresetLineDashValues.Dot,
                _ => DocumentFormat.OpenXml.Drawing.PresetLineDashValues.Solid
            };
            shapeProps.Append(new DocumentFormat.OpenXml.Drawing.Outline(
                new DocumentFormat.OpenXml.Drawing.SolidFill(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = borderColor.ToUpperInvariant() }),
                new DocumentFormat.OpenXml.Drawing.PresetDash { Val = dashStyle })
            { Width = borderWidthPx * OoxmlUnits.EmuPerPixel });
        }

        var graphic = new DocumentFormat.OpenXml.Drawing.Graphic(
            new DocumentFormat.OpenXml.Drawing.GraphicData(
                new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                    new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                        new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = (uint)_imageCounter, Name = $"Image{_imageCounter}" },
                        new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
                    blipFill,
                    shapeProps)
            )
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" });

        if (!isFloating)
        {
            return new Drawing(
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
                    new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = widthEmu, Cy = heightEmu },
                    new DocumentFormat.OpenXml.Drawing.Wordprocessing.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                    BuildImageDocProperties((uint)_imageCounter, $"Image{_imageCounter}", altText),
                    new DocumentFormat.OpenXml.Drawing.Wordprocessing.NonVisualGraphicFrameDrawingProperties(
                        new DocumentFormat.OpenXml.Drawing.GraphicFrameLocks { NoChangeAspect = true }),
                    graphic
                )
            );
        }

        long.TryParse(node.GetAttributeValue("data-x-emu", "0"), out var xEmu);
        long.TryParse(node.GetAttributeValue("data-y-emu", "0"), out var yEmu);
        var behind = posMode == "behind";

        var anchor = new DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.SimplePosition { X = 0L, Y = 0L },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.HorizontalPosition(
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.PositionOffset(xEmu.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            { RelativeFrom = DocumentFormat.OpenXml.Drawing.Wordprocessing.HorizontalRelativePositionValues.Page },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalPosition(
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.PositionOffset(yEmu.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            { RelativeFrom = DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalRelativePositionValues.Margin },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = widthEmu, Cy = heightEmu },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            BuildAnchorWrapElement(node),
            BuildImageDocProperties((uint)_imageCounter, $"Image{_imageCounter}", altText),
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.NonVisualGraphicFrameDrawingProperties(
                new DocumentFormat.OpenXml.Drawing.GraphicFrameLocks { NoChangeAspect = true }),
            graphic)
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U,
            SimplePos = false,
            RelativeHeight = (uint)(251_660_288 + _imageCounter),
            BehindDoc = behind,
            Locked = false,
            LayoutInCell = true,
            AllowOverlap = true
        };

        return new Drawing(anchor);
    }

    private readonly List<Drawing> _pendingTextBoxDrawings = new();

    private static bool IsTextBoxNode(HtmlNode node)
        => node.NodeType == HtmlNodeType.Element
           && node.Name.Equals("div", StringComparison.OrdinalIgnoreCase)
           && (node.HasClass("docx-textbox") || node.GetAttributeValue("data-textbox", "") == "1");

    private void BufferTextBoxDrawing(HtmlNode node)
    {
        var drawing = BuildTextBoxDrawing(node);
        if (drawing != null) _pendingTextBoxDrawings.Add(drawing);
    }

    private void AttachPendingTextBoxes(Paragraph paragraph)
    {
        if (_pendingTextBoxDrawings.Count == 0) return;
        foreach (var drawing in _pendingTextBoxDrawings)
            paragraph.Append(new Run(drawing));
        _pendingTextBoxDrawings.Clear();
    }

    private void FlushPendingTextBoxesInto(OpenXmlElement parent)
    {
        if (_pendingTextBoxDrawings.Count == 0) return;
        var paragraph = new Paragraph();
        AttachPendingTextBoxes(paragraph);
        parent.Append(paragraph);
    }

    private Run? BuildTextBoxRun(HtmlNode node)
    {
        var drawing = BuildTextBoxDrawing(node);
        return drawing != null ? new Run(drawing) : null;
    }

    private Drawing? BuildTextBoxDrawing(HtmlNode node)
    {
        var content = BuildTextBoxContent(node);
        if (content == null) return null;

        var style = node.GetAttributeValue("style", "");
        var widthEmu = ParseLongAttribute(node, "data-width-emu")
            ?? (CssPxValue(style, "width") ?? 200) * OoxmlUnits.EmuPerPixel;
        var heightEmu = ParseLongAttribute(node, "data-height-emu")
            ?? (CssPxValue(style, "min-height") ?? CssPxValue(style, "height") ?? 50) * OoxmlUnits.EmuPerPixel;
        if (widthEmu <= 0) widthEmu = 200 * OoxmlUnits.EmuPerPixel;
        if (heightEmu <= 0) heightEmu = 50 * OoxmlUnits.EmuPerPixel;

        _imageCounter++;
        var docPrName = $"TextBox{_imageCounter}";

        var spPr = new Wps.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = 0, Y = 0 },
                new A.Extents { Cx = widthEmu, Cy = heightEmu }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });

        int.TryParse(node.GetAttributeValue("data-border-width", "0"), out var borderWidthPx);
        var borderColor = node.GetAttributeValue("data-border-color", "").TrimStart('#');
        if (borderWidthPx > 0 && Regex.IsMatch(borderColor, "^[0-9A-Fa-f]{6}$"))
        {
            var dashStyle = node.GetAttributeValue("data-border-style", "solid") switch
            {
                "dashed" => A.PresetLineDashValues.Dash,
                "dotted" => A.PresetLineDashValues.Dot,
                _ => A.PresetLineDashValues.Solid
            };
            spPr.Append(new A.Outline(
                new A.SolidFill(new A.RgbColorModelHex { Val = borderColor.ToUpperInvariant() }),
                new A.PresetDash { Val = dashStyle })
            { Width = borderWidthPx * OoxmlUnits.EmuPerPixel });
        }

        var wsp = new Wps.WordprocessingShape(
            new Wps.NonVisualDrawingShapeProperties { TextBox = true },
            spPr,
            new Wps.TextBoxInfo2(content),
            BuildTextBodyProperties(node));

        var graphic = new A.Graphic(new A.GraphicData(wsp)
        { Uri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape" });

        static Wps.TextBodyProperties BuildTextBodyProperties(HtmlNode textBoxNode)
        {
            var bodyPr = new Wps.TextBodyProperties();
            var insets = textBoxNode.GetAttributeValue("data-tb-ins", "").Split(' ');
            if (insets.Length == 4
                && int.TryParse(insets[0], out var lIns) && int.TryParse(insets[1], out var tIns)
                && int.TryParse(insets[2], out var rIns) && int.TryParse(insets[3], out var bIns))
            {
                bodyPr.LeftInset = lIns;
                bodyPr.TopInset = tIns;
                bodyPr.RightInset = rIns;
                bodyPr.BottomInset = bIns;
            }
            var anchorToken = textBoxNode.GetAttributeValue("data-tb-anchor", "");
            if (anchorToken == "ctr") bodyPr.Anchor = A.TextAnchoringTypeValues.Center;
            else if (anchorToken == "b") bodyPr.Anchor = A.TextAnchoringTypeValues.Bottom;
            return bodyPr;
        }

        var posMode = node.GetAttributeValue("data-pos-mode", "");
        var isFloating = posMode == "front" || posMode == "behind"
            || style.Contains("position:absolute", StringComparison.OrdinalIgnoreCase);

        if (!isFloating)
        {
            return new Drawing(new Wp.Inline(
                new Wp.Extent { Cx = widthEmu, Cy = heightEmu },
                new Wp.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                BuildImageDocProperties((uint)_imageCounter, docPrName, null),
                new Wp.NonVisualGraphicFrameDrawingProperties(),
                graphic));
        }

        var xEmu = ParseLongAttribute(node, "data-x-emu")
            ?? (CssPxValue(style, "left") ?? 0) * OoxmlUnits.EmuPerPixel;
        var yEmu = ParseLongAttribute(node, "data-y-emu")
            ?? (CssPxValue(style, "top") ?? 0) * OoxmlUnits.EmuPerPixel;
        var behind = posMode == "behind";

        var anchor = new Wp.Anchor(
            new Wp.SimplePosition { X = 0L, Y = 0L },
            new Wp.HorizontalPosition(
                new Wp.PositionOffset(xEmu.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            { RelativeFrom = Wp.HorizontalRelativePositionValues.Page },
            new Wp.VerticalPosition(
                new Wp.PositionOffset(yEmu.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            { RelativeFrom = Wp.VerticalRelativePositionValues.Margin },
            new Wp.Extent { Cx = widthEmu, Cy = heightEmu },
            new Wp.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            BuildAnchorWrapElement(node),
            BuildImageDocProperties((uint)_imageCounter, docPrName, null),
            new Wp.NonVisualGraphicFrameDrawingProperties(),
            graphic)
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U,
            SimplePos = false,
            RelativeHeight = (uint)(251_660_288 + _imageCounter),
            BehindDoc = behind,
            Locked = false,
            LayoutInCell = true,
            AllowOverlap = true
        };

        return new Drawing(anchor);
    }

    private TextBoxContent? BuildTextBoxContent(HtmlNode node)
    {
        var saved = _pendingTextBoxDrawings.ToList();
        _pendingTextBoxDrawings.Clear();
        var content = new TextBoxContent();
        try
        {
            foreach (var child in node.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Text)
                {
                    var text = System.Net.WebUtility.HtmlDecode(child.InnerText);
                    if (!string.IsNullOrWhiteSpace(text)) content.Append(CreateParagraph(text));
                    continue;
                }
                if (child.NodeType != HtmlNodeType.Element) continue;

                switch (child.Name.ToLower())
                {
                    case "p":
                        content.Append(ConvertParagraphElement(child));
                        break;
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                        content.Append(ConvertHeadingElement(child, int.Parse(child.Name[1..])));
                        break;
                    case "table":
                        content.Append(ConvertTableElement(child));
                        break;
                    case "ul":
                    case "ol":
                        foreach (var el in ConvertListElement(child, child.Name.ToLower() == "ol"))
                            content.Append(el);
                        break;
                    case "div" when IsTextBoxNode(child):
                    {
                        var run = BuildTextBoxRun(child);
                        if (run != null) content.Append(new Paragraph(run));
                        break;
                    }
                    default:
                    {
                        var para = new Paragraph();
                        AppendInlineContent(para, child);
                        content.Append(para);
                        break;
                    }
                }
            }

            if (_pendingTextBoxDrawings.Count > 0)
            {
                var trailing = new Paragraph();
                AttachPendingTextBoxes(trailing);
                content.Append(trailing);
            }
        }
        finally
        {
            _pendingTextBoxDrawings.Clear();
            _pendingTextBoxDrawings.AddRange(saved);
        }

        if (!content.HasChildren) return null;
        if (!content.Elements<Paragraph>().Any()) content.Append(new Paragraph());
        return content;
    }

    private static OpenXmlElement BuildAnchorWrapElement(HtmlNode node)
        => node.GetAttributeValue("data-wrap", "") switch
        {
            "square" or "tight" or "through" => new Wp.WrapSquare { WrapText = Wp.WrapTextValues.BothSides },
            "topAndBottom" => new Wp.WrapTopBottom(),
            _ => new Wp.WrapNone(),
        };

    private static long? ParseLongAttribute(HtmlNode node, string attribute)
        => long.TryParse(node.GetAttributeValue(attribute, ""), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? CssPxValue(string style, string property)
    {
        if (string.IsNullOrEmpty(style)) return null;
        var match = Regex.Match(style,
            $@"(?:^|;)\s*{Regex.Escape(property)}\s*:\s*(-?\d+(?:\.\d+)?)px",
            RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups[1].Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (long)Math.Round(v)
            : null;
    }

    private Paragraph ConvertAnchorElement(HtmlNode node)
    {
        var para = new Paragraph();
        var href = node.GetAttributeValue("href", "#");
        var internalAnchor = node.GetAttributeValue("data-anchor", "");
        if (internalAnchor.Length == 0 && href.StartsWith('#') && href.Length > 1)
            internalAnchor = href.TrimStart('#');

        try
        {
            var hyperlink = internalAnchor.Length > 0
                ? new Hyperlink { Anchor = System.Net.WebUtility.HtmlDecode(internalAnchor) }
                : new Hyperlink { Id = _mainPart!.AddHyperlinkRelationship(new Uri(href, UriKind.RelativeOrAbsolute), true).Id };

            foreach (var child in node.ChildNodes)
            {
                var runs = CreateRunsFromNode(child);
                foreach (var run in runs)
                {
                    if (internalAnchor.Length == 0)
                    {
                        run.RunProperties ??= new RunProperties();
                        if (!run.RunProperties.Elements<Color>().Any())
                            run.RunProperties.Append(new Color { Val = "0563C1" });
                        if (!run.RunProperties.Elements<Underline>().Any())
                            run.RunProperties.Append(new Underline { Val = UnderlineValues.Single });
                        run.RunProperties.Append(new RunStyle { Val = "Hyperlink" });
                    }
                    hyperlink.Append(run);
                }
            }

            para.Append(hyperlink);
        }
        catch
        {
            AppendInlineContent(para, node);
        }

        return para;
    }

    private Paragraph ConvertInlineElement(HtmlNode node)
    {
        var para = new Paragraph();
        AppendInlineContent(para, node);
        return para;
    }

    private Paragraph ConvertBlockquoteElement(HtmlNode node)
    {
        var para = new Paragraph();
        var props = new ParagraphProperties();
        props.Append(new Indentation { Left = "720" });
        props.Append(new ParagraphBorders(
            new LeftBorder { Val = BorderValues.Single, Size = 24, Color = "CCCCCC", Space = 4 }
        ));
        para.Append(props);
        AppendInlineContent(para, node);
        return para;
    }

    private static Run CreateVmlHorizontalRuleRun(HtmlNode node)
    {
        const string ovml = "urn:schemas-microsoft-com:office:office";
        var heightPt = node.GetAttributeValue("data-hr-height-pt", "");
        var rect = new V.Rectangle
        {
            Style = string.IsNullOrEmpty(heightPt) ? "width:0;height:0" : $"width:0;height:{heightPt}pt",
            Stroked = false,
        };
        var fill = node.GetAttributeValue("data-hr-fill", "");
        if (!string.IsNullOrEmpty(fill)) rect.FillColor = fill;
        rect.AddNamespaceDeclaration("o", ovml);
        void SetOfficeAttr(string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
                rect.SetAttribute(new OpenXmlAttribute("o", name, ovml, value));
        }
        SetOfficeAttr("hralign", node.GetAttributeValue("data-hr-align", ""));
        SetOfficeAttr("hrpct", node.GetAttributeValue("data-hr-pct", ""));
        SetOfficeAttr("hrnoshade", node.GetAttributeValue("data-hr-noshade", ""));
        SetOfficeAttr("hrstd", node.GetAttributeValue("data-hr-std", ""));
        SetOfficeAttr("hr", "t");
        return new Run(new Picture(rect));
    }

    private Paragraph CreateHorizontalRule()
    {
        var para = new Paragraph();
        var props = new ParagraphProperties();
        props.Append(new ParagraphBorders(
            new BottomBorder { Val = BorderValues.Single, Size = 12, Color = "000000", Space = 1 }
        ));
        props.Append(new SpacingBetweenLines { Before = "120", After = "120" });
        para.Append(props);
        return para;
    }

    private void AppendInlineContent(Paragraph paragraph, HtmlNode node)
    {
        RunProperties? baseRunProps = null;
        var parentStyle = node.GetAttributeValue("style", "");
        if (!string.IsNullOrEmpty(parentStyle))
        {
            baseRunProps = new RunProperties();
            ApplyRunStyle(baseRunProps, parentStyle);
            if (!baseRunProps.HasChildren)
                baseRunProps = null;
        }

        AppendInlineChildren(paragraph, node.ChildNodes, baseRunProps);

        if (!paragraph.Elements<Run>().Any() && !paragraph.Elements<Hyperlink>().Any())
        {
            paragraph.Append(new Run(new Text("") { Space = SpaceProcessingModeValues.Preserve }));
        }
    }

    private void AppendInlineChildren(Paragraph paragraph, IEnumerable<HtmlNode> children, RunProperties? baseRunProps)
    {
        foreach (var child in children)
        {
            if (child.NodeType == HtmlNodeType.Element && IsTextBoxNode(child))
            {
                var tbRun = BuildTextBoxRun(child);
                if (tbRun != null) paragraph.Append(tbRun);
                continue;
            }

            if (child.NodeType == HtmlNodeType.Element
                && child.Name.Equals("span", StringComparison.OrdinalIgnoreCase)
                && child.HasClass("sdt-inline"))
            {
                var sdtRun = BuildSdtRunFromHtml(child, baseRunProps);
                if (sdtRun != null)
                    paragraph.Append(sdtRun);
                continue;
            }

            if (child.NodeType == HtmlNodeType.Element
                && child.Name.Equals("span", StringComparison.OrdinalIgnoreCase))
            {
                if (child.HasClass("field-page") || child.HasClass("page-number"))
                {
                    paragraph.Append(BuildFieldRun(" PAGE ", child));
                    continue;
                }
                if (child.HasClass("field-numpages"))
                {
                    paragraph.Append(BuildFieldRun(" NUMPAGES ", child));
                    continue;
                }
                if (child.HasClass("field-date"))
                {
                    paragraph.Append(BuildDateFieldRun(child));
                    continue;
                }
            }

            if (child.NodeType == HtmlNodeType.Element
                && child.Name.Equals("span", StringComparison.OrdinalIgnoreCase))
            {
                if (child.HasClass("docx-tab-text"))
                {
                    AppendInlineChildren(paragraph, child.ChildNodes, MergeRunProps(baseRunProps, child));
                    continue;
                }

                if (child.HasClass("docx-bookmark"))
                {
                    var bmName = System.Net.WebUtility.HtmlDecode(child.GetAttributeValue("data-bm-name", ""));
                    if (!string.IsNullOrEmpty(bmName))
                    {
                        var bmId = (_nextBookmarkId++).ToString();
                        paragraph.Append(new BookmarkStart { Name = bmName, Id = bmId });
                        paragraph.Append(new BookmarkEnd { Id = bmId });
                    }
                    continue;
                }
            }

            if (child.NodeType == HtmlNodeType.Element
                && child.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                var hyperlink = BuildHyperlinkElement(child, baseRunProps);
                if (hyperlink != null)
                {
                    paragraph.Append(hyperlink);
                    continue;
                }
            }

            var runs = CreateRunsFromNode(child, baseRunProps);
            foreach (var run in runs)
            {
                paragraph.Append(run);
            }
        }
    }

    private RunProperties? MergeRunProps(RunProperties? baseRunProps, HtmlNode node)
    {
        var style = node.GetAttributeValue("style", "");
        if (string.IsNullOrEmpty(style)) return baseRunProps;
        var merged = (baseRunProps?.CloneNode(true) as RunProperties) ?? new RunProperties();
        ApplyRunStyle(merged, style);
        return merged.HasChildren ? merged : baseRunProps;
    }

    private Hyperlink? BuildHyperlinkElement(HtmlNode node, RunProperties? inheritedProps)
    {
        var href = node.GetAttributeValue("href", "");
        var anchor = System.Net.WebUtility.HtmlDecode(node.GetAttributeValue("data-anchor", ""));
        if (string.IsNullOrEmpty(anchor) && href.StartsWith('#') && href.Length > 1)
            anchor = System.Net.WebUtility.HtmlDecode(href[1..]);

        Hyperlink hyperlink;
        var isInternal = !string.IsNullOrEmpty(anchor);
        if (isInternal)
        {
            hyperlink = new Hyperlink { Anchor = anchor, History = true };
        }
        else
        {
            if (string.IsNullOrEmpty(href) || href == "#") return null;
            try
            {
                var relId = _mainPart!.AddHyperlinkRelationship(new Uri(href, UriKind.RelativeOrAbsolute), true).Id;
                hyperlink = new Hyperlink { Id = relId, History = true };
            }
            catch
            {
                return null;
            }
        }

        foreach (var child in node.ChildNodes)
        {
            foreach (var run in CreateRunsFromNode(child, inheritedProps))
            {
                if (!isInternal)
                {
                    run.RunProperties ??= new RunProperties();
                    if (!run.RunProperties.Elements<Color>().Any())
                        run.RunProperties.Append(new Color { Val = "0563C1" });
                    if (!run.RunProperties.Elements<Underline>().Any())
                        run.RunProperties.Append(new Underline { Val = UnderlineValues.Single });
                    if (!run.RunProperties.Elements<RunStyle>().Any())
                        run.RunProperties.Append(new RunStyle { Val = "Hyperlink" });
                }
                hyperlink.Append(run);
            }
        }

        return hyperlink;
    }

    private List<Run> CreateRunsFromNode(HtmlNode node, RunProperties? inheritedProps = null)
    {
        var runs = new List<Run>();

        switch (node.NodeType)
        {
            case HtmlNodeType.Text:
                var text = System.Net.WebUtility.HtmlDecode(node.InnerText);
                if (!string.IsNullOrEmpty(text))
                {
                    var parts = text.Split('\t');
                    for (int pi = 0; pi < parts.Length; pi++)
                    {
                        if (pi > 0)
                        {
                            var tabRun = new Run();
                            if (inheritedProps != null)
                                tabRun.Append(inheritedProps.CloneNode(true));
                            tabRun.Append(new TabChar());
                            runs.Add(tabRun);
                        }
                        if (parts[pi].Length == 0) continue;
                        var run = new Run();
                        if (inheritedProps != null)
                            run.Append(inheritedProps.CloneNode(true));
                        run.Append(new Text(parts[pi]) { Space = SpaceProcessingModeValues.Preserve });
                        runs.Add(run);
                    }
                }
                break;

            case HtmlNodeType.Element:
                var tagName = node.Name.ToLower();

                if (node.GetAttributeValue("data-docx-xml", "") != ""
                    && TryRestorePreservedElement(node) is { } preservedInline)
                {
                    runs.Add(new Run(preservedInline));
                    break;
                }

                if (IsPageBreakNode(node))
                {
                    runs.Add(new Run(new Break { Type = BreakValues.Page }));
                    break;
                }

                if (node.NodeType == HtmlNodeType.Element && node.HasClass("docx-column-break"))
                {
                    runs.Add(new Run(new Break { Type = BreakValues.Column }));
                    break;
                }

                if (node.GetAttributeValue("data-docx-hr", "") != "")
                {
                    runs.Add(CreateVmlHorizontalRuleRun(node));
                    break;
                }

                if (node.Name.Equals("sup", StringComparison.OrdinalIgnoreCase) && node.HasClass("footnote-ref"))
                {
                    var run = CreateFootnoteReferenceRun(node, inheritedProps);
                    if (run != null) runs.Add(run);
                    break;
                }

                if (node.Name.Equals("sup", StringComparison.OrdinalIgnoreCase) && node.HasClass("endnote-ref"))
                {
                    var run = CreateEndnoteReferenceRun(node, inheritedProps);
                    if (run != null) runs.Add(run);
                    break;
                }

                if (node.HasClass("docx-tab-seg"))
                {
                    var segTab = new Run();
                    if (inheritedProps != null)
                        segTab.Append(inheritedProps.CloneNode(true));
                    segTab.Append(new TabChar());
                    runs.Add(segTab);
                    foreach (var segChild in node.ChildNodes)
                        runs.AddRange(CreateRunsFromNode(segChild, inheritedProps));
                    break;
                }

                if (node.HasClass("docx-tab-leader"))
                {
                    var leaderTab = new Run();
                    if (inheritedProps != null)
                        leaderTab.Append(inheritedProps.CloneNode(true));
                    leaderTab.Append(new TabChar());
                    runs.Add(leaderTab);
                    break;
                }

                if (node.HasClass("docx-fld-marker"))
                {
                    var kind = node.GetAttributeValue("data-fld", "");
                    if (kind == "begin")
                    {
                        var instr = System.Net.WebUtility.HtmlDecode(node.GetAttributeValue("data-fld-instr", "")).Trim();
                        if (!string.IsNullOrEmpty(instr))
                        {
                            runs.Add(new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }));
                            runs.Add(new Run(new FieldCode($" {instr} ") { Space = SpaceProcessingModeValues.Preserve }));
                            runs.Add(new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }));
                            _openFieldMarkerCount++;
                        }
                    }
                    else if (kind == "end" && _openFieldMarkerCount > 0)
                    {
                        runs.Add(new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
                        _openFieldMarkerCount--;
                    }
                    break;
                }

                if (node.HasClass("docx-bookmark"))
                    break;

                var newProps = (inheritedProps?.CloneNode(true) as RunProperties) ?? new RunProperties();

                switch (tagName)
                {
                    case "strong": case "b":
                        if (!newProps.Elements<Bold>().Any())
                            newProps.Append(new Bold());
                        break;
                    case "em": case "i":
                        if (!newProps.Elements<Italic>().Any())
                            newProps.Append(new Italic());
                        break;
                    case "u":
                        if (!newProps.Elements<Underline>().Any())
                            newProps.Append(new Underline { Val = UnderlineValues.Single });
                        break;
                    case "s": case "strike":
                        if (!newProps.Elements<Strike>().Any())
                            newProps.Append(new Strike());
                        break;
                    case "sub":
                        if (!newProps.Elements<VerticalTextAlignment>().Any())
                            newProps.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript });
                        break;
                    case "sup":
                        if (!newProps.Elements<VerticalTextAlignment>().Any())
                            newProps.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript });
                        break;
                    case "br":
                        runs.Add(new Run(new Break()));
                        return runs;
                    case "img":
                        var imgPara = ConvertImageElement(node);
                        if (imgPara != null)
                        {
                            foreach (var r in imgPara.Elements<Run>())
                            {
                                runs.Add((Run)r.CloneNode(true));
                            }
                        }
                        return runs;
                    case "a":
                        var href = node.GetAttributeValue("href", "#");
                        try
                        {
                            var relId = _mainPart!.AddHyperlinkRelationship(new Uri(href, UriKind.RelativeOrAbsolute), true).Id;
                            var linkProps = (newProps.CloneNode(true) as RunProperties) ?? new RunProperties();
                            if (!linkProps.Elements<Color>().Any())
                                linkProps.Append(new Color { Val = "0563C1" });
                            if (!linkProps.Elements<Underline>().Any())
                                linkProps.Append(new Underline { Val = UnderlineValues.Single });
                            
                            foreach (var child in node.ChildNodes)
                                runs.AddRange(CreateRunsFromNode(child, linkProps));
                        }
                        catch
                        {
                            foreach (var child in node.ChildNodes)
                                runs.AddRange(CreateRunsFromNode(child, newProps));
                        }
                        return runs;
                }

                var style = node.GetAttributeValue("style", "");
                ApplyRunStyle(newProps, style);

                foreach (var child in node.ChildNodes)
                {
                    runs.AddRange(CreateRunsFromNode(child, newProps));
                }
                break;
        }

        return runs;
    }

    private const long FootnoteSeparatorId = -1;
    private const long FootnoteContinuationSeparatorId = 0;

    private void AssignFootnoteOoxmlIds(IReadOnlyList<DomainFootnote>? footnotes)
    {
        if (footnotes == null) return;
        long next = 1;
        foreach (var footnote in footnotes)
        {
            if (string.IsNullOrEmpty(footnote.Id) || _footnoteOoxmlIdByHtmlId.ContainsKey(footnote.Id))
                continue;
            _footnoteOoxmlIdByHtmlId[footnote.Id] = next++;
        }
    }

    private Run? CreateFootnoteReferenceRun(HtmlNode node, RunProperties? inheritedProps)
    {
        var htmlId = node.GetAttributeValue("data-footnote-id", "");
        if (string.IsNullOrEmpty(htmlId) || !_footnoteOoxmlIdByHtmlId.TryGetValue(htmlId, out var ooxmlId))
            return null;

        _referencedFootnoteHtmlIds.Add(htmlId);

        var props = (inheritedProps?.CloneNode(true) as RunProperties) ?? new RunProperties();
        if (!props.Elements<VerticalTextAlignment>().Any())
            props.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript });

        var run = new Run();
        run.Append(props);
        run.Append(new FootnoteReference { Id = ooxmlId });
        return run;
    }

    private void AddFootnotes(IReadOnlyList<DomainFootnote>? footnotes)
    {
        if (_mainPart == null || footnotes == null || footnotes.Count == 0)
            return;
        if (_footnoteOoxmlIdByHtmlId.Count == 0)
            return;

        var footnotesPart = _mainPart.FootnotesPart ?? _mainPart.AddNewPart<FootnotesPart>();
        var root = new Footnotes();
        root.Append(CreateSeparatorFootnote(FootnoteSeparatorId, FootnoteEndnoteValues.Separator));
        root.Append(CreateSeparatorFootnote(FootnoteContinuationSeparatorId, FootnoteEndnoteValues.ContinuationSeparator));

        foreach (var footnote in footnotes)
        {
            if (!_footnoteOoxmlIdByHtmlId.TryGetValue(footnote.Id, out var ooxmlId))
                continue;
            root.Append(BuildFootnoteElement(footnote, ooxmlId, footnotesPart));
        }

        footnotesPart.Footnotes = root;
        footnotesPart.Footnotes.Save();
    }

    private WpFootnote BuildFootnoteElement(DomainFootnote model, long ooxmlId, FootnotesPart footnotesPart)
    {
        var footnote = new WpFootnote { Id = ooxmlId };

        var tempBody = new Body();
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(model.Html ?? string.Empty);

        var prevContainer = _currentImageContainer;
        _currentImageContainer = footnotesPart;
        try
        {
            ConvertHtmlToBody(htmlDoc.DocumentNode, tempBody);
        }
        finally
        {
            _currentImageContainer = prevContainer;
        }

        var blocks = tempBody.ChildElements
            .Where(e => e is Paragraph || e is Table)
            .Select(e => e.CloneNode(true))
            .ToList();

        if (blocks.Count == 0)
            blocks.Add(new Paragraph());

        if (blocks[0] is Paragraph firstParagraph)
        {
            var markRun = new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }),
                                  new FootnoteReferenceMark());
            var pPr = firstParagraph.GetFirstChild<ParagraphProperties>();
            if (pPr != null)
                firstParagraph.InsertAfter(markRun, pPr);
            else
                firstParagraph.InsertAt(markRun, 0);
        }

        foreach (var block in blocks)
            footnote.Append(block);

        return footnote;
    }

    private static WpFootnote CreateSeparatorFootnote(long id, FootnoteEndnoteValues type)
    {
        OpenXmlElement mark = type == FootnoteEndnoteValues.Separator
            ? new SeparatorMark()
            : new ContinuationSeparatorMark();
        return new WpFootnote(new Paragraph(new Run(mark))) { Id = id, Type = type };
    }

    private const long EndnoteSeparatorId = -1;
    private const long EndnoteContinuationSeparatorId = 0;

    private void AssignEndnoteOoxmlIds(IReadOnlyList<DomainEndnote>? endnotes)
    {
        if (endnotes == null) return;
        long next = 1;
        foreach (var endnote in endnotes)
        {
            if (string.IsNullOrEmpty(endnote.Id) || _endnoteOoxmlIdByHtmlId.ContainsKey(endnote.Id))
                continue;
            _endnoteOoxmlIdByHtmlId[endnote.Id] = next++;
        }
    }

    private Run? CreateEndnoteReferenceRun(HtmlNode node, RunProperties? inheritedProps)
    {
        var htmlId = node.GetAttributeValue("data-endnote-id", "");
        if (string.IsNullOrEmpty(htmlId) || !_endnoteOoxmlIdByHtmlId.TryGetValue(htmlId, out var ooxmlId))
            return null;

        _referencedEndnoteHtmlIds.Add(htmlId);

        var props = (inheritedProps?.CloneNode(true) as RunProperties) ?? new RunProperties();
        if (!props.Elements<VerticalTextAlignment>().Any())
            props.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript });

        var run = new Run();
        run.Append(props);
        run.Append(new EndnoteReference { Id = ooxmlId });
        return run;
    }

    private void AddEndnotes(IReadOnlyList<DomainEndnote>? endnotes)
    {
        if (_mainPart == null || endnotes == null || endnotes.Count == 0)
            return;
        if (_endnoteOoxmlIdByHtmlId.Count == 0)
            return;

        var endnotesPart = _mainPart.EndnotesPart ?? _mainPart.AddNewPart<EndnotesPart>();
        var root = new Endnotes();
        root.Append(CreateSeparatorEndnote(EndnoteSeparatorId, FootnoteEndnoteValues.Separator));
        root.Append(CreateSeparatorEndnote(EndnoteContinuationSeparatorId, FootnoteEndnoteValues.ContinuationSeparator));

        foreach (var endnote in endnotes)
        {
            if (!_endnoteOoxmlIdByHtmlId.TryGetValue(endnote.Id, out var ooxmlId))
                continue;
            root.Append(BuildEndnoteElement(endnote, ooxmlId, endnotesPart));
        }

        endnotesPart.Endnotes = root;
        endnotesPart.Endnotes.Save();
    }

    private WpEndnote BuildEndnoteElement(DomainEndnote model, long ooxmlId, EndnotesPart endnotesPart)
    {
        var endnote = new WpEndnote { Id = ooxmlId };

        var tempBody = new Body();
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(model.Html ?? string.Empty);

        var prevContainer = _currentImageContainer;
        _currentImageContainer = endnotesPart;
        try
        {
            ConvertHtmlToBody(htmlDoc.DocumentNode, tempBody);
        }
        finally
        {
            _currentImageContainer = prevContainer;
        }

        var blocks = tempBody.ChildElements
            .Where(e => e is Paragraph || e is Table)
            .Select(e => e.CloneNode(true))
            .ToList();

        if (blocks.Count == 0)
            blocks.Add(new Paragraph());

        if (blocks[0] is Paragraph firstParagraph)
        {
            var markRun = new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }),
                                  new EndnoteReferenceMark());
            var pPr = firstParagraph.GetFirstChild<ParagraphProperties>();
            if (pPr != null)
                firstParagraph.InsertAfter(markRun, pPr);
            else
                firstParagraph.InsertAt(markRun, 0);
        }

        foreach (var block in blocks)
            endnote.Append(block);

        return endnote;
    }

    private static WpEndnote CreateSeparatorEndnote(long id, FootnoteEndnoteValues type)
    {
        OpenXmlElement mark = type == FootnoteEndnoteValues.Separator
            ? new SeparatorMark()
            : new ContinuationSeparatorMark();
        return new WpEndnote(new Paragraph(new Run(mark))) { Id = id, Type = type };
    }

    private static Tabs? ParseTabStops(string attr)
    {
        var tabs = new Tabs();
        foreach (var entry in attr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var pos)) continue;

            var val = parts[1] switch
            {
                "center" => TabStopValues.Center,
                "right" => TabStopValues.Right,
                "decimal" => TabStopValues.Decimal,
                _ => TabStopValues.Left
            };
            var stop = new TabStop { Val = val, Position = pos };
            if (parts.Length >= 3)
            {
                stop.Leader = parts[2] switch
                {
                    "dot" => TabStopLeaderCharValues.Dot,
                    "hyphen" => TabStopLeaderCharValues.Hyphen,
                    "underscore" => TabStopLeaderCharValues.Underscore,
                    "middleDot" => TabStopLeaderCharValues.MiddleDot,
                    "heavy" => TabStopLeaderCharValues.Heavy,
                    _ => TabStopLeaderCharValues.None
                };
            }
            tabs.Append(stop);
        }
        return tabs.HasChildren ? tabs : null;
    }

    private void ApplyParagraphStyle(ParagraphProperties props, string style, bool includeIndentation = true)
    {
        if (string.IsNullOrEmpty(style)) return;

        if (Regex.IsMatch(style, @"(page-break-before|break-before)\s*:\s*(always|page)", RegexOptions.IgnoreCase))
            props.Append(new PageBreakBefore());
        else if (Regex.IsMatch(style, @"(page-break-before|break-before)\s*:\s*auto", RegexOptions.IgnoreCase))
            props.Append(new PageBreakBefore { Val = false });

        if (Regex.IsMatch(style, @"(page-break-after|break-after)\s*:\s*avoid", RegexOptions.IgnoreCase))
            props.Append(new KeepNext());
        else if (Regex.IsMatch(style, @"(page-break-after|break-after)\s*:\s*auto", RegexOptions.IgnoreCase))
            props.Append(new KeepNext { Val = false });
        if (Regex.IsMatch(style, @"(page-break-inside|break-inside)\s*:\s*avoid", RegexOptions.IgnoreCase))
            props.Append(new KeepLines());
        else if (Regex.IsMatch(style, @"(page-break-inside|break-inside)\s*:\s*auto", RegexOptions.IgnoreCase))
            props.Append(new KeepLines { Val = false });

        var alignMatch = Regex.Match(style, @"text-align:\s*(left|center|right|justify)");
        if (alignMatch.Success)
        {
            var align = alignMatch.Groups[1].Value switch
            {
                "center" => JustificationValues.Center,
                "right" => JustificationValues.Right,
                "justify" => JustificationValues.Both,
                _ => JustificationValues.Left
            };
            props.Append(new Justification { Val = align });
        }

        if (includeIndentation)
        {
            var indentation = new Indentation();
            bool hasIndent = false;
            var invInd = System.Globalization.CultureInfo.InvariantCulture;

            int? LengthToTwips(string prop)
            {
                var m = Regex.Match(style, $@"(?<![\w-]){Regex.Escape(prop)}:\s*(-?[\d.,]+)(px|pt)");
                if (!m.Success) return null;
                var val = double.Parse(m.Groups[1].Value.Replace(',', '.'), invInd);
                if (m.Groups[2].Value == "px") val = OoxmlUnits.PixelsToPoints(val);
                return (int)Math.Round(OoxmlUnits.PointsToTwips(val));
            }

            var leftTw = LengthToTwips("margin-left");
            if (leftTw.HasValue)
            {
                indentation.Left = leftTw.Value.ToString();
                hasIndent = true;
            }

            var rightTw = LengthToTwips("margin-right");
            if (rightTw.HasValue)
            {
                indentation.Right = rightTw.Value.ToString();
                hasIndent = true;
            }

            var indentTw = LengthToTwips("text-indent");
            if (indentTw.HasValue)
            {
                if (indentTw.Value < 0)
                    indentation.Hanging = Math.Abs(indentTw.Value).ToString();
                else
                    indentation.FirstLine = indentTw.Value.ToString();
                hasIndent = true;
            }

            if (hasIndent)
                props.Append(indentation);
        }

        var spacing = new SpacingBetweenLines();
        bool hasSpacing = false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        var marginTopMatch = Regex.Match(style, @"margin-top:\s*([\d.,]+)(px|pt)");
        if (marginTopMatch.Success)
        {
            var val = double.Parse(marginTopMatch.Groups[1].Value.Replace(',', '.'), inv);
            var unit = marginTopMatch.Groups[2].Value;
            if (unit == "px") val = OoxmlUnits.PixelsToPoints(val);
            spacing.Before = ((int)Math.Round(OoxmlUnits.PointsToTwips(val))).ToString();
            hasSpacing = true;
        }

        var marginBottomMatch = Regex.Match(style, @"margin-bottom:\s*([\d.,]+)(px|pt)");
        if (marginBottomMatch.Success)
        {
            var val = double.Parse(marginBottomMatch.Groups[1].Value.Replace(',', '.'), inv);
            var unit = marginBottomMatch.Groups[2].Value;
            if (unit == "px") val = OoxmlUnits.PixelsToPoints(val);
            spacing.After = ((int)Math.Round(OoxmlUnits.PointsToTwips(val))).ToString();
            hasSpacing = true;
        }

        var paddingBottomMatch = Regex.Match(style, @"(?<![\w-])padding-bottom:\s*([\d.,]+)(px|pt)?");
        if (paddingBottomMatch.Success)
        {
            var val = double.Parse(paddingBottomMatch.Groups[1].Value.Replace(',', '.'), inv);
            var unit = paddingBottomMatch.Groups[2].Value;
            if (unit == "px") val = OoxmlUnits.PixelsToPoints(val);
            var afterTw = (int)Math.Round(OoxmlUnits.PointsToTwips(val));
            if (afterTw > 0 || !marginBottomMatch.Success)
            {
                spacing.After = afterTw.ToString();
                hasSpacing = true;
            }
        }

        var atLeastMatch = Regex.Match(style, @"line-height:\s*max\(\s*([\d.,]+)pt");
        if (atLeastMatch.Success)
        {
            var val = double.Parse(atLeastMatch.Groups[1].Value.Replace(',', '.'), inv);
            spacing.Line = ((int)Math.Round(OoxmlUnits.PointsToTwips(val))).ToString();
            spacing.LineRule = LineSpacingRuleValues.AtLeast;
            hasSpacing = true;
        }

        var lineHeightMatch = Regex.Match(style, @"line-height:\s*([\d.,]+)(pt)?");
        if (!atLeastMatch.Success && lineHeightMatch.Success)
        {
            var val = double.Parse(lineHeightMatch.Groups[1].Value.Replace(',', '.'), inv);
            var unit = lineHeightMatch.Groups[2].Value;
            var gridTwMatch = Regex.Match(style, @"--w-line-tw\s*:\s*(\d+)");
            if (unit == "pt" && Regex.IsMatch(style, @"--w-line-grid\s*:\s*1") && gridTwMatch.Success)
            {
                spacing.Line = gridTwMatch.Groups[1].Value;
                spacing.LineRule = LineSpacingRuleValues.Auto;
            }
            else if (unit == "pt")
            {
                spacing.Line = ((int)Math.Round(OoxmlUnits.PointsToTwips(val))).ToString();
                spacing.LineRule = Regex.IsMatch(style, @"--w-line-rule\s*:\s*atLeast")
                    ? LineSpacingRuleValues.AtLeast
                    : LineSpacingRuleValues.Exact;
            }
            else
            {
                var lineTwMarker = Regex.Match(style, @"--w-line-tw\s*:\s*(\d+)");
                spacing.Line = lineTwMarker.Success
                    ? lineTwMarker.Groups[1].Value
                    : ((int)Math.Round(val * 240)).ToString();
                spacing.LineRule = LineSpacingRuleValues.Auto;
            }
            hasSpacing = true;
        }

        if (Regex.IsMatch(style, @"--w-before-auto\s*:\s*1"))
        {
            spacing.BeforeAutoSpacing = true;
            hasSpacing = true;
        }
        if (Regex.IsMatch(style, @"--w-after-auto\s*:\s*1"))
        {
            spacing.AfterAutoSpacing = true;
            hasSpacing = true;
        }

        var beforeLines = Regex.Match(style, @"--w-before-lines\s*:\s*(\d+)");
        if (beforeLines.Success)
        {
            spacing.BeforeLines = int.Parse(beforeLines.Groups[1].Value, inv);
            hasSpacing = true;
        }
        var afterLines = Regex.Match(style, @"--w-after-lines\s*:\s*(\d+)");
        if (afterLines.Success)
        {
            spacing.AfterLines = int.Parse(afterLines.Groups[1].Value, inv);
            hasSpacing = true;
        }

        if (hasSpacing)
            props.Append(spacing);

        var contextual = Regex.Match(style, @"--w-contextual-spacing\s*:\s*([01])");
        if (contextual.Success)
        {
            props.Append(contextual.Groups[1].Value == "1"
                ? new ContextualSpacing()
                : new ContextualSpacing { Val = false });
        }

        if (Regex.IsMatch(style, @"--w-snap-to-grid\s*:\s*0"))
            props.Append(new SnapToGrid { Val = false });

        var bgColor = ExtractColor(style, @"background(?:-color)?:\s*");
        if (bgColor != null)
        {
            props.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = bgColor });
        }

        ApplyParagraphBorders(props, style);

        NormalizeParagraphPropertiesOrder(props);
    }

    private static void NormalizeParagraphPropertiesOrder(ParagraphProperties props)
    {
        if (!props.HasChildren) return;

        static int Rank(OpenXmlElement el) => el switch
        {
            ParagraphStyleId => 0,
            KeepNext => 1,
            KeepLines => 2,
            PageBreakBefore => 3,
            WidowControl => 5,
            NumberingProperties => 6,
            ParagraphBorders => 8,
            Shading => 9,
            Tabs => 10,
            SnapToGrid => 11,
            SpacingBetweenLines => 12,
            Indentation => 13,
            ContextualSpacing => 14,
            Justification => 16,
            OutlineLevel => 18,
            ParagraphMarkRunProperties => 19,
            SectionProperties => 20,
            _ => 15
        };

        var ordered = props.ChildElements.OrderBy(Rank).ToList();
        props.RemoveAllChildren();
        foreach (var child in ordered)
            props.Append(child);
    }

    private void ApplyParagraphStyleExtras(ParagraphProperties props, string style)
    {
        if (string.IsNullOrEmpty(style)) return;

        var alignMatch = Regex.Match(style, @"text-align:\s*(left|center|right|justify)");
        if (alignMatch.Success)
        {
            var align = alignMatch.Groups[1].Value switch
            {
                "center" => JustificationValues.Center,
                "right" => JustificationValues.Right,
                "justify" => JustificationValues.Both,
                _ => JustificationValues.Left
            };
            props.Append(new Justification { Val = align });
            NormalizeParagraphPropertiesOrder(props);
        }
    }

    private void ApplyParagraphBorders(ParagraphProperties props, string style)
    {
        if (style.Contains("--w-pbdr-source:style", StringComparison.OrdinalIgnoreCase)) return;
        var borders = new ParagraphBorders();
        bool hasBorders = false;

        var borderPatterns = new[]
        {
            ("border-top", new Func<BorderType>(() => new TopBorder())),
            ("border-bottom", new Func<BorderType>(() => new BottomBorder())),
            ("border-left", new Func<BorderType>(() => new LeftBorder())),
            ("border-right", new Func<BorderType>(() => new RightBorder())),
        };

        foreach (var (prefix, createBorder) in borderPatterns)
        {
            var match = Regex.Match(style, $@"{Regex.Escape(prefix)}:\s*([\d.]+)px\s+(\w+)\s+#?([a-fA-F0-9]{{3,6}})");
            if (match.Success)
            {
                var border = createBorder();
                border.Val = ParseBorderStyle(match.Groups[2].Value);
                border.Size = CssBorderWidthToEighthPoints(match.Groups[1].Value, border.Val.Value);
                border.Color = NormalizeColor(match.Groups[3].Value);
                border.Space = 4;
                borders.Append(border);
                hasBorders = true;
            }
        }

        if (hasBorders)
            props.Append(borders);
    }

    private void ApplyRunStyle(RunProperties props, string style)
    {
        if (string.IsNullOrEmpty(style)) return;

        if (Regex.IsMatch(style, @"font-weight:\s*(bold|[7-9]\d{2})"))
        {
            if (!props.Elements<Bold>().Any())
                props.Append(new Bold());
        }

        if (style.Contains("font-style:italic") || style.Contains("font-style: italic"))
        {
            if (!props.Elements<Italic>().Any())
                props.Append(new Italic());
        }

        var textDecMatch = Regex.Match(style, @"text-decoration:\s*([^;]+)");
        if (textDecMatch.Success)
        {
            var decValue = textDecMatch.Groups[1].Value.ToLower();
            if (decValue.Contains("underline") && !props.Elements<Underline>().Any())
                props.Append(new Underline { Val = UnderlineValues.Single });
            if (decValue.Contains("line-through") && !props.Elements<Strike>().Any())
                props.Append(new Strike());
        }

        var fontSizeMatch = Regex.Match(style, @"font-size:\s*([\d.,]+)(pt|px|em|rem)");
        if (fontSizeMatch.Success)
        {
            var size = double.Parse(fontSizeMatch.Groups[1].Value.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
            var unit = fontSizeMatch.Groups[2].Value;
            
            double ptSize = unit switch
            {
                "px" => OoxmlUnits.PixelsToPoints(size),
                "em" => size * 11,
                "rem" => size * 11,
                _ => size
            };

            var halfPoints = ((int)OoxmlUnits.PointsToHalfPoints(ptSize)).ToString();
            SetOrReplaceFontSize(props, halfPoints);
        }
        else if (style.Contains("font-size:smaller") || style.Contains("font-size: smaller"))
        {
            SetOrReplaceFontSize(props, "18");
        }

        var decodedStyle = System.Net.WebUtility.HtmlDecode(style);
        var fontFamilyMatch = Regex.Match(decodedStyle, @"font-family:\s*([^,;]+)");
        if (fontFamilyMatch.Success)
        {
            var fontName = fontFamilyMatch.Groups[1].Value.Trim().Trim('"', '\'').Trim();
            if (fontName.Length > 0)
            {
                var existingFonts = props.Elements<RunFonts>().FirstOrDefault();
                if (existingFonts != null)
                {
                    existingFonts.Ascii = fontName;
                    existingFonts.HighAnsi = fontName;
                }
                else
                {
                    props.Append(new RunFonts { Ascii = fontName, HighAnsi = fontName });
                }
            }
        }

        var colorVal = ExtractColor(style, @"(?<!background-)color:\s*");
        if (colorVal != null)
        {
            var existingColor = props.Elements<Color>().FirstOrDefault();
            if (existingColor != null)
                existingColor.Val = colorVal;
            else
                props.Append(new Color { Val = colorVal });
        }

        var bgColorVal = ExtractColor(style, @"background-color:\s*");
        if (bgColorVal != null && !props.Elements<Shading>().Any())
        {
            props.Append(new Shading { Fill = bgColorVal, Val = ShadingPatternValues.Clear });
        }

        if (style.Contains("vertical-align:super") && !props.Elements<VerticalTextAlignment>().Any())
            props.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript });
        if (style.Contains("vertical-align:sub") && !props.Elements<VerticalTextAlignment>().Any())
            props.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript });

        var letterSpacingMatch = Regex.Match(style, @"letter-spacing:\s*(-?[\d.,]+)(pt|px)");
        if (letterSpacingMatch.Success && !props.Elements<Spacing>().Any())
        {
            var ls = double.Parse(letterSpacingMatch.Groups[1].Value.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
            var lsUnit = letterSpacingMatch.Groups[2].Value;
            if (lsUnit == "px") ls = OoxmlUnits.PixelsToPoints(ls);
            props.Append(new Spacing { Val = (int)OoxmlUnits.PointsToTwips(ls) });
        }

        if (style.Contains("text-transform:uppercase") || style.Contains("text-transform: uppercase"))
        {
            if (!props.Elements<Caps>().Any())
                props.Append(new Caps());
        }
        
        if (style.Contains("font-variant:small-caps") || style.Contains("font-variant: small-caps"))
        {
            if (!props.Elements<SmallCaps>().Any())
                props.Append(new SmallCaps());
        }
    }

    private static void SetOrReplaceFontSize(RunProperties props, string halfPoints)
    {
        var existing = props.Elements<FontSize>().FirstOrDefault();
        if (existing != null)
            existing.Val = halfPoints;
        else
            props.Append(new FontSize { Val = halfPoints });
    }

    private Paragraph CreateParagraph(string text)
    {
        return new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private Paragraph CreatePageBreak()
    {
        return new Paragraph(new Run(new Break { Type = BreakValues.Page }));
    }

    private static bool IsPageBreakNode(HtmlNode node)
    {
        if (node.NodeType != HtmlNodeType.Element) return false;
        if (node.HasClass("page-break")) return true;
        if (string.Equals(node.GetAttributeValue("data-docx-break", ""), "page", StringComparison.OrdinalIgnoreCase))
            return true;
        var style = node.GetAttributeValue("style", "");
        return Regex.IsMatch(style, @"(page-break-before|break-before)\s*:\s*(always|page)", RegexOptions.IgnoreCase);
    }

    private static bool IsSectionBreakNode(HtmlNode node) =>
        node.NodeType == HtmlNodeType.Element && node.HasClass("docx-section-break");

    private static bool NextElementSiblingIsSectionBreak(HtmlNode node)
    {
        var next = node.NextSibling;
        while (next != null && (next.NodeType == HtmlNodeType.Comment ||
               (next.NodeType == HtmlNodeType.Text && string.IsNullOrWhiteSpace(next.InnerText))))
        {
            next = next.NextSibling;
        }
        return next != null && IsSectionBreakNode(next);
    }

    private Paragraph CreateSectionBreakParagraph(HtmlNode node)
    {
        var closingSection = new SectionProperties();
        AppendSectionBreakType(closingSection, _currentSection.BreakType);
        AppendSectionGeometry(closingSection, _currentSection);

        _firstSectionProps ??= closingSection;
        _emittedSectionProps.Add(closingSection);
        _hasSectionMarkers = true;
        _currentSection = ReadSectionGeometryFromMarker(node, _currentSection);

        return new Paragraph(new ParagraphProperties(closingSection));
    }

    private static SectionGeometry ReadSectionGeometryFromMarker(HtmlNode node, SectionGeometry previous)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double? Attr(string name)
        {
            var raw = node.GetAttributeValue(name, string.Empty);
            return !string.IsNullOrEmpty(raw) &&
                   double.TryParse(raw, System.Globalization.NumberStyles.Float, inv, out var v)
                ? v : null;
        }

        Domain.Models.PageSize? pageSize = null;
        if (Attr("data-page-width-cm") is { } w && Attr("data-page-height-cm") is { } h)
        {
            pageSize = new Domain.Models.PageSize
            {
                WidthCm = w,
                HeightCm = h,
                Orientation = node.GetAttributeValue("data-orientation", "portrait")
            };
        }

        PageMargins? margins = null;
        var top = Attr("data-margin-top-cm");
        var bottom = Attr("data-margin-bottom-cm");
        var left = Attr("data-margin-left-cm");
        var right = Attr("data-margin-right-cm");
        if (top != null || bottom != null || left != null || right != null)
        {
            margins = new PageMargins
            {
                Top = top ?? previous.Margins?.Top ?? 2.5,
                Bottom = bottom ?? previous.Margins?.Bottom ?? 2.5,
                Left = left ?? previous.Margins?.Left ?? 2.5,
                Right = right ?? previous.Margins?.Right ?? 2.5
            };
        }

        return new SectionGeometry
        {
            PageSize = pageSize ?? previous.PageSize,
            Margins = margins ?? previous.Margins,
            HeaderDistanceCm = Attr("data-header-distance-cm"),
            FooterDistanceCm = Attr("data-footer-distance-cm"),
            BreakType = node.GetAttributeValue("data-break-type", "nextPage"),
            Columns = ParseColumnDataAttributes(node),
            DocGrid = ParseDocGridDataAttributes(node)
        };
    }

    private static void AppendSectionBreakType(SectionProperties sectionProps, string? breakType)
    {
        SectionMarkValues? val = breakType switch
        {
            "continuous" => SectionMarkValues.Continuous,
            "oddPage" => SectionMarkValues.OddPage,
            "evenPage" => SectionMarkValues.EvenPage,
            "nextColumn" => SectionMarkValues.NextColumn,
            _ => null
        };
        if (val is { } v && !sectionProps.Elements<SectionType>().Any())
            sectionProps.Append(new SectionType { Val = v });
    }

    private void SetDocumentMetadata(WordprocessingDocument document, DocumentMetadata metadata)
    {
        WriteCoreProperties(document, metadata);

        var extPropsPart = document.AddExtendedFilePropertiesPart();
        extPropsPart.Properties = new Properties();

        if (!string.IsNullOrEmpty(metadata.Company))
            extPropsPart.Properties.Company = new Company(metadata.Company);
        if (!string.IsNullOrEmpty(metadata.Manager))
            extPropsPart.Properties.Manager = new Manager(metadata.Manager);

        extPropsPart.Properties.Application = new DocumentFormat.OpenXml.ExtendedProperties.Application("Doc2 D2Tools");
        extPropsPart.Properties.Save();
    }

    private static void WriteCoreProperties(WordprocessingDocument document, DocumentMetadata metadata)
    {
        XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace dcterms = "http://purl.org/dc/terms/";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var root = new XElement(cp + "coreProperties",
            new XAttribute(XNamespace.Xmlns + "cp", cp),
            new XAttribute(XNamespace.Xmlns + "dc", dc),
            new XAttribute(XNamespace.Xmlns + "dcterms", dcterms),
            new XAttribute(XNamespace.Xmlns + "xsi", xsi));

        void Add(XName name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) root.Add(new XElement(name, value));
        }
        XElement DateElement(XName name, DateTime value) => new(name,
            new XAttribute(xsi + "type", "dcterms:W3CDTF"),
            value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

        Add(cp + "category", metadata.Category);
        Add(cp + "contentStatus", metadata.ContentStatus);
        root.Add(DateElement(dcterms + "created", metadata.Created ?? DateTime.UtcNow));
        Add(dc + "creator", metadata.Author);
        Add(dc + "description", metadata.Description);
        Add(cp + "keywords", metadata.Keywords);
        Add(cp + "lastModifiedBy", metadata.LastModifiedBy);
        root.Add(DateElement(dcterms + "modified", DateTime.UtcNow));
        Add(cp + "revision", metadata.Revision);
        Add(dc + "subject", metadata.Subject);
        Add(dc + "title", metadata.Title);
        Add(cp + "version", metadata.Version);

        var corePart = document.CoreFilePropertiesPart ?? document.AddCoreFilePropertiesPart();
        using var stream = corePart.GetStream(FileMode.Create, FileAccess.Write);
        new XDocument(root).Save(stream);
    }

    private void AddPageSettings(Body body, HeaderFooterContent? header = null, HeaderFooterContent? footer = null, PageMargins? margins = null, Domain.Models.PageSize? pageSize = null)
    {
        var sectionProps = body.Elements<SectionProperties>().FirstOrDefault();
        if (sectionProps == null)
        {
            sectionProps = new SectionProperties();
            body.Append(sectionProps);
        }

        var geometry = _hasSectionMarkers
            ? _currentSection
            : new SectionGeometry { PageSize = pageSize, Margins = margins, Columns = _docDefaultColumns, DocGrid = _docDefaultDocGrid };

        if (_hasSectionMarkers)
            AppendSectionBreakType(sectionProps, geometry.BreakType);
        AppendSectionGeometry(sectionProps, geometry);
    }

    private void AppendSectionGeometry(SectionProperties sectionProps, SectionGeometry geometry)
    {
        if (!sectionProps.Elements<OoxmlPageSize>().Any())
        {
            AppendBeforeTitlePage(sectionProps, BuildPageSize(geometry.PageSize));
        }

        int defaultMarginTwips = OoxmlUnits.TwipsPerInch;
        int defaultBandTwips = OoxmlUnits.TwipsPerInch / 2;
        var margins = geometry.Margins;
        int leftTwips  = margins != null ? (int)Math.Round(OoxmlUnits.CmToTwips(margins.Left))  : defaultMarginTwips;
        int rightTwips = margins != null ? (int)Math.Round(OoxmlUnits.CmToTwips(margins.Right)) : defaultMarginTwips;

        var headerHeightTwips = _headerBandCm is { } hb ? (int)OoxmlUnits.CmToTwips(hb) : defaultBandTwips;
        var footerHeightTwips = _footerBandCm is { } fb ? (int)OoxmlUnits.CmToTwips(fb) : defaultBandTwips;

        int topTwips    = margins != null ? (int)Math.Round(OoxmlUnits.CmToTwips(margins.Top))    : defaultMarginTwips;
        int bottomTwips = margins != null ? (int)Math.Round(OoxmlUnits.CmToTwips(margins.Bottom)) : defaultMarginTwips;

        const int maxBandDistanceTwips = 720;
        uint headerDistance = geometry.HeaderDistanceCm is { } hd
            ? (uint)Math.Max(0, (int)Math.Round(OoxmlUnits.CmToTwips(hd)))
            : (uint)Math.Clamp(topTwips - headerHeightTwips, 0, maxBandDistanceTwips);
        uint footerDistance = geometry.FooterDistanceCm is { } fd
            ? (uint)Math.Max(0, (int)Math.Round(OoxmlUnits.CmToTwips(fd)))
            : (uint)Math.Clamp(bottomTwips - footerHeightTwips, 0, maxBandDistanceTwips);

        if (!sectionProps.Elements<PageMargin>().Any())
        {
            AppendBeforeTitlePage(sectionProps, new PageMargin
            {
                Top    = topTwips,
                Right  = (uint)rightTwips,
                Bottom = bottomTwips,
                Left   = (uint)leftTwips,
                Header = headerDistance,
                Footer = footerDistance
            });
        }

        AppendColumns(sectionProps, geometry.Columns);
        AppendDocGrid(sectionProps, geometry.DocGrid);
    }

    private static void AppendColumns(SectionProperties sectionProps, ColumnLayout? cols)
    {
        if (cols == null || cols.Count <= 1) return;
        if (sectionProps.Elements<Columns>().Any()) return;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var columns = new Columns
        {
            ColumnCount = (short)cols.Count,
            Space = cols.SpaceTwips.ToString(inv),
            EqualWidth = cols.EqualWidth,
        };
        if (cols.Separator) columns.Separator = true;

        if (!cols.EqualWidth && cols.Columns is { Count: > 0 } list)
        {
            foreach (var c in list)
            {
                var col = new Column { Width = c.WidthTwips.ToString(inv) };
                if (c.SpaceTwips > 0) col.Space = c.SpaceTwips.ToString(inv);
                columns.Append(col);
            }
        }

        AppendBeforeTitlePage(sectionProps, columns);
    }

    private static void AppendBeforeTitlePage(SectionProperties sectionProps, OpenXmlElement element)
    {
        var titlePg = sectionProps.GetFirstChild<TitlePage>();
        if (titlePg != null) sectionProps.InsertBefore(element, titlePg);
        else sectionProps.Append(element);
    }

    private static OoxmlPageSize BuildPageSize(Domain.Models.PageSize? pageSize)
    {
        const int a4WidthTwips = 11906;
        const int a4HeightTwips = 16838;

        if (pageSize == null || pageSize.WidthCm <= 0 || pageSize.HeightCm <= 0)
            return new OoxmlPageSize { Width = a4WidthTwips, Height = a4HeightTwips };

        var result = new OoxmlPageSize
        {
            Width = (uint)Math.Round(OoxmlUnits.CmToTwips(pageSize.WidthCm)),
            Height = (uint)Math.Round(OoxmlUnits.CmToTwips(pageSize.HeightCm))
        };
        if (string.Equals(pageSize.Orientation, "landscape", StringComparison.OrdinalIgnoreCase))
            result.Orient = PageOrientationValues.Landscape;
        return result;
    }

    private static DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties BuildImageDocProperties(uint id, string name, string? altText)
    {
        var props = new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = id, Name = name };
        if (!string.IsNullOrWhiteSpace(altText))
            props.Description = altText;
        return props;
    }

    private SimpleField BuildFieldRun(string instruction, HtmlNode fieldNode)
    {
        var run = new Run();
        var style = fieldNode.GetAttributeValue("style", "");
        if (string.IsNullOrEmpty(style))
            style = fieldNode.ParentNode?.GetAttributeValue("style", "") ?? string.Empty;
        if (!string.IsNullOrEmpty(style))
        {
            var rPr = new RunProperties();
            ApplyRunStyle(rPr, style);
            if (rPr.HasChildren) run.Append(rPr);
        }
        var inner = fieldNode.InnerText?.Trim() ?? string.Empty;
        if (inner.Length == 0 || inner.Contains("{page", StringComparison.OrdinalIgnoreCase) || !inner.Any(char.IsDigit))
            inner = "1";
        run.Append(new Text(inner) { Space = SpaceProcessingModeValues.Preserve });
        return new SimpleField(run) { Instruction = instruction };
    }

    private SimpleField BuildDateFieldRun(HtmlNode fieldNode)
    {
        var instruction = System.Net.WebUtility.HtmlDecode(
            fieldNode.GetAttributeValue("data-fld-instr", "")).Trim();
        if (instruction.Length == 0)
            instruction = "DATE \\@ \"dd.MM.yyyy\"";

        var run = new Run();
        var style = fieldNode.GetAttributeValue("style", "");
        if (string.IsNullOrEmpty(style))
            style = fieldNode.ParentNode?.GetAttributeValue("style", "") ?? string.Empty;
        if (!string.IsNullOrEmpty(style))
        {
            var rPr = new RunProperties();
            ApplyRunStyle(rPr, style);
            if (rPr.HasChildren) run.Append(rPr);
        }

        var inner = fieldNode.InnerText?.Trim() ?? string.Empty;
        if (inner.Length == 0)
            inner = DateTime.Now.ToString("dd.MM.yyyy");
        run.Append(new Text(inner) { Space = SpaceProcessingModeValues.Preserve });
        return new SimpleField(run) { Instruction = $" {instruction} " };
    }

    private int PxToTwips(int px) => (int)OoxmlUnits.PixelsToTwips(px);

    private OpenXmlElement? TryRestorePreservedElement(HtmlNode node)
    {
        var encoded = node.GetAttributeValue("data-docx-xml", "");
        if (string.IsNullOrEmpty(encoded)) return null;
        try
        {
            var xmlBytes = System.Convert.FromBase64String(encoded);
            if (xmlBytes.Length == 0 || xmlBytes.Length > DocxToHtmlConverter.MaxPreservedXmlBytes) return null;
            var xml = System.Text.Encoding.UTF8.GetString(xmlBytes);

            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0
            };
            using (var sr = new StringReader(xml))
            using (var xr = System.Xml.XmlReader.Create(sr, settings))
            {
                while (xr.Read()) { }
            }

            var root = System.Xml.Linq.XElement.Parse(xml);
            const string wNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            const string mcNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";
            OpenXmlElement? element = (root.Name.NamespaceName, root.Name.LocalName) switch
            {
                (wNs, "drawing") => new Drawing(xml),
                (wNs, "pict") => new Picture(xml),
                (wNs, "object") => new EmbeddedObject(xml),
                (mcNs, "AlternateContent") => new AlternateContent(xml),
                _ => null
            };
            if (element == null) return null;

            return RestorePreservedRels(node, element) ? element : null;
        }
        catch
        {
            return null;
        }
    }

    private bool RestorePreservedRels(HtmlNode node, OpenXmlElement element)
    {
        var referenced = DocxToHtmlConverter.CollectRelationshipIds(element);
        if (referenced.Count == 0) return true;

        var container = _currentImageContainer ?? (OpenXmlPart?)_mainPart;
        if (container == null) return false;

        Dictionary<string, PreservedRelEntry>? map = null;
        var encodedRels = node.GetAttributeValue("data-docx-rels", "");
        if (!string.IsNullOrEmpty(encodedRels))
        {
            var jsonBytes = System.Convert.FromBase64String(encodedRels);
            if (jsonBytes.Length > DocxToHtmlConverter.MaxPreservedRelsBytes * 2) return false;
            map = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, PreservedRelEntry>>(jsonBytes);
        }
        if (map == null || referenced.Any(r => !map.ContainsKey(r))) return false;

        foreach (var rid in referenced)
        {
            var entry = map[rid];
            var bytes = System.Convert.FromBase64String(entry.data);
            var newPart = CreatePreservedPart(container, entry.ct);
            if (newPart == null) return false;
            using (var stream = new MemoryStream(bytes))
            {
                newPart.FeedData(stream);
            }
            ReplaceRelationshipId(element, rid, container.GetIdOfPart(newPart));
        }
        return true;
    }

    private static OpenXmlPart? CreatePreservedPart(OpenXmlPart container, string contentType)
    {
        try
        {
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return container switch
                {
                    MainDocumentPart m => m.AddImagePart(contentType),
                    HeaderPart h => h.AddImagePart(contentType),
                    FooterPart f => f.AddImagePart(contentType),
                    FootnotesPart fn => fn.AddImagePart(contentType),
                    EndnotesPart en => en.AddImagePart(contentType),
                    _ => null
                };
            }
            return container.AddNewPart<EmbeddedObjectPart>(contentType);
        }
        catch
        {
            return null;
        }
    }

    private static void ReplaceRelationshipId(OpenXmlElement element, string oldId, string newId)
    {
        void Fix(OpenXmlElement el)
        {
            foreach (var attr in el.GetAttributes())
            {
                if (attr.NamespaceUri == DocxToHtmlConverter.OoxmlRelationshipNs && attr.Value == oldId)
                    el.SetAttribute(new OpenXmlAttribute(attr.Prefix, attr.LocalName, attr.NamespaceUri, newId));
            }
            foreach (var child in el.ChildElements) Fix(child);
        }
        Fix(element);
    }

    private static SdtProperties BuildSdtProperties(HtmlNode node)
    {
        var encoded = node.GetAttributeValue("data-sdt-props", "");
        if (!string.IsNullOrEmpty(encoded))
        {
            try
            {
                var xml = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(encoded));
                var restored = new SdtProperties(xml);
                restored.Elements<SdtId>().ToList().ForEach(e => e.Remove());
                restored.ChildElements
                    .Where(e => e.LocalName is "placeholder" or "dataBinding")
                    .ToList().ForEach(e => e.Remove());
                return restored;
            }
            catch
            {
            }
        }

        var props = new SdtProperties();
        var tag = node.GetAttributeValue("data-sdt-tag", "");
        var alias = node.GetAttributeValue("data-sdt-alias", "");
        if (!string.IsNullOrEmpty(alias))
            props.Append(new SdtAlias { Val = System.Net.WebUtility.HtmlDecode(alias) });
        if (!string.IsNullOrEmpty(tag))
            props.Append(new Tag { Val = System.Net.WebUtility.HtmlDecode(tag) });
        return props;
    }

    private SdtBlock BuildSdtBlockFromHtml(HtmlNode node)
    {
        var sdt = new SdtBlock();
        sdt.Append(BuildSdtProperties(node));

        var content = new SdtContentBlock();
        foreach (var child in node.ChildNodes)
        {
            foreach (var el in ConvertHtmlNode(child))
                content.Append(el);
        }

        if (!content.Elements<Paragraph>().Any() && !content.Elements<Table>().Any())
            content.Append(new Paragraph());

        sdt.Append(content);
        return sdt;
    }

    private SdtRun? BuildSdtRunFromHtml(HtmlNode node, RunProperties? inheritedProps)
    {
        var sdt = new SdtRun();
        sdt.Append(BuildSdtProperties(node));

        var content = new SdtContentRun();
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Element
                && child.Name.Equals("span", StringComparison.OrdinalIgnoreCase))
            {
                if (child.HasClass("field-page") || child.HasClass("page-number"))
                {
                    content.Append(BuildFieldRun(" PAGE ", child));
                    continue;
                }
                if (child.HasClass("field-numpages"))
                {
                    content.Append(BuildFieldRun(" NUMPAGES ", child));
                    continue;
                }
            }

            foreach (var run in CreateRunsFromNode(child, inheritedProps))
                content.Append(run);
        }

        if (!content.Elements<Run>().Any() && !content.Elements<SimpleField>().Any())
            content.Append(new Run(new Text("") { Space = SpaceProcessingModeValues.Preserve }));

        sdt.Append(content);
        return sdt;
    }
}
