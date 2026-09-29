using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>
/// Zadeklarowany poziom obsługi jednej konstrukcji DOCX przez trzy komponenty naszego pipeline'u.
/// <c>Note</c> mówi, CO konkretnie ginie lub jest przybliżane; <c>CodePointer</c> wskazuje miejsce
/// w kodzie, od którego zaczyna się naprawa. <c>LossIsBenign</c> = utrata jest zamierzona albo
/// nieszkodliwa (np. ustawienia korespondencji seryjnej) — raportujemy jako informację, nie ostrzeżenie.
/// </summary>
public sealed record EditorCapability(
    string Key,
    string Label,
    AppSupportLevel Reader,
    AppSupportLevel Editor,
    AppSupportLevel Writer,
    string Note,
    string? CodePointer,
    bool LossIsBenign = false)
{
    /// <summary>Najsłabsze ogniwo decyduje o tym, co użytkownik dostanie w v2.</summary>
    public AppSupportLevel Effective
    {
        get
        {
            var levels = new[] { Reader, Editor, Writer };

            if (levels.Contains(AppSupportLevel.Unsupported)) return AppSupportLevel.Unsupported;
            if (levels.Contains(AppSupportLevel.Unknown)) return AppSupportLevel.Unknown;
            if (levels.Contains(AppSupportLevel.PassThrough)) return AppSupportLevel.PassThrough;
            if (levels.Contains(AppSupportLevel.Partial)) return AppSupportLevel.Partial;

            return AppSupportLevel.Full;
        }
    }
}

/// <summary>
/// Rejestr możliwości NASZEJ implementacji (DocxToHtmlConverter → edytor Angular → HtmlToDocxConverter)
/// zweryfikowany w kodzie i w <c>DOCX_CONVERSION.md</c>, <c>COMPLIANCE_MATRIX.md</c>,
/// <c>AUDIT_WORD_COMPATIBILITY.md</c> §9 oraz ADR-ach. Każdy wpis to twierdzenie o kodzie —
/// gdy round-trip mu przeczy, raport oznacza pozycję jako „nieoczekiwana utrata" i to jest sygnał,
/// żeby poprawić kod ALBO ten rejestr. Wpis bez pewności dostaje <see cref="AppSupportLevel.Unknown"/>,
/// nie zgadujemy.
/// </summary>
public static class EditorCapabilityRegistry
{
    private const AppSupportLevel Full = AppSupportLevel.Full;
    private const AppSupportLevel Partial = AppSupportLevel.Partial;
    private const AppSupportLevel PassThrough = AppSupportLevel.PassThrough;
    private const AppSupportLevel Unsupported = AppSupportLevel.Unsupported;
    private const AppSupportLevel Unknown = AppSupportLevel.Unknown;

    private static readonly IReadOnlyDictionary<string, EditorCapability> Entries = new[]
    {
        new EditorCapability(FeatureKeys.Tables, "Tabele", Full, Full, Full,
            "Pełny round-trip: gridSpan/vMerge, style tabel (tblStyle+tblLook), pozycyjne obramowania, cieniowanie, szerokości z tblGrid (przy autofit bez tblW z preferowanych tcW — tblGrid to cache układu Worda, ADR-0108 r.8), wysokości wierszy, cantSplit/tblHeader, marginesy komórek. Inny zapis, ten sam wygląd: w:tblBorders → w:tcBorders per komórka, w:start/w:end → w:left/w:right. NIE: przekątne obramowania (TB-01), warunkowe formatowanie TEKSTU ze stylu tabeli (TB-02), w:tblCellSpacing ≠ 0 (reader renderuje border-spacing, writer nie odtwarza).",
            "DocxToHtmlConverter.AppendTableCellHtml / ResolveTableStyleContext; HtmlToDocxConverter (tblGrid z colgroup)"),
        new EditorCapability(FeatureKeys.NestedTables, "Tabele zagnieżdżone", Full, Full, Full,
            "Reader rekurencyjny, writer iteruje tylko bezpośrednie wiersze (bez duplikacji). Głębokie zagnieżdżenia podnoszą koszt paginacji edytora (ADR-0052: cięcie per komórka).",
            "DocxToHtmlConverter (rekurencja tabel); wysiwyg-editor `_measureRowLayout`"),
        new EditorCapability(FeatureKeys.FloatingTables, "Tabele pływające (w:tblpPr)", Partial, Partial, Partial,
            "Pozycja tabeli pływającej czytana od ADR-0108 r.6 (wcześniej ignorowana). Edytor nie oblewa jej tekstem jak Word; writer odtwarza tblpPr z data-*.",
            "DocxToHtmlConverter (w:tblpPr, ADR-0108 r.6)"),
        new EditorCapability(FeatureKeys.Lists, "Listy (numeracja/punktory)", Full, Full, Partial,
            "Wariant A (ADR-0036/0115): definicje list jadą w data-list-abstracts, Tab/Shift+Tab/Enter w modelu płaskim. Luki: numeracja dziedziczona ze STYLU akapitu (GAP-L8: writer dopisuje numPr wprost na akapit), w:lvlJc, znacznik w:tab poziomu, +4 pt przy szerokim numerze. Od ADR-0120 numbering.xml oryginału zostaje (definicje stylów list), a definicje z edytora są dopisywane pod nowymi numId.",
            "DocxToHtmlConverter.BuildListAbstractsAttribute; HtmlToDocxConverter.CaptureSourceListAbstracts/ParseLevelSpec; LIST_NUMBERING_GAP_REPORT.md"),
        new EditorCapability(FeatureKeys.Footnotes, "Przypisy dolne", Full, Full, Full,
            "Model boczny (ADR-0032/0049/0050/0083): treść w DocumentContent.Footnotes, odwołania niosą data-footnote-id, numeracja z w:footnotePr/w:numFmt. Word i edytor mogą inaczej rozłożyć przypisy między strony.",
            "DocxToHtmlConverter.ExtractFootnotes; HtmlToDocxConverter.AddFootnotes"),
        new EditorCapability(FeatureKeys.Endnotes, "Przypisy końcowe", Full, Full, Full,
            "Typ lustrzany do przypisów dolnych (ADR-0039/0041/0078); region na ostatniej stronie; format numeracji z settings.xml.",
            "DocxToHtmlConverter (Endnotes); HtmlToDocxConverter.PreserveNoteProperties"),
        new EditorCapability(FeatureKeys.Comments, "Komentarze", Unsupported, Unsupported, Unsupported,
            "Reader nie ma obsługi w:commentReference/comments.xml (0 odwołań w DocxToHtmlConverter) — komentarze nie trafiają do edytora; pass-through pakietu nie kopiuje comments.xml, więc w v2 giną po pierwszym autosave (KR-03).",
            "DocxToHtmlConverter (brak gałęzi CommentReference); HtmlToDocxConverter.PreserveOriginalParts (kopiuje tylko styles/theme/fontTable)"),
        new EditorCapability(FeatureKeys.TrackedChanges, "Śledzone zmiany (w:ins/w:del)", Partial, Unsupported, Unsupported,
            "Reader AKCEPTUJE rewizje przy imporcie (ADR-0088: w:ins rozpakowany, w:del usunięty) — treść końcowa jest zachowana, ale historia zmian, autorzy i daty giną; edytor nie ma trybu recenzji, writer nie emituje w:ins/w:del.",
            "DocxToHtmlConverter.AcceptTrackedRevisions"),
        new EditorCapability(FeatureKeys.ContentControls, "Formanty (w:sdt)", Partial, Partial, Partial,
            "SdtBlock/SdtRun/SdtCell renderowane (treść + pole wyboru w14:checkbox klikalne, ADR-0092/0113; podział bloków SDT przy paginacji ADR-0059). NIE round-tripują: w:dataBinding, w:placeholder, listy rozwijane/daty jako formanty (zostaje sam tekst).",
            "DocxToHtmlConverter case SdtRun/SdtBlock; HtmlToDocxConverter (sdt z data-*)"),
        new EditorCapability(FeatureKeys.CustomXmlMarkup, "Znaczniki w:customXml w treści", Partial, Unsupported, Unsupported,
            "Reader rozpakowuje treść z w:customXml (ADR-0103: wcześniej cichy drop), ale sam znacznik i jego atrybuty nie wracają przy zapisie — treść zostaje, semantyka XML ginie.",
            "DocxToHtmlConverter (CustomXmlElement)"),
        new EditorCapability(FeatureKeys.AltChunk, "Treść dołączona przez w:altChunk", Unsupported, Unsupported, Unsupported,
            "Reader nie obsługuje w:altChunk (0 odwołań) — dołączony HTML/RTF/DOCX NIE pojawia się w edytorze i ginie w v2. Word scala go przy otwarciu, więc użytkownik widzi w Wordzie treść, której u nas nie ma.",
            "DocxToHtmlConverter (brak gałęzi AltChunk)"),
        new EditorCapability(FeatureKeys.InlineImages, "Obrazy w wierszu", Full, Full, Full,
            "Wymiary EMU, obramowanie a:ln, alt text, MIME po bajtach (ADR-0108 r.11); EMZ/WMZ dekompresowane. Obraz jedzie jako base64 w HTML — bardzo duże obrazy obciążają edytor.",
            "DocxToHtmlConverter.ConvertDrawingToHtml; HtmlToDocxConverter (image path)"),
        new EditorCapability(FeatureKeys.AnchoredImages, "Obrazy zakotwiczone (wp:anchor)", Partial, Partial, Partial,
            "Model kotwicy = pozycja w DOM + kontrakt data-pos-* (ADR-0030/0051/0087/0094). Ograniczenia: relativeFrom/align przybliżane do offsetów (IM-04), oblewanie tylko square/przód/tył, pozycja nie śledzi przesunięć treści jak w Wordzie.",
            "DocxToHtmlConverter (anchor read); GUI core/utils/floating-anchor.util.ts"),
        new EditorCapability(FeatureKeys.WrapTightThrough, "Oblewanie tight/through", Unsupported, Unsupported, Partial,
            "CSS nie ma odpowiednika oblewania po konturze (IM-03) — edytor pokazuje oblewanie prostokątne; writer zapisuje tryb z data-*, jeśli reader go przeniósł.",
            "DocxToHtmlConverter (wrap mode → data-*)"),
        new EditorCapability(FeatureKeys.ImageRotation, "Obrót obrazu (a:xfrm/@rot)", Unsupported, Unsupported, Unsupported,
            "Brak odczytu i zapisu obrotu (IM-01): obraz w edytorze i w v2 stoi prosto.",
            "DocxToHtmlConverter.ConvertDrawingToHtml (a:xfrm)"),
        new EditorCapability(FeatureKeys.ImageCrop, "Przycięcie obrazu (a:srcRect)", Partial, Partial, Partial,
            "data-crop-* ↔ a:srcRect round-tripuje, ale podgląd używa clip-path: inset() bez przeskalowania do ramki jak Word.",
            "DocxToHtmlConverter (data-crop-*)"),
        new EditorCapability(FeatureKeys.DrawingShapes, "Kształty DrawingML (wps:wsp)", PassThrough, PassThrough, PassThrough,
            "Pass-through XML (ADR-0056..0081, R-41): podgląd generowany z geometrii presetów (domyślne adjust values poza roundRect), element NIEEDYTOWALNY (bez drag/resize/edycji tekstu), oryginalny w:drawing wraca 1:1 z data-docx-xml; usunięcie w edytorze = usunięcie z dokumentu.",
            "DocxToHtmlConverter (data-docx-xml/data-docx-rels); GUI core/utils/shape-xml.util.ts; GRAPHICS_CONVERSION.md"),
        new EditorCapability(FeatureKeys.DrawingGroups, "Grupy kształtów (wpg)", PassThrough, PassThrough, PassThrough,
            "Render rekurencyjny do głębokości 8; JEDNO nieobsługiwane dziecko degraduje CAŁĄ grupę do niewidocznego placeholdera (R-41f). XML wraca 1:1.",
            "DocxToHtmlConverter (wpg render)"),
        new EditorCapability(FeatureKeys.TextBoxes, "Pola tekstowe (txbxContent)", Partial, PassThrough, PassThrough,
            "Tekst pola ekstrahowany do podglądu (KR-06 minimum), geometria/obramowanie z pass-through; treść pola NIE jest edytowalna w edytorze, zmiany są niemożliwe bez Worda.",
            "DocxToHtmlConverter.RenderTextBoxContent"),
        new EditorCapability(FeatureKeys.VmlShapes, "Kształty VML (legacy)", PassThrough, PassThrough, PassThrough,
            "VML poza mc:Fallback jedzie pass-through (w:pict w data-docx-xml); obraz z v:imagedata renderowany jako zwykły obraz; gdy mc:Choice się renderuje, gałąź Fallback ginie przy zapisie (R-41e, ECMA-poprawne).",
            "DocxToHtmlConverter.ConvertPictureToHtml / ConvertAlternateContentToHtml"),
        new EditorCapability(FeatureKeys.OleObjects, "Obiekty OLE (w:object)", PassThrough, PassThrough, PassThrough,
            "Podgląd z v:imagedata + oryginalny XML i relacje w pass-through; obiekt > 4 MB idzie BEZ pass-through — podgląd zapisuje się jako zwykły obraz (widoczna degradacja, R-41d).",
            "DocxToHtmlConverter (EmbeddedObject)"),
        new EditorCapability(FeatureKeys.Charts, "Wykresy (c:chart)", PassThrough, PassThrough, PassThrough,
            "Reader nie maluje wykresu — wstawia NIEWIDOCZNY placeholder pass-through (RenderPreservedPlaceholder \"chart\"); użytkownik nie widzi wykresu w edytorze ani w PDF z edytora, ale XML i część wykresu wracają do v2.",
            "DocxToHtmlConverter.RenderPreservedPlaceholder"),
        new EditorCapability(FeatureKeys.SmartArt, "SmartArt (dgm)", PassThrough, PassThrough, PassThrough,
            "Jak wykresy: niewidoczny placeholder pass-through, brak podglądu w edytorze; XML wraca 1:1.",
            "DocxToHtmlConverter.RenderPreservedPlaceholder"),
        new EditorCapability(FeatureKeys.Math, "Równania (m:oMath)", Unsupported, Unsupported, Unsupported,
            "Brak obsługi OMML (0 odwołań w readerze) — równanie nie trafia do edytora i ginie w v2 (KR-06). Word pokazuje wzór, u nas znika bez ostrzeżenia.",
            "DocxToHtmlConverter (brak gałęzi m:oMath)"),
        new EditorCapability(FeatureKeys.MetafileImages, "Metapliki EMF/WMF", Partial, Partial, Full,
            "Własny tłumacz EMF/WMF→SVG (MetafileVectorTranslator) do podglądu; oryginalne bajty metapliku wracają przy zapisie z data-original-src. Tłumacz nie pokrywa wszystkich rekordów (EMF+, tryby mapowania) — podgląd może odbiegać od Worda.",
            "GraphicConversionService / MetafileVectorTranslator; HtmlToDocxConverter (data-original-src); GRAPHICS_CONVERSION.md"),
        new EditorCapability(FeatureKeys.SvgImages, "Obrazy SVG", Full, Full, Partial,
            "SVG sanityzowany (SanitizeSvg) i renderowany; przy zapisie writer nie umie osadzić SVG w v:imagedata/a:blip pakietu (\"psuje pakiet\") — obraz wraca przez rastrowy fallback lub zostaje inline w HTML.",
            "DocxToHtmlConverter.SanitizeSvg; HtmlToDocxConverter (image/svg+xml → false)"),
        new EditorCapability(FeatureKeys.ComplexFields, "Pola złożone (fldChar)", Partial, Partial, Partial,
            "Maszyna pól (ADR-0066/0070/0084/0086): PAGE/NUMPAGES/SECTIONPAGES dynamiczne, DATE/TIME z wartości cache, PAGEREF w spisach treści. Pozostałe kody (REF/SEQ/MERGEFIELD/FORM*) zachowują WARTOŚĆ jako tekst, KOD pola ginie (KR-05) — Word nie zaktualizuje ich po zapisie z edytora.",
            "DocxToHtmlConverter (FieldChar/FieldSpan); HtmlToDocxConverter.BuildFieldRun"),
        new EditorCapability(FeatureKeys.SimpleFields, "Pola proste (fldSimple)", Partial, Partial, Partial,
            "Jak pola złożone: PAGE/NUMPAGES/DATE obsługiwane, reszta = wartość jako tekst bez kodu pola.",
            "DocxToHtmlConverter.ConvertSimpleFieldToHtml"),
        new EditorCapability(FeatureKeys.TableOfContents, "Spis treści (TOC)", Partial, Partial, Unsupported,
            "Wpisy spisu zachowane jako tekst z PAGEREF i zakładkami (docx-bookmark); samo pole TOC nie round-tripuje — po zapisie Word nie odświeży spisu (KR-05), numery stron zostają z chwili importu.",
            "DocxToHtmlConverter (wpisy TOC, PAGEREF); HtmlToDocxConverter (docx-bookmark)"),
        new EditorCapability(FeatureKeys.Hyperlinks, "Hiperłącza", Full, Full, Full,
            "Zewnętrzne (relationship) i wewnętrzne (w:anchor) round-tripują; tab w hiperłączu rozcina segmenty (wpisy TOC).",
            "DocxToHtmlConverter.ConvertHyperlinkToHtml"),
        new EditorCapability(FeatureKeys.Bookmarks, "Zakładki", Full, Partial, Full,
            "bookmarkStart/End → markery docx-bookmark i z powrotem z unikalnym w:id (cele PAGEREF/TOC). Edytor nie pokazuje zakładek i nie chroni ich przed skasowaniem razem z tekstem.",
            "DocxToHtmlConverter.RenderBookmarkStart; HtmlToDocxConverter (docx-bookmark, _nextBookmarkId)"),
        new EditorCapability(FeatureKeys.TabStops, "Tabulatory i tab-stopy", Full, Full, Partial,
            "Segmenty pozycyjne na stopach (ADR-0066/0070/0084/0086/0108 r.13), w:tabs per akapit round-tripują; w:ptab (tab pozycyjny) czytany, ale writer zapisuje go jako zwykły w:tab (0 odwołań do PositionalTab w writerze).",
            "DocxToHtmlConverter.GetEffectiveTabStops/BuildPositionedTabContent; HtmlToDocxConverter.ParseTabStops"),
        new EditorCapability(FeatureKeys.MultipleSections, "Wiele sekcji", Full, Full, Full,
            "Geometria i nagłówki/stopki per sekcja (ADR-0023/0025) przez markery div.docx-section-break; body-level sectPr = OSTATNIA sekcja. Ścieżka Sign nie przenosi sekcyjnych nagłówków (KR-14).",
            "DocxToHtmlConverter.GetSectionPropertiesInDocumentOrder; HtmlToDocxConverter.CreateSectionBreakParagraph"),
        new EditorCapability(FeatureKeys.Columns, "Kolumny (w:cols)", Full, Partial, Full,
            "Model kolumn sekcji (ADR-0039): multicol per strona + pasmo dla przerwy ciągłej 1→N. Edytor nie ma UI do zmiany liczby kolumn; podział między kolumny liczy przeglądarka, nie Word.",
            "DocumentContent.Columns; wysiwyg-editor (docx-col-band); scratchpad cdp-columns-probe"),
        new EditorCapability(FeatureKeys.HeaderFooter, "Nagłówki i stopki", Full, Full, Full,
            "Wariant wybierany wg referencji sekcji (nie kolejności partów); tabele, obrazy i pola PAGE/NUMPAGES w stopce obsługiwane; za wysoka stopka psuje wysokość kolumn paginacji.",
            "DocxToHtmlConverter.ExtractHeader/ExtractFooter; HtmlToDocxConverter.AddHeaderAndFooter"),
        new EditorCapability(FeatureKeys.FirstPageHeader, "Inna pierwsza strona (titlePg)", Full, Full, Full,
            "Czytane tylko przy w:titlePg (ADR-0037: null = dziedzicz, \"\" = puste); edycja z panelu nagłówka/stopki.",
            "DocxToHtmlConverter.HasTitlePage"),
        new EditorCapability(FeatureKeys.EvenOddHeaders, "Inne strony parzyste (evenAndOddHeaders)", Full, Unsupported, Full,
            "Wariant parzysty jest CZYTANY i ZACHOWYWANY przy zapisie, ale edytor renderuje contenteditable tylko dla strony 0 — nie da się go edytować z GUI.",
            "DocxToHtmlConverter.HasEvenAndOddHeaders; wysiwyg-editor.html (editingSection === 'header' && $index === 0)"),
        new EditorCapability(FeatureKeys.PageNumberFormat, "Format/start numeracji stron (pgNumType)", Unsupported, Unsupported, Unsupported,
            "Brak odczytu w:pgNumType (0 odwołań): pole PAGE liczy strony od 1 i cyframi arabskimi; restart numeracji w sekcji i format rzymski giną w v2 (KR-10).",
            "DocxToHtmlConverter (sectPr → brak pgNumType)"),
        new EditorCapability(FeatureKeys.EmbeddedFonts, "Fonty osadzone (odttf)", Unsupported, Unsupported, Unknown,
            "Edytor nie używa osadzonych fontów do renderowania (przeglądarka bierze font systemowy/generyczny — inne łamanie wierszy niż Word). Pass-through kopiuje fontTable.xml strumieniem, ale NIE kopiuje części fontów (0 odwołań do FontPart/odttf) — relacje w:embed* mogą zostać wiszące; zweryfikować raportem kondycji na wyniku round-tripu.",
            "HtmlToDocxConverter.PreserveOriginalParts (FontTablePart.FeedData bez części fontów)"),
        new EditorCapability(FeatureKeys.Symbols, "Znaki symboliczne (w:sym)", Full, Full, Partial,
            "w:sym + PUA tłumaczone na Unicode z dokładną nazwą fontu (fallback span+font); writer zapisuje znak jako tekst, nie w:sym (0 odwołań do SymbolChar) — Word pokaże glif, jeśli font symboliczny jest dostępny.",
            "DocxToHtmlConverter.ConvertSymbolCharToHtml"),
        new EditorCapability(FeatureKeys.PageBorders, "Obramowanie strony (pgBorders)", Unsupported, Unsupported, Unsupported,
            "Brak odczytu i zapisu w:pgBorders — ramka strony znika w edytorze i w v2.",
            "DocxToHtmlConverter (sectPr)"),
        new EditorCapability(FeatureKeys.LineNumbering, "Numerowanie wierszy (lnNumType)", Unsupported, Unsupported, Unsupported,
            "Brak obsługi w:lnNumType — numery wierszy niewidoczne i tracone w v2.",
            "DocxToHtmlConverter (sectPr)"),
        new EditorCapability(FeatureKeys.Watermark, "Znak wodny", Unknown, Unknown, Unknown,
            "Znak wodny to kształt VML w nagłówku — powinien przejść pass-through jak inne VML, ale ścieżka nagłówka nie była weryfikowana pod tym kątem; sprawdzić podgląd i wynik round-tripu.",
            "DocxToHtmlConverter.ExtractHeader + ścieżka VML"),
        new EditorCapability(FeatureKeys.RtlBidi, "Tekst od prawej (bidi/rtl)", Unsupported, Unsupported, Unsupported,
            "Brak obsługi kierunku tekstu (0 odwołań do BiDi/RightToLeft) — akapity RTL renderują się od lewej i tak zapisują (CH-03).",
            "DocxToHtmlConverter.ConvertParagraphPropertiesToCss"),
        new EditorCapability(FeatureKeys.HiddenText, "Tekst ukryty (w:vanish)", Unsupported, Unsupported, Unsupported,
            "Reader ignoruje w:vanish (0 odwołań): tekst ukryty w Wordzie staje się WIDOCZNY w edytorze, w v2 i w PDF z edytora (KR-12 — ryzyko ujawnienia treści roboczych).",
            "DocxToHtmlConverter.ConvertRunPropertiesToCss"),
        new EditorCapability(FeatureKeys.FramesDropCaps, "Ramki akapitów / inicjały (framePr)", Unsupported, Unsupported, Unsupported,
            "Brak obsługi w:framePr — inicjał (drop cap) staje się zwykłym akapitem, pozycjonowanie ramki ginie.",
            "DocxToHtmlConverter.ConvertParagraphPropertiesToCss"),
        new EditorCapability(FeatureKeys.LegacyFormFields, "Pola formularza legacy (ffData)", Unsupported, Unsupported, Unsupported,
            "FORMTEXT/FORMCHECKBOX/FORMDROPDOWN (0 odwołań do ffData): zostaje sam tekst wyniku pola; formularz przestaje być formularzem po zapisie.",
            "DocxToHtmlConverter (FieldChar bez ffData)"),
        new EditorCapability(FeatureKeys.DocumentProtection, "Ochrona dokumentu", Full, Full, Full,
            "Wymuszona w:documentProtection/w:writeProtection → DocumentContent.IsReadOnlyProtected → edytor tylko do odczytu; ustawienie zachowane przy eksporcie (PreserveEditProtection). Trybów częściowych (komentarze/formularze) nie egzekwujemy — każda ochrona blokuje całość.",
            "DocxToHtmlConverter (IsReadOnlyProtected); HtmlToDocxConverter.PreserveEditProtection"),
        new EditorCapability(FeatureKeys.MailMerge, "Korespondencja seryjna (settings)", Unsupported, Unsupported, Unsupported,
            "Ustawienia w:mailMerge nie są czytane, a settings.xml jest odtwarzany wybiórczo — powiązanie ze źródłem danych ginie w v2 (zwykle pożądane); pola MERGEFIELD zostają jako tekst.",
            "HtmlToDocxConverter (settings.xml generowany)", LossIsBenign: true),
        new EditorCapability(FeatureKeys.Glossary, "Bloki konstrukcyjne (glossary)", Unsupported, Unsupported, Unsupported,
            "Część glossary (Quick Parts, autotekst) nie jest kopiowana w pass-through — ginie w v2. Bez wpływu na treść dokumentu.",
            "HtmlToDocxConverter.PreserveOriginalParts", LossIsBenign: true),
        new EditorCapability(FeatureKeys.CustomXmlParts, "Części customXml (magazyn danych)", Unsupported, Unsupported, Unsupported,
            "customXml/item*.xml nie są kopiowane w pass-through — formanty z w:dataBinding tracą źródło danych, metadane systemów obiegu (np. SharePoint) giną w v2.",
            "HtmlToDocxConverter.PreserveOriginalParts"),
        new EditorCapability(FeatureKeys.VbaMacros, "Makra VBA", Unsupported, Unsupported, Unsupported,
            "Bramka uploadu odrzuca dokumenty z makrami (UploadSecurity:RejectDocxMacros) — plik nie wejdzie do aplikacji; to celowa polityka bezpieczeństwa.",
            "FileUploadSecurityService.ValidateDocxStructure", LossIsBenign: true),
        new EditorCapability(FeatureKeys.DigitalSignature, "Podpis cyfrowy XML-DSig", Unsupported, Unsupported, Unsupported,
            "Podpis pakietu nie przeżywa żadnego zapisu (zmiana bajtów); aplikacja ma WŁASNY format podpisu (DigitalSignatureService) i nie weryfikuje XML-DSig z Worda.",
            "DigitalSignatureService", LossIsBenign: true)
    }.ToDictionary(capability => capability.Key, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, EditorCapability> All => Entries;

    public static EditorCapability? Find(string key) => Entries.GetValueOrDefault(key);

    /// <summary>Konstrukcja z inwentarza, dla której rejestr nie ma wpisu — raportowana jako niezweryfikowana, nigdy pomijana.</summary>
    public static EditorCapability Unregistered(string key) => new(
        key, key, Unknown, Unknown, Unknown,
        "Konstrukcja wykryta przez inwentarz, ale rejestr możliwości edytora nie ma dla niej wpisu — uzupełnij EditorCapabilityRegistry po sprawdzeniu kodu.",
        "EditorCapabilityRegistry");
}
