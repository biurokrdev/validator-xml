namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

/// <summary>
/// Limity porównania dwóch pakietów DOCX. Liczba różnic jest przycinana do limitu prezentacyjnego
/// (licznik pozostaje pełny); wycinki XML mają twardy sufit znaków; wyrównanie LCS przełącza się na
/// heurystykę zachłanną, gdy tablica DP przekroczyłaby limit komórek (długie listy akapitów).
/// </summary>
public sealed class DocumentCompareOptions
{
    public const string SectionName = "DocumentCompare";

    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Ile różnic trafia do raportu łącznie.</summary>
    public int MaxDifferences { get; set; } = 5_000;

    /// <summary>Ile różnic trafia do raportu z jednej części.</summary>
    public int MaxDifferencesPerPart { get; set; } = 2_000;

    /// <summary>Sufit znaków wycinka XML jednej strony.</summary>
    public int MaxExcerptChars { get; set; } = 6_000;

    /// <summary>Powyżej tylu komórek n×m wyrównanie LCS ustępuje heurystyce zachłannej.</summary>
    public long LcsCellLimit { get; set; } = 4_000_000;

    /// <summary>Okno poszukiwania dopasowania w heurystyce zachłannej.</summary>
    public int GreedyWindow { get; set; } = 300;

    /// <summary>Maksymalna długość tekstu kontekstu (akapit-gospodarz różnicy).</summary>
    public int MaxContextChars { get; set; } = 160;
}
