namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class StructureInspectionOptions
{
    public const string SectionName = "StructureInspection";

    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;
    public int MaxZipEntries { get; set; } = 2_000;
    public long MaxSingleEntryBytes { get; set; } = 20L * 1024 * 1024;
    public long MaxTotalUncompressedBytes { get; set; } = 100L * 1024 * 1024;
    public double MaxCompressionRatio { get; set; } = 250d;
    public long MaxXmlCharacters { get; set; } = 30_000_000;
    public int MaxElements { get; set; } = 50_000;
    public int MaxXmlDepth { get; set; } = 160;

    public int MaxSchemaIssues { get; set; } = 500;

    public int MaxStoredInspections { get; set; } = 4;

    public long MaxStoredBytes { get; set; } = 120L * 1024 * 1024;

    public TimeSpan InspectionTimeToLive { get; set; } = TimeSpan.FromMinutes(30);

    public string DefaultSchemaTarget { get; set; } = "Microsoft365";
}
