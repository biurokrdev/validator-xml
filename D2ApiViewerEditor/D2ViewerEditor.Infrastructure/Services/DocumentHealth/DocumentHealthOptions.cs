namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public sealed class DocumentHealthOptions
{
    public const string SectionName = "DocumentHealth";

    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;
    public int MaxZipEntries { get; set; } = 2_000;
    public long MaxSingleEntryBytes { get; set; } = 20L * 1024 * 1024;
    public long MaxTotalUncompressedBytes { get; set; } = 100L * 1024 * 1024;
    public double MaxCompressionRatio { get; set; } = 250d;

    public int MaxFindings { get; set; } = 400;

    public int MaxFindingsPerCode { get; set; } = 25;

    public int TableNestingWarningDepth { get; set; } = 4;

    public long LargeImageBytes { get; set; } = 15L * 1024 * 1024;

    public long TotalImageBytesWarning { get; set; } = 80L * 1024 * 1024;

    public int ElementCountWarning { get; set; } = 400_000;

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public bool EnableRoundTripProbe { get; set; } = true;

    public string? LibreOfficePath { get; set; }

    public bool EnableLibreOfficeProbe { get; set; } = true;

    public TimeSpan LibreOfficeTimeout { get; set; } = TimeSpan.FromSeconds(120);
}
