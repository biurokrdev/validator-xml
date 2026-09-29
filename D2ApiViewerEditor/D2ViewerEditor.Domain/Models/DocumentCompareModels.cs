namespace D2ViewerEditor.Domain.Models;

/// <summary>Rodzaj różnicy między dwoma pakietami DOCX. Kolejność od poziomu pakietu do poziomu tekstu.</summary>
public enum DifferenceKind
{
    PartOnlyInLeft = 0,
    PartOnlyInRight = 1,
    BinaryPartChanged = 2,
    ContentTypeChanged = 3,
    ElementOnlyInLeft = 4,
    ElementOnlyInRight = 5,
    ElementNameChanged = 6,
    AttributeOnlyInLeft = 7,
    AttributeOnlyInRight = 8,
    AttributeValueChanged = 9,
    TextChanged = 10,

    /// <summary>Identyczne poddrzewo istnieje po obu stronach, ale w innym miejscu listy rodzeństwa (nie usunięte).</summary>
    ElementMoved = 11,

    /// <summary>
    /// Część binarna o IDENTYCZNYCH bajtach istnieje po obu stronach pod inną ścieżką (np. <c>word/media/image1.png</c> →
    /// <c>media/image.png</c>) — zmiana nazwy/lokalizacji, nie utrata ani nowy plik.
    /// </summary>
    PartRenamed = 12
}

/// <summary>Stan części pakietu w porównaniu.</summary>
public enum ComparedPartStatus
{
    Identical = 0,
    Changed = 1,
    OnlyInLeft = 2,
    OnlyInRight = 3,
    Unreadable = 4
}

/// <summary>
/// DLACZEGO różnica istnieje — z perspektywy „lewy = oryginał (v1), prawy = kopia zapisana z naszego edytora (v2)”.
/// To odpowiedź na pytanie operacyjne: czy czegoś brakuje, bo użytkownik to usunął, czy bo MY tego nie
/// konwertujemy / nie kopiujemy / nie obsługujemy.
/// </summary>
public enum DifferenceCause
{
    /// <summary>Nie umiemy rozstrzygnąć automatycznie — wymaga spojrzenia człowieka.</summary>
    Unknown = 0,

    /// <summary>Szum Worda: identyfikatory sesji, znaczniki korekty/renderowania, metadane zapisu.</summary>
    WordNoise = 1,

    /// <summary>Identyfikator przepisany spójnie (Id relationshipu, w:id zakładki/przypisu) — odwołania nadal działają.</summary>
    IdentifierRewrite = 2,

    /// <summary>Artefakt parowania komparatora (dwie różne relacje sparowane pozycyjnie) — nie jest realną różnicą.</summary>
    PairingArtifact = 3,

    /// <summary>Nasz writer zapisuje to samo inaczej (jawne formatowanie, zaokrąglenie jednostek, pusty akapit domykający).</summary>
    WriterNormalization = 4,

    /// <summary>Nasz pipeline generuje tę część/konstrukcję od nowa zamiast kopiować (numbering, settings, style bez pass-through).</summary>
    PipelineRegenerated = 5,

    /// <summary>Konstrukcja obsługiwana częściowo — część właściwości nie przechodzi przez reader/edytor/writer.</summary>
    PipelinePartial = 6,

    /// <summary>Konstrukcja NIEOBSŁUGIWANA przez nasz pipeline — ginie po pierwszym zapisie niezależnie od użytkownika.</summary>
    PipelineUnsupported = 7,

    /// <summary>Treść się różni — najpewniej celowa edycja użytkownika w edytorze.</summary>
    UserEdit = 8,

    /// <summary>Treść z oryginału nie ma odpowiednika: użytkownik ją usunął ALBO reader ją pominął — do rozstrzygnięcia po kontekście.</summary>
    UserEditOrLoss = 9
}

/// <summary>Skutek różnicy dla użytkownika końcowego (co zobaczy w Wordzie / w PDF).</summary>
public enum DifferenceImpact
{
    /// <summary>Żaden — plik inny, dokument ten sam.</summary>
    None = 0,

    /// <summary>Kosmetyczny: XML bogatszy/uboższy, wygląd i treść te same.</summary>
    Cosmetic = 1,

    /// <summary>Układ lub wygląd mogą się różnić (odstępy, fonty, style, kolejność).</summary>
    Layout = 2,

    /// <summary>Konwersja do PDF da inny wynik niż z oryginału (np. ukryty tekst widoczny, inna numeracja stron).</summary>
    PdfDifference = 3,

    /// <summary>Word może zgłosić naprawę pliku.</summary>
    WordRepair = 4,

    /// <summary>Utrata treści lub danych (komentarze, równania, dołączona treść, obiekty).</summary>
    DataLoss = 5
}

/// <summary>Analiza jednej różnicy: przyczyna, skutek, wyjaśnienie po polsku, klucz konstrukcji z rejestru możliwości i wskaźnik do kodu.</summary>
public sealed record DifferenceAnalysis(
    DifferenceCause Cause,
    DifferenceImpact Impact,
    string Explanation,
    string? FeatureKey,
    string? CodePointer);

/// <summary>
/// Pojedyncza różnica. <c>LeftPath</c>/<c>RightPath</c> to ścieżki elementu w każdej z części (indeksy
/// pozycyjne mogą się różnić, gdy po jednej stronie wstawiono rodzeństwo). Wycinki to XML całego
/// elementu (przycięty do limitu), a <c>LeftContext</c>/<c>RightContext</c> to tekst akapitu-gospodarza —
/// żeby dało się rozpoznać miejsce w dokumencie bez czytania XML. <c>Analysis</c> mówi, dlaczego różnica
/// istnieje i co z niej wynika (null = nieprzeanalizowana).
/// </summary>
public sealed record DocumentDifference(
    DifferenceKind Kind,
    string Category,
    string PartPath,
    string? LeftPath,
    string? RightPath,
    int? LeftLine,
    int? RightLine,
    string? Name,
    string? LeftValue,
    string? RightValue,
    string? LeftExcerpt,
    string? RightExcerpt,
    bool ExcerptTruncated,
    string? LeftContext,
    string? RightContext,
    DifferenceAnalysis? Analysis = null);

/// <summary>Część pakietu po porównaniu: status, liczba różnic i rozmiary po obu stronach.</summary>
public sealed record ComparedPart(
    string Path,
    ComparedPartStatus Status,
    bool IsXml,
    int DifferenceCount,
    long? LeftSize,
    long? RightSize,
    int? LeftElementCount,
    int? RightElementCount,
    string? Note);

public sealed record ComparedFile(string FileName, long SizeInBytes, string DetectedFormat, int PartCount, IReadOnlyList<string> Notes);

/// <summary>Raport porównania dwóch pakietów DOCX — samowystarczalny, bez stanu po stronie serwera.</summary>
public sealed class DocumentComparisonReport
{
    public required ComparedFile Left { get; init; }
    public required ComparedFile Right { get; init; }
    public required bool IgnoreRevisionIds { get; init; }
    public required bool IgnoreDocumentProperties { get; init; }
    public required bool Identical { get; init; }
    public required int TotalDifferences { get; init; }
    public required bool Truncated { get; init; }
    public required int IgnoredAttributeCount { get; init; }
    public required IReadOnlyDictionary<string, int> CountsByKind { get; init; }
    public required IReadOnlyDictionary<string, int> CountsByCategory { get; init; }

    /// <summary>Liczniki przyczyn (nazwy <see cref="DifferenceCause"/>) — odpowiedź „ile z tego to nasza wina, ile użytkownika, ile szumu”.</summary>
    public IReadOnlyDictionary<string, int> CountsByCause { get; init; } = new Dictionary<string, int>();

    /// <summary>Liczniki skutków (nazwy <see cref="DifferenceImpact"/>).</summary>
    public IReadOnlyDictionary<string, int> CountsByImpact { get; init; } = new Dictionary<string, int>();

    public required IReadOnlyList<ComparedPart> Parts { get; init; }
    public required IReadOnlyList<DocumentDifference> Differences { get; init; }
    public required DateTimeOffset ComparedAtUtc { get; init; }
    public required long DurationMs { get; init; }
}
