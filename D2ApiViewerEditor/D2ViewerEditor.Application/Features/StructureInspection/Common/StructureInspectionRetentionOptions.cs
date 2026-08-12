namespace D2ViewerEditor.Application.Features.StructureInspection.Common;

public sealed class StructureInspectionRetentionOptions
{
    public const string SectionName = "StructureInspection";

    public TimeSpan InspectionTimeToLive { get; set; } = TimeSpan.FromMinutes(30);
}
