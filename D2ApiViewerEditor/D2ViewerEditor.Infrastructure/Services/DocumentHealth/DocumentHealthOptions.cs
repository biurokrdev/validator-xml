namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>
/// Limity i progi diagnostyki „Kondycja dokumentu". Wejście jest niezaufane (dowolne bajty),
/// więc każdy wymiar ma twardy sufit; progi ostrzeżeń opisują, od kiedy dokument jest „ciężki"
/// dla konwerterów. Próby konwersji są ograniczone czasem — zawieszony konwerter to też wynik.
/// </summary>
public sealed class DocumentHealthOptions
{
    public const string SectionName = "DocumentHealth";

    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;
    public int MaxZipEntries { get; set; } = 2_000;
    public long MaxSingleEntryBytes { get; set; } = 20L * 1024 * 1024;
    public long MaxTotalUncompressedBytes { get; set; } = 100L * 1024 * 1024;
    public double MaxCompressionRatio { get; set; } = 250d;

    /// <summary>Ile ustaleń trafia do raportu (liczniki pozostają pełne).</summary>
    public int MaxFindings { get; set; } = 400;

    /// <summary>Ile powtórzeń tej samej reguły raportujemy z osobna, zanim zaczniemy tylko liczyć.</summary>
    public int MaxFindingsPerCode { get; set; } = 25;

    /// <summary>Głębokość zagnieżdżenia tabel, od której ostrzegamy (LibreOffice i biblioteki OOXML gubią układ).</summary>
    public int TableNestingWarningDepth { get; set; } = 4;

    /// <summary>Pojedynczy obraz większy niż ten próg spowalnia lub wywraca konwertery.</summary>
    public long LargeImageBytes { get; set; } = 15L * 1024 * 1024;

    /// <summary>Łączny rozmiar obrazów, od którego konwersja jest „ciężka".</summary>
    public long TotalImageBytesWarning { get; set; } = 80L * 1024 * 1024;

    /// <summary>Liczba elementów XML w częściach treści, od której ostrzegamy o czasie konwersji.</summary>
    public int ElementCountWarning { get; set; } = 400_000;

    /// <summary>Limit czasu pojedynczej próby (SDK, edytor, konwerter PDF).</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Próba round-trip DOCX→HTML→DOCX (writer edytora, ścieżka pass-through jak autosave) z pomiarem,
    /// które konstrukcje dokumentu giną po zapisie. Biegnie tylko razem z próbami konwersji.
    /// </summary>
    public bool EnableRoundTripProbe { get; set; } = true;

    /// <summary>
    /// Ścieżka do binarki LibreOffice (<c>soffice</c>). Pusta = weź ze zmiennej środowiskowej
    /// <c>SOFFICE_BIN</c>; brak obu albo brak pliku = próba pominięta (nigdy nie instalujemy nic sami).
    /// </summary>
    public string? LibreOfficePath { get; set; }

    public bool EnableLibreOfficeProbe { get; set; } = true;

    public TimeSpan LibreOfficeTimeout { get; set; } = TimeSpan.FromSeconds(120);
}
