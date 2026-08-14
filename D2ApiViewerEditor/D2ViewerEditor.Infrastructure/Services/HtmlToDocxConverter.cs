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

/// <summary>
/// Serwis do konwersji HTML na dokument DOCX
/// Własna implementacja generatora OpenXML z wysoką dokładnością odwzorowania stylów
/// </summary>
public class HtmlToDocxConverter : IHtmlToDocxConverter
{
    private MainDocumentPart? _mainPart;
    private readonly Dictionary<string, string> _imageRelationships = new();
    private int _imageCounter = 0;
    private int _numberingId = 1;
    private NumberingDefinitionsPart? _numberingPart;
    private readonly Dictionary<int, int> _abstractNumIds = new(); // track list numbering
    // HTML data-num-id (tożsamość listy z readera) → numId w generowanym pakiecie. Fragmenty listy
    // rozdzielone akapitem, ale należące do tej samej listy logicznej Worda, współdzielą jedną
    // NumberingInstance — Word kontynuuje wtedy numerację. Różne data-num-id → osobne instancje.
    private readonly Dictionary<string, int> _numIdByHtmlList = new();
    // HTML data-abstract-num-id (tożsamość DEFINICJI z readera) → abstractNumId w generowanym
    // pakiecie. Listy współdzielące abstrakt w oryginale (np. „Rozpocznij od nowa" = nowy w:num
    // ze startOverride na TEN SAM abstrakt) współdzielą go też po zapisie — bez tego restart
    // degradował się do niezależnej listy z przepisanym w:start.
    private readonly Dictionary<string, int> _abstractIdByHtmlAbstract = new();
    // Poziomy z WYEKSPORTOWANYM punktatorem graficznym (w:lvlPicBulletId) per abstrakt/numId —
    // dla nich marker <span class="list-marker"><img/></span> NIE jest bake'owany w treść runu.
    private readonly Dictionary<int, HashSet<int>> _picBulletLevelsByAbstract = new();
    private readonly Dictionary<int, HashSet<int>> _picBulletLevelsByNum = new();
    // Poziomy abstraktu zbudowane z jawnych data-* (nie z drabinki domyślnej) — późniejszy
    // fragment współdzielący abstrakt może dosłać definicje brakujących poziomów
    // (UpgradeSharedAbstractLevels), ale nigdy nie podmienia już zdefiniowanych.
    private readonly Dictionary<int, HashSet<int>> _specLevelsByAbstract = new();
    // Deduplikacja obrazów punktatorów: data URI → numPicBulletId (jeden w:numPicBullet
    // dla wielu poziomów/list używających tego samego obrazu).
    private readonly Dictionary<string, int> _picBulletIdByDataUri = new();
    private int _picBulletId = 1;

    // Przypisy dolne. Identyfikatory OOXML są przydzielane DETERMINISTYCZNIE po kolejności listy
    // przekazanej do Convert (htmlId → 1..N); technicznym separatorom rezerwujemy -1 i 0, więc
    // przypisy użytkownika nigdy z nimi nie kolidują. _referencedFootnoteHtmlIds notuje, które
    // przypisy faktycznie mają odwołanie w treści (walidacja: brak osieroconych odwołań/treści).
    private readonly Dictionary<string, long> _footnoteOoxmlIdByHtmlId = new();
    private readonly HashSet<string> _referencedFootnoteHtmlIds = new();

    // Przypisy końcowe — osobny słownik id/rejestr odwołań (endnotes.xml jest oddzielną częścią;
    // numeracja OOXML endnotes jest niezależna od footnotes).
    private readonly Dictionary<string, long> _endnoteOoxmlIdByHtmlId = new();
    private readonly HashSet<string> _referencedEndnoteHtmlIds = new();

    // Domyślne ustawienia dokumentu (firmowa czcionka itp.). Wstrzykiwane przez DI;
    // dla benchmarków / testów konstruktor bezparametrowy używa wartości domyślnych.
    private readonly DocumentDefaultsOptions _defaults;

    public HtmlToDocxConverter()
    {
        _defaults = new DocumentDefaultsOptions();
    }

    public HtmlToDocxConverter(IOptions<DocumentDefaultsOptions> defaults)
    {
        _defaults = defaults?.Value ?? new DocumentDefaultsOptions();
    }


    /// <summary>
    /// Część (Part) do której mają być dodawane obrazki w bieżącym kontekście:
    /// MainDocumentPart dla body, HeaderPart / FooterPart dla nagłówka/stopki.
    /// Obrazki muszą być powiązane z częścią w której są używane (relationship),
    /// inaczej Word ich nie wyświetli.
    /// </summary>
    private OpenXmlPart? _currentImageContainer;

    /// <summary>
    /// Czy aktualnie konwertujemy header/footer (wpływa na limit szerokości obrazka).
    /// </summary>
    private bool _inHeaderFooter = false;

    /// <summary>
    /// Domyślny StyleId dla paragrafów w bieżącej sekcji (Header / Footer).
    /// Aplikowany na paragrafy, które nie mają własnego <c>data-style-id</c>.
    /// W body pozostaje <c>null</c> → Word użyje stylu Normal.
    /// </summary>
    private string? _currentSectionStyleId = null;

    /// <summary>
    /// Geometria jednej sekcji strony (cm). Wartości null = dziedziczone/domyślne.
    /// <c>BreakType</c> opisuje, jak sekcja się ZACZYNA (w:sectPr/w:type następnej sekcji).
    /// </summary>
    private sealed class SectionGeometry
    {
        public Domain.Models.PageSize? PageSize { get; set; }
        public PageMargins? Margins { get; set; }
        public double? HeaderDistanceCm { get; set; }
        public double? FooterDistanceCm { get; set; }
        public string? BreakType { get; set; }
        /// <summary>Układ kolumn sekcji (w:cols). Null = jednokolumnowa (ADR-0039).</summary>
        public ColumnLayout? Columns { get; set; }
    }

    /// <summary>
    /// Geometria AKTUALNIE otwartej sekcji podczas konwersji body. Start = argumenty
    /// Convert (pierwsza sekcja); każdy marker <c>div.docx-section-break</c> zamyka
    /// bieżącą sekcję (w:p/pPr/sectPr z tą geometrią) i otwiera następną z data-*.
    /// Na końcu trzyma geometrię OSTATNIEJ sekcji → trafia do body-level sectPr.
    /// </summary>
    private SectionGeometry _currentSection = new();

    /// <summary>Pierwszy sectPr w kolejności dokumentu — tu wpinamy referencje
    /// nagłówka/stopki i titlePg (kolejne sekcje dziedziczą je w Wordzie).</summary>
    private SectionProperties? _firstSectionProps;

    /// <summary>Paragraph-level sectPr w kolejności emisji: element [k] zamyka sekcję o
    /// indeksie k (0-based) — cel dla własnych nagłówków/stopek sekcji (SectionHeaderFooter).</summary>
    private readonly List<SectionProperties> _emittedSectionProps = new();

    /// <summary>Czy body zawierało markery sekcji (dokument wielosekcyjny).</summary>
    private bool _hasSectionMarkers;

    /// <summary>Wysokości pasm nagłówka/stopki (cm) do wyliczenia w:header/w:footer
    /// distance dla sectPr sekcji pośrednich (te same, co dla body-level sectPr).</summary>
    private double? _headerBandCm;
    private double? _footerBandCm;

    // Domyślne wartości dokumentu z kontenera .document-content (reader emituje inline
    // font-family/font-size + data-default-*). Odtwarzane w docDefaults/Normal generowanego
    // pakietu; brak kontenera → dotychczasowe wartości z konfiguracji (zero regresji).
    private string? _docDefaultFontFamily;
    private double? _docDefaultFontSizePt;
    private string? _docDefaultSpacingBeforeTw;
    private string? _docDefaultSpacingAfterTw;
    private string? _docDefaultSpacingLine;
    private string? _docDefaultSpacingLineRule;
    // Układ kolumn sekcji bazowej (0) z data-col-* kontenera .document-content (ADR-0039).
    private ColumnLayout? _docDefaultColumns;
    // Licznik pól złożonych otwartych markerem docx-fld-marker (TOC/PAGEREF) — End emitowany
    // tylko przy dodatnim liczniku (osierocony End uszkadza dokument), brakujące End domykane
    // po konwersji body.
    private int _openFieldMarkerCount;
    // Unikalne w:id zakładek odtwarzanych z markerów docx-bookmark (cele PAGEREF/TOC).
    private int _nextBookmarkId = 1;

    /// <summary>
    /// Konwertuje HTML na plik DOCX
    /// </summary>
    public byte[] Convert(string html, DocumentMetadata? metadata = null, HeaderFooterContent? header = null, HeaderFooterContent? footer = null, PageMargins? margins = null, Domain.Models.PageSize? pageSize = null, IReadOnlyList<SectionHeaderFooter>? sectionHeadersFooters = null, IReadOnlyList<DomainFootnote>? footnotes = null, IReadOnlyList<DomainEndnote>? endnotes = null, string? footnoteNumberFormat = null, string? endnoteNumberFormat = null)
    {
        using var memoryStream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(memoryStream, WordprocessingDocumentType.Document))
        {
            _mainPart = document.AddMainDocumentPart();
            _mainPart.Document = new Document();

            // Stan sekcji per konwersja: pierwsza sekcja = geometria z argumentów;
            // markery w HTML nadpisują ją dla kolejnych sekcji.
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
            _numberingPart = null; // część należy do POPRZEDNIEGO pakietu — EnsureNumberingPart utworzy nową
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
            _openFieldMarkerCount = 0;
            _nextBookmarkId = 1;

            var body = new Body();
            _mainPart.Document.Body = body;

            // Parsuj HTML PRZED stylami — kontener .document-content niesie domyślny font
            // (inline style) i odstępy akapitowe dokumentu (data-default-*), które muszą
            // trafić do docDefaults generowanego pakietu. Wcześniej każdy zapis podmieniał
            // je na hardkodowane 11pt / after=160 / line=259 (tekst malał, wiersze tabel
            // ze stylem Worda puchły, bo styl tabeli nie istnieje w regenerowanym pakiecie).
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);
            CaptureDocumentDefaults(htmlDoc);
            // Kolumny sekcji bazowej (0) z kontenera → geometria pierwszej sekcji. W dokumencie
            // wielosekcyjnym pierwszy marker zamknie tę sekcję z jej kolumnami; sekcje ≥1 niosą
            // własne kolumny w markerach (ADR-0039).
            _currentSection.Columns = _docDefaultColumns;

            // Dodaj style dokumentu
            AddDocumentStyles(document);

            ConvertHtmlToBody(htmlDoc.DocumentNode, body);

            // Strażnik balansu pól złożonych: pole otwarte markerem begin (TOC/PAGEREF),
            // którego marker końca zniknął w edycji, domykamy na końcu treści — niedomknięty
            // fldChar Begin uszkadza dokument w Wordzie.
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

            // Ustaw metadane
            if (metadata != null)
            {
                SetDocumentMetadata(document, metadata);
            }

            // Dodaj nagłówek i stopkę
            AddHeaderAndFooter(document, header, footer);

            // Dodaj ustawienia strony
            AddPageSettings(body, header, footer, margins, pageSize);

            // Własne nagłówki/stopki sekcji ≥ 1 (po AddPageSettings — body-level sectPr istnieje)
            AddSectionHeadersFooters(document, sectionHeadersFooters);

            // Część przypisów (footnotes.xml + relacja + content type) — tylko gdy dokument ma przypisy.
            AddFootnotes(footnotes);

            // Część przypisów końcowych (endnotes.xml + relacja + content type) — analogicznie.
            AddEndnotes(endnotes);

            // Format numeracji przypisów POKAZYWANY w edytorze → jawny w:numFmt w settings.xml
            // (plik musi wyglądać jak ekran; bez tego Word wracał do swoich domyślnych).
            ApplyNoteNumberFormats(document, footnoteNumberFormat, endnoteNumberFormat);

            document.Save();
        }

        return memoryStream.ToArray();
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
            // Pass-through is best-effort: a malformed / unexpected original must never break the
            // save — fall back to the fully self-contained generated document.
            return generated;
        }
    }

    /// <summary>
    /// Replaces the generated package's styles.xml, theme and fontTable with the originals so the
    /// full style set (incl. table styles) and the document/theme fonts survive the round-trip.
    /// Body, sectPr, headers/footers, images and numbering remain as generated (they carry the
    /// editor's actual changes and standard style IDs that exist in the original styles.xml).
    /// </summary>
    private static byte[] PreserveOriginalParts(byte[] generated, Stream originalPackage)
    {
        var ms = new MemoryStream();
        ms.Write(generated, 0, generated.Length);
        ms.Position = 0;

        if (originalPackage.CanSeek) originalPackage.Position = 0;

        using (var original = WordprocessingDocument.Open(originalPackage, false))
        using (var target = WordprocessingDocument.Open(ms, true))
        {
            var origMain = original.MainDocumentPart;
            var targetMain = target.MainDocumentPart;
            if (origMain == null || targetMain == null)
                return generated;

            // styles.xml — pełny zestaw stylów (w tym ~100 stylów tabel) + docDefaults (font/theme).
            // FeedData do ISTNIEJĄCEGO partu zachowuje kanoniczną nazwę (styles.xml) i relację;
            // nie odwołujemy się do .Styles, więc Save nie nadpisze strumienia zserializowanym DOM.
            if (origMain.StyleDefinitionsPart != null)
            {
                var styles = targetMain.StyleDefinitionsPart ?? targetMain.AddNewPart<StyleDefinitionsPart>();
                using var s = origMain.StyleDefinitionsPart.GetStream(FileMode.Open, FileAccess.Read);
                styles.FeedData(s);
            }

            // theme — definicje theme fonts/colors (np. minor=Cambria), do których odwołują się
            // docDefaults (asciiTheme=minorHAnsi). Musi być spójny ze stylami.
            if (origMain.ThemePart != null)
            {
                var theme = targetMain.ThemePart ?? targetMain.AddNewPart<ThemePart>();
                using var s = origMain.ThemePart.GetStream(FileMode.Open, FileAccess.Read);
                theme.FeedData(s);
            }

            // fontTable — tabela fontów używanych w dokumencie.
            if (origMain.FontTablePart != null)
            {
                var fonts = targetMain.FontTablePart ?? targetMain.AddNewPart<FontTablePart>();
                using var s = origMain.FontTablePart.GetStream(FileMode.Open, FileAccess.Read);
                fonts.FeedData(s);
            }

            PreserveNoteProperties(origMain, targetMain);

            target.Save();
        }

        return ms.ToArray();
    }

    /// <summary>Token w:numFmt przypisów (podzbiór wspierany przez GUI) → wartość OOXML.</summary>
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

        // Sekwencja CT_Settings: footnotePr przed endnotePr, oba za evenAndOddHeaders.
        settings.RemoveAllChildren<FootnoteDocumentWideProperties>();
        settings.RemoveAllChildren<EndnoteDocumentWideProperties>();
        if (footnoteFmt != null)
            settings.AppendChild(new FootnoteDocumentWideProperties(new NumberingFormat { Val = footnoteFmt }));
        if (endnoteFmt != null)
            settings.AppendChild(new EndnoteDocumentWideProperties(new NumberingFormat { Val = endnoteFmt }));
        settings.Save();
    }

    /// <summary>
    /// FALLBACK dla klientów niewysyłających formatu z modelu: przenosi w:footnotePr /
    /// w:endnotePr (m.in. w:numFmt) z oryginalnego settings.xml do wygenerowanego pakietu —
    /// regeneracja gubiła te elementy i Word wracał do domyślnej numeracji endnotes
    /// (lowerRoman). Element już obecny w celu (ApplyNoteNumberFormats — format z edytora)
    /// WYGRYWA i nie jest nadpisywany. Kopiujemy wybiórczo (nie cały settings.xml): pełna
    /// kopia przywracałaby documentProtection i nadpisywała evenAndOddHeaders.
    /// </summary>
    private static void PreserveNoteProperties(MainDocumentPart origMain, MainDocumentPart targetMain)
    {
        var origSettings = origMain.DocumentSettingsPart?.Settings;
        var origFootnotePr = origSettings?.GetFirstChild<FootnoteDocumentWideProperties>();
        var origEndnotePr = origSettings?.GetFirstChild<EndnoteDocumentWideProperties>();

        // Sekcyjny override (w:sectPr/w:footnotePr|w:endnotePr) ma w Wordzie pierwszeństwo,
        // a regenerowany sectPr go nie niesie — przenosimy go na poziom document-wide
        // (ten sam efekt wizualny dla dokumentów o jednym formacie przypisów).
        var origFirstSect = origMain.Document?.Body?.Descendants<SectionProperties>().FirstOrDefault();
        var sectFootnotePr = origFirstSect?.GetFirstChild<FootnoteProperties>();
        var sectEndnotePr = origFirstSect?.GetFirstChild<EndnoteProperties>();

        var footnotePr = sectFootnotePr != null
            ? new FootnoteDocumentWideProperties(sectFootnotePr.ChildElements.Select(c => c.CloneNode(true)))
            : (FootnoteDocumentWideProperties?)origFootnotePr?.CloneNode(true);
        var endnotePr = sectEndnotePr != null
            ? new EndnoteDocumentWideProperties(sectEndnotePr.ChildElements.Select(c => c.CloneNode(true)))
            : (EndnoteDocumentWideProperties?)origEndnotePr?.CloneNode(true);

        // Odwołania do przypisów-separatorów (<w:footnote w:id="-1"/> itd.) wskazują ID
        // w CZĘŚCIACH pakietu oryginału — w regenerowanym pakiecie część może nie istnieć
        // (dokument bez przypisów dolnych) i Word zgłasza uszkodzenie „Przypisy dolne".
        // Separatory Word odtwarza z domyślnych, więc odwołania są zbędne.
        footnotePr?.RemoveAllChildren<FootnoteSpecialReference>();
        endnotePr?.RemoveAllChildren<EndnoteSpecialReference>();
        if (footnotePr is { HasChildren: false }) footnotePr = null;
        if (endnotePr is { HasChildren: false }) endnotePr = null;
        if (footnotePr == null && endnotePr == null) return;

        var settingsPart = targetMain.DocumentSettingsPart ?? targetMain.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings ??= new Settings();
        var settings = settingsPart.Settings;

        // Format z modelu edytora (ApplyNoteNumberFormats) ma pierwszeństwo — dopełniamy
        // tylko brakujące elementy. Sekwencja CT_Settings: footnotePr przed endnotePr,
        // oba za evenAndOddHeaders; endnotePr dopinany na końcu jest zawsze poprawny,
        // footnotePr wstawiamy przed istniejącym endnotePr.
        if (footnotePr != null && settings.GetFirstChild<FootnoteDocumentWideProperties>() == null)
        {
            if (settings.GetFirstChild<EndnoteDocumentWideProperties>() is { } existingEndnotePr)
                settings.InsertBefore(footnotePr, existingEndnotePr);
            else
                settings.AppendChild(footnotePr);
        }
        if (endnotePr != null && settings.GetFirstChild<EndnoteDocumentWideProperties>() == null)
            settings.AppendChild(endnotePr);
        settings.Save();
    }

    /// <summary>
    /// Writes the document's headers and footers. Each variant (default / first / even) is
    /// emitted independently as its own part with a type=Default/First/Even reference, and
    /// the section/settings opt-ins (titlePg, evenAndOddHeaders) are written so Word and a
    /// round-trip import pick them up — including titlePg with a blank first-page band.
    /// </summary>
    private void AddHeaderAndFooter(WordprocessingDocument document, HeaderFooterContent? header, HeaderFooterContent? footer)
    {
        if (_mainPart == null) return;

        if (header != null)
        {
            // Variants are written independently of the default part: a titlePg document
            // may legitimately have a first-page header and NO default one (blank ordinary
            // pages) — skipping variants when Html is empty destroyed them on first save.
            if (!string.IsNullOrWhiteSpace(header.Html))
                WriteHeaderPart(header.Html, HeaderFooterValues.Default);

            if (header.DifferentFirstPage)
            {
                // titlePg must survive even when the first-page band is blank; dropping it
                // would leak the default header onto page 1 after reopening in Word.
                // Null FirstPageHtml = no explicit part (Word inherits/blank), empty = blank part.
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

        // Image relationships must be scoped to THIS part — Word won't resolve cross-part rIds.
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

        // CT_HdrFtr requires at least one block-level element — a blank band (titlePg with
        // an intentionally empty first page) is expressed as a single empty paragraph.
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

        // See WriteHeaderPart: CT_HdrFtr requires at least one block-level element.
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

        // CT_SectPr sequence: titlePg precedes textDirection/bidi/rtlGutter/docGrid/
        // printerSettings — a plain Append lands after docGrid on preserved packages.
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

    /// <summary>
    /// sectPr, do którego wpinamy referencje nagłówka/stopki i titlePg: PIERWSZY sectPr
    /// w kolejności dokumentu. W dokumencie wielosekcyjnym to sectPr pierwszego markera —
    /// sekcje bez własnych referencji dziedziczą je w Wordzie z poprzedniej sekcji; gdyby
    /// referencje trafiły tylko do body-level sectPr (ostatnia sekcja), wcześniejsze strony
    /// nie miałyby nagłówka/stopki.
    /// </summary>
    private SectionProperties? GetReferenceSectionProps() => _firstSectionProps ?? GetOrCreateSectionProps();

    /// <summary>
    /// Konwertuje HTML na elementy nagłówka/stopki.
    /// Akceptuje wszystkie tagi obsługiwane przez body (h1-6, p, ul/ol, table, img, blockquote, hr).
    /// Treść która nie tworzy block-level (np. tekst bez paragrafu, samodzielny &lt;span&gt;) jest
    /// pakowana w domyślny &lt;p&gt;. Header w DOCX nie może zawierać lużnych Runów.
    /// </summary>
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
                    // pusty paragraf po flushy nie powinien być dodawany
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
                    // Inline-level w header/footer — pakujemy do bieżącego (lub nowego) paragrafu.
                    // Specjalne klasy field-page / field-numpages = pole liczby strony.
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
                        // Reużyj CreateRunsFromNode z dziedziczeniem stylu rodzica (span style=...)
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
                    // Pass-through (ADR-0056): podgląd VML/OLE z data-docx-xml → oryginalny
                    // fragment zamiast konwersji podglądowego SVG (który jako a:blip psuje pakiet).
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
                    // Pass-through grafiki XML (ADR-0056): kształt/grupa w paśmie wraca 1:1
                    // z data-docx-xml (dotąd generyczna rekursja gubiła go przy każdym zapisie).
                    if (child.GetAttributeValue("data-docx-xml", "") != ""
                        && TryRestorePreservedElement(child) is { } preservedHfBlock)
                    {
                        FlushPending();
                        parent.Append(new Paragraph(new Run(preservedHfBlock)));
                        break;
                    }
                    // Pole tekstowe (np. adres w stopce Qutasator) — do bufora, przypinane do
                    // następnego akapitu; pozostałe kontenery — zejdź w dzieci.
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
                    // nieznany tag — traktujemy jak inline (zejdź w dzieci do bieżącego paragrafu)
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

        // Pola tekstowe bez akapitu-następnika (np. textbox jako ostatni element pasma) —
        // domykający akapit staje się ich akapitem-kotwicą.
        FlushPendingTextBoxesInto(parent);

        // Header/Footer MUSZĄ zawierać co najmniej jeden block-level (np. Paragraph),
        // inaczej Word odmówi otwarcia dokumentu.
        if (!parent.Elements<Paragraph>().Any() && !parent.Elements<Table>().Any())
        {
            parent.Append(new Paragraph());
        }

        // Każdy paragraf w header/footer bez własnego stylu otrzymuje domyślny
        // styl sekcji ("Header"/"Footer"), tak jak robi to Word natywnie.
        // Dzięki temu czcionka i odstępy są zgodne z konwencją Worda
        // i tekst nie pojawia się jako "Normal".
        // WYJĄTEK (bug 13261178): akapit z WŁASNYMI w:tabs (odtworzonymi z data-tab-stops =
        // kompletny efektywny zestaw) nie może dostać stylu, którego definicja niesie stopy —
        // stopy stylu SUMUJĄ się z bezpośrednimi (ECMA-376). Nasza definicja Header/Footer
        // jest już bez stopów, ale ConvertPreservingPackage podmienia styles.xml na ORYGINALNY,
        // gdzie wordowy Header/Footer ma 4536:center/9072:right — tabulator pasma przeskakiwał
        // wtedy na dodany stop przy każdym zapisie.
        if (_currentSectionStyleId != null)
        {
            foreach (var p in parent.Elements<Paragraph>())
            {
                if (p.ParagraphProperties?.GetFirstChild<Tabs>() != null) continue;
                ApplyDefaultSectionStyle(p, _currentSectionStyleId);
            }
        }
    }

    /// <summary>
    /// Ustawia <c>ParagraphStyleId</c> dla paragrafu, o ile nie ma już ustawionego stylu.
    /// Używane dla paragrafów w nagłówku/stopce, by przejęły styl "Header"/"Footer".
    /// </summary>
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

    /// <summary>
    /// Wpina WŁASNE nagłówki/stopki sekcji ≥ 1 na ich sectPr: sekcja o indeksie s (0-based)
    /// → _emittedSectionProps[s] (paragraph-level sectPr zamykający tę sekcję) albo body-level
    /// sectPr, gdy s to sekcja ostatnia. Sekcja 0 (bazowa) idzie standardową ścieżką
    /// AddHeaderAndFooter → pierwszy sectPr; sekcje bez wpisu dziedziczą w Wordzie.
    /// </summary>
    private void AddSectionHeadersFooters(WordprocessingDocument document, IReadOnlyList<SectionHeaderFooter>? sections)
    {
        if (sections == null || sections.Count == 0) return;
        var body = _mainPart?.Document?.Body;
        if (body == null) return;
        var bodySectPr = body.Elements<SectionProperties>().FirstOrDefault();

        foreach (var entry in sections)
        {
            if (entry.SectionIndex < 1) continue; // sekcja 0 = pola bazowe Header/Footer

            SectionProperties? target = null;
            if (entry.SectionIndex < _emittedSectionProps.Count)
                target = _emittedSectionProps[entry.SectionIndex];
            else if (entry.SectionIndex == _emittedSectionProps.Count)
                target = bodySectPr; // ostatnia sekcja
            if (target == null) continue; // markery usunięte z treści → sekcja nie istnieje

            // See AddHeaderAndFooter: variants are independent of the default part (empty
            // Html = the section inherits the default in Word), and titlePg survives even
            // with no explicit first part (null FirstPageHtml = inherit from previous section).
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

    /// <summary>
    /// Odczytuje domyślne wartości dokumentu z kontenera .document-content wygenerowanego
    /// przez DocxToHtmlConverter: inline font-family/font-size oraz data-default-* z odstępami
    /// akapitowymi docDefaults oryginału. Bez kontenera pola zostają null (fallback konfiguracja).
    /// </summary>
    private void CaptureDocumentDefaults(HtmlDocument htmlDoc)
    {
        var container = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class,'document-content')]");
        if (container == null) return;

        var style = container.GetAttributeValue("style", "");
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // Pierwszy krój z listy font-family (reader emituje 'Nazwa',fallback).
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
    }

    /// <summary>
    /// Odtwarza <see cref="ColumnLayout"/> z atrybutów data-col-* (kontener .document-content
    /// albo marker div.docx-section-break). Zwraca null, gdy brak data-col-count &gt; 1
    /// (układ jednokolumnowy — ADR-0039).
    /// </summary>
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

    /// <summary>Domyślne odstępy akapitowe pakietu: z kontenera dokumentu albo fallback Worda.</summary>
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

    /// <summary>
    /// Dodaje domyślne style do dokumentu z dokładnym odwzorowaniem
    /// </summary>
    private void AddDocumentStyles(WordprocessingDocument document)
    {
        var stylesPart = _mainPart!.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        // Domyślny krój/rozmiar: z kontenera dokumentu (reader emituje je z docDefaults
        // oryginału), a gdy brak — firmowa czcionka z konfiguracji (DocumentDefaults).
        var bodyFont = _docDefaultFontFamily
            ?? (string.IsNullOrWhiteSpace(_defaults.FontFamily) ? "Calibri" : _defaults.FontFamily);
        var headingFont = string.IsNullOrWhiteSpace(_defaults.HeadingFontFamily) ? bodyFont : _defaults.HeadingFontFamily;
        // Rozmiar w DOCX jest podawany w pół-punktach (1pt = 2 jednostki).
        var fontSizePt = _docDefaultFontSizePt ?? _defaults.FontSizePt;
        var halfPt = ((int)Math.Round(fontSizePt * 2)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Domyślne właściwości dokumentu
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

        // Styl normalny
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

        // Style nagłówków z dokładnymi rozmiarami jak w Word
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
            
            // Kolejność w <w:pPr> wg schematu (EG_PPrBase): keepNext → keepLines → spacing → outlineLvl.
            var paraProps = new StyleParagraphProperties(
                new KeepNext(),
                new KeepLines(),
                new SpacingBetweenLines { Before = headingSpaceBefore[i - 1].ToString(), After = "0" },
                new OutlineLevel { Val = i - 1 }
            );
            headingStyle.Append(paraProps);
            
            // Kolejność dzieci w <w:rPr> jest narzucona schematem OOXML (EG_RPrBase):
            // rFonts → b → i → … → color → … → sz. Zła kolejność (np. sz przed color, b po color)
            // = błąd schematu → Word zgłasza „dokument uszkodzony". Emitujemy w poprawnej sekwencji.
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

        // Styl hiperłącza
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

        // Styl akapitu listy
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

        // Styl Nagłówka (Header) — wbudowany styl Worda, używany dla treści
        // nagłówka strony (odstępy zgodne z konwencją Worda).
        // UWAGA: styl NIE deklaruje tab-stopów (Word ma tu 4536:center/9072:right).
        // Tab-stopy stylu SUMUJĄ się z bezpośrednimi w:tabs akapitu (ECMA-376),
        // a kompletny efektywny zestaw każdego akapitu i tak wraca per akapit
        // z data-tab-stops — stopy w stylu DODAWAŁY pozycje, których dokument
        // źródłowy nie miał, i po pierwszym zapisie tabulator stopki/nagłówka
        // przeskakiwał na 4536:center zamiast własnego stopu (bug 13261178).
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

        // Styl Stopki (Footer) — bez tab-stopów, patrz komentarz przy stylu Header.
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

        // Domyślny styl Run (wymagany jako bazowy dla LinkedStyle)
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

    /// <summary>
    /// Konwertuje węzły HTML na elementy Body
    /// </summary>
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

        // Pola tekstowe bez akapitu-następnika (textbox na samym końcu treści) —
        // dopisz akapit-kotwicę, żeby drawing nie przepadł.
        FlushPendingTextBoxesInto(body);

        if (!body.Elements<Paragraph>().Any() && !body.Elements<Table>().Any())
        {
            body.Append(new Paragraph());
        }
    }

    /// <summary>
    /// Konwertuje węzeł HTML na elementy OpenXML
    /// </summary>
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

    /// <summary>
    /// Konwertuje element HTML na elementy OpenXML z pełnym odwzorowaniem
    /// </summary>
    private List<OpenXmlElement> ConvertHtmlElement(HtmlNode node)
    {
        var elements = new List<OpenXmlElement>();
        var tagName = node.Name.ToLower();

        // Pass-through grafiki XML (ADR-0056): element z data-docx-xml (div.docx-shape,
        // span.docx-preserved, img podglądu VML/OLE) niesie oryginalny OOXML — odtwarzamy
        // 1:1 zamiast stratnej konwersji HTML (dotąd kształty ginęły w generycznej rekursji).
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
                    // Pole tekstowe Worda — NIE spłaszczaj do zwykłych akapitów (tak ginęła
                    // cała ramka przy pierwszym autosave). Drawing czeka w buforze i zostanie
                    // przypięty do NASTĘPNEGO akapitu (reader emituje div bezpośrednio przed
                    // akapitem-kotwicą — patrz DocxToHtmlConverter.HoistTextBox).
                    BufferTextBoxDrawing(node);
                }
                else if (IsSectionBreakNode(node))
                {
                    elements.Add(CreateSectionBreakParagraph(node));
                }
                else if (node.HasClass("docx-column-break"))
                {
                    // Podział kolumny hoistowany przez przeglądarkę do bloku top-level (po edycji)
                    // → akapit z runem Break type=column. Wewnątrz akapitu obsługuje CreateRunsFromNode.
                    elements.Add(new Paragraph(new Run(new Break { Type = BreakValues.Column })));
                }
                else if (IsPageBreakNode(node))
                {
                    // Marker sekcji tuż za page-breakiem = przerwa sekcji typu nextPage;
                    // sectPr sam łamie stronę, dodatkowy w:br type=page dawałby pustą stronę.
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

    /// <summary>
    /// Konwertuje element P na Paragraph z pełnym parsowaniem stylów
    /// </summary>
    private Paragraph ConvertParagraphElement(HtmlNode node)
    {
        var paragraph = new Paragraph();
        var props = new ParagraphProperties();

        var style = node.GetAttributeValue("style", "");
        ApplyParagraphStyle(props, style);

        // Sprawdź data-style-id dla zachowania oryginalnego stylu Word
        var styleId = node.GetAttributeValue("data-style-id", "");
        if (!string.IsNullOrEmpty(styleId))
        {
            props.Append(new ParagraphStyleId { Val = styleId });
        }

        // Tab-stopy per akapit (reader: data-tab-stops="pos:align[:leader];…", pos w twips) —
        // odtwarzane jako w:tabs, żeby pozycje/wyrównania/leadery nie ginęły przy zapisie.
        var tabStopsAttr = node.GetAttributeValue("data-tab-stops", "");
        if (!string.IsNullOrEmpty(tabStopsAttr) && ParseTabStops(tabStopsAttr) is { } tabs)
        {
            props.Append(tabs);
        }

        // pStyle/tabs dokładane są PO ApplyParagraphStyle — przywróć kolejność schematu
        // (pStyle pierwsze, tabs przed spacing/jc).
        NormalizeParagraphPropertiesOrder(props);

        if (props.HasChildren)
            paragraph.Append(props);

        // Pola tekstowe zbuforowane przez poprzedzające je div.docx-textbox — ten akapit
        // jest ich akapitem-kotwicą (run z drawingiem na początku treści akapitu).
        AttachPendingTextBoxes(paragraph);

        AppendInlineContent(paragraph, node);

        return paragraph;
    }

    /// <summary>
    /// Konwertuje nagłówek na Paragraph z odpowiednim stylem
    /// </summary>
    private Paragraph ConvertHeadingElement(HtmlNode node, int level)
    {
        var paragraph = new Paragraph();
        var props = new ParagraphProperties();
        props.Append(new ParagraphStyleId { Val = $"Heading{level}" });

        // PEŁNE właściwości akapitu z inline CSS (gap-analysis pkt 1): dotąd nagłówki szły
        // przez ApplyParagraphStyleExtras (sam text-align) — spacing/wcięcia/tło/ramki/
        // pageBreakBefore GINĘŁY przy każdym zapisie.
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

    /// <summary>
    /// Konwertuje listę na paragrafy z prawidłową definicją numeracji Word
    /// </summary>
    private List<OpenXmlElement> ConvertListElement(HtmlNode node, bool ordered, int level = 0, int? parentNumId = null)
    {
        var elements = new List<OpenXmlElement>();

        // Fragment listy może zaczynać się na głębszym poziomie (data-ilvl z readera, np.
        // kontynuacja poziomu 1 po zwykłym akapicie) — bez tego eksport spłaszczał go do
        // ilvl=0: złe wcięcie i format poziomu 0 zamiast właściwego.
        level = ResolveListLevel(node, level);

        int numId;
        if (parentNumId.HasValue)
        {
            // Zagnieżdżona lista — współdziel numId z rodzicem
            numId = parentNumId.Value;
        }
        else
        {
            EnsureNumberingPart();

            // Tożsamość listy z readera: fragmenty tej samej listy logicznej (np. rozdzielone
            // zwykłym akapitem) niosą ten sam data-num-id → współdzielą jedną NumberingInstance,
            // więc Word kontynuuje numerację. Bez atrybutu (lista utworzona w edytorze) — nowa.
            var htmlListId = node.GetAttributeValue("data-num-id", "");
            if (htmlListId.Length > 0 && _numIdByHtmlList.TryGetValue(htmlListId, out var existingNumId))
            {
                numId = existingNumId;
            }
            else
            {
                // Przeskanuj strukturę listy aby określić format dla każdego poziomu
                var levelFormats = new Dictionary<int, bool>();
                ScanListLevels(node, ordered, level, levelFormats);

                // Wykryj poziomy z punktatorem obrazkowym (DocxToHtmlConverter wstawia
                // <span class="list-marker"><img .../></span> jako wizualny marker).
                // Osadzalny obraz (data URI) → prawdziwy w:numPicBullet + w:lvlPicBulletId;
                // nieosadzalny → jak dotąd: numFmt=none i obraz inline w treści.
                var pictureBulletLevels = new Dictionary<int, string?>();
                ScanPictureBulletLevels(node, level, pictureBulletLevels);

                // Definicje poziomów round-tripowane z readera (data-num-fmt / data-lvl-text /
                // data-start / data-bullet-font / data-suffix / data-is-legal / data-lvl-restart /
                // data-ind-*-tw) — zachowują oryginalny format numeracji zamiast hardkodowanej drabinki.
                var levelSpecs = new Dictionary<int, HtmlListLevelSpec>();
                ScanListLevelSpecs(node, level, levelSpecs);

                // Poziomy z data-lvl-override = pełne w:lvlOverride/w:lvl NA INSTANCJI —
                // ich definicje nie trafiają do abstraktu (inne instancje tego samego
                // abstraktu wyglądają inaczej).
                var overrideSpecs = levelSpecs
                    .Where(kv => kv.Value.IsLvlOverride)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var abstractSpecs = levelSpecs
                    .Where(kv => !kv.Value.IsLvlOverride)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var abstractPicLevels = pictureBulletLevels
                    .Where(kv => !overrideSpecs.ContainsKey(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                // Definicja (w:abstractNum) współdzielona między listami o tym samym
                // data-abstract-num-id; instancja (w:num) per data-num-id. „Rozpocznij od nowa"
                // = nowa instancja ze startOverride na wspólny abstrakt (FR-EXPORT-004),
                // a nie kopia definicji z przepisanym w:start.
                var htmlAbstractId = node.GetAttributeValue("data-abstract-num-id", "");
                int abstractNumId;
                if (htmlAbstractId.Length > 0 && _abstractIdByHtmlAbstract.TryGetValue(htmlAbstractId, out var existingAbstract))
                {
                    abstractNumId = existingAbstract;
                    // Ten fragment może używać poziomów, których fragment tworzący abstrakt
                    // nie widział (miały drabinkę domyślną) — dosyłamy ich definicje.
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

                // Pełne nadpisania poziomów budowane tym samym mechanizmem co poziomy abstraktu.
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
            
            // Sprawdź czy li zawiera zagnieżdżone listy
            var nestedLists = child.SelectNodes("./ul|./ol");
            
            // Utwórz paragraf z elementem listy
            var para = new Paragraph();
            var props = new ParagraphProperties();
            props.Append(new ParagraphStyleId { Val = "ListParagraph" });
            props.Append(new NumberingProperties(
                new NumberingLevelReference { Val = level },
                new NumberingId { Val = numId }
            ));
            
            // PEŁNE właściwości akapitu z inline CSS li (gap-analysis pkt 1): dotąd tylko
            // text-align — spacing/tło/ramki punktu listy GINĘŁY przy każdym zapisie.
            // BEZ sekcji wcięć: wcięcia list niesie kontrakt data-ind-*-tw
            // (AppendListItemIndentation), a margin-left na li to delta prezentacyjna.
            var liStyle = child.GetAttributeValue("style", "");
            if (!string.IsNullOrEmpty(liStyle))
            {
                ApplyParagraphStyle(props, liStyle, includeIndentation: false);
            }

            // Direct w:ind elementu listy (kontrakt data-ind-*-tw z readera) — bez tego
            // wcięcie nadpisane na akapicie wracałoby po zapisie do definicji poziomu.
            AppendListItemIndentation(props, child);

            // Kolor znacznika per pozycja (14104878): rPr ZNAKU KOŃCA AKAPITU — Word koloruje
            // nim numer/punktator. Bez odtworzenia kolor znacznika ginął przy pierwszym zapisie.
            var markColorRaw = child.GetAttributeValue("data-mark-color", "");
            var markSizeRaw = child.GetAttributeValue("data-mark-size", "");
            var hasMarkColor = Regex.IsMatch(markColorRaw, "^[0-9A-Fa-f]{6}$");
            var hasMarkSize = Regex.IsMatch(markSizeRaw, @"^\d{1,4}$");
            if (hasMarkColor || hasMarkSize)
            {
                // Jeden wspólny rPr znaku końca akapitu (kolejność EG_RPrBase: color przed sz).
                var markProps = new ParagraphMarkRunProperties();
                if (hasMarkColor) markProps.Append(new Color { Val = markColorRaw });
                if (hasMarkSize) markProps.Append(new FontSize { Val = markSizeRaw });
                props.Append(markProps);
                NormalizeParagraphPropertiesOrder(props);
            }

            para.Append(props);

            // Buduj base RunProperties ze stylu <li> (dziedziczenie do span/text wewnątrz)
            RunProperties? liBaseProps = null;
            if (!string.IsNullOrEmpty(liStyle))
            {
                liBaseProps = new RunProperties();
                ApplyRunStyle(liBaseProps, liStyle);
                if (!liBaseProps.HasChildren) liBaseProps = null;
            }

            // Dodaj zawartość (bez zagnieżdżonej listy)
            foreach (var liChild in child.ChildNodes)
            {
                var liChildName = liChild.Name.ToLower();
                if (liChildName == "ul" || liChildName == "ol")
                    continue; // Zagnieżdżona lista będzie obsłużona osobno

                // <span class="list-marker"> jest artefaktem prezentacyjnym dodanym przez
                // DocxToHtmlConverter, żeby przeglądarka pokazała niestandardowy punktator
                // (obrazek lub znak Wingdings/Symbol). Przy eksporcie:
                //   - obrazek, którego poziom dostał PRAWDZIWY w:numPicBullet — pomijamy
                //     (Word narysuje go sam z definicji numeracji; inline run by go zdublował),
                //   - obrazek nieosadzalny (poziom z numFmt=none) — zachowaj jako wiodący
                //     inline run, żeby grafika nie zginęła,
                //   - tekstowy marker (np. ✓, ✗) pomijamy — Word wstawi własny automatyczny
                //     punktator z definicji numeracji.
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

            // Obsłuż zagnieżdżone listy — współdziel numId
            if (nestedLists != null)
            {
                foreach (var nestedList in nestedLists)
                {
                    var isOrdered = nestedList.Name.ToLower() == "ol";
                    // ResolveListLevel wewnątrz honoruje data-ilvl zagnieżdżonego kontenera
                    // (skoki poziomów, np. 0 → 2, są legalne w WordprocessingML).
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

    /// <summary>
    /// Definicja poziomu listy odczytana z data-* kontenera (round-trip z DocxToHtmlConverter).
    /// </summary>
    private readonly record struct HtmlListLevelSpec(
        string? Fmt, string? LvlText, int Start, string? BulletFont,
        int StartOverride, string? Suffix, bool IsLegal, int LvlRestart,
        string? IndLeftTw, string? IndHangingTw, string? IndFirstLineTw,
        bool IsLvlOverride, string? MarkerColor = null, string? MarkerSizeHalfPoints = null);

    /// <summary>
    /// Zbiera definicje poziomów z atrybutów data-* na kontenerach ul/ol (każdy zagnieżdżony
    /// kontener opisuje swój poziom): data-num-fmt / data-lvl-text / data-start / data-bullet-font
    /// / data-start-override / data-suffix / data-is-legal / data-lvl-restart / data-ind-*-tw.
    /// Pierwsze napotkane wystąpienie poziomu wygrywa.
    /// </summary>
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

    /// <summary>
    /// Efektywny poziom listy dla kontenera ul/ol: data-ilvl z readera (fragment listy może
    /// zaczynać się na GŁĘBSZYM poziomie, np. kontynuacja ilvl=1 po zwykłym akapicie),
    /// z fallbackiem do poziomu wynikającego z zagnieżdżenia HTML.
    /// </summary>
    private static int ResolveListLevel(HtmlNode node, int fallback)
    {
        var raw = node.GetAttributeValue("data-ilvl", "");
        if (int.TryParse(raw, out var ilvl) && ilvl is >= 0 and <= 8)
            return ilvl;
        return Math.Clamp(fallback, 0, 8);
    }

    /// <summary>Mapuje token data-num-fmt (nazwy w:numFmt) na NumberFormatValues.</summary>
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
                // Token spoza mapy pochodzi z data-num-fmt readera (surowa wartość w:numFmt
                // z importowanego dokumentu: ordinal/cardinalText/ordinalText/chicago/
                // formaty językowe…) — odtwórz 1:1 zamiast degradować do decimal
                // (pkt 22.10 specyfikacji list). Guard na kształt tokenu odsiewa śmieci
                // z ręcznie edytowanego HTML.
                if (Regex.IsMatch(token, "^[a-zA-Z][a-zA-Z0-9]*$"))
                {
                    fmt = new NumberFormatValues(token);
                    return true;
                }
                fmt = NumberFormatValues.Decimal;
                return false;
        }
    }

    /// <summary>
    /// Skanuje strukturę HTML listy aby określić format (ordered/unordered) dla każdego poziomu zagnieżdżenia
    /// </summary>
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

    /// <summary>
    /// Wykrywa poziomy listy, które używają punktatora obrazkowego — czyli mają
    /// <c>&lt;span class="list-marker"&gt;&lt;img/&gt;&lt;/span&gt;</c> wewnątrz &lt;li&gt;.
    /// Wartość = data URI obrazu (eksport jako w:numPicBullet) albo null, gdy źródło nie jest
    /// osadzalne — wtedy fallback: obraz zostaje inline w treści, poziom bez markera Worda.
    /// </summary>
    private static void ScanPictureBulletLevels(HtmlNode node, int level, Dictionary<int, string?> pictureBulletLevels)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.Name.ToLower() != "li") continue;

            // Sprawdź bezpośrednie dzieci <li>, czy któreś z nich jest markerem obrazkowym.
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

    /// <summary>
    /// Czy element to <c>&lt;span class="list-marker"&gt;</c> emitowany przez DocxToHtmlConverter.
    /// </summary>
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

    /// <summary>
    /// Upewnia się że dokument ma NumberingDefinitionsPart
    /// </summary>
    private void EnsureNumberingPart()
    {
        if (_numberingPart != null) return;
        
        _numberingPart = _mainPart!.AddNewPart<NumberingDefinitionsPart>();
        _numberingPart.Numbering = new Numbering();
        _numberingPart.Numbering.Save();
    }

    /// <summary>
    /// Tworzy definicję abstrakcyjnej numeracji
    /// </summary>
    private int CreateAbstractNumbering(
        Dictionary<int, bool> levelFormats,
        Dictionary<int, string?>? pictureBulletLevels = null,
        Dictionary<int, HtmlListLevelSpec>? levelSpecs = null)
    {
        var abstractNumId = _numberingId++;

        var abstractNum = new AbstractNum { AbstractNumberId = abstractNumId };

        // Dodaj wymagany identyfikator Nsid dla poprawnej obsługi numeracji w Word
        var nsidValue = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
        abstractNum.Append(new Nsid { Val = nsidValue });
        abstractNum.Append(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel });

        var exportedPicBulletLevels = new HashSet<int>();
        var specLevels = new HashSet<int>();

        // Zdefiniuj 9 poziomów — każdy poziom ma format zgodny ze strukturą HTML
        for (int lvl = 0; lvl < 9; lvl++)
        {
            var isOrdered = ResolveLevelOrdered(levelFormats, lvl);
            HtmlListLevelSpec? spec = levelSpecs != null && levelSpecs.TryGetValue(lvl, out var s) ? s : null;
            string? picBulletUri = null;
            var hasPicBulletMarker = pictureBulletLevels != null
                && pictureBulletLevels.TryGetValue(lvl, out picBulletUri);

            // Poziomy zbudowane z jawnych data-* notujemy — późniejszy fragment współdzielący
            // abstrakt może dosłać definicje poziomów, których ten fragment nie używał
            // (UpgradeSharedAbstractLevels podmienia wtedy poziomy z drabinki domyślnej).
            if (spec != null || hasPicBulletMarker) specLevels.Add(lvl);

            abstractNum.Append(BuildAbstractLevel(
                lvl, isOrdered, hasPicBulletMarker, picBulletUri, spec, exportedPicBulletLevels));
        }

        _specLevelsByAbstract[abstractNumId] = specLevels;
        if (exportedPicBulletLevels.Count > 0)
            _picBulletLevelsByAbstract[abstractNumId] = exportedPicBulletLevels;

        // Wstaw na początku (przed instancjami)
        var firstInstance = _numberingPart!.Numbering.Elements<NumberingInstance>().FirstOrDefault();
        if (firstInstance != null)
            _numberingPart.Numbering.InsertBefore(abstractNum, firstInstance);
        else
            _numberingPart.Numbering.Append(abstractNum);

        _numberingPart.Numbering.Save();
        return abstractNumId;
    }

    /// <summary>Format poziomu (ordered/unordered) — domyślnie jak poziom najwyższy.</summary>
    private static bool ResolveLevelOrdered(Dictionary<int, bool> levelFormats, int lvl) =>
        levelFormats.TryGetValue(lvl, out var fmt)
            ? fmt
            : (levelFormats.TryGetValue(0, out var defaultFmt) && defaultFmt);

    /// <summary>
    /// Buduje definicję pojedynczego poziomu (w:lvl) w kolejności sekwencji CT_Lvl — używane
    /// zarówno dla poziomów abstraktu, jak i pełnych nadpisań w:lvlOverride/w:lvl na instancji.
    /// </summary>
    private Level BuildAbstractLevel(int lvl, bool isOrdered, bool hasPicBulletMarker,
        string? picBulletUri, HtmlListLevelSpec? spec, HashSet<int> exportedPicBulletLevels)
    {
            var levelDef = new Level { LevelIndex = lvl };
            levelDef.Append(new StartNumberingValue { Val = spec?.Start ?? 1 });

            if (hasPicBulletMarker && picBulletUri != null
                && TryCreatePictureBullet(picBulletUri, out var numPicBulletId))
            {
                // Prawdziwy punktator graficzny (FR-EXPORT-006): znak z lvlText jest w Wordzie
                // zastępowany obrazem wskazanym przez w:lvlPicBulletId.
                levelDef.Append(new NumberingFormat { Val = NumberFormatValues.Bullet });
                levelDef.Append(new LevelText { Val = "" });
                levelDef.Append(new LevelPictureBulletId { Val = numPicBulletId });
                exportedPicBulletLevels.Add(lvl);
            }
            else if (hasPicBulletMarker)
            {
                // Obraz nieosadzalny (src nie jest data URI / SVG) — grafika zostaje inline
                // w treści runu, automatyczny marker Worda wyłączony.
                levelDef.Append(new NumberingFormat { Val = NumberFormatValues.None });
                levelDef.Append(new LevelText { Val = string.Empty });
            }
            else if (spec is { Fmt: not null } sp && TryMapNumFmt(sp.Fmt, out var mappedFmt))
            {
                // Round-trip z data-*: dokładny format/lvlText/font punktatora z oryginału.
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
                    // lvlText z placeholderem %N; bez niego (lub uszkodzony) — standardowe "%N.".
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
                
                // Standardowe definicje bullet Word z prawidłowymi czcionkami:
                var bulletType = lvl % 3;
                switch (bulletType)
                {
                    case 0: // Wypełnione kółko (Symbol)
                        levelDef.Append(new LevelText { Val = "\uF0B7" });
                        levelDef.Append(new NumberingSymbolRunProperties(
                            new RunFonts { Ascii = "Symbol", HighAnsi = "Symbol", Hint = FontTypeHintValues.Default }
                        ));
                        break;
                    case 1: // Puste kółko (Courier New)
                        levelDef.Append(new LevelText { Val = "o" });
                        levelDef.Append(new NumberingSymbolRunProperties(
                            new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New", ComplexScript = "Courier New", Hint = FontTypeHintValues.Default }
                        ));
                        break;
                    case 2: // Wypełniony kwadrat (Wingdings)
                        levelDef.Append(new LevelText { Val = "\uF0A7" });
                        levelDef.Append(new NumberingSymbolRunProperties(
                            new RunFonts { Ascii = "Wingdings", HighAnsi = "Wingdings", Hint = FontTypeHintValues.Default }
                        ));
                        break;
                }
            }
            
            // Kolejność schematu CT_Lvl (sequence!): start, numFmt, lvlRestart, isLgl, suff,
            // lvlText, lvlPicBulletId, lvlJc, pPr, rPr. Gałęzie wyżej dołożyły start/numFmt/
            // lvlText(/lvlPicBulletId) — nowe elementy wstawiamy tuż PO numFmt.
            if (spec is { } specProps && levelDef.GetFirstChild<NumberingFormat>() is { } numFmtAnchor)
            {
                OpenXmlElement anchor = numFmtAnchor;
                if (specProps.LvlRestart >= 0)
                {
                    // Surowa wartość w:lvlRestart (jednobazowa; 0 = poziom nigdy nie restartuje).
                    var lvlRestartEl = new LevelRestart { Val = specProps.LvlRestart };
                    levelDef.InsertAfter(lvlRestartEl, anchor);
                    anchor = lvlRestartEl;
                }
                if (specProps.IsLegal)
                {
                    // w:isLgl — etykieta „legal": wszystkie poziomy formatowane jako decimal.
                    var isLglEl = new IsLegalNumberingStyle();
                    levelDef.InsertAfter(isLglEl, anchor);
                    anchor = isLglEl;
                }
                if (specProps.Suffix is { } suffixToken)
                {
                    // w:suff — separator znacznik→tekst; tab jest domyślny, emitujemy odstępstwa.
                    levelDef.InsertAfter(new LevelSuffix
                    {
                        Val = suffixToken == "space" ? LevelSuffixValues.Space : LevelSuffixValues.Nothing
                    }, anchor);
                }
            }

            // w:rPr musi być OSTATNIM dzieckiem w:lvl — gałęzie dokładają go po lvlText,
            // więc przenosimy go za lvlJc/pPr (wcześniej lądował przed nimi, poza schematem).
            var markerRunProps = levelDef.GetFirstChild<NumberingSymbolRunProperties>();
            markerRunProps?.Remove();

            if (spec is { MarkerColor: not null } specWithColor)
            {
                markerRunProps ??= new NumberingSymbolRunProperties();
                // Kolejność EG_RPrBase: rFonts przed color.
                markerRunProps.Append(new Color { Val = specWithColor.MarkerColor });
            }
            if (spec is { MarkerSizeHalfPoints: not null } specWithSize)
            {
                // w:sz rozmiaru numeru/punktatora — bez odtworzenia rozmiar znacznika ginął
                // przy pierwszym zapisie (marker wracał do rozmiaru tekstu/domyślnego).
                markerRunProps ??= new NumberingSymbolRunProperties();
                markerRunProps.Append(new FontSize { Val = specWithSize.MarkerSizeHalfPoints });
            }

            levelDef.Append(new LevelJustification { Val = LevelJustificationValues.Left });

            // Wcięcia: dokładne twips z definicji poziomu oryginału (data-ind-*-tw); bez nich
            // dotychczasowa drabinka 720×(lvl+1) z wcięciem wiszącym 360.
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

    /// <summary>
    /// Dosyła do WSPÓŁDZIELONEGO abstraktu definicje poziomów, których fragment tworzący
    /// abstrakt nie używał (dostały drabinkę domyślną). Poziomy zbudowane wcześniej z jawnych
    /// data-* nie są podmieniane (pierwsza definicja wygrywa — spójnie z resztą kontraktu).
    /// Bezpieczne: fragment tworzący abstrakt nie miał elementów na upgradowanym poziomie
    /// (inaczej niosłyby data-*), więc wygląd żadnego wcześniejszego akapitu się nie zmienia.
    /// </summary>
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

    /// <summary>
    /// Tworzy (lub reużywa — deduplikacja po data URI) w:numPicBullet w części numeracji:
    /// ImagePart + relacja + VML v:shape/v:imagedata (wariant, który zapisuje sam Word).
    /// Zwraca false dla nieparsowalnego data URI lub SVG (imagedata na SVG psuje pakiet) —
    /// wtedy caller zostawia obraz inline w treści (kontrolowana degradacja, zero utraty).
    /// </summary>
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

        // Kolejność schematu w:numbering: numPicBullet* → abstractNum* → num*.
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

    /// <summary>
    /// Tworzy instancję numeracji. „Rozpocznij od nowa"/„Ustaw wartość" = w:lvlOverride
    /// z w:startOverride na instancji (FR-EXPORT-004) — restart bez kopiowania definicji.
    /// Pełne nadpisanie wyglądu poziomu (data-lvl-override) = w:lvlOverride z własnym w:lvl.
    /// </summary>
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
            // Sekwencja CT_NumLvl: w:startOverride, potem w:lvl.
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

    /// <summary>
    /// Konwertuje tabelę HTML na Table z pełnym odwzorowaniem stylów
    /// </summary>
    private Table ConvertTableElement(HtmlNode node)
    {
        var table = new Table();
        var tableProps = new TableProperties();

        // Domyślne obramowania — None (żadne linie), chyba że CSS tabeli jawnie definiuje `border:`.
        // Wcześniej wymuszaliśmy solid-black jako default, co powodowało fałszywe czarne linie
        // w tabelach, które w oryginalnym DOCX miały `w:tblBorders` z val=nil/none lub w ogóle bez
        // definicji (bordery per-komórka są w pełni opisane przez ApplyCellBorders).
        // Kolejność dzieci wg sekwencji CT_TblBorders: top, left, bottom, right, insideH,
        // insideV (bottom przed left = błąd walidacji — jak tcBorders w ADR-0031).
        var defaultBorders = new TableBorders(
            new TopBorder { Val = BorderValues.None, Size = 0 },
            new LeftBorder { Val = BorderValues.None, Size = 0 },
            new BottomBorder { Val = BorderValues.None, Size = 0 },
            new RightBorder { Val = BorderValues.None, Size = 0 },
            new InsideHorizontalBorder { Val = BorderValues.None, Size = 0 },
            new InsideVerticalBorder { Val = BorderValues.None, Size = 0 }
        );
        
        // Parsuj style tabeli
        var tableStyle = node.GetAttributeValue("style", "");

        // Referencja stylu tabeli Worda zachowana przez reader w data-* — emitujemy ją z powrotem.
        // w:tblStyle musi być PIERWSZYM dzieckiem tblPr (kolejność schematu). Rozwiązane wartości
        // stylu i tak są w inline CSS komórek, więc wygląd odtwarza formatowanie bezpośrednie,
        // a referencja stylu przeżywa round-trip (edycja w Wordzie dalej "widzi" styl).
        var tblStyleId = node.GetAttributeValue("data-tbl-style", "");
        if (!string.IsNullOrEmpty(tblStyleId))
            tableProps.Append(new TableStyle { Val = System.Net.WebUtility.HtmlDecode(tblStyleId) });

        // Szerokość (reader emituje też ułamkowe %: 66.66%). Marker data-tbl-w="auto" oznacza,
        // że px w CSS jest tylko renderowe (geometria z tblGrid), a oryginał miał tblW=auto —
        // wtedy nie wolno utrwalić dxa.
        var tableWidthIsAuto = node.GetAttributeValue("data-tbl-w", "") == "auto";
        var tableWidthMatch = Regex.Match(tableStyle, @"width:\s*([\d.]+)(px|%)?");
        if (tableWidthIsAuto)
        {
            tableProps.Append(new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto });
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
            // No explicit width (e.g. CSS width:auto) → AUTO, sizing to content/grid like Word.
            // Previously this forced 100% (pct 5000), which stretched content-sized tables to
            // full text width. Patrz analiza orginał_GOOD (tblW auto) vs zapisany_BAD (pct 5000).
            tableProps.Append(new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto });
        }

        // Wyrównanie tabeli
        if (tableStyle.Contains("margin-left:auto") && tableStyle.Contains("margin-right:auto"))
        {
            tableProps.Append(new TableJustification { Val = TableRowAlignmentValues.Center });
        }
        else if (tableStyle.Contains("margin-left:auto"))
        {
            tableProps.Append(new TableJustification { Val = TableRowAlignmentValues.Right });
        }

        // Odstęp między komórkami: preferuj dokładne twips z data-*, inaczej border-spacing px.
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
                    tableProps.Append(new TableCellSpacing { Width = ((int)Math.Round(spacingPx * 15)).ToString(), Type = TableWidthUnitValues.Dxa });
            }
        }

        // Wcięcie tabeli (margin-left w px, nie 'auto') → w:tblInd. Wcześniej gubione na eksporcie.
        var indentMatch = Regex.Match(tableStyle, @"margin-left:\s*(-?[\d.]+)px");
        if (indentMatch.Success)
        {
            var indentPx = double.Parse(indentMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(indentPx) > 0.01)
                tableProps.Append(new TableIndentation { Width = (int)Math.Round(indentPx * 15), Type = TableWidthUnitValues.Dxa });
        }

        // Parsuj obramowania tabeli z CSS
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

        // Tabela ze stylem Worda (data-tbl-style) i BEZ jawnego CSS-owego obramowania na
        // <table>: NIE emituj tblBorders — bezpośrednie val=none NADPISYWAŁO obramowania
        // stylu (Tabela – Siatka traciła linie w Wordzie), a przy ponownym otwarciu reader
        // znakował tabelę data-no-borders i strata się utrwalała. Jawny brak obramowań
        // oryginału niesie data-no-borders="1" — wtedy val=none jest zamierzone.
        var noBordersMarker = node.GetAttributeValue("data-no-borders", "") == "1";
        if (borderMatch.Success || noBordersMarker || string.IsNullOrEmpty(tblStyleId))
            tableProps.Append(defaultBorders);

        // Reader emituje table-layout:fixed dla tabel z geometrią kolumn z tblGrid —
        // wymuszanie Autofit gubiło układ Worda przy każdym zapisie (autosave!).
        // Marker data-tbl-layout="autofit" = fixed w CSS jest tylko renderowe.
        var isFixedLayout = tableStyle.Contains("table-layout:fixed")
            && node.GetAttributeValue("data-tbl-layout", "") != "autofit";
        tableProps.Append(new TableLayout { Type = isFixedLayout ? TableLayoutValues.Fixed : TableLayoutValues.Autofit });
        
        // Domyślne marginesy komórek = domyślne Worda (TableNormal): top/bottom=0, left/right=108
        // twips. Wcześniej hardkodowane 40/80 dodawało pionowy margines do każdej komórki (tabele
        // rosły w pionie). Per-komórkowe tcMar z CSS i tak nadpisują tę wartość.
        tableProps.Append(new TableCellMarginDefault(
            new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
            new TableCellLeftMargin { Width = 108, Type = TableWidthValues.Dxa },
            new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
            new TableCellRightMargin { Width = 108, Type = TableWidthValues.Dxa }
        ));

        // w:tblLook (flagi formatowania warunkowego stylu) — round-trip z data-tbl-look.
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

        // Oblicz liczbę kolumn
        var maxCols = 0;
        // Tylko wiersze TEJ tabeli (bezpośrednie lub w thead/tbody/tfoot) — `.//tr` schodziło
        // do tabel ZAGNIEŻDŻONYCH i duplikowało ich wiersze jako wiersze tabeli zewnętrznej.
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

        // Siatka tabeli. Szerokości kolumn pochodzą z <colgroup> (reader emituje je z tblGrid);
        // wcześniej grid był odtwarzany z samej LICZBY komórek bez szerokości, więc geometria
        // kolumn Worda ginęła przy każdym zapisie.
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

        // Przetwórz wiersze. HTML pomija komórki przykryte przez rowspan w kolejnych
        // wierszach — OOXML wymaga tam jawnych komórek kontynuacji (vMerge bez val).
        // Bez nich komórki przesuwały się w lewo i tabela była uszkodzona w Wordzie.
        // activeRowSpans: kolumna gridu → (pozostałe wiersze scalenia, rozpiętość kolumn).
        var activeRowSpans = new Dictionary<int, (int RemainingRows, int ColSpan)>();
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
                        row.Append(CreateVerticalMergeContinuationCell(span.ColSpan));
                        gridCursor += span.ColSpan;
                    }
                }

                // Właściwości wiersza (kolejność schematu trPr: gridBefore → gridAfter →
                // wBefore → wAfter → cantSplit → trHeight → tblHeader).
                // Wysokość: preferuj dokładne twips + regułę z data-* (round-trip bez strat
                // px→twips i bez gubienia hRule=exact); fallback: height/min-height px → atLeast.
                var rowStyle = rowNode.GetAttributeValue("style", "");
                var rowProps = new TableRowProperties();

                // Dystansowe <td data-grid-spacer> (reader: sloty siatki w:gridBefore/w:gridAfter)
                // wracają jako właściwości wiersza, NIE jako realne komórki — inaczej każdy zapis
                // zamieniałby wcięcie wiersza w siatce na pustą komórkę (zmiana struktury tabeli).
                var rowCellNodes = rowNode.SelectNodes("./td|./th");
                var gridBeforeSpan = GridSpacerSpan(rowCellNodes?.FirstOrDefault(), "before");
                var gridAfterSpan = GridSpacerSpan(rowCellNodes?.LastOrDefault(), "after");
                if (gridBeforeSpan > 0)
                    rowProps.Append(new GridBefore { Val = gridBeforeSpan });
                if (gridAfterSpan > 0)
                    rowProps.Append(new GridAfter { Val = gridAfterSpan });
                // wBefore/wAfter tylko gdy wszystkie objęte kolumny niosą dokładne twips z tblGrid.
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

                // Wiersz nagłówkowy powtarzany na kolejnych stronach (w:tblHeader).
                if (rowNode.GetAttributeValue("data-tbl-header", "") == "1")
                    rowProps.Append(new TableHeader());

                if (rowProps.HasChildren)
                    row.Append(rowProps);

                var cells = rowCellNodes;
                if (cells != null)
                {
                    foreach (var cellNode in cells)
                    {
                        // Spacer siatki (gridBefore/gridAfter) nie jest komórką — konsumuje
                        // wyłącznie sloty siatki (kursor), właściwości poszły do trPr wyżej.
                        if (cellNode.GetAttributeValue("data-grid-spacer", "") != "")
                        {
                            gridCursor += Math.Max(1, cellNode.GetAttributeValue("colspan", 1));
                            continue;
                        }

                        AppendPendingContinuations();

                        var cell = new TableCell();
                        var cellProps = new TableCellProperties();

                        // Colspan
                        var colspanAttr = cellNode.GetAttributeValue("colspan", "1");
                        if (!int.TryParse(colspanAttr, out var colspan) || colspan < 1) colspan = 1;
                        if (colspan > 1)
                            cellProps.Append(new GridSpan { Val = colspan });

                        // Rowspan
                        var rowspanAttr = cellNode.GetAttributeValue("rowspan", "1");
                        if (int.TryParse(rowspanAttr, out var rowspan) && rowspan > 1)
                        {
                            cellProps.Append(new VerticalMerge { Val = MergedCellValues.Restart });
                            activeRowSpans[gridCursor] = (rowspan - 1, colspan);
                            spansStartedThisRow.Add(gridCursor);
                        }
                        var cellStartColumn = gridCursor;
                        gridCursor += colspan;

                        // Parsuj style komórki
                        var cellStyle = cellNode.GetAttributeValue("style", "");
                        ApplyCellStyle(cellProps, cellStyle);

                        // Gdy wszystkie kolumny siatki pod komórką mają DOKŁADNE twips
                        // (data-w-tw, bez ręcznego resize), tcW = ich suma — spójne z
                        // w:tblGrid i bez dryfu zaokrągleń px→twips per zapis.
                        if (cellStartColumn + colspan <= colWidthsTwips.Count)
                        {
                            var spanned = colWidthsTwips.GetRange(cellStartColumn, colspan);
                            var tcW = cellProps.GetFirstChild<TableCellWidth>();
                            // Nie ruszamy szerokości procentowych (inna semantyka niż dxa).
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
                        
                        // Obramowania komórki
                        ApplyCellBorders(cellProps, cellStyle);

                        // Kolejność dzieci tcPr wg schematu (CT_TcPr) — elementy zbierane są
                        // z kilku miejsc (colspan/vMerge → ApplyCellStyle → ApplyCellBorders)
                        // i bez sortowania tcW lądował za gridSpan, a tcMar/vAlign przed
                        // tcBorders (błędy walidacji OOXML).
                        NormalizeTableCellPropertiesOrder(cellProps);

                        cell.Append(cellProps);

                        // Zawartość komórki
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

                // Kontynuacje scaleń wypadające ZA ostatnią komórką wiersza.
                AppendPendingContinuations();

                // Scalenia rozpoczęte w tym wierszu obejmują dopiero KOLEJNE wiersze.
                foreach (var col in activeRowSpans.Keys.ToList())
                {
                    if (spansStartedThisRow.Contains(col)) continue;
                    var (remaining, span) = activeRowSpans[col];
                    if (remaining <= 1) activeRowSpans.Remove(col);
                    else activeRowSpans[col] = (remaining - 1, span);
                }

                table.Append(row);
            }
        }

        return table;
    }

    /// <summary>
    /// Porządkuje dzieci w:tcPr zgodnie ze schematem OOXML (CT_TcPr). Sort stabilny.
    /// </summary>
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

    /// <summary>
    /// Komórka kontynuacji scalenia pionowego (w:vMerge bez w:val = continue) —
    /// odpowiednik komórki, którą HTML pomija pod komórką z rowspan.
    /// </summary>
    private static TableCell CreateVerticalMergeContinuationCell(int colSpan)
    {
        var props = new TableCellProperties();
        if (colSpan > 1)
            props.Append(new GridSpan { Val = colSpan });
        props.Append(new VerticalMerge());
        return new TableCell(props, new Paragraph());
    }

    /// <summary>
    /// Liczba slotów siatki dystansowego &lt;td data-grid-spacer="before|after"&gt; (reader:
    /// w:gridBefore/w:gridAfter). 0, gdy węzeł nie jest spacerem wskazanej strony.
    /// </summary>
    private static int GridSpacerSpan(HtmlNode? cellNode, string side)
    {
        if (cellNode == null || cellNode.GetAttributeValue("data-grid-spacer", "") != side)
            return 0;
        return Math.Max(1, cellNode.GetAttributeValue("colspan", 1));
    }

    /// <summary>
    /// Szerokości kolumn (twips) z &lt;colgroup&gt; tabeli. Preferowane dokładne twips z
    /// data-w-tw (reader emituje je z w:tblGrid; Exact=true) — konwersja px→twips zaokrągla,
    /// więc siatka dryfowała o kilka twips przy KAŻDYM zapisie (3020→3015→…). Ręczny resize
    /// kolumny w edytorze usuwa data-w-tw i wraca fallback px (Exact=false).
    /// Kolumna bez szerokości daje 0 (Word rozłoży resztę).
    /// </summary>
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

    /// <summary>
    /// Sprawdza czy tag jest inline
    /// </summary>
    private bool IsInlineTag(string tagName) => tagName switch
    {
        "span" or "strong" or "b" or "em" or "i" or "u" or "s" or "strike" or "sub" or "sup" or "a" => true,
        _ => false
    };

    /// <summary>
    /// Aplikuje style do komórki tabeli
    /// </summary>
    private void ApplyCellStyle(TableCellProperties cellProps, string style)
    {
        if (string.IsNullOrEmpty(style)) return;
        
        // Szerokość (min-width pomijamy — to artefakt edytora, nie geometria Worda).
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
        
        // Kolor tła
        var bgColor = ExtractColor(style, @"background(?:-color)?:\s*");
        if (bgColor != null)
        {
            cellProps.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = bgColor });
        }
        
        // Wyrównanie pionowe
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
        
        // Padding
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

        // NoWrap
        if (style.Contains("white-space:nowrap") || style.Contains("white-space: nowrap"))
        {
            cellProps.Append(new NoWrap());
        }

        // Writing mode
        if (style.Contains("writing-mode:vertical-rl"))
        {
            cellProps.Append(new TextDirection { Val = TextDirectionValues.TopToBottomRightToLeft });
        }
        else if (style.Contains("writing-mode:vertical-lr"))
        {
            cellProps.Append(new TextDirection { Val = TextDirectionValues.BottomToTopLeftToRight });
        }
    }

    /// <summary>
    /// Aplikuje obramowania do komórki
    /// </summary>
    private void ApplyCellBorders(TableCellProperties cellProps, string style)
    {
        if (string.IsNullOrEmpty(style)) return;

        var sides = ResolveCssBorderSides(style);
        if (sides == null) return;

        // Kolejność wg schematu CT_TcBorders: top → left → bottom → right
        // (inna kolejność = błąd walidacji OOXML).
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
            // Word rozstrzyga konflikt krawędzi WAGAMI: val="none" ma wagę 0 i PRZEGRYWA
            // z widoczną linią ze stylu tabeli (czarna siatka wracała na „pustych" stronach).
            // val="nil" = absolutny brak linii, poza rozstrzyganiem konfliktów — tego samego
            // używa Word przy „Brak krawędzi".
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

    /// <summary>
    /// Rozwiązuje obramowania komórki z inline CSS PER STRONA: [top, right, bottom, left];
    /// element null = brak informacji o stronie (nie emitować — o linii zdecyduje styl tabeli).
    /// Obsługiwane formy serializacji (przeglądarka przepisuje style po każdej edycji!):
    ///  - per-strona: <c>border-top: 0.7px solid #000</c> / <c>medium none</c>,
    ///  - skrót: <c>border: …</c> (także bez koloru / z currentcolor),
    ///  - zbiorcze longhandy z 1–4 wartościami: <c>border-style: none solid solid;
    ///    border-width: medium 0.7px 0.7px; border-color: currentcolor rgb(…) rgb(…)</c> —
    ///    tak CSSOM serializuje MIESZANE strony; wcześniej „none" w takiej liście zerowało
    ///    wszystkie strony albo strona ginęła całkiem i Word malował czarną siatkę ze stylu.
    /// Strona niewidoczna w edytorze (szerokość 0 / transparent / rgba z alpha=0) = jawny None.
    /// </summary>
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

        // „none" / „medium none" / „medium none currentcolor" / „hidden" — formy serializacji braku linii.
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

    /// <summary>Kolor, przy którym linia jest w edytorze niewidoczna — semantycznie brak linii.</summary>
    private static bool IsInvisibleCssColor(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();
        if (token.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return true;
        return Regex.IsMatch(token, @"^rgba\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*0(\.0+)?\s*\)$");
    }

    /// <summary>Wartości box-model CSS (1–4 tokeny) rozwinięte do [top, right, bottom, left].</summary>
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

    /// <summary>Wartość pojedynczej deklaracji CSS (np. „border-color") lub null, gdy jej brak.</summary>
    private static string? GetCssDeclarationValue(string style, string property)
    {
        var match = Regex.Match(style, $@"(?<![a-z-]){Regex.Escape(property)}\s*:\s*([^;]+)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>Normalizuje token koloru CSS (hex #rgb/#rrggbb, rgb(), rgba()) do 6-znakowego hex; null gdy nie kolor.</summary>
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

    /// <summary>
    /// Parsuje styl obramowania CSS na wartość OpenXML
    /// </summary>
    /// <summary>
    /// CSS px → w:sz (1/8 pt): sz = px × 72/96 × 8 = px × 6. Symetryczne do readera (sz/6 → px);
    /// wcześniejsze ×8 pogrubiało każdą linię o 33% przy każdym round-tripie. Minimum 2 (0.25 pt).
    /// </summary>
    private static uint CssPxToBorderEighthPoints(string px)
    {
        var v = double.Parse(px, System.Globalization.CultureInfo.InvariantCulture);
        return (uint)Math.Max(2, Math.Round(v * 6));
    }

    /// <summary>
    /// Jak <see cref="CssPxToBorderEighthPoints"/>, ale świadome stylu: CSS `double` mieści
    /// w border-width TRZY pasma (linia/przerwa/linia), a w:sz opisuje szerokość jednej linii —
    /// bez dzielenia przez 3 każdy round-trip potraja grubość podwójnej ramki.
    /// </summary>
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

    /// <summary>
    /// Normalizuje kolor (3 znaki na 6)
    /// </summary>
    private string NormalizeColor(string color)
    {
        if (color.Length == 3)
            return $"{color[0]}{color[0]}{color[1]}{color[1]}{color[2]}{color[2]}";
        return color;
    }

    /// <summary>
    /// Wyciąga kolor z CSS (obsługuje hex i rgb)
    /// </summary>
    private string? ExtractColor(string style, string prefix)
    {
        // Najpierw hex
        var hexMatch = Regex.Match(style, $@"{prefix}#?([a-fA-F0-9]{{3,6}})");
        if (hexMatch.Success)
            return NormalizeColor(hexMatch.Groups[1].Value);
        
        // Potem rgb()
        var rgbMatch = Regex.Match(style, $@"{prefix}rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)");
        if (rgbMatch.Success)
        {
            var r = int.Parse(rgbMatch.Groups[1].Value);
            var g = int.Parse(rgbMatch.Groups[2].Value);
            var b = int.Parse(rgbMatch.Groups[3].Value);
            return $"{r:X2}{g:X2}{b:X2}";
        }
        
        // rgba()
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

    /// <summary>
    /// Konwertuje obraz na Paragraph z obrazem - z dokładnym odwzorowaniem wymiarów
    /// </summary>
    /// <summary>
    /// Zwraca efektywny data:URL obrazu do zapisu. Dla legacy EMF/WMF `src` to tylko placeholder
    /// SVG (podgląd w przeglądarce) — prawdziwy metafile jest w `data-original-src`. Zapis placeholdera
    /// SVG jako gołego `a:blip` daje NIEPOPRAWNY DOCX (Word: „dokument uszkodzony"), więc preferujemy oryginał.
    /// </summary>
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

    /// <summary>
    /// Tworzy paragraf z obrazem z zachowaniem oryginalnych wymiarów EMU.
    /// </summary>
    private Paragraph CreateImageParagraph(byte[] imageBytes, string contentType, HtmlNode node)
    {
        var drawing = BuildImageDrawing(imageBytes, contentType, node);
        return drawing != null ? new Paragraph(new Run(drawing)) : new Paragraph();
    }

    /// <summary>
    /// Tworzy inline Run z obrazem (do osadzania w paragrafie header/footer/body).
    /// </summary>
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

    /// <summary>
    /// Buduje Drawing dla obrazka, dodaje ImagePart do aktualnej części (body/header/footer).
    /// </summary>
    private Drawing? BuildImageDrawing(byte[] imageBytes, string contentType, HtmlNode node)
    {
        var container = _currentImageContainer ?? (OpenXmlPart?)_mainPart;
        if (container == null) return null;

        // HARD GUARD: goły `a:blip` na SVG (bez rastrowego fallbacku) jest NIEPOPRAWNY w DOCX —
        // Word zgłasza uszkodzony plik. Placeholdery legacy niosą prawdziwy metafile w
        // `data-original-src` (obsłużone w ResolveImageSrc), więc tu SVG = brak fallbacku → pomiń
        // (kontrolowana strata, NIGDY uszkodzony dokument).
        if (contentType == "image/svg+xml")
            return null;

        // Part musi dostać PRAWDZIWY content type danych. Wcześniej wszystko spoza krótkiej listy
        // (TIFF/ICO/WEBP/EMZ…) lądowało jako rzekomy Jpeg — obraz przeżywał pierwszy zapis z błędną
        // deklaracją typu i przestawał się renderować po round-tripie.
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
        // Nieznany typ: zachowaj zadeklarowany image/* (np. image/webp, image/x-emz) zamiast
        // fałszować Jpeg; wartości niebędące poprawnym typem obrazu → bezpieczny fallback Jpeg
        // (zachowanie historyczne dla dziwnych wejść z edytora).
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

        // Alt text → round-tripped into wp:docPr/@descr. DeEntitize so the stored text is
        // the real string (the reader re-encodes once); otherwise entities double-escape.
        var altText = HtmlEntity.DeEntitize(node.GetAttributeValue("alt", "")) ?? string.Empty;

        // Próbuj najpierw użyć oryginalnych wymiarów EMU (zachowanych z DOCX)
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
            // Fallback: parsuj ze stylu CSS (obsługuje liczby zmiennoprzecinkowe)
            var style = node.GetAttributeValue("style", "");
            var widthMatch = Regex.Match(style, @"width:\s*([\d.]+)px");
            var heightMatch = Regex.Match(style, @"height:\s*([\d.]+)px");

            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var width = widthMatch.Success ? double.Parse(widthMatch.Groups[1].Value, ci) : 200;
            var height = heightMatch.Success ? double.Parse(heightMatch.Groups[1].Value, ci) : width * 0.75;

            // Atrybuty width/height (HTML)
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

        // Limit szerokości:
        //  - body: ~15 cm (5 400 000 EMU)
        //  - header/footer: ~17 cm (6 120 000 EMU) – w nagłówku obrazki są zwykle szersze
        var maxWidthEmu = _inHeaderFooter ? 6_120_000L : 5_400_000L;
        if (widthEmu > maxWidthEmu)
        {
            var scale = (double)maxWidthEmu / widthEmu;
            widthEmu = maxWidthEmu;
            heightEmu = (long)(heightEmu * scale);
        }
        if (widthEmu < OoxmlUnits.EmuPerPixel) widthEmu = OoxmlUnits.EmuPerPixel;   // min 1 px
        if (heightEmu < OoxmlUnits.EmuPerPixel) heightEmu = OoxmlUnits.EmuPerPixel;

        _imageCounter++;

        // Word-like positioning: when the editor marks the image as floating
        // (data-pos-mode="front"|"behind") we emit wp:anchor with position offsets;
        // otherwise it stays an inline image (default OOXML behaviour). Offsets are
        // read from data-x-emu / data-y-emu set by the editor on drag-end.
        var posMode = node.GetAttributeValue("data-pos-mode", "");
        var isFloating = posMode == "front" || posMode == "behind";

        // Optional border (a:ln in pic:spPr): width in EMU = px * 9525.
        int.TryParse(node.GetAttributeValue("data-border-width", "0"), out var borderWidthPx);
        var borderColor = node.GetAttributeValue("data-border-color", "").TrimStart('#');
        var borderStyle = node.GetAttributeValue("data-border-style", "solid");

        // Optional crop (a:srcRect on pic:blipFill): l/t/r/b in 1/1000 of a percent.
        int.TryParse(node.GetAttributeValue("data-crop-l", "0"), out var cropL);
        int.TryParse(node.GetAttributeValue("data-crop-r", "0"), out var cropR);
        int.TryParse(node.GetAttributeValue("data-crop-t", "0"), out var cropT);
        int.TryParse(node.GetAttributeValue("data-crop-b", "0"), out var cropB);
        var hasCrop = cropL > 0 || cropR > 0 || cropT > 0 || cropB > 0;

        // BlipFill — with optional srcRect carrying the crop percentages.
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

        // ShapeProperties — with optional outline (a:ln) for the border.
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

        // Floating mode: wp:anchor with position offsets and the "no wrap" mode that
        // matches Word's "Behind text" / "In front of text" options (no text reflow).
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
            // Horizontal offset is page-relative (editor origin = left page edge), but the
            // editor's vertical origin is the TOP OF THE TEXT AREA (the body band starts one
            // margin below the page edge). Emitting Y relative to the top text margin keeps the
            // offset consistent both ways and makes Word place the object where the editor shows
            // it — a page-relative Y would render one top-margin too high.
            { RelativeFrom = DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalRelativePositionValues.Margin },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = widthEmu, Cy = heightEmu },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            // Oryginalny tryb zawijania z data-wrap (reader); brak → WrapNone jak dotąd.
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

    /// <summary>Pola tekstowe oczekujące na przypięcie do najbliższego następnego akapitu.</summary>
    private readonly List<Drawing> _pendingTextBoxDrawings = new();

    /// <summary>
    /// Czy węzeł to pole tekstowe Worda wyemitowane przez DocxToHtmlConverter
    /// (<c>div.docx-textbox</c> / <c>data-textbox="1"</c>).
    /// </summary>
    private static bool IsTextBoxNode(HtmlNode node)
        => node.NodeType == HtmlNodeType.Element
           && node.Name.Equals("div", StringComparison.OrdinalIgnoreCase)
           && (node.HasClass("docx-textbox") || node.GetAttributeValue("data-textbox", "") == "1");

    /// <summary>
    /// Buforuje drawing pola tekstowego napotkanego na poziomie blokowym. Model kotwicy:
    /// reader emituje div bezpośrednio PRZED akapitem-kotwicą, więc drawing zostaje
    /// przypięty do NASTĘPNEGO konwertowanego akapitu (<see cref="AttachPendingTextBoxes"/>).
    /// Dzięki temu relacja „obiekt ↔ akapit" przeżywa pełny round-trip bez sztucznych ID.
    /// </summary>
    private void BufferTextBoxDrawing(HtmlNode node)
    {
        var drawing = BuildTextBoxDrawing(node);
        if (drawing != null) _pendingTextBoxDrawings.Add(drawing);
    }

    /// <summary>
    /// Przypina zbuforowane pola tekstowe do akapitu (run z drawingiem na początku treści —
    /// dla obiektu pływającego pozycja runa w akapicie nie wpływa na układ, a początek
    /// jest deterministyczny dla round-tripu).
    /// </summary>
    private void AttachPendingTextBoxes(Paragraph paragraph)
    {
        if (_pendingTextBoxDrawings.Count == 0) return;
        foreach (var drawing in _pendingTextBoxDrawings)
            paragraph.Append(new Run(drawing));
        _pendingTextBoxDrawings.Clear();
    }

    /// <summary>
    /// Awaryjny flush: treść skończyła się bez akapitu-następnika (textbox jako ostatni
    /// element) — tworzony jest akapit-kotwica, żeby drawing nie przepadł.
    /// </summary>
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

    /// <summary>
    /// Odtwarza pole tekstowe Worda z <c>div.docx-textbox</c> jako <c>wps:wsp</c> z
    /// <c>w:txbxContent</c> (format DrawingML, Word 2010+). Kotwiczone (data-pos-mode
    /// front/behind albo position:absolute) → <c>wp:anchor</c> w konwencji edytora — te same
    /// osie co obrazy: X = PositionOffset od lewej krawędzi strony (relativeFrom=page),
    /// Y = od górnego marginesu (relativeFrom=margin); pozostałe → <c>wp:inline</c>.
    /// Rozmiar z data-width/height-emu (fallback: width/min-height px ze stylu),
    /// obramowanie data-border-* → <c>a:ln</c>. Null, gdy pole nie ma treści.
    /// </summary>
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
            // Pionowo Margin — origin Y edytora to góra obszaru treści (spójnie z obrazami;
            // reader dodaje i odejmuje ten sam górny margines → round-trip idempotentny).
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

    /// <summary>
    /// Treść pola tekstowego (<c>w:txbxContent</c>) z dzieci div.docx-textbox. Bufor
    /// zagnieżdżonych textboxów jest izolowany — pole w polu degraduje do drawingu inline
    /// wewnątrz treści, zamiast „uciec" do akapitów body.
    /// </summary>
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

            // Zagnieżdżone textboxy zbuforowane przez dzieci — nie mogą czekać na akapit body.
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
        // w:txbxContent wymaga co najmniej jednego akapitu (sama tabela nie wystarcza Wordowi).
        if (!content.Elements<Paragraph>().Any()) content.Append(new Paragraph());
        return content;
    }

    /// <summary>
    /// Element wrap* dla <c>wp:anchor</c> z <c>data-wrap</c> (round-trip z readera).
    /// wrapTight/wrapThrough wymagają wrapPolygon, którego HTML nie niesie — przybliżenie
    /// przez wrapSquare (Word nadal opływa obiekt); brak/none → WrapNone (front/behind).
    /// </summary>
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

    /// <summary>Wartość CSS w px dla danej właściwości z inline style (np. width/left/top).</summary>
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

    /// <summary>
    /// Konwertuje link na Paragraph z hiperłączem
    /// </summary>
    private Paragraph ConvertAnchorElement(HtmlNode node)
    {
        var para = new Paragraph();
        var href = node.GetAttributeValue("href", "#");
        var internalAnchor = node.GetAttributeValue("data-anchor", "");
        if (internalAnchor.Length == 0 && href.StartsWith('#') && href.Length > 1)
            internalAnchor = href.TrimStart('#');

        try
        {
            // Kotwica wewnętrzna (wpisy TOC): w:anchor bez wymuszania stylizacji —
            // Word renderuje takie linki formatowaniem runów, nie stylem Hyperlink.
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
            // Jeśli URI jest nieprawidłowy, dodaj jako zwykły tekst
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

    /// <summary>
    /// Tworzy paragraf stylizowany na linię horyzontalną
    /// </summary>
    /// <summary>
    /// Odtwarza poziomą linię Worda z markera readera (span.docx-hr + data-hr-*):
    /// w:r → w:pict → v:rect z o:hr="t" (+ hralign/hrpct/hrnoshade/hrstd, fillcolor,
    /// wysokość w pt) — struktura 1:1 z „Wstaw → linia pozioma" Worda.
    /// </summary>
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

    /// <summary>
    /// Dodaje inline content do paragrafu.
    /// Style font-* / color / font-weight / font-style ustawione na rodzicu (<p>/<h1>/<li>/...)
    /// dziedziczą się w HTML kaskadowo na dzieci. W DOCX run NIE dziedziczy automatycznie,
    /// więc budujemy bazowe `RunProperties` ze stylu rodzica i przekazujemy je
    /// do `CreateRunsFromNode` jako `inheritedProps`.
    /// </summary>
    private void AppendInlineContent(Paragraph paragraph, HtmlNode node)
    {
        RunProperties? baseRunProps = null;
        var parentStyle = node.GetAttributeValue("style", "");
        if (!string.IsNullOrEmpty(parentStyle))
        {
            baseRunProps = new RunProperties();
            ApplyRunStyle(baseRunProps, parentStyle);
            // jeżeli ApplyRunStyle nic nie dodał, traktuj jako brak
            if (!baseRunProps.HasChildren)
                baseRunProps = null;
        }

        AppendInlineChildren(paragraph, node.ChildNodes, baseRunProps);

        if (!paragraph.Elements<Run>().Any() && !paragraph.Elements<Hyperlink>().Any())
        {
            paragraph.Append(new Run(new Text("") { Space = SpaceProcessingModeValues.Preserve }));
        }
    }

    /// <summary>
    /// Pętla treści inline akapitu — wydzielona z <see cref="AppendInlineContent"/>, bo wrappery
    /// segmentów linii tabulatorowej readera (span.docx-tab-text) są PRZEZROCZYSTE: ich dzieci
    /// (hyperlink wpisu spisu treści, markery pól, zakładki) muszą trafić na poziom akapitu.
    /// </summary>
    private void AppendInlineChildren(Paragraph paragraph, IEnumerable<HtmlNode> children, RunProperties? baseRunProps)
    {
        foreach (var child in children)
        {
            // Pole tekstowe wewnątrz akapitu/li (div w <li> jest poprawnym flow content —
            // reader nie hoistuje go przed element listy) → drawing inline w tym akapicie.
            if (child.NodeType == HtmlNodeType.Element && IsTextBoxNode(child))
            {
                var tbRun = BuildTextBoxRun(child);
                if (tbRun != null) paragraph.Append(tbRun);
                continue;
            }

            // Inline content control (formant) zachowany z odczytu DOCX — owijamy ponownie w SdtRun.
            if (child.NodeType == HtmlNodeType.Element
                && child.Name.Equals("span", StringComparison.OrdinalIgnoreCase)
                && child.HasClass("sdt-inline"))
            {
                var sdtRun = BuildSdtRunFromHtml(child, baseRunProps);
                if (sdtRun != null)
                    paragraph.Append(sdtRun);
                continue;
            }

            // Pola numeru strony w TREŚCI (nie tylko w stopce) — bez tego span.field-page/
            // page-number/field-numpages wychodził jako LITERALNY tekst „{page}"/„{pages}"
            // (placeholder readera) zamiast pola PAGE/NUMPAGES.
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
                // Wrapper segmentu linii tabulatorowej (reader, spis treści) — przezroczysty:
                // dzieci są treścią akapitu (hyperlink/markery pól muszą wrócić na ten poziom).
                if (child.HasClass("docx-tab-text"))
                {
                    AppendInlineChildren(paragraph, child.ChildNodes, MergeRunProps(baseRunProps, child));
                    continue;
                }

                // Zakładka (cel PAGEREF/hyperlinków wewnętrznych, np. _Toc… przy nagłówku) —
                // odtwarzamy parę bookmarkStart/bookmarkEnd z unikalnym w:id.
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

            // Hiperłącze inline → PRAWDZIWY w:hyperlink (wcześniej degradowało do stylizowanych
            // runów — klik przestawał działać w Wordzie po pierwszym zapisie).
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

    /// <summary>Klon odziedziczonych RunProperties wzbogacony o inline style danego węzła.</summary>
    private RunProperties? MergeRunProps(RunProperties? baseRunProps, HtmlNode node)
    {
        var style = node.GetAttributeValue("style", "");
        if (string.IsNullOrEmpty(style)) return baseRunProps;
        var merged = (baseRunProps?.CloneNode(true) as RunProperties) ?? new RunProperties();
        ApplyRunStyle(merged, style);
        return merged.HasChildren ? merged : baseRunProps;
    }

    /// <summary>
    /// Odtwarza w:hyperlink z inline'owego &lt;a&gt;. Kotwica wewnętrzna (data-anchor lub
    /// href="#zakładka" — wpisy spisu treści) → w:anchor BEZ narzucania koloru (wpisy TOC
    /// w Wordzie wyglądają jak zwykły tekst; stylizację niosą runy). URL → relacja zewnętrzna
    /// ze stylem Hyperlink (parytet z dotychczasowym wyglądem). Null przy nieprawidłowym URI —
    /// wywołujący degraduje do zwykłych runów.
    /// </summary>
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
            // Markery pól (PAGEREF wpisu TOC żyje WEWNĄTRZ hyperlinka) i taby idą
            // przez CreateRunsFromNode jak każda treść inline.
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

    /// <summary>
    /// Tworzy Run z węzła HTML z pełnym odwzorowaniem formatowania
    /// </summary>
    private List<Run> CreateRunsFromNode(HtmlNode node, RunProperties? inheritedProps = null)
    {
        var runs = new List<Run>();

        switch (node.NodeType)
        {
            case HtmlNodeType.Text:
                var text = System.Net.WebUtility.HtmlDecode(node.InnerText);
                if (!string.IsNullOrEmpty(text))
                {
                    // Literalny znak tabulacji → w:tab (element), nie tekst w w:t — Word nie
                    // renderuje tabów zapisanych w treści w:t, więc ginęły po round-tripie.
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

                // Pass-through grafiki XML (ADR-0056) w kontekście inline: kształt/VML/OLE
                // z data-docx-xml wraca jako oryginalny run child (w:drawing/w:pict/w:object).
                if (node.GetAttributeValue("data-docx-xml", "") != ""
                    && TryRestorePreservedElement(node) is { } preservedInline)
                {
                    runs.Add(new Run(preservedInline));
                    break;
                }

                // Manualny page break: reader emituje <div class="page-break"> WEWNĄTRZ akapitu
                // (Break siedzi w runie), więc trafia tu, a nie do bloku. Bez tego znak rozpoczęcia
                // nowej strony ginął po round-tripie (R-15). Jeden węzeł page-break → jeden Break,
                // bez duplikacji; dokument bez page-breaków nie dostaje żadnego.
                if (IsPageBreakNode(node))
                {
                    runs.Add(new Run(new Break { Type = BreakValues.Page }));
                    break;
                }

                // Podział kolumny: reader emituje <div class="docx-column-break"> z w:br type=column.
                // Odtwarzamy go jako run z Break type=column (ADR-0039).
                if (node.NodeType == HtmlNodeType.Element && node.HasClass("docx-column-break"))
                {
                    runs.Add(new Run(new Break { Type = BreakValues.Column }));
                    break;
                }

                // Pozioma linia Worda (reader: span.docx-hr z w:pict/v:rect o:hr="t") —
                // odtwarzamy oryginalny VML z data-hr-*, żeby linia nie znikała po zapisie.
                if (node.GetAttributeValue("data-docx-hr", "") != "")
                {
                    runs.Add(CreateVmlHorizontalRuleRun(node));
                    break;
                }

                // Odwołanie do przypisu dolnego: <sup class="footnote-ref" data-footnote-id="fn-N">.
                // Emituje w:footnoteReference z identyfikatorem OOXML przypisanym w AssignFootnoteOoxmlIds.
                // Odwołanie do nieistniejącego przypisu jest POMIJANE (brak osieroconego w:footnoteReference).
                if (node.Name.Equals("sup", StringComparison.OrdinalIgnoreCase) && node.HasClass("footnote-ref"))
                {
                    var run = CreateFootnoteReferenceRun(node, inheritedProps);
                    if (run != null) runs.Add(run);
                    break;
                }

                // Odwołanie do przypisu końcowego: <sup class="endnote-ref" data-endnote-id="en-N">.
                // Emituje w:endnoteReference; odwołanie do nieistniejącego przypisu jest POMIJANE.
                if (node.Name.Equals("sup", StringComparison.OrdinalIgnoreCase) && node.HasClass("endnote-ref"))
                {
                    var run = CreateEndnoteReferenceRun(node, inheritedProps);
                    if (run != null) runs.Add(run);
                    break;
                }

                // Segment pozycyjny tab-stopu (reader: nagłówek/stopka z w:tabs) — segment
                // zaczyna się od tabulatora; pozycję odtwarza pPr/w:tabs z data-tab-stops.
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

                // Wypełniacz tabulatora (spis treści, w:leader) → sam w:tab. Kropki są
                // prezentacyjne (pseudo-element CSS GUI), a pozycję i znak wypełniacza
                // odtwarza pPr/w:tabs z data-tab-stops — nie schodzimy w dzieci (w środku
                // jest tylko literalny \t, który dałby DRUGI tabulator).
                if (node.HasClass("docx-tab-leader"))
                {
                    var leaderTab = new Run();
                    if (inheritedProps != null)
                        leaderTab.Append(inheritedProps.CloneNode(true));
                    leaderTab.Append(new TabChar());
                    runs.Add(leaderTab);
                    break;
                }

                // Marker pola złożonego (reader: TOC/PAGEREF) → fldChar begin+instrText+separate
                // lub end. Dzięki temu Word po zapisie z edytora dalej umie zaktualizować spis
                // treści (F9). Osierocony end (użytkownik usunął początek) jest pomijany —
                // niesparowany fldChar uszkadza dokument.
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

                // Zakładka w kontekście inline — parę bookmarkStart/End odtwarza poziom akapitu
                // (AppendInlineContent); tu tylko nie emitujemy nic (span jest pusty).
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
                            // Wyciągnij run z obrazem
                            foreach (var r in imgPara.Elements<Run>())
                            {
                                runs.Add((Run)r.CloneNode(true));
                            }
                        }
                        return runs;
                    case "a":
                        // Hiperłącze w inline context
                        var href = node.GetAttributeValue("href", "#");
                        try
                        {
                            var relId = _mainPart!.AddHyperlinkRelationship(new Uri(href, UriKind.RelativeOrAbsolute), true).Id;
                            // Dodaj runy z linkiem - w inline kontekście nie możemy dodać Hyperlink do Run
                            // więc po prostu stylizujemy tekst
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

                // Parsuj style inline
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

    // Zarezerwowane identyfikatory OOXML dla technicznych elementów części przypisów.
    // Przypisy użytkownika zaczynają się od 1, więc nigdy z nimi nie kolidują.
    private const long FootnoteSeparatorId = -1;
    private const long FootnoteContinuationSeparatorId = 0;

    /// <summary>
    /// Deterministycznie przydziela identyfikatory OOXML przypisom po kolejności listy modelu
    /// (htmlId → 1..N). Odwołania w treści odwzorowują się przez ten słownik; sama treść jest
    /// jednym źródłem prawdy (numer widoczny nie jest tożsamością).
    /// </summary>
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

    /// <summary>
    /// Buduje run z <c>w:footnoteReference</c> dla odwołania <c>&lt;sup class="footnote-ref"&gt;</c>.
    /// Zwraca null, gdy odwołanie wskazuje przypis spoza listy (nie emitujemy osieroconego odwołania).
    /// </summary>
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

    /// <summary>
    /// Tworzy część <c>word/footnotes.xml</c> (relacja + content type przez <see cref="MainDocumentPart.AddNewPart"/>)
    /// z wymaganymi separatorami technicznymi i treścią przypisów użytkownika. Nic nie tworzy dla
    /// dokumentów bez przypisów (brak nadmiarowej części). Zapisujemy wyłącznie przypisy z listy
    /// modelu — reader zwraca je w kolejności odwołań, więc każdy ma odpowiadające odwołanie.
    /// </summary>
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

    /// <summary>
    /// Odtwarza treść przypisu z HTML przez istniejące konwertery treści (akapity/runy/listy/linki),
    /// aby nie duplikować logiki mapowania. Pierwszy akapit dostaje run ze znacznikiem auto-numeru
    /// (<c>w:footnoteRef</c>). Relacje obrazów są zakresowane do części przypisów.
    /// </summary>
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

        // Znacznik auto-numeru na początku pierwszego akapitu (po pPr, jeśli istnieje).
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

    /// <summary>Techniczny przypis-separator (w:separator / w:continuationSeparator) z zarezerwowanym id.</summary>
    private static WpFootnote CreateSeparatorFootnote(long id, FootnoteEndnoteValues type)
    {
        OpenXmlElement mark = type == FootnoteEndnoteValues.Separator
            ? new SeparatorMark()
            : new ContinuationSeparatorMark();
        return new WpFootnote(new Paragraph(new Run(mark))) { Id = id, Type = type };
    }

    // ── Przypisy końcowe (endnotes.xml) ──────────────────────────────────────────
    // Rezerwacja id separatorów jest współdzielona semantycznie z footnotes (-1/0), ale
    // endnotes.xml to osobna część z własną przestrzenią id, więc kolizji nie ma.
    private const long EndnoteSeparatorId = -1;
    private const long EndnoteContinuationSeparatorId = 0;

    /// <summary>Deterministycznie przydziela id OOXML przypisom końcowym (htmlId → 1..N).</summary>
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

    /// <summary>
    /// Buduje run z <c>w:endnoteReference</c> dla <c>&lt;sup class="endnote-ref"&gt;</c>.
    /// Zwraca null, gdy odwołanie wskazuje przypis spoza listy (bez osieroconego odwołania).
    /// </summary>
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

    /// <summary>
    /// Tworzy część <c>word/endnotes.xml</c> (relacja + content type przez <see cref="MainDocumentPart.AddNewPart"/>)
    /// z separatorami technicznymi i treścią przypisów końcowych. Nic nie tworzy dla dokumentów bez nich.
    /// </summary>
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

    /// <summary>
    /// Odtwarza treść przypisu końcowego z HTML przez istniejące konwertery treści. Pierwszy akapit
    /// dostaje run ze znacznikiem auto-numeru (<c>w:endnoteRef</c>). Relacje obrazów → część endnotes.
    /// </summary>
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

    /// <summary>Techniczny przypis końcowy-separator (w:separator / w:continuationSeparator).</summary>
    private static WpEndnote CreateSeparatorEndnote(long id, FootnoteEndnoteValues type)
    {
        OpenXmlElement mark = type == FootnoteEndnoteValues.Separator
            ? new SeparatorMark()
            : new ContinuationSeparatorMark();
        return new WpEndnote(new Paragraph(new Run(mark))) { Id = id, Type = type };
    }

    /// <summary>
    /// Parsuje atrybut data-tab-stops ("4536:center;9072:right:dot") na w:tabs.
    /// Zwraca null przy braku poprawnych wpisów.
    /// </summary>
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

    /// <summary>
    /// Aplikuje styl CSS do ParagraphProperties z pełnym parsowaniem
    /// </summary>
    /// <param name="props">Właściwości akapitu (mutowane).</param>
    /// <param name="style">Inline CSS akapitu.</param>
    /// <param name="includeIndentation">
    /// false dla elementów LIST: ich wcięcia autorytatywnie niesie kontrakt data-ind-*-tw
    /// (AppendListItemIndentation), a margin-left na li to DELTA względem paddingu kontenera —
    /// zmapowana wprost na w:ind podwajałaby wcięcie.
    /// </param>
    private void ApplyParagraphStyle(ParagraphProperties props, string style, bool includeIndentation = true)
    {
        if (string.IsNullOrEmpty(style)) return;

        // „Podział strony przed" jako WŁAŚCIWOŚĆ akapitu (w:pageBreakBefore) — jak checkbox
        // w dialogu Worda i style nagłówków. To celowo INNA reprezentacja niż ręczny podział
        // (marker div.page-break → w:br type=page): właściwość round-tripuje bez dodatkowego
        // pustego akapitu i w Wordzie pozostaje zaznaczonym checkboxem, nie twardym breakiem.
        if (Regex.IsMatch(style, @"(page-break-before|break-before)\s*:\s*(always|page)", RegexOptions.IgnoreCase))
            props.Append(new PageBreakBefore());
        else if (Regex.IsMatch(style, @"(page-break-before|break-before)\s*:\s*auto", RegexOptions.IgnoreCase))
            // Jawne wyłączenie (reader: direct w:pageBreakBefore val=false; dialog: odznaczenie
            // aktywnego podziału) — val=false nadpisuje pageBreakBefore z definicji STYLU.
            props.Append(new PageBreakBefore { Val = false });

        // Text-align
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

        // Wcięcia. Regexy łapią px i pt oraz wartości ujemne/ułamkowe (gap-analysis pkt 2:
        // dialog akapitu pisze pt, a `(\d+)px` gubił wszystko poza całkowitymi px).
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

        // Odstępy
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

        // ADR-0053: reader emituje w:after jako padding-bottom (odstępy akapitów SUMUJĄ się
        // jak w Wordzie zamiast kolapsować jak marginesy CSS); margin-bottom wyżej zostaje
        // honorowane (akapity z tłem/obramowaniem i treść autorstwa GUI sprzed zmiany).
        // Gdy występują oba, padding-bottom (bardziej szczegółowe źródło) wygrywa.
        var paddingBottomMatch = Regex.Match(style, @"(?<![\w-])padding-bottom:\s*([\d.,]+)(px|pt)?");
        if (paddingBottomMatch.Success)
        {
            var val = double.Parse(paddingBottomMatch.Groups[1].Value.Replace(',', '.'), inv);
            var unit = paddingBottomMatch.Groups[2].Value;
            if (unit == "px") val = OoxmlUnits.PixelsToPoints(val);
            var afterTw = (int)Math.Round(OoxmlUnits.PointsToTwips(val));
            // padding-bottom:0 przy akapicie z tłem/ramką to tylko reset klasowego
            // domyślnego odstępu (after siedzi w margin-bottom) — zero nie może
            // nadpisać niezerowej wartości z margin-bottom.
            if (afterTw > 0 || !marginBottomMatch.Success)
            {
                spacing.After = afterTw.ToString();
                hasSpacing = true;
            }
        }

        // atLeast z readera (PG-10): line-height:max(Xpt, var(--w-line-single…)) — bierzemy pt.
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
            if (unit == "pt")
            {
                // Dokładna wartość w pt. Reader oznacza regułę atLeast markerem
                // --w-line-rule:atLeast — bez niego atLeast wracało jako exact,
                // a exact przycina w Wordzie tekst wyższy niż linia.
                spacing.Line = ((int)Math.Round(OoxmlUnits.PointsToTwips(val))).ToString();
                spacing.LineRule = Regex.IsMatch(style, @"--w-line-rule\s*:\s*atLeast")
                    ? LineSpacingRuleValues.AtLeast
                    : LineSpacingRuleValues.Exact;
            }
            else
            {
                // Mnożnik. Reader emituje wartość SKALIBROWANĄ metrykami fontu (PG-09)
                // + marker --w-line-tw z oryginalnym w:line w 240-tych — round-trip
                // bierze marker, nie wartość renderową (inaczej każdy zapis rozciągałby
                // interlinię o współczynnik kalibracji). Bez markera (np. starsza treść)
                // wartość traktujemy jak mnożnik Worda — dotychczasowe zachowanie.
                var lineTwMarker = Regex.Match(style, @"--w-line-tw\s*:\s*(\d+)");
                spacing.Line = lineTwMarker.Success
                    ? lineTwMarker.Groups[1].Value
                    : ((int)Math.Round(val * 240)).ToString();
                spacing.LineRule = LineSpacingRuleValues.Auto;
            }
            hasSpacing = true;
        }
        
        if (hasSpacing)
            props.Append(spacing);

        // w:contextualSpacing (znosi odstępy między paragrafami tego samego stylu) —
        // oznaczony w CSS jako --w-contextual-spacing:1
        if (Regex.IsMatch(style, @"--w-contextual-spacing\s*:\s*1"))
        {
            props.Append(new ContextualSpacing());
        }

        // Kolor tła paragrafu
        var bgColor = ExtractColor(style, @"background(?:-color)?:\s*");
        if (bgColor != null)
        {
            props.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = bgColor });
        }

        // Obramowania paragrafu
        ApplyParagraphBorders(props, style);

        // Kolejność dzieci pPr wg schematu (CT_PPrBase): jc musi być PO spacing/ind.
        // Dotąd jc szło pierwsze i naruszenie ujawniało się dopiero, gdy akapit miał
        // jednocześnie wyrównanie i odstępy (np. wyśrodkowana komórka tabeli ze
        // spacingiem ze stylu tabeli) — Word potrafi taki plik zgłosić jako uszkodzony.
        NormalizeParagraphPropertiesOrder(props);
    }

    /// <summary>
    /// Porządkuje dzieci w:pPr zgodnie ze schematem OOXML (EG_PPrBase). Sort stabilny —
    /// elementy o tej samej randze zachowują kolejność wstawienia.
    /// </summary>
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
            SpacingBetweenLines => 11,
            Indentation => 12,
            ContextualSpacing => 13,
            Justification => 15,
            OutlineLevel => 18,
            ParagraphMarkRunProperties => 19,
            SectionProperties => 20,
            _ => 14 // nieznane zostają między contextualSpacing a jc (kolejność wstawienia)
        };

        var ordered = props.ChildElements.OrderBy(Rank).ToList();
        props.RemoveAllChildren();
        foreach (var child in ordered)
            props.Append(child);
    }

    /// <summary>
    /// Aplikuje dodatkowe style do paragrafu (bez nadpisywania StyleId)
    /// </summary>
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

    /// <summary>
    /// Aplikuje obramowania paragrafu z CSS
    /// </summary>
    private void ApplyParagraphBorders(ParagraphProperties props, string style)
    {
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

    /// <summary>
    /// Aplikuje styl CSS do RunProperties z pełnym parsowaniem
    /// </summary>
    private void ApplyRunStyle(RunProperties props, string style)
    {
        if (string.IsNullOrEmpty(style)) return;

        // Font-weight
        if (Regex.IsMatch(style, @"font-weight:\s*(bold|[7-9]\d{2})"))
        {
            if (!props.Elements<Bold>().Any())
                props.Append(new Bold());
        }

        // Font-style
        if (style.Contains("font-style:italic") || style.Contains("font-style: italic"))
        {
            if (!props.Elements<Italic>().Any())
                props.Append(new Italic());
        }

        // Text-decoration: obsługa wielu wartości
        var textDecMatch = Regex.Match(style, @"text-decoration:\s*([^;]+)");
        if (textDecMatch.Success)
        {
            var decValue = textDecMatch.Groups[1].Value.ToLower();
            if (decValue.Contains("underline") && !props.Elements<Underline>().Any())
                props.Append(new Underline { Val = UnderlineValues.Single });
            if (decValue.Contains("line-through") && !props.Elements<Strike>().Any())
                props.Append(new Strike());
        }

        // Font-size (obsługa pt, px, em, rem)
        var fontSizeMatch = Regex.Match(style, @"font-size:\s*([\d.,]+)(pt|px|em|rem)");
        if (fontSizeMatch.Success)
        {
            var size = double.Parse(fontSizeMatch.Groups[1].Value.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
            var unit = fontSizeMatch.Groups[2].Value;
            
            double ptSize = unit switch
            {
                "px" => OoxmlUnits.PixelsToPoints(size),
                "em" => size * 11, // Assume base 11pt
                "rem" => size * 11,
                _ => size // pt
            };

            var halfPoints = ((int)OoxmlUnits.PointsToHalfPoints(ptSize)).ToString();
            // Nested span with an explicit size overrides an inherited ancestor size (CSS
            // cascade — nearest wins). props may already carry a FontSize cloned from the
            // parent; replace it instead of skipping, otherwise the per-word size is dropped.
            SetOrReplaceFontSize(props, halfPoints);
        }
        // font-size: smaller/larger
        else if (style.Contains("font-size:smaller") || style.Contains("font-size: smaller"))
        {
            SetOrReplaceFontSize(props, "18"); // ~9pt
        }

        // Font-family. The first family wins (the rest is the generic fallback list, e.g.
        // 'Times New Roman',serif). Multi-word names arrive quoted, and crucially the browser
        // serialises innerHTML with inner double quotes as the ENTITY &quot; — which HtmlAgilityPack
        // does NOT decode. We must HTML-decode the style FIRST: otherwise the `;` inside `&quot;`
        // is mistaken for a CSS declaration separator and the name is truncated (e.g. "&quot"),
        // leaking into w:rFonts so Word silently reverts to its default font.
        var decodedStyle = System.Net.WebUtility.HtmlDecode(style);
        var fontFamilyMatch = Regex.Match(decodedStyle, @"font-family:\s*([^,;]+)");
        if (fontFamilyMatch.Success)
        {
            var fontName = fontFamilyMatch.Groups[1].Value.Trim().Trim('"', '\'').Trim();
            if (fontName.Length > 0)
            {
                // A nested span with an explicit font-family must OVERRIDE the font inherited
                // from an ancestor span (CSS cascade — nearest wins). The editor nests per-word
                // font spans inside the original run's font span, so props usually already
                // carries a RunFonts cloned from the parent. Skipping here (the old behaviour)
                // silently dropped the per-word font and collapsed the whole sentence to the
                // ancestor font. Update the Latin faces in place, preserving any inherited
                // EastAsia/ComplexScript/Hint and the schema-mandated element order.
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

        // Color (obsługa hex, rgb, rgba). Nested span overrides inherited color (patrz font-family).
        var colorVal = ExtractColor(style, @"(?<!background-)color:\s*");
        if (colorVal != null)
        {
            var existingColor = props.Elements<Color>().FirstOrDefault();
            if (existingColor != null)
                existingColor.Val = colorVal;
            else
                props.Append(new Color { Val = colorVal });
        }

        // Background-color
        var bgColorVal = ExtractColor(style, @"background-color:\s*");
        if (bgColorVal != null && !props.Elements<Shading>().Any())
        {
            props.Append(new Shading { Fill = bgColorVal, Val = ShadingPatternValues.Clear });
        }

        // Vertical align
        if (style.Contains("vertical-align:super") && !props.Elements<VerticalTextAlignment>().Any())
            props.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript });
        if (style.Contains("vertical-align:sub") && !props.Elements<VerticalTextAlignment>().Any())
            props.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript });

        // Letter spacing
        var letterSpacingMatch = Regex.Match(style, @"letter-spacing:\s*([\d.,]+)(pt|px)");
        if (letterSpacingMatch.Success && !props.Elements<Spacing>().Any())
        {
            var ls = double.Parse(letterSpacingMatch.Groups[1].Value.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
            var lsUnit = letterSpacingMatch.Groups[2].Value;
            if (lsUnit == "px") ls = OoxmlUnits.PixelsToPoints(ls);
            props.Append(new Spacing { Val = (int)OoxmlUnits.PointsToTwips(ls) });
        }

        // Text-transform
        if (style.Contains("text-transform:uppercase") || style.Contains("text-transform: uppercase"))
        {
            if (!props.Elements<Caps>().Any())
                props.Append(new Caps());
        }
        
        // font-variant
        if (style.Contains("font-variant:small-caps") || style.Contains("font-variant: small-caps"))
        {
            if (!props.Elements<SmallCaps>().Any())
                props.Append(new SmallCaps());
        }
    }

    /// <summary>
    /// Ustawia rozmiar czcionki na RunProperties, nadpisując wartość odziedziczoną po
    /// przodku (kaskada CSS — wygrywa najbliższy). Bez nadpisywania zagnieżdżony span
    /// z własnym rozmiarem gubił go, gdy props niósł już FontSize sklonowany z rodzica.
    /// </summary>
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

    /// <summary>
    /// Recognises a manual page break in any of the representations the pipeline may produce:
    /// the project marker <c>class="page-break"</c> (reader output + insertPageBreak), an explicit
    /// <c>data-docx-break="page"</c>, or a CSS <c>page-break-before</c>/<c>break-before: page</c>.
    /// Used so the break survives DOCX → HTML → DOCX (R-15). Natural Word pagination is NOT a
    /// page break and never matches here.
    /// </summary>
    private static bool IsPageBreakNode(HtmlNode node)
    {
        if (node.NodeType != HtmlNodeType.Element) return false;
        if (node.HasClass("page-break")) return true;
        if (string.Equals(node.GetAttributeValue("data-docx-break", ""), "page", StringComparison.OrdinalIgnoreCase))
            return true;
        var style = node.GetAttributeValue("style", "");
        return Regex.IsMatch(style, @"(page-break-before|break-before)\s*:\s*(always|page)", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Marker końca sekcji emitowany przez DocxToHtmlConverter (R-10): niewidoczny
    /// <c>div.docx-section-break</c> z geometrią NASTĘPNEJ sekcji w data-*.
    /// </summary>
    private static bool IsSectionBreakNode(HtmlNode node) =>
        node.NodeType == HtmlNodeType.Element && node.HasClass("docx-section-break");

    /// <summary>
    /// Czy następny znaczący sąsiad (pomijając komentarze i białe znaki) to marker sekcji.
    /// Reader emituje parę <c>div.page-break</c> + <c>div.docx-section-break</c> dla przerwy
    /// nextPage — page-break jest wtedy tylko wizualny i nie może stać się w:br type=page.
    /// </summary>
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

    /// <summary>
    /// Zamyka bieżącą sekcję: paragraf z pPr/sectPr niosącym geometrię sekcji ZAMYKANEJ
    /// (tak koduje to OOXML), po czym otwiera następną sekcję geometrią z data-* markera.
    /// Pierwszy taki sectPr zapamiętujemy — do niego pójdą referencje nagłówka/stopki
    /// (kolejne sekcje dziedziczą je w Wordzie, gdy nie deklarują własnych).
    /// </summary>
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

    /// <summary>
    /// Geometria sekcji z data-* markera; brakujące wartości dziedziczą z poprzedniej
    /// sekcji (przybliżenie dziedziczenia sekcji Worda).
    /// </summary>
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
            // Kolumny NIE dziedziczą po poprzedniej sekcji — w OOXML brak w:cols = jednokolumnowa.
            Columns = ParseColumnDataAttributes(node)
        };
    }

    /// <summary>
    /// w:type sekcji — zapisywany tylko, gdy różni się od domyślnego nextPage.
    /// Musi poprzedzać pgSz/pgMar (kolejność schematu sectPr).
    /// </summary>
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
        // docProps/core.xml pisane wprost. `document.PackageProperties` tworzyło OPC-ową
        // część `package/services/metadata/core-properties/*.psmdcp` — technicznie legalną,
        // ale nietypową dla DOCX i flagowaną przez narzędzia/wersje Worda jako podejrzana.
        WriteCoreProperties(document, metadata);

        // Extended Properties (app.xml — Company, Manager)
        var extPropsPart = document.AddExtendedFilePropertiesPart();
        extPropsPart.Properties = new Properties();

        if (!string.IsNullOrEmpty(metadata.Company))
            extPropsPart.Properties.Company = new Company(metadata.Company);
        if (!string.IsNullOrEmpty(metadata.Manager))
            extPropsPart.Properties.Manager = new Manager(metadata.Manager);

        extPropsPart.Properties.Application = new DocumentFormat.OpenXml.ExtendedProperties.Application("Qutas D2Tools");
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

        // Kolejność wg sekwencji schematu OPC core-properties.
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

    /// <summary>
    /// Dodaje ustawienia strony z dokładnymi marginesami
    /// </summary>
    private void AddPageSettings(Body body, HeaderFooterContent? header = null, HeaderFooterContent? footer = null, PageMargins? margins = null, Domain.Models.PageSize? pageSize = null)
    {
        var sectionProps = body.Elements<SectionProperties>().FirstOrDefault();
        if (sectionProps == null)
        {
            sectionProps = new SectionProperties();
            body.Append(sectionProps);
        }

        // Body-level sectPr opisuje OSTATNIĄ sekcję. Gdy body zawierało markery sekcji,
        // jej geometria pochodzi z ostatniego markera (_currentSection); bez markerów —
        // z argumentów (dotychczasowe zachowanie, jedna sekcja).
        var geometry = _hasSectionMarkers
            ? _currentSection
            : new SectionGeometry { PageSize = pageSize, Margins = margins, Columns = _docDefaultColumns };

        if (_hasSectionMarkers)
            AppendSectionBreakType(sectionProps, geometry.BreakType);
        AppendSectionGeometry(sectionProps, geometry);
    }

    /// <summary>
    /// Dopisuje w:pgSz + w:pgMar do sectPr wg geometrii sekcji (jedno źródło reguł dla
    /// body-level i paragraph-level sectPr).
    ///
    /// Margins (cm) → twips via the central converter (the exact factor is 1440/2.54).
    /// Defaults are 1 inch sides, 0.5 inch header/footer bands. Body margins are written
    /// AS AUTHORED — they must not be inflated. The reader derives the header/footer band
    /// height as (margin − distance); here we invert that to recover the original
    /// w:header / w:footer distance = (margin − band), clamped to [0, 720] — unless the
    /// section marker carried the authored distances (data-header/footer-distance-cm),
    /// which round-trip verbatim. The previous Math.Max(top, headerHeight + 720) +
    /// hardcoded Header/Footer=720 pushed a small-margin / small-header document
    /// (top=567, header=6) to top=1281, adding pages. Patrz analiza orginał_GOOD vs zapisany_BAD.
    /// </summary>
    private void AppendSectionGeometry(SectionProperties sectionProps, SectionGeometry geometry)
    {
        // Round-trip the authored page size/orientation; fall back to A4 portrait when unknown.
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

        const int maxBandDistanceTwips = 720; // 0.5"
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
    }

    /// <summary>
    /// Dopisuje w:cols do sectPr (po pgSz/pgMar, przed titlePg/docGrid — kolejność CT_SectPr).
    /// Emituje TYLKO gdy sekcja jest realnie wielokolumnowa (Count &gt; 1). Równe → num+space+
    /// equalWidth; nierówne → equalWidth="0" + w:col per kolumna z w:w/w:space (ADR-0039).
    /// </summary>
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

    /// <summary>
    /// pgSz/pgMar must precede titlePg in CT_SectPr, but AddHeaderAndFooter (which sets
    /// titlePg) runs before AddPageSettings — a plain Append would put them after it.
    /// </summary>
    private static void AppendBeforeTitlePage(SectionProperties sectionProps, OpenXmlElement element)
    {
        var titlePg = sectionProps.GetFirstChild<TitlePage>();
        if (titlePg != null) sectionProps.InsertBefore(element, titlePg);
        else sectionProps.Append(element);
    }

    private static OoxmlPageSize BuildPageSize(Domain.Models.PageSize? pageSize)
    {
        // A4 portrait default (twips) matches Word's default new-document section.
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

    /// <summary>
    /// Builds a PAGE/NUMPAGES field run, carrying the field span's font (font-size etc.) so the
    /// page number keeps the footer's size on round-trip instead of falling back to a default.
    /// Falls back to the parent span's style for the wrapped {page} case.
    /// </summary>
    private SimpleField BuildFieldRun(string instruction, HtmlNode fieldNode)
    {
        // fldSimple is paragraph-level; the run properties (font-size etc.) live on an inner run
        // so Word — and our reader on the next round-trip — keep the page number's footer font.
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
        // Cached field result. The reader's placeholders ({page}/{pages}) must NEVER become the
        // cached text — Word shows the cached value until the field recalculates, so a leaked
        // "{page}" would display literally. Fall back to "1" for placeholders/empty/non-numeric.
        var inner = fieldNode.InnerText?.Trim() ?? string.Empty;
        if (inner.Length == 0 || inner.Contains("{page", StringComparison.OrdinalIgnoreCase) || !inner.Any(char.IsDigit))
            inner = "1";
        run.Append(new Text(inner) { Space = SpaceProcessingModeValues.Preserve });
        return new SimpleField(run) { Instruction = instruction };
    }

    /// <summary>
    /// span.field-date → żywe pole daty (w:fldSimple) z instrukcją z data-fld-instr (ADR-0084).
    /// Wartość zbuforowana = tekst wyświetlany w edytorze; Word dalej umie pole zaktualizować.
    /// Bez instrukcji (starszy zapis) — pole DATE z domyślnym polskim obrazem daty.
    /// </summary>
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

    // ═════════════════ Pass-through grafik XML (ADR-0056) ═════════════════
    // Element HTML z data-docx-xml niesie ORYGINALNY fragment OOXML (w:drawing/w:pict/
    // w:object/mc:AlternateContent, base64 OuterXml — wzorzec data-sdt-props) oraz bajty
    // części relacji w data-docx-rels (base64 JSON {rId:{ct,data}}). Odtwarzamy fragment
    // 1:1 z przepięciem rId na nowo dodane części — bez tego kształty/OLE/grupy ginęły
    // przy KAŻDYM zapisie (body/nagłówki są regenerowane z HTML). Uszkodzony/nadmiarowy
    // marker → null (degradacja do stratnej ścieżki, nigdy wysypka ani uszkodzony pakiet).

    /// <summary>
    /// Odtwarza zachowany fragment OOXML z atrybutów pass-through. Null, gdy marker jest
    /// nieobecny/uszkodzony/ponad limit, root spoza białej listy albo brakuje części relacji.
    /// </summary>
    private OpenXmlElement? TryRestorePreservedElement(HtmlNode node)
    {
        var encoded = node.GetAttributeValue("data-docx-xml", "");
        if (string.IsNullOrEmpty(encoded)) return null;
        try
        {
            var xmlBytes = System.Convert.FromBase64String(encoded);
            if (xmlBytes.Length == 0 || xmlBytes.Length > DocxToHtmlConverter.MaxPreservedXmlBytes) return null;
            var xml = System.Text.Encoding.UTF8.GetString(xmlBytes);

            // Twarda walidacja XML (XXE/DTD/encje zablokowane) PRZED oddaniem SDK.
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
            // Uszkodzony marker (ręczna edycja, obce dane) — degradacja bez wyjątku.
            return null;
        }
    }

    /// <summary>
    /// Odtwarza części relacji fragmentu (data-docx-rels) w bieżącym kontenerze
    /// (body/header/footer/przypisy) i przepina rId we fragmencie na nowe. False, gdy
    /// jakikolwiek wymagany rId nie ma bajtów (wiszący rId = uszkodzony dokument).
    /// </summary>
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

    /// <summary>Nowa część dla odtwarzanej relacji: obrazy → ImagePart, reszta → EmbeddedObjectPart.</summary>
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
            // Kontener nie wspiera danego typu części — degradacja (fragment nie zostanie odtworzony).
            return null;
        }
    }

    /// <summary>Przepina wszystkie atrybuty r:* o wartości oldId na newId w całym fragmencie.</summary>
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

    /// <summary>
    /// Buduje SdtProperties (Tag/Alias) na podstawie atrybutów data-sdt-* z elementu HTML.
    /// </summary>
    private static SdtProperties BuildSdtProperties(HtmlNode node)
    {
        // Pełne właściwości formantu (typ + parametry) zachowane przez reader w data-sdt-props
        // (base64 OuterXml). Odtwarzamy je 1:1 — bez tego formant tracił typ (checkbox/dropdown/
        // date/…) i stawał się generyczny przy każdym eksporcie/autosave.
        var encoded = node.GetAttributeValue("data-sdt-props", "");
        if (!string.IsNullOrEmpty(encoded))
        {
            try
            {
                var xml = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(encoded));
                var restored = new SdtProperties(xml);
                // w:id musi być unikalne w dokumencie — usuń, Word nada nowe (kolizje = uszkodzony plik).
                restored.Elements<SdtId>().ToList().ForEach(e => e.Remove());
                // w:placeholder (docPart z GLOSARIUSZA oryginału) i w:dataBinding (customXml/
                // storeItemID) wskazują części pakietu, których regenerowany DOCX nie zawiera —
                // wiszące odwołania wyzwalają w Wordzie „znaleziono zawartość, której nie może
                // odczytać" i tryb naprawy. LocalName łapie też wariant w15:dataBinding.
                restored.ChildElements
                    .Where(e => e.LocalName is "placeholder" or "dataBinding")
                    .ToList().ForEach(e => e.Remove());
                return restored;
            }
            catch
            {
                // Uszkodzone/niezgodne data-sdt-props — degradacja do tag/alias zamiast wysypki.
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

    /// <summary>
    /// Buduje SdtBlock z wrappera &lt;div class="sdt-block"&gt; ze znacznikami data-sdt-*.
    /// </summary>
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

        // SdtContentBlock musi mieć przynajmniej jeden Paragraph, inaczej Word
        // potraktuje SDT jako uszkodzony.
        if (!content.Elements<Paragraph>().Any() && !content.Elements<Table>().Any())
            content.Append(new Paragraph());

        sdt.Append(content);
        return sdt;
    }

    /// <summary>
    /// Buduje SdtRun z &lt;span class="sdt-inline"&gt; ze znacznikami data-sdt-*.
    /// </summary>
    private SdtRun? BuildSdtRunFromHtml(HtmlNode node, RunProperties? inheritedProps)
    {
        var sdt = new SdtRun();
        sdt.Append(BuildSdtProperties(node));

        var content = new SdtContentRun();
        // Złóż run-y z dzieci span-a w tym samym kontekście co AppendInlineContent.
        foreach (var child in node.ChildNodes)
        {
            // Pola numeru strony wewnątrz formantu (galeria Worda „Strona X z Y") muszą
            // wrócić jako pola — CreateRunsFromNode zrobiłoby z nich literalny „{page}".
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
