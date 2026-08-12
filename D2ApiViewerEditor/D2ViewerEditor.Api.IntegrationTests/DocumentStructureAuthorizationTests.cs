using System.Net;
using System.Net.Http;
using FluentAssertions;

namespace D2ViewerEditor.Api.IntegrationTests;

[TestFixture]
public class DocumentStructureAuthorizationTests
{
    private const string InspectionId = "00000000-0000-0000-0000-000000000001";

    private static readonly (string Name, HttpMethod Method, string Path)[] Endpoints =
    [
        ("analyze", HttpMethod.Post, "/api/documentstructure/analyze"),
        ("elements", HttpMethod.Get, $"/api/documentstructure/{InspectionId}/elements"),
        ("element-details", HttpMethod.Get, $"/api/documentstructure/{InspectionId}/elements/p0"),
        ("element-xml", HttpMethod.Get, $"/api/documentstructure/{InspectionId}/elements/p0/xml"),
        ("part-xml", HttpMethod.Get, $"/api/documentstructure/{InspectionId}/parts/xml?path=word%2Fdocument.xml"),
        ("schema-issues", HttpMethod.Get, $"/api/documentstructure/{InspectionId}/schema-issues"),
        ("package-diagnostics", HttpMethod.Get, $"/api/documentstructure/{InspectionId}/package-diagnostics"),
        ("sections", HttpMethod.Get, $"/api/documentstructure/{InspectionId}/sections"),
        ("delete", HttpMethod.Delete, $"/api/documentstructure/{InspectionId}")
    ];

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

    private static IEnumerable<TestCaseData> EndpointCases() =>
        Endpoints.Select(endpoint =>
            new TestCaseData(endpoint.Method, endpoint.Path).SetArgDisplayNames(endpoint.Name));

    [TestCaseSource(nameof(EndpointCases))]
    public async Task Endpoint_WithoutToken_Returns401(HttpMethod method, string path)
    {
        var client = CreateClient(roles: null);

        var response = await client.SendAsync(new HttpRequestMessage(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestCaseSource(nameof(EndpointCases))]
    public async Task Endpoint_AuthenticatedButNoRole_Returns403(HttpMethod method, string path)
    {
        var client = CreateClient(roles: "");

        var response = await client.SendAsync(new HttpRequestMessage(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestCaseSource(nameof(EndpointCases))]
    public async Task Endpoint_WithOperatorRole_Returns403(HttpMethod method, string path)
    {
        var client = CreateClient(roles: "Operator");

        var response = await client.SendAsync(new HttpRequestMessage(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestCaseSource(nameof(EndpointCases))]
    public async Task Endpoint_WithAdministratorRole_PassesAuthorization(HttpMethod method, string path)
    {
        var client = CreateClient(roles: "Administrator");

        var response = await client.SendAsync(new HttpRequestMessage(method, path));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}
