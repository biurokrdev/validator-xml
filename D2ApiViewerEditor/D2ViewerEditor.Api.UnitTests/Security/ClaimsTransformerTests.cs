using System.Security.Claims;
using D2ViewerEditor.Api.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace D2ViewerEditor.Api.UnitTests.Security;

[TestFixture]
public class ClaimsTransformerTests
{
    private static ClaimsTransformer BuildTransformer() =>
        new(Options.Create(new RolesOptions
        {
            GroupPrefix = "KUTAS_200_",
            Roles = new List<RoleGroupMapping>
            {
                new() { RoleName = "Operator", GroupNames = new() { "KUTAS_200_Pracownik" } },
                new() { RoleName = "Administrator", GroupNames = new() { "KUTAS_200_Administrator" } },
            }
        }));

    private static ClaimsPrincipal PrincipalWithGroups(params string[] groups)
    {
        var claims = groups.Select(g => new Claim("groups", g)).ToList();
        var identity = new ClaimsIdentity(claims, authenticationType: "TestAuth", nameType: "name", roleType: "roles");
        return new ClaimsPrincipal(identity);
    }

    [Test]
    public async Task Maps_admin_group_to_admin_role()
    {
        var principal = PrincipalWithGroups("KUTAS_200_Administrator");

        var result = await BuildTransformer().TransformAsync(principal);

        result.IsInRole("Administrator").Should().BeTrue();
        result.IsInRole("Operator").Should().BeFalse();
    }

    [Test]
    public async Task Maps_operator_group_case_insensitively()
    {
        var principal = PrincipalWithGroups("kutas_200_pracownik");

        var result = await BuildTransformer().TransformAsync(principal);

        result.IsInRole("Operator").Should().BeTrue();
    }

    [Test]
    public async Task Maps_group_identifier_carried_under_role_claim_type()
    {
        
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "KUTAS_200_Administrator") },
            "TestAuth", "name", "roles");
        var principal = new ClaimsPrincipal(identity);

        var result = await BuildTransformer().TransformAsync(principal);

        result.IsInRole("Administrator").Should().BeTrue();
    }

    [Test]
    public async Task No_matching_group_grants_no_role()
    {
        var principal = PrincipalWithGroups("KUTAS_200_SomethingElse");

        var result = await BuildTransformer().TransformAsync(principal);

        result.IsInRole("Administrator").Should().BeFalse();
        result.IsInRole("Operator").Should().BeFalse();
    }

    [Test]
    public async Task Is_idempotent_when_run_twice()
    {
        var transformer = BuildTransformer();
        var principal = PrincipalWithGroups("KUTAS_200_Administrator");

        var once = await transformer.TransformAsync(principal);
        var twice = await transformer.TransformAsync(once);

        twice.FindAll("roles").Count(c => c.Value == "Administrator").Should().Be(1);
    }

    [Test]
    public async Task Unauthenticated_principal_is_left_untouched()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); 

        var result = await BuildTransformer().TransformAsync(anonymous);

        result.FindAll("roles").Should().BeEmpty();
    }

    [Test]
    public async Task Preserves_native_app_role_claims()
    {
        
        var identity = new ClaimsIdentity(
            new[] { new Claim("roles", "Administrator"), new Claim("groups", "KUTAS_200_Pracownik") },
            "TestAuth", "name", "roles");
        var principal = new ClaimsPrincipal(identity);

        var result = await BuildTransformer().TransformAsync(principal);

        result.IsInRole("Administrator").Should().BeTrue();      
        result.IsInRole("Operator").Should().BeTrue();  
    }
}
