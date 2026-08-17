namespace D2ViewerEditor.Api.Security;

public sealed class RolesOptions
{
    public const string SectionName = "Roles";

    public string GroupPrefix { get; set; } = string.Empty;

    public List<RoleGroupMapping> Roles { get; set; } = new();
}

public sealed class RoleGroupMapping
{
    
    public string RoleName { get; set; } = string.Empty;

    public List<string> GroupNames { get; set; } = new();
}
