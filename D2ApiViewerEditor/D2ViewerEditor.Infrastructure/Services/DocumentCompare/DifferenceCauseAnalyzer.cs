using System.Globalization;
using System.Text.RegularExpressions;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.DocumentHealth;

namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

/// <summary>
/// Odpowiada na pytanie operacyjne porównywarki: „w oryginale to jest, w kopii z edytora nie ma — DLACZEGO
/// i co z tego wynika?”. Trzy źródła prawdy: (1) rejestr możliwości edytora (<see cref="EditorCapabilityRegistry"/>)
/// — czy konstrukcję w ogóle obsługujemy; (2) wiedza o writerze — co generuje od nowa, co zapieka inline,
/// co przepisuje (identyfikatory); (3) szum Worda — co Word dopisuje bez znaczenia. Gdy element treści
/// (akapit, run, tabela, obraz) zniknął, a rejestr deklaruje obsługę, przyczyny nie da się rozstrzygnąć
/// automatycznie: albo użytkownik usunął, albo reader pominął — raport mówi to wprost zamiast zgadywać.
/// Perspektywa: lewy = oryginał (v1), prawy = kopia zapisana z edytora (v2).
/// </summary>
public static class DifferenceCauseAnalyzer
{
    private const int UnitTolerance = 2;

    private static readonly Regex NoiseAttribute = new(
        @"^(w:rsid\w*|w14:paraId|w14:textId|w16cid:durableId|w16du:dateUtc|mc:Ignorable|xml:space|w:hint|wp14:anchorId|wp14:editId|distT|distB|distL|distR|bwMode|rotWithShape)$",
        RegexOptions.Compiled);

    /// <summary>Atrybuty-odwołania do relacji i generowane nazwy/id obiektów graficznych — przepisywane spójnie przy zapisie.</summary>
    private static readonly Regex RelationshipOrGeneratedIdAttribute = new(@"^(r:embed|r:id|r:link|r:pict)$", RegexOptions.Compiled);

    private static readonly Regex UnitAttribute = new(
        @"^(w:w|w:h|w:left|w:right|w:top|w:bottom|w:start|w:end|w:hanging|w:firstLine|w:before|w:after|w:line|w:pos|w:sz|w:szCs|w:space|w:val|w:tblpX|w:tblpY|w:header|w:footer|w:gutter|cx|cy|x|y)$",
        RegexOptions.Compiled);

    private static readonly Regex ContentPart = new(
        @"^word/(document|header\d*|footer\d*|footnotes|endnotes|comments|glossary/document)\.xml$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> NoiseElements = new(StringComparer.Ordinal)
    {
        "w:proofErr", "w:lastRenderedPageBreak", "w:noProof", "w:rsid", "w:rsids", "w:rsidRoot", "w:bookmarkEnd"
    };

    /// <summary>Atrybuty w twipach: 1 px edytora = 15 twips, więc dryf ≤ 15 to zaokrąglenie twips→px→twips, nie edycja.</summary>
    private const int PixelRoundingTwips = 15;

    private static readonly Regex ThemeFontAttribute = new(@"^w:(ascii|hAnsi|eastAsia|cs)[Tt]heme$", RegexOptions.Compiled);

    /// <summary>Elementy formatowania, które writer zapieka inline (ze stylu / obliczone) — Word trzymał je w stylu lub domyślnych.</summary>
    private static readonly HashSet<string> BakedFormatting = new(StringComparer.Ordinal)
    {
        "w:rFonts", "w:sz", "w:szCs", "w:color", "w:spacing", "w:jc", "w:ind", "w:kern", "w:b", "w:bCs", "w:i", "w:iCs",
        "w:tblGrid", "w:gridCol", "w:tcW", "w:tblW", "w:tblLook", "w:tblInd", "w:tblCellMar", "w:tblLayout", "w:tblBorders",
        "w:tcBorders", "w:shd", "w:vAlign", "w:widowControl", "w:contextualSpacing", "w:snapToGrid", "w:trHeight",
        "w:tabs", "w:tab", "w:pBdr", "w:keepNext", "w:keepLines", "w:cantSplit", "w:tblHeader", "w:gridSpan", "w:vMerge",
        "w:tblpPr", "w:tblStyle", "w:rStyle", "w:pStyle", "w:u", "w:strike", "w:caps", "w:smallCaps", "w:vertAlign", "w:highlight"
    };

    /// <summary>Element/atrybut → klucz konstrukcji z rejestru możliwości edytora (po nazwie kwalifikowanej ostatniego segmentu ścieżki).</summary>
    private static readonly Dictionary<string, string> FeatureByName = new(StringComparer.Ordinal)
    {
        ["w:commentReference"] = FeatureKeys.Comments, ["w:commentRangeStart"] = FeatureKeys.Comments, ["w:commentRangeEnd"] = FeatureKeys.Comments,
        ["w:comment"] = FeatureKeys.Comments, ["w:annotationRef"] = FeatureKeys.Comments,
        ["m:oMath"] = FeatureKeys.Math, ["m:oMathPara"] = FeatureKeys.Math, ["m:r"] = FeatureKeys.Math, ["m:t"] = FeatureKeys.Math,
        ["w:altChunk"] = FeatureKeys.AltChunk,
        ["w:vanish"] = FeatureKeys.HiddenText, ["w:specVanish"] = FeatureKeys.HiddenText,
        ["w:pgNumType"] = FeatureKeys.PageNumberFormat,
        ["w:pgBorders"] = FeatureKeys.PageBorders, ["w:lnNumType"] = FeatureKeys.LineNumbering,
        ["w:bidi"] = FeatureKeys.RtlBidi, ["w:rtl"] = FeatureKeys.RtlBidi,
        ["w:framePr"] = FeatureKeys.FramesDropCaps,
        ["w:ffData"] = FeatureKeys.LegacyFormFields,
        ["w:ins"] = FeatureKeys.TrackedChanges, ["w:del"] = FeatureKeys.TrackedChanges, ["w:moveFrom"] = FeatureKeys.TrackedChanges,
        ["w:moveTo"] = FeatureKeys.TrackedChanges, ["w:rPrChange"] = FeatureKeys.TrackedChanges, ["w:pPrChange"] = FeatureKeys.TrackedChanges,
        ["w:tblPrChange"] = FeatureKeys.TrackedChanges, ["w:sectPrChange"] = FeatureKeys.TrackedChanges, ["w:delText"] = FeatureKeys.TrackedChanges,
        ["w:sdt"] = FeatureKeys.ContentControls, ["w:sdtPr"] = FeatureKeys.ContentControls, ["w:sdtContent"] = FeatureKeys.ContentControls,
        ["w:dataBinding"] = FeatureKeys.ContentControls, ["w:placeholder"] = FeatureKeys.ContentControls, ["w:docPartObj"] = FeatureKeys.ContentControls,
        ["w15:appearance"] = FeatureKeys.ContentControls, ["w15:color"] = FeatureKeys.ContentControls,
        ["w:customXml"] = FeatureKeys.CustomXmlMarkup, ["w:customXmlPr"] = FeatureKeys.CustomXmlMarkup,
        ["w:bookmarkStart"] = FeatureKeys.Bookmarks,
        ["w:footnoteReference"] = FeatureKeys.Footnotes, ["w:footnote"] = FeatureKeys.Footnotes, ["w:footnotePr"] = FeatureKeys.Footnotes,
        ["w:endnoteReference"] = FeatureKeys.Endnotes, ["w:endnote"] = FeatureKeys.Endnotes, ["w:endnotePr"] = FeatureKeys.Endnotes,
        ["w:numPr"] = FeatureKeys.Lists, ["w:numId"] = FeatureKeys.Lists, ["w:ilvl"] = FeatureKeys.Lists,
        ["w:abstractNum"] = FeatureKeys.Lists, ["w:num"] = FeatureKeys.Lists, ["w:lvl"] = FeatureKeys.Lists, ["w:lvlOverride"] = FeatureKeys.Lists,
        ["w15:restartNumberingAfterBreak"] = FeatureKeys.Lists,
        ["w:fldChar"] = FeatureKeys.ComplexFields, ["w:instrText"] = FeatureKeys.ComplexFields, ["w:fldSimple"] = FeatureKeys.SimpleFields,
        ["w:hyperlink"] = FeatureKeys.Hyperlinks,
        ["w:tab"] = FeatureKeys.TabStops, ["w:ptab"] = FeatureKeys.TabStops, ["w:tabs"] = FeatureKeys.TabStops,
        ["w:cols"] = FeatureKeys.Columns, ["w:titlePg"] = FeatureKeys.FirstPageHeader, ["w:evenAndOddHeaders"] = FeatureKeys.EvenOddHeaders,
        ["w:sym"] = FeatureKeys.Symbols,
        ["w:embedRegular"] = FeatureKeys.EmbeddedFonts, ["w:embedBold"] = FeatureKeys.EmbeddedFonts, ["w:embedItalic"] = FeatureKeys.EmbeddedFonts,
        ["w:embedBoldItalic"] = FeatureKeys.EmbeddedFonts,
        ["w:documentProtection"] = FeatureKeys.DocumentProtection, ["w:writeProtection"] = FeatureKeys.DocumentProtection,
        ["w:mailMerge"] = FeatureKeys.MailMerge,
        ["w:object"] = FeatureKeys.OleObjects, ["o:OLEObject"] = FeatureKeys.OleObjects,
        ["w:pict"] = FeatureKeys.VmlShapes, ["v:shape"] = FeatureKeys.VmlShapes, ["v:rect"] = FeatureKeys.VmlShapes, ["v:group"] = FeatureKeys.VmlShapes,
        ["v:textbox"] = FeatureKeys.TextBoxes, ["w:txbxContent"] = FeatureKeys.TextBoxes, ["wps:txbx"] = FeatureKeys.TextBoxes,
        ["wps:wsp"] = FeatureKeys.DrawingShapes, ["wpg:wgp"] = FeatureKeys.DrawingGroups,
        ["c:chart"] = FeatureKeys.Charts, ["dgm:relIds"] = FeatureKeys.SmartArt,
        ["wp:inline"] = FeatureKeys.InlineImages, ["wp:anchor"] = FeatureKeys.AnchoredImages, ["pic:pic"] = FeatureKeys.InlineImages,
        ["a:blip"] = FeatureKeys.InlineImages, ["w:drawing"] = FeatureKeys.InlineImages,
        ["wp:wrapTight"] = FeatureKeys.WrapTightThrough, ["wp:wrapThrough"] = FeatureKeys.WrapTightThrough,
        ["a:srcRect"] = FeatureKeys.ImageCrop,
        ["w:tbl"] = FeatureKeys.Tables, ["w:tr"] = FeatureKeys.Tables, ["w:tc"] = FeatureKeys.Tables,
        ["asvg:svgBlip"] = FeatureKeys.SvgImages
    };

    public static DifferenceAnalysis Analyze(DocumentDifference difference)
    {
        var part = difference.PartPath;
        var element = ElementNameOf(difference);
        var name = difference.Name ?? string.Empty;
        var inContent = ContentPart.IsMatch(part);

        return difference.Kind switch
        {
            DifferenceKind.PartOnlyInLeft => MissingPart(part),
            DifferenceKind.PartOnlyInRight => AddedPart(part),
            DifferenceKind.BinaryPartChanged => BinaryChanged(part),
            DifferenceKind.ContentTypeChanged => new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                "Ten sam plik ma inny content type — writer deklaruje typ po swojemu; Word czyta po zawartości.", null, null),
            DifferenceKind.PartRenamed => new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"Identyczne bajty pod inną ścieżką ({difference.LeftValue} → {difference.RightValue}): writer zapisuje obrazy pod własnymi nazwami i w katalogu /media zamiast /word/media. Word to akceptuje, ale ścieżka jest niekanoniczna — do ujednolicenia w writerze.", FeatureKeys.InlineImages, "HtmlToDocxConverter (AddImagePart / nazwy części obrazów)"),
            DifferenceKind.ElementMoved => MovedElement(difference, element, inContent),
            DifferenceKind.ElementNameChanged => new DifferenceAnalysis(DifferenceCause.Unknown, DifferenceImpact.Layout,
                $"W tym samym miejscu stoi inny element ({difference.LeftValue} → {difference.RightValue}) — struktura po zapisie różni się od oryginału; sprawdź wycinki obu stron.", null, null),
            DifferenceKind.ElementOnlyInLeft => LostElement(difference, element, inContent),
            DifferenceKind.ElementOnlyInRight => AddedElement(difference, element, inContent, part),
            DifferenceKind.AttributeOnlyInLeft => LostAttribute(element, name, inContent, part),
            DifferenceKind.AttributeOnlyInRight => AddedAttribute(element, name),
            DifferenceKind.AttributeValueChanged => ChangedAttribute(difference, element, name, part, inContent),
            DifferenceKind.TextChanged => ChangedText(difference),
            _ => new DifferenceAnalysis(DifferenceCause.Unknown, DifferenceImpact.None, "Nieznany rodzaj różnicy.", null, null)
        };
    }

    // ── Części pakietu ────────────────────────────────────────────────────────

    private static DifferenceAnalysis MissingPart(string part)
    {
        if (Regex.IsMatch(part, @"^docProps/app\.xml$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.None,
                "Writer nie odtwarza docProps/app.xml (metadane aplikacji: liczba stron/słów, program). Word toleruje; do naprawienia w HtmlToDocxConverter.", null, "HtmlToDocxConverter (ExtendedFilePropertiesPart)");
        }

        if (Regex.IsMatch(part, @"^docProps/|webSettings\.xml$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.None,
                "Część pomocnicza (metadane, ustawienia WWW) — writer generuje pakiet bez niej; bez wpływu na treść i otwarcie.", null, null);
        }

        if (Regex.IsMatch(part, @"^word/(styles|theme/theme\d*|fontTable)\.xml$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.Layout,
                "Część stylów/motywu/fontów oryginału nie przetrwała — pass-through pakietu nie zadziałał (zapis bez masterId albo wyjątek w PreserveOriginalParts); formatowanie ze stylów będzie inne (R-16).", null, "HtmlToDocxConverter.PreserveOriginalParts");
        }

        if (Regex.IsMatch(part, @"^word/(comments|commentsExtended|commentsIds|people)\.xml$", RegexOptions.IgnoreCase))
        {
            return FromRegistry(FeatureKeys.Comments, lost: true, DifferenceImpact.DataLoss);
        }

        if (Regex.IsMatch(part, @"^customXml/", RegexOptions.IgnoreCase))
        {
            return FromRegistry(FeatureKeys.CustomXmlParts, lost: true, DifferenceImpact.DataLoss);
        }

        if (Regex.IsMatch(part, @"^word/glossary/", RegexOptions.IgnoreCase))
        {
            return FromRegistry(FeatureKeys.Glossary, lost: true, DifferenceImpact.None);
        }

        if (Regex.IsMatch(part, @"^word/fonts/", RegexOptions.IgnoreCase))
        {
            return FromRegistry(FeatureKeys.EmbeddedFonts, lost: true, DifferenceImpact.Layout);
        }

        if (Regex.IsMatch(part, @"^word/embeddings/", RegexOptions.IgnoreCase))
        {
            return FromRegistry(FeatureKeys.OleObjects, lost: true, DifferenceImpact.DataLoss);
        }

        if (Regex.IsMatch(part, @"^word/media/", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.UserEditOrLoss, DifferenceImpact.DataLoss,
                "Obraz z oryginału nie ma odpowiednika po zapisie: użytkownik usunął grafikę ALBO writer zapisał ją pod inną nazwą/formatem (sprawdź „dodane” obrazy po prawej i relacje) ALBO reader ją pominął (np. obraz linkowany, nierozpoznany format).", FeatureKeys.InlineImages, "DocxToHtmlConverter.ConvertDrawingToHtml");
        }

        if (Regex.IsMatch(part, @"^_rels/\.rels$|^\[Content_Types\]\.xml$|^word/document\.xml$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.Unknown, DifferenceImpact.WordRepair,
                "Brak części obowiązkowej pakietu po zapisie — plik jest uszkodzony; sprawdź go w „Kondycji dokumentu”.", null, null);
        }

        return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.Cosmetic,
            "Część z oryginału nie istnieje po zapisie — writer składa pakiet od nowa i kopiuje tylko styles/theme/fontTable; sprawdź, czy ta część niosła treść.", null, "HtmlToDocxConverter.PreserveOriginalParts");
    }

    private static DifferenceAnalysis AddedPart(string part)
    {
        if (Regex.IsMatch(part, @"^word/numbering\.xml$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.None,
                "Writer zawsze generuje numbering.xml (listy wariant A) — oryginał nie miał tej części.", FeatureKeys.Lists, null);
        }

        if (Regex.IsMatch(part, @"^word/styles\d+\.xml$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.Cosmetic,
                "Niekanoniczna nazwa części stylów (styles2.xml) — artefakt pass-through (R-21); Word czyta po relacji.", null, "HtmlToDocxConverter.PreserveOriginalParts");
        }

        if (Regex.IsMatch(part, @"^word/media/", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.Cosmetic,
                "Nowa część obrazu — writer zapisuje grafiki pod własnymi nazwami (image1.png…); dopasuj do brakującego obrazu po stronie oryginału.", FeatureKeys.InlineImages, null);
        }

        return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.None,
            "Część, której nie było w oryginale — writer dodaje ją przy generowaniu pakietu.", null, null);
    }

    private static DifferenceAnalysis BinaryChanged(string part)
    {
        if (Regex.IsMatch(part, @"^word/media/", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.Cosmetic,
                "Bajty obrazu się zmieniły — writer przekodował grafikę (base64 z edytora, metaplik → podgląd); jeśli oryginał był EMF/WMF/SVG, jakość może odbiegać.", FeatureKeys.MetafileImages, "HtmlToDocxConverter (data-original-src)");
        }

        return new DifferenceAnalysis(DifferenceCause.Unknown, DifferenceImpact.DataLoss,
            "Zmieniła się część binarna spoza obrazów (font, OLE, podpis) — nasz zapis nie powinien jej dotykać.", null, null);
    }

    // ── Elementy ──────────────────────────────────────────────────────────────

    /// <summary>Kontenery właściwości, w których kolejność dzieci narzuca schemat (a Word toleruje dowolną).</summary>
    private static readonly HashSet<string> PropertyContainers = new(StringComparer.Ordinal)
    {
        "w:rPr", "w:pPr", "w:tcPr", "w:trPr", "w:tblPr", "w:tblPrEx", "w:sectPr", "w:tblCellMar", "w:tcMar", "w:tblBorders",
        "w:tcBorders", "w:pBdr", "w:numPr", "w:tabs", "w:tblLook", "w:framePr", "w:pgMar", "w:pgSz", "w:cols",
    };

    /// <summary>Kontenery marginesów/obramowań, w których `w:start`/`w:end` (zapis dwukierunkowy) znaczą to samo co `w:left`/`w:right` dla LTR.</summary>
    private static readonly HashSet<string> BidiSideContainers = new(StringComparer.Ordinal)
    {
        "w:tblCellMar", "w:tcMar", "w:tblBorders", "w:tcBorders", "w:pBdr", "w:ind",
    };

    /// <summary>Samozamykający się element bez atrybutów (poza deklaracjami przestrzeni nazw), np. `&lt;w:trPr/&gt;`.</summary>
    private static readonly Regex EmptyElementExcerpt = new(@"^\s*<[\w:.-]+(\s+xmlns(:\w+)?=""[^""]*"")*\s*/>\s*$", RegexOptions.Compiled);

    /// <summary>Kontekst akapitu (tekst) znany i identyczny po obu stronach — komparator podaje go także dla różnic jednostronnych.</summary>
    private static bool SameParagraphText(DocumentDifference difference) =>
        !string.IsNullOrEmpty(difference.LeftContext) && string.Equals(difference.LeftContext, difference.RightContext, StringComparison.Ordinal);

    /// <summary>Nazwa rodzica z pozycyjnej ścieżki XML: `/w:tbl[1]/w:tblPr[1]/w:tblCellMar[1]/w:start[1]` → `w:tblCellMar`.</summary>
    private static string? ParentElementOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length < 2 ? null : Regex.Replace(segments[^2], @"\[\d+\]$", string.Empty);
    }

    private static DifferenceAnalysis MovedElement(DocumentDifference difference, string? element, bool inContent)
    {
        var leftParent = ParentElementOf(difference.LeftPath);
        var rightParent = ParentElementOf(difference.RightPath);
        var leftContainer = difference.LeftPath is { } lp ? lp[..lp.LastIndexOf('/')] : null;
        var rightContainer = difference.RightPath is { } rp ? rp[..rp.LastIndexOf('/')] : null;

        // Ten sam kontener właściwości po obu stronach, inna pozycja dziecka: writer pisze dzieci w kolejności ze schematu
        // (np. CT_TcPr: noWrap PRZED tcMar), oryginał z generatora miał inną — Word czyta obie. To nie przeniesienie treści.
        if (leftParent is not null && leftParent == rightParent && leftContainer == rightContainer && PropertyContainers.Contains(leftParent))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"{element} stoi w tym samym {leftParent}, tylko na innej pozycji — writer porządkuje dzieci kontenera właściwości wg schematu OOXML (oryginał miał kolejność spoza schematu, którą Word toleruje). Wartości bez zmian.", null, null);
        }

        return inContent
            ? new DifferenceAnalysis(DifferenceCause.UserEditOrLoss, DifferenceImpact.Layout,
                "Element treści jest w obu plikach, ale w innym miejscu: albo użytkownik przeniósł fragment, albo reader/writer zmienił kolejność (np. tabela pływająca, przypis). Porównaj kontekst akapitów obok.", null, null)
            : new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                "Kolejność w tej części nie ma znaczenia (relacje, typy zawartości, style) — Word i SDK zapisują ją dowolnie.", null, null);
    }

    private static DifferenceAnalysis LostElement(DocumentDifference difference, string? element, bool inContent)
    {
        if (element is not null && NoiseElements.Contains(element))
        {
            return new DifferenceAnalysis(DifferenceCause.WordNoise, DifferenceImpact.None,
                $"{element} to znacznik pomocniczy Worda (korekta, sesja, podział strony z renderowania) — jego brak nie zmienia dokumentu.", null, null);
        }

        // Pusty kontener (<w:trPr/>, <w:rPr/>): writer nie zapisuje pustych kontenerów właściwości — bez skutku.
        if (element is not null && PropertyContainers.Contains(element) && EmptyElementExcerpt.IsMatch(difference.LeftExcerpt ?? string.Empty))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"Pusty {element} z oryginału (bez dzieci) nie jest zapisywany przez writer — kontener bez właściwości niczego nie zmienia.", null, null);
        }

        // w:start/w:end (zapis dwukierunkowy) w marginesach/obramowaniach: writer zapisuje w:left/w:right — dla LTR to ta sama wartość.
        if (element is "w:start" or "w:end" && ParentElementOf(difference.LeftPath) is { } sideParent && BidiSideContainers.Contains(sideParent))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"{sideParent}/{element} (zapis dwukierunkowy start/end) writer zapisuje jako {(element == "w:start" ? "w:left" : "w:right")} — dla tekstu od lewej to ta sama wartość, Word czyta obie formy (patrz dodany w:left/w:right w tym samym kontenerze).", null, null);
        }

        // Pusty run (<w:r/>) — bez tekstu i właściwości; writer go nie zapisuje.
        if (element == "w:r" && EmptyElementExcerpt.IsMatch(difference.LeftExcerpt ?? string.Empty))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                "Pusty run (w:r bez tekstu i właściwości) z oryginału nie jest zapisywany — niczego nie zmienia.", null, null);
        }

        // Podział runów: ten sam tekst akapitu po obu stronach, a run/tekst/podział wiersza „zniknął” tylko jako węzeł —
        // writer zapisuje każdy <br>/span jako osobny run (oryginał: <w:r><w:t/><w:br/><w:t/></w:r>).
        if (element is "w:r" or "w:t" or "w:br" or "w:tab" && SameParagraphText(difference))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"{element} z oryginału nie ma węzła-odpowiednika, ale tekst akapitu po obu stronach jest identyczny („{difference.LeftContext}”) — writer dzieli run na osobne runy przy podziale wiersza/zmianie formatowania; treść i łamanie bez zmian.", null, "HtmlToDocxConverter (runy per <br>/<span>)");
        }

        if ((element == "w:tblBorders" || ParentElementOf(difference.LeftPath) == "w:tblBorders") && inContent)
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.Cosmetic,
                "Obramowanie z poziomu tabeli (w:tblBorders) writer zapisuje jako obramowania KAŻDEJ komórki (w:tcBorders) — wygląd ten sam, ale zmiana stylu tabeli w Wordzie nie zmieni już ramek (są jawne per komórka).", FeatureKeys.Tables, "HtmlToDocxConverter (tblBorders → tcBorders)");
        }

        if (element == "w:tblCellSpacing")
        {
            var spacing = Regex.Match(difference.LeftExcerpt ?? string.Empty, @"w:w=""(-?\d+)""").Groups[1].Value;

            return spacing is "" or "0"
                ? new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                    "w:tblCellSpacing = 0 to brak odstępu między komórkami (domyślne) — writer nie zapisuje zerowego odstępu; wygląd ten sam.", FeatureKeys.Tables, null)
                : new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Layout,
                    $"Odstęp między komórkami tabeli (w:tblCellSpacing = {spacing} twips) nie wraca po zapisie — reader renderuje go (border-spacing), ale writer nie odtwarza w:tblCellSpacing: komórki po zapisie stykają się ramkami.", FeatureKeys.Tables, "HtmlToDocxConverter (tblPr — brak TableCellSpacing z border-spacing)");
        }

        switch (element)
        {
            case "w:lang":
                return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Cosmetic,
                    "Język runu (w:lang) nie wraca po zapisie — Word użyje języka domyślnego: inny słownik korekty i inne dzielenie wyrazów (przy włączonym dzieleniu może zmienić łamanie wierszy).", null, "DocxToHtmlConverter.ConvertRunPropertiesToCss (lang → data-*)");
            case "w:kern":
            case "w:szCs":
                return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Cosmetic,
                    $"{element} nie przechodzi przez reader/writer (kerning, rozmiar dla pism złożonych) — dla tekstu łacińskiego różnica jest kosmetyczna.", null, "DocxToHtmlConverter.ConvertRunPropertiesToCss");
            case "w:rPr" when (difference.LeftPath ?? string.Empty).Contains("/w:pPr[", StringComparison.Ordinal):
                return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Layout,
                    "Formatowanie ZNAKU KOŃCA AKAPITU (w:pPr/w:rPr) nie wraca — decyduje o wysokości pustych akapitów i o formacie tekstu dopisanego na końcu akapitu w Wordzie; puste akapity mogą być niższe/wyższe niż w oryginale.", null, "HtmlToDocxConverter (ParagraphMarkRunProperties)");
            case "w:rPr":
                return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Cosmetic,
                    "Właściwości runu bez tekstu (np. run z obrazem: noProof, lang) nie wracają — reader nie opakowuje obrazu spanem z formatowaniem; bez wpływu na wygląd obrazu.", null, "DocxToHtmlConverter.ConvertRunToHtml (Drawing)");
            case "a:ln":
                return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Cosmetic,
                    "Definicja obrysu obrazu (a:ln) nie wraca — reader przenosi tylko WIDOCZNE obramowania (data-border-*); a:ln z noFill lub domyślne nie zmienia wyglądu.", FeatureKeys.InlineImages, "DocxToHtmlConverter.ConvertDrawingToHtml (a:ln)");
            case "a:extLst":
                return new DifferenceAnalysis(DifferenceCause.WordNoise, DifferenceImpact.None,
                    "Rozszerzenia DrawingML (a:extLst: useLocalDpi, identyfikatory) — Word dopisuje je automatycznie, bez wpływu na obraz.", null, null);
            case "w:cols":
                return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                    "Writer pomija w:cols bez podziału na kolumny (domyślny odstęp 708) — układ jednokolumnowy bez zmian.", FeatureKeys.Columns, null);
            case "w:compat":
            case "w:compatSetting":
                return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.PdfDifference,
                    "settings.xml bez w:compat/compatibilityMode=15: Word otworzy plik w TRYBIE ZGODNOŚCI (Word 2007–2010) — inne łamanie wierszy, odstępy i tabele niż w oryginale, a PDF z Worda/konwertera będzie się różnił. Writer powinien kopiować w:compat z oryginału.", null, "HtmlToDocxConverter (settings.xml — brak kopiowania w:compat)");
        }

        if (element == "w:bookmarkStart" && (difference.LeftExcerpt ?? string.Empty).Contains("_GoBack", StringComparison.Ordinal))
        {
            return new DifferenceAnalysis(DifferenceCause.WordNoise, DifferenceImpact.None, "Zakładka _GoBack to pozycja kursora Worda — szum.", null, null);
        }

        if (element is not null && FeatureByName.TryGetValue(element, out var feature))
        {
            return LostFeature(feature, element, inContent);
        }

        if (!inContent)
        {
            return OutsideContentLoss(difference.PartPath, element);
        }

        if (element is not null && BakedFormatting.Contains(element))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Layout,
                $"Właściwość formatowania {element} z oryginału nie wróciła po zapisie — reader nie przenosi jej do HTML albo writer jej nie odtwarza; porównaj wygląd akapitu w Wordzie i w edytorze.", null, "DocxToHtmlConverter / HtmlToDocxConverter");
        }

        if (element is "w:p" or "w:r" or "w:t" or "w:tbl" or "w:tr" or "w:tc" or "w:br" or "w:cr")
        {
            return new DifferenceAnalysis(DifferenceCause.UserEditOrLoss, DifferenceImpact.DataLoss,
                $"Fragment treści ({element}) z oryginału nie ma odpowiednika po zapisie. Dwie możliwości: użytkownik usunął go w edytorze ALBO reader go pominął (np. treść w nieobsługiwanej konstrukcji). Rozstrzygnięcie: zobacz „cały obiekt” i kontekst akapitu — jeśli tekst nie występuje nigdzie po prawej, a użytkownik go nie kasował, to strata konwersji.", null, "DocxToHtmlConverter");
        }

        return new DifferenceAnalysis(DifferenceCause.Unknown, DifferenceImpact.Layout,
            $"Element {element} z oryginału nie ma odpowiednika po zapisie i nie ma go w rejestrze możliwości edytora — dopisz do EditorCapabilityRegistry/DifferenceCauseAnalyzer po sprawdzeniu, co robi z nim reader.", null, "EditorCapabilityRegistry");
    }

    private static DifferenceAnalysis LostFeature(string feature, string element, bool inContent)
    {
        var capability = EditorCapabilityRegistry.Find(feature);

        if (capability is null)
        {
            return new DifferenceAnalysis(DifferenceCause.Unknown, DifferenceImpact.Layout, $"Brak wpisu rejestru dla {feature}.", feature, null);
        }

        return capability.Effective switch
        {
            AppSupportLevel.Unsupported => FromRegistry(feature, lost: true,
                capability.LossIsBenign ? DifferenceImpact.None : feature is FeatureKeys.HiddenText or FeatureKeys.PageNumberFormat ? DifferenceImpact.PdfDifference : DifferenceImpact.DataLoss),
            AppSupportLevel.PassThrough => new DifferenceAnalysis(DifferenceCause.UserEditOrLoss, DifferenceImpact.DataLoss,
                $"{capability.Label}: konstrukcja przechodzi pass-through 1:1, więc jej brak oznacza, że użytkownik usunął obiekt w edytorze ALBO pass-through nie zadziałał (relacja zewnętrzna, część > 4 MB, nieobsługiwane dziecko grupy — R-41). {capability.Note}", feature, capability.CodePointer),
            AppSupportLevel.Partial => new DifferenceAnalysis(DifferenceCause.PipelinePartial, LayoutOrPdf(feature),
                $"{capability.Label}: obsługa częściowa — {element} nie przechodzi przez reader/writer. {capability.Note}", feature, capability.CodePointer),
            AppSupportLevel.Unknown => new DifferenceAnalysis(DifferenceCause.Unknown, DifferenceImpact.Layout,
                $"{capability.Label}: obsługa niezweryfikowana — {capability.Note}", feature, capability.CodePointer),
            _ => inContent
                ? new DifferenceAnalysis(DifferenceCause.UserEditOrLoss, DifferenceImpact.DataLoss,
                    $"{capability.Label}: rejestr deklaruje pełną obsługę, więc brak {element} to albo usunięcie przez użytkownika, albo regresja readera/writera. Jeśli użytkownik tego nie kasował — zgłoś jako błąd (kod: {capability.CodePointer}).", feature, capability.CodePointer)
                : new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.Cosmetic,
                    $"{capability.Label}: definicja poza treścią generowana od nowa przez writer. {capability.Note}", feature, capability.CodePointer)
        };
    }

    private static DifferenceAnalysis OutsideContentLoss(string part, string? element)
    {
        if (Regex.IsMatch(part, @"numbering\.xml$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.Cosmetic,
                $"numbering.xml jest generowany od nowa (listy wariant A) — {element} z oryginalnej definicji listy nie wraca; numeracja zostaje, giną rozszerzenia Worda i nieużyte poziomy.", FeatureKeys.Lists, "HtmlToDocxConverter.CaptureSourceListAbstracts");
        }

        if (Regex.IsMatch(part, @"settings\.xml$", RegexOptions.IgnoreCase))
        {
            var (impact, detail) = element switch
            {
                "w:compat" or "w:compatSetting" => (DifferenceImpact.PdfDifference,
                    " Bez compatibilityMode=15 Word otwiera plik w TRYBIE ZGODNOŚCI (Word 2007–2010): inne łamanie wierszy, odstępy, tabele — PDF będzie się różnił."),
                "w:defaultTabStop" => (DifferenceImpact.Layout, " Domyślny tabulator wraca do 720 twips (oryginał 708) — przesunięcia tekstu po tabulatorach bez jawnych stopów."),
                "w:characterSpacingControl" or "w:docGrid" => (DifferenceImpact.Layout, " Wpływa na rozmieszczenie znaków/siatkę dokumentu."),
                "w:themeFontLang" => (DifferenceImpact.Cosmetic, " Język fontów motywu — słownik i dzielenie wyrazów."),
                "w:clrSchemeMapping" => (DifferenceImpact.Cosmetic, " Mapowanie kolorów motywu; domyślne mapowanie Worda jest identyczne, o ile oryginał go nie zmieniał."),
                _ => (DifferenceImpact.None, string.Empty)
            };

            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, impact,
                $"settings.xml jest odtwarzany wybiórczo (przypisy, ochrona, flaga zgodności odstępów) — {element} nie wraca.{detail}", null, "HtmlToDocxConverter (settings.xml)");
        }

        if (Regex.IsMatch(part, @"^\[Content_Types\]\.xml$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.None,
                $"{element} z typów zawartości nie wraca — writer deklaruje typy dla części, które sam zapisuje (brak Override = część nieodtworzona, patrz „Część tylko w oryginale”).", null, null);
        }

        if (Regex.IsMatch(part, @"styles\.xml$|theme|fontTable\.xml$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.Layout,
                $"{element} zniknął z części, która w pass-through jest kopiowana 1:1 — ten zapis poszedł BEZ pass-through (regeneracja stylów): formatowanie ze stylów będzie inne.", null, "HtmlToDocxConverter.PreserveOriginalParts");
        }

        if (Regex.IsMatch(part, @"\.rels$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.None,
                "Relacja z oryginału nie wraca — wskazywała część, której writer nie odtwarza (patrz „Część tylko w oryginale” dla tego celu).", null, null);
        }

        return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.None,
            $"{element} w części {part} nie wraca — część generowana od nowa przez writer.", null, null);
    }

    private static DifferenceAnalysis AddedElement(DocumentDifference difference, string? element, bool inContent, string part)
    {
        if (element is not null && NoiseElements.Contains(element))
        {
            return new DifferenceAnalysis(DifferenceCause.WordNoise, DifferenceImpact.None, $"{element} to znacznik pomocniczy bez wpływu na treść.", null, null);
        }

        // Lustro podziału runów: dodany run/tekst/podział wiersza przy identycznym tekście akapitu.
        if (element is "w:r" or "w:t" or "w:br" or "w:tab" && SameParagraphText(difference))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"Dodatkowy węzeł {element} przy identycznym tekście akapitu („{difference.RightContext}”) — writer dzieli run na osobne runy (per <br>/<span>); treść bez zmian.", null, "HtmlToDocxConverter (runy per <br>/<span>)");
        }

        // Lustro reguły start/end: writer zapisał w:left/w:right tam, gdzie oryginał miał w:start/w:end.
        if (element is "w:left" or "w:right" && ParentElementOf(difference.RightPath) is { } sideParent && BidiSideContainers.Contains(sideParent))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"{sideParent}/{element} zapisany przez writer w miejsce {(element == "w:left" ? "w:start" : "w:end")} z oryginału (zapis dwukierunkowy) — ta sama wartość dla tekstu od lewej.", null, null);
        }

        if (element is not null && BakedFormatting.Contains(element))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.Cosmetic,
                $"Writer dopisał {element}, którego Word nie zapisał — formatowanie ze stylu/obliczone zapieczone inline: wygląd ten sam, plik cięższy i mniej podatny na zmianę stylu.", null, "HtmlToDocxConverter.ApplyRunStyle / ApplyParagraphStyleExtras");
        }

        if (element is "w:p" && inContent)
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                "Dodatkowy akapit — writer domyka komórkę/sekcję pustym w:p (wymóg Worda) albo edytor rozdzielił blok; zwykle bez wpływu na wygląd.", null, null);
        }

        if (element is "w:rPr" or "w:pPr" && inContent)
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.Cosmetic,
                $"Writer dopisał {element} tam, gdzie Word trzymał formatowanie w stylu — kontener na zapieczone właściwości (sz/color/shd/spacing); wygląd ten sam.", null, "HtmlToDocxConverter.ApplyRunStyle / ApplyParagraphStyle");
        }

        if (element is not null && FeatureByName.TryGetValue(element, out var feature))
        {
            var capability = EditorCapabilityRegistry.Find(feature);

            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.Cosmetic,
                $"Writer dodał {element} ({capability?.Label ?? feature}) — jawny zapis tego, co oryginał trzymał domyślnie. {capability?.Note}", feature, capability?.CodePointer);
        }

        if (!inContent)
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.None,
                $"Dodatkowy {element} w części {part} — część generowana przez writer (style, ustawienia, relacje).", null, null);
        }

        return new DifferenceAnalysis(DifferenceCause.Unknown, DifferenceImpact.Cosmetic,
            $"Writer dodał element {element}, którego nie było w oryginale — sprawdź w wycinku, czy to celowa normalizacja, czy artefakt.", null, null);
    }

    // ── Atrybuty ──────────────────────────────────────────────────────────────

    private static DifferenceAnalysis LostAttribute(string? element, string name, bool inContent, string part)
    {
        if (NoiseAttribute.IsMatch(name))
        {
            return new DifferenceAnalysis(DifferenceCause.WordNoise, DifferenceImpact.None, $"Atrybut {name} to szum Worda (sesje edycji, identyfikatory) — jego brak niczego nie zmienia.", null, null);
        }

        if (ThemeFontAttribute.IsMatch(name))
        {
            return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Layout,
                $"Odwołanie do fontu MOTYWU ({name}) zastąpione literalną nazwą fontu (w:ascii/w:hAnsi): wygląd ten sam, dopóki motyw się nie zmieni; dla nagłówków (majorHAnsi = „Calibri Light”) writer wpisuje font rozwiązany przez reader — sprawdź, czy nie spłaszczył go do fontu treści.", null, "DocxToHtmlConverter.GetFontName / HtmlToDocxConverter.ApplyRunStyle (rFonts)");
        }

        if (element == "w:shd" && name == "w:color")
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                "w:shd bez w:color=\"auto\" — atrybut domyślny, Word traktuje brak jak auto.", null, null);
        }

        // Poza treścią (numbering/settings/styles) decyduje to, że writer generuje część od nowa — ważniejsze niż klucz konstrukcji.
        if (!inContent)
        {
            return OutsideContentLoss(part, $"{element}/@{name}");
        }

        if (FeatureByName.TryGetValue(name, out var attributeFeature))
        {
            var capability = EditorCapabilityRegistry.Find(attributeFeature);
            return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Cosmetic,
                $"Atrybut {name} ({capability?.Label ?? attributeFeature}) nie wraca po zapisie. {capability?.Note}", attributeFeature, capability?.CodePointer);
        }

        return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Layout,
            $"Atrybut {name} elementu {element} z oryginału nie wrócił — reader nie czyta tej właściwości albo writer jej nie zapisuje.", null, "DocxToHtmlConverter / HtmlToDocxConverter");
    }

    private static DifferenceAnalysis AddedAttribute(string? element, string name)
    {
        if (NoiseAttribute.IsMatch(name))
        {
            return new DifferenceAnalysis(DifferenceCause.WordNoise, DifferenceImpact.None, $"Atrybut {name} to szum bez wpływu na treść.", null, null);
        }

        return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.Cosmetic,
            $"Writer dodał atrybut {name} na {element} — jawny zapis wartości, którą Word trzymał domyślnie; wygląd bez zmian.", null, "HtmlToDocxConverter");
    }

    /// <summary>
    /// Ścieżka celu relacji sprowadzona do postaci bezwzględnej w pakiecie: dla części `word/_rels/document.xml.rels`
    /// cel `media/image1.png` i `/word/media/image1.png` to ta sama część. Cele zewnętrzne (http…) zostają bez zmian.
    /// </summary>
    private static string NormalizeRelationshipTarget(string target, string relsPart)
    {
        if (target.Length == 0 || target.Contains("://", StringComparison.Ordinal))
        {
            return target;
        }

        if (target.StartsWith('/'))
        {
            return target.TrimStart('/').ToLowerInvariant();
        }

        // word/_rels/document.xml.rels → katalog bazowy „word/”; _rels/.rels → katalog główny.
        var relsDirectory = relsPart.Replace('\\', '/');
        var relsIndex = relsDirectory.IndexOf("_rels/", StringComparison.OrdinalIgnoreCase);
        var baseDirectory = relsIndex <= 0 ? string.Empty : relsDirectory[..relsIndex];
        var segments = new List<string>(baseDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments).ToLowerInvariant();
    }

    private static DifferenceAnalysis ChangedAttribute(DocumentDifference difference, string? element, string name, string part, bool inContent)
    {
        var left = difference.LeftValue ?? string.Empty;
        var right = difference.RightValue ?? string.Empty;

        if (Regex.IsMatch(part, @"(^|/)_rels/", RegexOptions.IgnoreCase) && name == "Id")
        {
            var leftType = Regex.Match(difference.LeftExcerpt ?? string.Empty, "Type=\"([^\"]+)\"").Groups[1].Value;
            var rightType = Regex.Match(difference.RightExcerpt ?? string.Empty, "Type=\"([^\"]+)\"").Groups[1].Value;

            if (leftType.Length > 0 && rightType.Length > 0 && leftType != rightType)
            {
                return new DifferenceAnalysis(DifferenceCause.PairingArtifact, DifferenceImpact.None,
                    "Dwie RÓŻNE relacje sparowane pozycyjnie (inny Type) — to nie jest realna różnica; prawdziwa jest relacja obecna tylko po jednej stronie (osobny wiersz).", null, null);
            }

            return new DifferenceAnalysis(DifferenceCause.IdentifierRewrite, DifferenceImpact.None,
                "Identyfikator relacji jest dowolny: Word nadaje rId+n, Open XML SDK R+hex — odwołania w treści są przepisane spójnie.", null, null);
        }

        // Type/Target relacji: gdy Id-y się różnią (rId+n vs R+hex), relacje z luk parują się POZYCYJNIE i „zmiana Type”
        // albo „zmiana Target” to dwie różne relacje obok siebie, nie realna różnica (v1↔v2 2026-09-27: 10× Target
        // media/image4.png → /word/styles.xml lądowało w „do naprawy u nas”). Ta sama część pod ścieżką względną
        // i bezwzględną (media/x.png ↔ /word/media/x.png) to normalizacja zapisu bez skutku.
        if (Regex.IsMatch(part, @"(^|/)_rels/", RegexOptions.IgnoreCase) && name is "Type" or "Target")
        {
            var leftType = name == "Type" ? left : Regex.Match(difference.LeftExcerpt ?? string.Empty, "Type=\"([^\"]+)\"").Groups[1].Value;
            var rightType = name == "Type" ? right : Regex.Match(difference.RightExcerpt ?? string.Empty, "Type=\"([^\"]+)\"").Groups[1].Value;

            if (leftType.Length > 0 && rightType.Length > 0 && leftType != rightType)
            {
                return new DifferenceAnalysis(DifferenceCause.PairingArtifact, DifferenceImpact.None,
                    $"Dwie RÓŻNE relacje sparowane pozycyjnie (inny Type: {leftType.Split('/')[^1]} ↔ {rightType.Split('/')[^1]}) — to nie jest realna różnica; prawdziwa jest relacja obecna tylko po jednej stronie (osobny wiersz).", null, null);
            }

            if (name == "Target" && NormalizeRelationshipTarget(left, part) == NormalizeRelationshipTarget(right, part))
            {
                return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                    $"Ta sama część pod inną postacią ścieżki ({left} → {right}) — względna vs bezwzględna; Word i SDK czytają obie.", null, null);
            }
        }

        if (NoiseAttribute.IsMatch(name))
        {
            return new DifferenceAnalysis(DifferenceCause.WordNoise, DifferenceImpact.None, $"Atrybut {name} to szum Worda — wartość bez znaczenia.", null, null);
        }

        // Siatka tabeli: w:tblGrid to CACHE ostatniego układu Worda, nie źródło geometrii. Przy autofit Word układa kolumny
        // po preferowanych szerokościach komórek (tcW) i treści, a przy zapisie sam przepisuje gridCol (Word COM na
        // dokument_tabele_orginal.docx: grid 1800/3000/2400, tcW 3×3437 → po układzie 3×3437). Reader/writer robią to samo
        // (ADR-0108 r.8). Zmiana użytkownika (resize kolumny) zmienia też w:tcW — wtedy są osobne wiersze dla tcW.
        if (element == "w:gridCol" && name == "w:w")
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"Szerokość kolumny siatki (w:gridCol {left} → {right}) przepisana z preferowanych szerokości komórek — w:tblGrid to cache układu Worda, który Word i tak odświeża przy zapisie; układ tabeli ten sam. Jeśli w tej tabeli zmieniły się także w:tcW, to zmiana użytkownika (resize kolumny).", FeatureKeys.Tables, "DocxToHtmlConverter.ReadFirstRowPreferredColumnsPx (ADR-0108 r.8)");
        }

        // Ten sam boolean w innym zapisie (ST_OnOff: 0/false/off, 1/true/on) — SDK serializuje „false”, Word „0”.
        if (IsOnOff(left) && IsOnOff(right) && OnOff(left) == OnOff(right))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"Ta sama wartość logiczna zapisana inaczej ({left} → {right}) — ST_OnOff dopuszcza obie formy.", null, null);
        }

        if (RelationshipOrGeneratedIdAttribute.IsMatch(name))
        {
            return new DifferenceAnalysis(DifferenceCause.IdentifierRewrite, DifferenceImpact.None,
                $"Odwołanie do relacji ({name}) przepisane spójnie — writer nadaje własne identyfikatory relacji (R+hex), część docelowa jest ta sama.", null, null);
        }

        if (element is "wp:docPr" or "pic:cNvPr" or "wps:cNvPr" && name is "name" or "id" or "descr" && string.IsNullOrEmpty(left) == string.IsNullOrEmpty(right))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                $"Nazwa/identyfikator obiektu graficznego ({element}/@{name}: {left} → {right}) generowane przez writer — Word też nadaje je automatycznie.", null, null);
        }

        if (name == "w:id" && element is "w:bookmarkStart" or "w:bookmarkEnd" or "w:footnoteReference" or "w:endnoteReference" or "w:footnote" or "w:endnote" or "w:comment" or "w:commentReference")
        {
            return new DifferenceAnalysis(DifferenceCause.IdentifierRewrite, DifferenceImpact.None,
                "Identyfikator zakładki/przypisu nadawany od nowa przy eksporcie (1..N) — pary start/end i odwołania pozostają spójne.", null, null);
        }

        if (string.Equals(left, "auto", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(right, "^(000000|FFFFFF)$", RegexOptions.IgnoreCase))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                "Kolor „auto” zapisany jako jawny (ADR-0077) — Word rozwiązuje auto identycznie.", null, null);
        }

        if (element == "w:pgMar" && name is "w:header" or "w:footer")
        {
            return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Layout,
                $"Odległość {(name == "w:header" ? "nagłówka" : "stopki")} od krawędzi ({left} → {right} twips): writer wpisuje własną wartość zamiast oryginalnej z w:pgMar — przy istniejącym nagłówku/stopce zmienia się ich pozycja i wysokość obszaru treści.", null, "HtmlToDocxConverter.AddPageSettings (pgMar header/footer)");
        }

        if (UnitAttribute.IsMatch(name) &&
            double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNumber) &&
            double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber))
        {
            var delta = Math.Abs(leftNumber - rightNumber);

            if (delta <= UnitTolerance)
            {
                return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                    delta == 0 ? "Ta sama wartość w innym zapisie liczbowym." : $"Różnica {delta:0.##} jednostek to zaokrąglenie px↔twips/EMU po round-tripie (R-13) — niewidoczna.", null, null);
            }

            if (delta <= PixelRoundingTwips && element is "w:ind" or "w:spacing" or "w:pgMar" or "w:tblInd" or "w:tcW" or "w:tblW" or "w:gridCol" or "w:tab" or "w:trHeight")
            {
                return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.Cosmetic,
                    $"Dryf {delta:0.##} twips ({left} → {right}) = zaokrąglenie do pełnego piksela (1 px = 15 twips) w drodze twips→px→twips; poniżej 0,3 mm, ale systematyczny — writer powinien nieść oryginalne twipsy w data-*.", null, "OoxmlUnits / HtmlToDocxConverter (ind/spacing z px)");
            }

            return new DifferenceAnalysis(DifferenceCause.UserEditOrLoss, DifferenceImpact.Layout,
                $"Wartość {name} zmieniła się o {delta:0.##} jednostek ({left} → {right}) — więcej niż zaokrąglenie: użytkownik zmienił formatowanie ALBO przeliczenie jednostek w readerze/writerze jest błędne (sprawdź, czy ta sama zmiana powtarza się we wszystkich akapitach — wtedy to my).", null, "OoxmlUnits / HtmlToDocxConverter");
        }

        if (name == "w:val" && element is "w:pStyle" or "w:rStyle" or "w:tblStyle")
        {
            return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Layout,
                $"Odwołanie do stylu zmieniło się ({left} → {right}): writer wpisał identyfikator ze swojej listy domyślnej (np. Heading1) zamiast oryginalnego (np. Nagwek1 z polskiego Worda). Jeśli „{right}” nie istnieje w zachowanym styles.xml, Word użyje stylu Normalny — nagłówek traci poziom konspektu (nawigacja, spis treści), a writer nadrabia to zapieczonym formatowaniem inline.", null, "HtmlToDocxConverter (w:pStyle z data-style-id) / DocxToHtmlConverter (mapowanie nagłówków na h1–h6)");
        }

        if (Regex.IsMatch(part, @"^\[Content_Types\]\.xml$", RegexOptions.IgnoreCase) && name == "ContentType" &&
            right.Contains("wordprocessingml.document.main+xml", StringComparison.OrdinalIgnoreCase) && element == "Default")
        {
            return new DifferenceAnalysis(DifferenceCause.PipelinePartial, DifferenceImpact.Cosmetic,
                "Writer deklaruje <Default Extension=\"xml\"> z content type GŁÓWNEJ CZĘŚCI dokumentu zamiast application/xml + Override dla word/document.xml — każda inna część .xml bez Override udaje dokument główny. Word to toleruje, rygorystyczne narzędzia nie; do poprawy w writerze.", null, "HtmlToDocxConverter ([Content_Types] — Default/Override)");
        }

        if (!inContent)
        {
            return new DifferenceAnalysis(DifferenceCause.PipelineRegenerated, DifferenceImpact.Cosmetic,
                $"Inna wartość {name} w części {part} ({left} → {right}) — część generowana od nowa; sprawdź, czy dotyczy używanego stylu/ustawienia.", null, null);
        }

        return new DifferenceAnalysis(DifferenceCause.UserEditOrLoss, DifferenceImpact.Layout,
            $"Inna wartość {name} elementu {element} ({left} → {right}): celowa zmiana formatowania przez użytkownika ALBO reader/writer przekształca tę właściwość. Powtarzalność w wielu miejscach = nasz pipeline; pojedyncze wystąpienie = raczej edycja.", null, null);
    }

    // ── Tekst ─────────────────────────────────────────────────────────────────

    private static DifferenceAnalysis ChangedText(DocumentDifference difference)
    {
        var left = difference.LeftValue ?? string.Empty;
        var right = difference.RightValue ?? string.Empty;

        if (Regex.Replace(left, @"\s+", string.Empty) == Regex.Replace(right, @"\s+", string.Empty))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.Cosmetic,
                "Różnica tylko w białych znakach (spacje/NBSP/tabulatory) — normalizacja HTML↔XML; w Wordzie może zmienić łamanie wiersza.", null, "HtmlToDocxConverter (white-space)");
        }

        if (left.Trim().Length == 0 || right.Trim().Length == 0)
        {
            return new DifferenceAnalysis(DifferenceCause.UserEditOrLoss, DifferenceImpact.DataLoss,
                "Tekst istnieje tylko po jednej stronie — użytkownik go usunął/dopisał ALBO run zginął w konwersji; sprawdź kontekst akapitu.", null, null);
        }

        if (right.Contains(left, StringComparison.Ordinal) || left.Contains(right, StringComparison.Ordinal))
        {
            return new DifferenceAnalysis(DifferenceCause.WriterNormalization, DifferenceImpact.None,
                "Ten sam tekst w innym podziale na runy (Word dzieli runy po sesjach edycji, edytor scala) — treść identyczna, patrz kontekst akapitu.", null, null);
        }

        return new DifferenceAnalysis(DifferenceCause.UserEdit, DifferenceImpact.None,
            "Treść tekstu się różni — najpewniej edycja użytkownika w edytorze. Jeśli użytkownik NIE edytował tego akapitu, to błąd konwersji (np. utracone znaki specjalne, symbole, pola).", null, null);
    }

    // ── Pomocnicze ────────────────────────────────────────────────────────────

    private static DifferenceAnalysis FromRegistry(string feature, bool lost, DifferenceImpact impact)
    {
        var capability = EditorCapabilityRegistry.Find(feature);

        if (capability is null)
        {
            return new DifferenceAnalysis(DifferenceCause.Unknown, impact, $"Brak wpisu rejestru dla {feature}.", feature, null);
        }

        var cause = capability.Effective switch
        {
            AppSupportLevel.Unsupported => DifferenceCause.PipelineUnsupported,
            AppSupportLevel.Partial or AppSupportLevel.PassThrough => DifferenceCause.PipelinePartial,
            _ => DifferenceCause.Unknown
        };

        var prefix = lost
            ? $"{capability.Label}: NASZ pipeline tego nie obsługuje — konstrukcja znika po zapisie niezależnie od tego, co robił użytkownik."
            : $"{capability.Label}.";

        return new DifferenceAnalysis(cause, impact, $"{prefix} {capability.Note}", feature, capability.CodePointer);
    }

    private static DifferenceImpact LayoutOrPdf(string feature) => feature switch
    {
        FeatureKeys.TrackedChanges or FeatureKeys.ContentControls or FeatureKeys.CustomXmlMarkup or FeatureKeys.Bookmarks => DifferenceImpact.Cosmetic,
        FeatureKeys.ComplexFields or FeatureKeys.SimpleFields or FeatureKeys.TableOfContents => DifferenceImpact.PdfDifference,
        _ => DifferenceImpact.Layout
    };

    private static bool IsOnOff(string value) => value is "0" or "1" or "true" or "false" or "on" or "off";

    private static bool OnOff(string value) => value is "1" or "true" or "on";

    private static string? ElementNameOf(DocumentDifference difference)
    {
        var path = difference.RightPath ?? difference.LeftPath;

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var segment = path[(path.LastIndexOf('/') + 1)..];
        var bracket = segment.IndexOf('[');

        return bracket < 0 ? segment : segment[..bracket];
    }
}
