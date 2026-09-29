using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>
/// Jak NASZA aplikacja radzi sobie z konkretnym problemem dokumentu: czy go naprawia (jak Word),
/// obchodzi, ignoruje, czy pogłębia. Mapa kod ustalenia → (poziom, notatka) — uzupełnienie
/// ogólnego opisu reguły o zachowanie readera/edytora/writera zweryfikowane w kodzie. Kod bez
/// wpisu zostaje <see cref="AppSupportLevel.Unknown"/> bez notatki (GUI nic nie pokazuje).
/// </summary>
public static class FindingAppHandling
{
    private const AppSupportLevel Full = AppSupportLevel.Full;
    private const AppSupportLevel Partial = AppSupportLevel.Partial;
    private const AppSupportLevel PassThrough = AppSupportLevel.PassThrough;
    private const AppSupportLevel Unsupported = AppSupportLevel.Unsupported;
    private const AppSupportLevel Unknown = AppSupportLevel.Unknown;

    private const string SdkImportNote =
        "Reader edytora stoi na Open XML SDK i nie naprawia pakietów jak Word — import zakończy się wyjątkiem (patrz próba „Edytor: import DOCX → HTML”); plik trzeba naprawić przed wgraniem.";

    private static readonly IReadOnlyDictionary<string, (AppSupportLevel Level, string Note)> Map =
        new Dictionary<string, (AppSupportLevel, string)>(StringComparer.Ordinal)
        {
            // Plik / kontener
            [DocumentHealthCodes.FileLegacyBinaryDoc] = (Partial,
                "Własny konwerter binarnego .doc (LegacyDocBinaryConverter, ADR-0074): tekst, akapity i formatowanie znaków z CHPX; brak tabel złożonych, grafik i pól — zapis zawsze jako .docx."),
            [DocumentHealthCodes.FileEncryptedPackage] = (Partial,
                "DocumentInputNormalizer rozpoznaje pakiet zaszyfrowany (Agile) i deszyfruje go własnym OoxmlAgileDecryptor TYLKO gdy dostanie hasło; bez hasła import zgłasza błąd."),
            [DocumentHealthCodes.FileNotOoxmlPackage] = (Unsupported,
                "Aplikacja przyjmuje DOCX (i binarny .doc); HTML/RTF/ODT są odrzucane przez bramkę uploadu — konwersji z tych formatów nie ma."),
            [DocumentHealthCodes.FileZipUnreadable] = (Unsupported, SdkImportNote),
            [DocumentHealthCodes.ZipEntryCrcMismatch] = (Unsupported, SdkImportNote),

            // Pakiet OPC
            [DocumentHealthCodes.MacrosPresent] = (Unsupported,
                "Bramka uploadu odrzuca projekt VBA (UploadSecurity:RejectDocxMacros) — plik nie wejdzie do edytora; to polityka bezpieczeństwa, nie luka."),
            [DocumentHealthCodes.MacroEnabledDocument] = (Unsupported,
                "DOCM/DOTM: bramka odrzuca makra, a writer i tak zapisuje pakiet jako zwykły .docx."),
            [DocumentHealthCodes.TemplateContentType] = (Unknown,
                "Writer generuje główną część z content type dokumentu (.docx) — szablon .dotx po zapisie z edytora prawdopodobnie przestaje być szablonem; nie zweryfikowano."),
            [DocumentHealthCodes.SignedPackage] = (Unsupported,
                "Podpis XML-DSig nie przeżyje zapisu; aplikacja ma własny format podpisu (DigitalSignatureService) i nie weryfikuje podpisów Worda."),
            [DocumentHealthCodes.OptionalPartMissing] = (Full,
                "Reader używa DefaultWordStyles i docDefaults z fallbackiem; brak styles.xml oznacza, że pass-through nie ma czego kopiować i writer generuje style od zera."),
            [StructureIssueCodes.RelationshipTargetMissing] = (Partial,
                "Reader pomija obraz/część bez celu relationshipu (log ostrzeżenia) — element znika z edytora i z v2, import się nie wywraca."),

            // XML
            [DocumentHealthCodes.XmlNotWellFormed] = (Unsupported, SdkImportNote),
            [DocumentHealthCodes.XmlEmpty] = (Unsupported, SdkImportNote),
            [DocumentHealthCodes.XmlDtdPresent] = (Unsupported, SdkImportNote),
            [DocumentHealthCodes.XmlRootUnexpected] = (Unsupported, SdkImportNote),
            [DocumentHealthCodes.XmlIgnorablePrefixUndeclared] = (Unsupported, SdkImportNote),

            // Struktura
            [DocumentHealthCodes.BodyEmpty] = (Full,
                "Import kończy się stałym kodem DOCUMENT_CONTENT_EMPTY (backend) z jednym źródłem komunikatu w GUI — świadome odrzucenie, nie awaria."),
            [DocumentHealthCodes.SectionPropertiesMissing] = (Full,
                "Reader przyjmuje A4 i domyślne marginesy (fallback ExtractPageSize/ExtractPageMargins); writer zapisze jawny sectPr."),
            [DocumentHealthCodes.PageSizeOutOfRange] = (Unknown,
                "Reader przelicza w:pgSz bez walidacji zakresu Worda — edytor spróbuje wyrenderować stronę o tym rozmiarze; nie sprawdzono, co robi paginacja przy 22+ calach."),
            [DocumentHealthCodes.PageMarginsExceedPage] = (Unknown,
                "Marginesy większe niż strona dają ujemną szerokość treści w edytorze — paginacja może się zapętlić lub dać puste strony; nie zweryfikowano."),
            [DocumentHealthCodes.TableGridMissing] = (Partial,
                "Reader buduje colgroup z w:tblGrid (ADR-0077); bez siatki bierze szerokości z tcW/auto — układ kolumn może odbiegać od Worda, writer dopisze tblGrid z colgroup przy zapisie."),
            [DocumentHealthCodes.TableCellWithoutParagraph] = (Partial,
                "Reader nie wymaga w:p na końcu komórki (renderuje, co jest); writer ZAWSZE domyka komórkę akapitem — zapis z edytora naprawia ten błąd."),
            [DocumentHealthCodes.TableWithoutRows] = (Partial,
                "Reader pomija tabelę bez wierszy; po zapisie z edytora tabeli nie ma (jak po naprawie w Wordzie)."),
            [DocumentHealthCodes.TableRowWithoutCells] = (Partial,
                "Reader renderuje pusty <tr>; writer zapisuje wiersz bez komórek tylko, jeśli edytor go nie usunie — do sprawdzenia w próbie round-trip."),
            [DocumentHealthCodes.TablesAdjacent] = (Unknown,
                "Edytor pokazuje dwie osobne tabele; writer może zapisać je znów bez akapitu pomiędzy (Word je sklei) — zweryfikować round-tripem."),
            [DocumentHealthCodes.TableNestingDeep] = (Partial,
                "Reader i writer obsługują dowolną głębokość; paginacja edytora tnie wiersze per komórka (ADR-0052) — koszt rośnie z głębokością, cantSplit/exact są atomowe."),
            [DocumentHealthCodes.ParagraphNested] = (Unknown,
                "SDK wczyta zagnieżdżony w:p jako nieznany element wewnątrz akapitu — reader może pominąć jego tekst; sprawdź próbę importu."),
            [DocumentHealthCodes.RunOutsideParagraph] = (Unknown,
                "Reader iteruje runy wewnątrz akapitów — run poza w:p prawdopodobnie zostanie pominięty (utrata tekstu); sprawdź próbę importu."),
            [DocumentHealthCodes.TextOutsideRun] = (Unknown,
                "Tekst poza w:r nie przejdzie przez ConvertRunToHtml — prawdopodobna cicha utrata; sprawdź próbę importu."),
            [DocumentHealthCodes.FieldUnbalanced] = (Partial,
                "Maszyna pól (ADR-0066) zakłada sekwencję begin→separate→end; przy niedomkniętym polu reszta akapitu może zostać uznana za kod pola i zniknąć z podglądu (jak w Wordzie)."),
            [DocumentHealthCodes.BookmarkUnclosed] = (Full,
                "Zakładki jadą jako markery docx-bookmark; osierocony start bez końca nie szkodzi — writer odtwarza parę z unikalnym w:id."),
            [DocumentHealthCodes.CommentRangeUnbalanced] = (Unsupported,
                "Komentarze nie są obsługiwane (reader nie czyta comments.xml) — zakresy i tak giną w v2."),
            [DocumentHealthCodes.DrawingExtentMissing] = (Partial,
                "Reader czyta wp:extent do data-*-emu; bez wymiarów obraz dostaje rozmiar 0×0 albo rozmiar naturalny bitmapy — inaczej niż Word."),
            [DocumentHealthCodes.DrawingExtentInvalid] = (Partial,
                "Wymiar 0/ujemny → obraz niewidoczny w edytorze (0×0), a writer zapisze to, co ma w data-*-emu."),
            [DocumentHealthCodes.ImageRelationshipMissing] = (Partial,
                "Reader pomija obraz bez relationshipu (log) — w edytorze i w v2 obrazu nie ma, import się nie wywraca."),
            [DocumentHealthCodes.ImageBlipWithoutSource] = (Partial,
                "Jak wyżej: obraz bez r:embed jest pomijany przy imporcie."),
            [DocumentHealthCodes.ImagePartEmpty] = (Partial,
                "Sniff MIME po bajtach nie rozpozna pustej części — obraz pomijany, brak w edytorze i w v2."),
            [DocumentHealthCodes.ImageUnrecognized] = (Partial,
                "Reader rozpoznaje format po sygnaturze bajtów (ADR-0108 r.11); nierozpoznane bajty = obraz pominięty z logiem."),
            [DocumentHealthCodes.ImageContentTypeMismatch] = (Full,
                "Reader ignoruje zadeklarowany content type i dekoduje po bajtach (ADR-0108 r.11) — u nas obraz się wyświetli, zapis dostaje poprawny typ."),
            [DocumentHealthCodes.ImageLarge] = (Partial,
                "Obrazy jadą jako base64 w HTML dokumentu (DocumentImage) — brak downscalingu; bardzo duży obraz spowalnia edytor, autosave i pomiary paginacji."),
            [DocumentHealthCodes.ImagesTotalLarge] = (Partial,
                "Łączny rozmiar obrazów przekłada się 1:1 (×1,33 base64) na rozmiar HTML w edytorze i w każdym autosave."),
            [DocumentHealthCodes.ImageLinkedExternal] = (Unsupported,
                "Reader obsługuje tylko r:embed — obraz linkowany (r:link) jest pomijany i ginie w v2."),
            [DocumentHealthCodes.HyperlinkRelationshipMissing] = (Partial,
                "Reader renderuje tekst hiperłącza bez adresu (brak href) — treść zostaje, link ginie."),
            [DocumentHealthCodes.AltChunkPresent] = (Unsupported,
                "Reader nie obsługuje w:altChunk (0 odwołań w DocxToHtmlConverter) — dołączona treść nie pojawia się w edytorze i ginie w v2. To luka naszej implementacji, nie błąd pliku."),
            [DocumentHealthCodes.AltChunkRelationshipMissing] = (Unsupported,
                "Niezależnie od relationshipu: w:altChunk nie jest importowany."),
            [DocumentHealthCodes.EmbeddedObjectPresent] = (PassThrough,
                "OLE = podgląd z v:imagedata + oryginalny XML w pass-through (ADR-0056); obiekt > 4 MB idzie bez pass-through i zapisuje się jako zwykły obraz (R-41d)."),
            [DocumentHealthCodes.EmbeddedObjectRelationshipMissing] = (Partial,
                "Bez relationshipu pass-through nie ma czego zachować — reader zostawi sam podgląd albo pominie obiekt."),
            [DocumentHealthCodes.LegacyVmlPresent] = (PassThrough,
                "VML jedzie pass-through (w:pict w data-docx-xml) z podglądem; nieedytowalne w GUI; gałąź mc:Fallback ginie, gdy renderuje się mc:Choice (R-41e)."),
            [DocumentHealthCodes.AlternateContentWithoutFallback] = (Partial,
                "Reader renderuje mc:Choice, gdy umie (w:drawing); bez Fallback nic dodatkowego nie traci — przy zapisie i tak wraca sam w:drawing."),
            [DocumentHealthCodes.NoteReferenceTargetMissing] = (Partial,
                "Model boczny przypisów (ADR-0032) mapuje odwołania po w:id — odwołanie bez celu daje pusty przypis albo jest pomijane; komentarze nieobsługiwane w ogóle."),
            [DocumentHealthCodes.NoteIdDuplicate] = (Partial,
                "Przy zduplikowanym w:id reader weźmie pierwszy wpis; drugi ginie w v2 (na eksporcie identyfikatory są nadawane od nowa 1..N)."),
            [DocumentHealthCodes.NumberingInstanceMissing] = (Partial,
                "Listy wariant A: numPr bez definicji → akapit bez znacznika listy (jak Word); numbering.xml i tak jest regenerowany przy zapisie."),
            [DocumentHealthCodes.AbstractNumberingMissing] = (Partial,
                "Jak wyżej — brak abstractNum = akapit bez numeracji w edytorze i w v2."),
            [DocumentHealthCodes.StylesPartMissing] = (Full,
                "Reader używa DefaultWordStyles; brak części stylów nie blokuje importu."),
            [DocumentHealthCodes.StyleMissing] = (Full,
                "Odwołanie do nieznanego stylu → styl domyślny (jak Word); writer zachowuje w:pStyle z data-style-id."),
            [DocumentHealthCodes.ContentControlWithoutContent] = (Unknown,
                "Reader iteruje w:sdtContent — formant bez treści prawdopodobnie znika bez śladu; sprawdź próbę importu."),
            [DocumentHealthCodes.TrackedChangesPresent] = (Partial,
                "Reader AKCEPTUJE rewizje przy imporcie (ADR-0088): treść końcowa zostaje, historia zmian/autorzy/daty giną; edytor nie ma trybu recenzji."),
            [DocumentHealthCodes.CommentsPresent] = (Unsupported,
                "Komentarze nie są czytane (0 odwołań do CommentReference) ani kopiowane w pass-through — giną w v2 po pierwszym autosave (KR-03)."),
            [DocumentHealthCodes.MailMergeSettings] = (Unsupported,
                "Przy pass-through (ADR-0120) settings.xml oryginału wraca w całości, ale w:mailMerge ze źródłem danych (relacja r:id) jest usuwany — brak części źródła w nowym pakiecie; MERGEFIELD zostają jako tekst."),
            [DocumentHealthCodes.UpdateFieldsOnOpen] = (Full,
                "Od ADR-0120 settings.xml oryginału jest kopiowany przy pass-through — w:updateFields WRACA i Word zapyta o aktualizację pól; przy regeneracji (bez masterId) flaga ginie."),
            [DocumentHealthCodes.AttachedTemplate] = (Full,
                "Dołączony szablon jest ignorowany przez reader; przy pass-through element jest usuwany (relacja do nieistniejącej części) — bez wpływu na treść."),
            [DocumentHealthCodes.DocumentProtection] = (Full,
                "Wymuszona ochrona → DocumentContent.IsReadOnlyProtected → edytor tylko do odczytu; ustawienie zachowane przy eksporcie (PreserveEditProtection)."),
            [DocumentHealthCodes.EmbeddedFontMissing] = (Unsupported,
                "Edytor nie używa osadzonych fontów; pass-through kopiuje fontTable.xml, ale nie części fontów — patrz pokrycie „Fonty osadzone”."),
            [DocumentHealthCodes.EmbeddedFontObfuscated] = (Unsupported,
                "Fonty ODTTF nie są dekodowane ani używane do renderowania — przeglądarka bierze font systemowy/generyczny (inne łamanie wierszy niż Word)."),
            [DocumentHealthCodes.DocumentVeryLarge] = (Partial,
                "Paginacja edytora biegnie w przeglądarce po każdym keystroke (gorąca pętla, ADR-0085/0108) — bardzo duże dokumenty spowalniają edycję i autosave."),

            // Próby
            [DocumentHealthCodes.SchemaErrors] = (Partial,
                "Reader nie waliduje schematu — toleruje niezgodności tak jak Word. Jeśli plik pochodzi z NASZEGO edytora (zapis v2), błędy generuje HtmlToDocxConverter — patrz ustalenie APP_ROUND_TRIP_SCHEMA_ERRORS z próbami; strażnik writera: GeneratedPackageValidityTests."),
            [DocumentHealthCodes.SdkOpenFailed] = (Unsupported,
                "Reader edytora otwiera pakiet TYM SAMYM WordprocessingDocument.Open — import zakończy się identycznym wyjątkiem."),
            [DocumentHealthCodes.UploadGateRejected] = (Unsupported,
                "To nasza bramka: plik nie wejdzie do aplikacji, dopóki przyczyna odrzucenia nie zniknie."),
            [DocumentHealthCodes.EditorImportFailed] = (Unsupported,
                "Wyjątek pochodzi z naszego readera (DocxToHtmlConverter) — komunikat i stos w szczegółach próby wskazują konstrukcję do obsłużenia."),
            [DocumentHealthCodes.PdfOutputInvalid] = (Partial,
                "Podgląd PDF (pdf.js) nie wyrenderuje takiego pliku; usługa konwersji jest za interfejsem IDocxToPdfConversionService (dziś atrapa, ADR-0111).")
        };

    public static HealthFinding Annotate(HealthFinding finding)
    {
        if (finding.AppSupport != AppSupportLevel.Unknown || finding.AppNote is not null)
        {
            return finding;
        }

        return Map.TryGetValue(finding.Code, out var handling)
            ? finding with { AppSupport = handling.Level, AppNote = handling.Note }
            : finding;
    }
}
