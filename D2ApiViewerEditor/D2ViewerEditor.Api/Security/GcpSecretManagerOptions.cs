namespace D2ViewerEditor.Api.Security;

public sealed class GcpSecretManagerOptions
{
    public const string SectionName = "GCPSecretManager";

    public bool Enabled { get; set; }

    public string ProjectId { get; set; } = string.Empty;

    public string EntraSecretName { get; set; } = string.Empty;
}
