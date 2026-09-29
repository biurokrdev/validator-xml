using System.Net;
using System.Net.Http;
using FluentAssertions;

namespace D2ViewerEditor.Api.IntegrationTests;

/// <summary>
/// Kontrola dostępu do „Porównania dokumentów” (<c>api/documentcompare</c>): endpoint wymaga polityki
/// RequireAppAdmin — samo uwierzytelnienie ani rola Operator nie wystarczają.
/// </summary>
[TestFixture]
public class DocumentCompareAuthorizationTests
{
    private const string AnalyzePath = "/api/documentcompare/analyze";

    private AuthTestWebApplicationFactory _factory = null!;

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
    public async Task Analyze_WithoutToken_Returns401()
    {
        var response = await CreateClient(roles: null).PostAsync(AnalyzePath, new MultipartFormDataContent());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Analyze_AuthenticatedButNoRole_Returns403()
    {
        var response = await CreateClient(roles: "").PostAsync(AnalyzePath, new MultipartFormDataContent());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Analyze_WithOperatorRole_Returns403()
    {
        var response = await CreateClient(roles: "Operator").PostAsync(AnalyzePath, new MultipartFormDataContent());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Analyze_WithAdministratorRole_PassesAuthorization()
    {
        var response = await CreateClient(roles: "Administrator").PostAsync(AnalyzePath, new MultipartFormDataContent());

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}
