namespace D2ViewerEditor.Domain.Models;

/// <summary>
/// Etap diagnostyki „Kondycja dokumentu". Etapy są UPORZĄDKOWANE od najbardziej zewnętrznej
/// warstwy: to, co pada wcześniej, unieważnia wnioski z etapów późniejszych (uszkodzony ZIP
/// = nie ma sensu oceniać struktury tabel).
/// </summary>
public enum HealthStage
{
    /// <summary>Plik jako kontener: podpis binarny, archiwum ZIP, wpisy, sumy kontrolne.</summary>
    File = 0,

    /// <summary>Pakiet OPC: typy zawartości, relationshipy, główna część dokumentu.</summary>
    Package = 1,

    /// <summary>Części XML: poprawność składniowa, korzenie, prefiksy mc:Ignorable.</summary>
    Xml = 2,

    /// <summary>Struktura WordprocessingML: reguły, których Word wymaga, żeby otworzyć dokument bez naprawy.</summary>
    Structure = 3,

    /// <summary>Próby przetworzenia dokumentu przez realne komponenty (SDK, edytor, konwerter PDF).</summary>
    Conversion = 4,

    /// <summary>
    /// Nasza implementacja: co pipeline tej aplikacji (reader DOCX→HTML, edytor, writer HTML→DOCX)
    /// z tym dokumentem robi źle, częściowo albo wcale — luki, nie uszkodzenia pliku.
    /// </summary>
    Application = 5
}

/// <summary>Poziom obsługi konstrukcji DOCX przez jeden z komponentów tej aplikacji.</summary>
public enum AppSupportLevel
{
    /// <summary>Nie wiemy — brak weryfikacji; wymaga sprawdzenia w kodzie lub na dokumencie.</summary>
    Unknown = 0,

    /// <summary>Pełna obsługa: import, edycja/prezentacja i zapis zachowują konstrukcję.</summary>
    Full = 1,

    /// <summary>Obsługa z udokumentowanymi ograniczeniami (część właściwości ginie lub jest przybliżana).</summary>
    Partial = 2,

    /// <summary>Konstrukcja przeżywa zapis 1:1 (pass-through XML), ale nie jest edytowalna; podgląd może być uproszczony.</summary>
    PassThrough = 3,

    /// <summary>Brak obsługi: konstrukcja jest pomijana przy imporcie i ginie w wersji roboczej po pierwszym zapisie.</summary>
    Unsupported = 4
}

/// <summary>Wynik porównania liczby wystąpień konstrukcji przed i po round-tripie DOCX→HTML→DOCX.</summary>
public enum RoundTripOutcome
{
    /// <summary>Round-trip nie został uruchomiony albo się nie powiódł.</summary>
    NotVerified = 0,
    Preserved = 1,
    Reduced = 2,
    Lost = 3
}

/// <summary>Werdykt pokrycia jednej konstrukcji przez naszą implementację.</summary>
public enum CoverageStatus
{
    Supported = 0,
    Partial = 1,
    PassThrough = 2,
    Unsupported = 3,

    /// <summary>Rejestr deklaruje obsługę, a round-trip konstrukcję zgubił — sygnał regresji lub luki w rejestrze.</summary>
    UnexpectedLoss = 4,

    /// <summary>Konstrukcja występuje w dokumencie, a nasza obsługa nie została zweryfikowana.</summary>
    Unverified = 5
}

/// <summary>
/// Jedna konstrukcja DOCX obecna w dokumencie zestawiona z tym, co robi z nią nasz pipeline:
/// zadeklarowany poziom obsługi per komponent (rejestr zweryfikowany w kodzie), zmierzony wynik
/// round-tripu i wskazówka, gdzie w kodzie leży obsługa albo jej brak.
/// </summary>
public sealed record ImplementationCoverageItem(
    string FeatureKey,
    string Label,
    int SourceCount,
    string? SampleLocation,
    AppSupportLevel Reader,
    AppSupportLevel Editor,
    AppSupportLevel Writer,
    int? RoundTripCount,
    RoundTripOutcome RoundTrip,
    CoverageStatus Status,
    string Note,
    string? CodePointer);

/// <summary>Wpływ problemu na otwarcie pliku w MS Word.</summary>
public enum WordOpenImpact
{
    None = 0,

    /// <summary>Word otworzy plik dopiero po komunikacie „znaleziono nieczytelną zawartość" i naprawie.</summary>
    Repair = 1,

    /// <summary>Word nie otworzy pliku (uszkodzone archiwum, niepoprawny XML, brak głównej części).</summary>
    CannotOpen = 2
}

/// <summary>Wpływ problemu na konwersję DOCX → PDF przez zewnętrzną usługę.</summary>
public enum PdfConversionImpact
{
    None = 0,

    /// <summary>Konwertery zwykle sobie radzą, ale wynik może różnić się od Worda albo pominąć treść.</summary>
    Possible = 1,

    /// <summary>Znana przyczyna błędów konwerterów innych niż Word (LibreOffice, biblioteki OOXML).</summary>
    Likely = 2,

    /// <summary>Żaden konwerter nie przetworzy tego pliku bez wcześniejszej naprawy.</summary>
    Blocking = 3
}

/// <summary>
/// Pojedyncze ustalenie diagnostyki. <c>Code</c> jest stabilnym identyfikatorem reguły (GUI i logi
/// rozpoznają przypadek po kodzie), <c>Location</c> wskazuje część pakietu i miejsce w XML
/// (ścieżka + linia) tak, żeby dało się je odnaleźć w „Walidatorze struktury".
/// <c>AppSupport</c>/<c>AppNote</c> mówią, jak z tym konkretnym problemem radzi sobie NASZA
/// aplikacja (reader/edytor/writer) — czy go naprawia, obchodzi, czy pogłębia.
/// </summary>
public sealed record HealthFinding(
    string Code,
    StructureIssueSeverity Severity,
    HealthStage Stage,
    string Title,
    string Description,
    string? Location,
    WordOpenImpact WordImpact,
    PdfConversionImpact PdfImpact,
    string? Remedy,
    AppSupportLevel AppSupport = AppSupportLevel.Unknown,
    string? AppNote = null);

/// <summary>Status jednej próby przetworzenia dokumentu.</summary>
public enum ProbeStatus
{
    Passed = 0,
    Warning = 1,
    Failed = 2,
    Skipped = 3
}

/// <summary>
/// Wynik próby przetworzenia dokumentu przez realny komponent. Treść <c>Details</c> to surowy
/// materiał dowodowy (komunikat wyjątku, stderr konwertera) — celowo nieinterpretowany.
/// </summary>
public sealed record ConversionProbeResult(
    string Id,
    string Name,
    string Description,
    ProbeStatus Status,
    long DurationMs,
    string? Message,
    string? Details);

/// <summary>Liczniki opisujące dokument — kontekst dla ustaleń (np. „12 pól, 3 z nich niedomknięte").</summary>
public sealed record DocumentHealthStatistics(
    int PackageEntries,
    int XmlParts,
    int ImageParts,
    long ImageBytes,
    int Elements,
    int Paragraphs,
    int Tables,
    int MaxTableNesting,
    int Drawings,
    int Fields,
    int Sections,
    int Footnotes,
    int Endnotes,
    int Comments,
    int TrackedRevisions,
    int ContentControls,
    int AltChunks,
    int EmbeddedFonts);

/// <summary>Werdykt ogólny — od najlepszego do najgorszego.</summary>
public enum HealthVerdict
{
    Healthy = 0,
    Warnings = 1,
    NeedsRepair = 2,
    Corrupt = 3
}

/// <summary>Odpowiedź na pytanie „czy Word otworzy ten plik".</summary>
public enum WordOpenVerdict
{
    Ok = 0,
    Repair = 1,
    CannotOpen = 2
}

/// <summary>Odpowiedź na pytanie „czy konwersja do PDF się powiedzie".</summary>
public enum PdfConversionVerdict
{
    Ok = 0,
    AtRisk = 1,
    Likely = 2,
    Blocked = 3
}

/// <summary>
/// Pełny raport „Kondycja dokumentu". Raport jest samowystarczalny (bez identyfikatora sesji
/// i bez stanu po stronie serwera) — GUI może go zapisać albo wkleić do zgłoszenia.
/// </summary>
public sealed class DocumentHealthReport
{
    public required string FileName { get; init; }
    public required long FileSizeInBytes { get; init; }

    /// <summary>Format rozpoznany po bajtach, nie po rozszerzeniu (np. <c>docx</c>, <c>doc</c>, <c>encrypted-ooxml</c>, <c>rtf</c>).</summary>
    public required string DetectedFormat { get; init; }

    public string? MainDocumentPartPath { get; init; }

    public required HealthVerdict Verdict { get; init; }
    public required string VerdictSummary { get; init; }
    public required WordOpenVerdict WordOpen { get; init; }
    public required string WordOpenSummary { get; init; }
    public required PdfConversionVerdict PdfConversion { get; init; }
    public required string PdfConversionSummary { get; init; }

    public required IReadOnlyList<HealthFinding> Findings { get; init; }

    /// <summary>Lista ustaleń została przycięta do limitu prezentacyjnego (liczniki pozostają pełne).</summary>
    public required bool FindingsTruncated { get; init; }

    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
    public required int InfoCount { get; init; }

    public required IReadOnlyList<ConversionProbeResult> Probes { get; init; }
    public required DocumentHealthStatistics Statistics { get; init; }

    /// <summary>
    /// Pokrycie konstrukcji dokumentu przez naszą implementację — tylko konstrukcje, które w
    /// dokumencie WYSTĘPUJĄ. Pusta lista = dokument nie użył niczego z rejestru albo analiza
    /// struktury nie doszła do skutku.
    /// </summary>
    public required IReadOnlyList<ImplementationCoverageItem> Coverage { get; init; }

    /// <summary>Jedno zdanie: ile konstrukcji obsługujemy w pełni, ile częściowo, ile gubimy.</summary>
    public required string CoverageSummary { get; init; }

    public required DateTimeOffset AnalyzedAtUtc { get; init; }
    public required long DurationMs { get; init; }
}
