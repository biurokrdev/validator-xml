namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

public sealed class DocumentCompareOptions
{
    public const string SectionName = "DocumentCompare";

    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;

    public int MaxDifferences { get; set; } = 5_000;

    public int MaxDifferencesPerPart { get; set; } = 2_000;

    public int MaxExcerptChars { get; set; } = 6_000;

    public long LcsCellLimit { get; set; } = 4_000_000;

    public int GreedyWindow { get; set; } = 300;

    public int MaxContextChars { get; set; } = 160;
}
