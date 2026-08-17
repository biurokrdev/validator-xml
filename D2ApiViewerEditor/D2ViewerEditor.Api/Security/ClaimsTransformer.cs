using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Api.Security;

public sealed class ClaimsTransformer : IClaimsTransformation
{
    private const string GroupsClaimType = "groups";

    private const string RolesClaimType = "roles";

    private readonly RolesOptions _roles;

    public ClaimsTransformer(IOptions<RolesOptions> roles) => _roles = roles.Value;

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        if (_roles.Roles.Count == 0)
            return Task.FromResult(principal);

        var candidateValues = principal.Claims
            .Where(c => c.Type == GroupsClaimType
                     || c.Type == RolesClaimType
                     || c.Type == ClaimTypes.Role
                     || c.Type.Contains("identity/claims/role", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value.Trim())
            .Where(v => v.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (candidateValues.Count == 0)
            return Task.FromResult(principal);

        foreach (var mapping in _roles.Roles)
        {
            if (string.IsNullOrWhiteSpace(mapping.RoleName))
                continue;

            var granted = mapping.GroupNames?.Any(g => candidateValues.Contains(g.Trim())) is true;
            if (!granted)
                continue;

            if (!principal.HasClaim(RolesClaimType, mapping.RoleName))
                identity.AddClaim(new Claim(RolesClaimType, mapping.RoleName));
        }

        return Task.FromResult(principal);
    }
}
