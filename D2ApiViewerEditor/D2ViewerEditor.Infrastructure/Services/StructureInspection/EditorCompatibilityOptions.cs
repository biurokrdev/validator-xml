namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class EditorCompatibilityOptions
{
    public const string SectionName = "EditorCompatibility";

    public string ProfileName { get; set; } = "Nieskonfigurowany";
    public string DefaultLevel { get; set; } = "Unknown";
    public Dictionary<string, string> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
