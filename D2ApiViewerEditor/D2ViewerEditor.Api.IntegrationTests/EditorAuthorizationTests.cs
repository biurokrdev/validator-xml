using System.Net;
using System.Net.Http;
using FluentAssertions;

namespace D2ViewerEditor.Api.IntegrationTests;

[TestFixture]
public class EditorAuthorizationTests
{
    private AuthTestWebApplicationFactory _factory = null!;

    private const string EditorEndpoint = "/api/document/templates";

    private const string AdminEndpoint = "/api/documentstorage";

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new AuthTestWebApplicationFactory();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    private HttpClient CreateClient(string? roles)
    {
        var client = _factory.CreateClient();
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeader, "true");
            if (roles.Length > 0)
                client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        }
        return client;
    }

    [Test]
    public async Task EditorEndpoint_WithoutToken_Returns401()
    {
        var client = CreateClient(roles: null);

        var response = await client.GetAsync(EditorEndpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task EditorEndpoint_AuthenticatedButNoRole_Returns403()
    {
        var client = CreateClient(roles: "");

        var response = await client.GetAsync(EditorEndpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task EditorEndpoint_WithUnrelatedRole_Returns403()
    {
        var client = CreateClient(roles: "SomeOtherRole");

        var response = await client.GetAsync(EditorEndpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task EditorEndpoint_WithOperatorRole_Returns200()
    {
        var client = CreateClient(roles: "Operator");

        var response = await client.GetAsync(EditorEndpoint);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task EditorEndpoint_WithAdministratorRole_Returns200()
    {
        
        var client = CreateClient(roles: "Administrator");

        var response = await client.GetAsync(EditorEndpoint);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task EditorSaveEndpoint_WithoutToken_Returns401()
    {
        var client = CreateClient(roles: null);

        var response = await client.PostAsync("/api/document/save",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task EditorSaveEndpoint_AuthenticatedButNoRole_Returns403()
    {
        var client = CreateClient(roles: "");

        var response = await client.PostAsync("/api/document/save",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task AdminEndpoint_WithOperatorRole_Returns403()
    {
        
        var client = CreateClient(roles: "Operator");

        var response = await client.GetAsync(AdminEndpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task AdminEndpoint_WithoutToken_Returns401()
    {
        var client = CreateClient(roles: null);

        var response = await client.GetAsync(AdminEndpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task DeleteDocument_WithOperatorRole_Returns403()
    {
        
        var client = CreateClient(roles: "Operator");

        var response = await client.DeleteAsync($"{AdminEndpoint}/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task DeleteDocument_WithoutToken_Returns401()
    {
        var client = CreateClient(roles: null);

        var response = await client.DeleteAsync($"{AdminEndpoint}/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
