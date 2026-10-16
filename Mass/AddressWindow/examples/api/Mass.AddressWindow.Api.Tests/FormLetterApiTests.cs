using System.Net;
using System.Net.Http.Json;
using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Api.Documents;
using Microsoft.AspNetCore.Mvc;
using static Mass.AddressWindow.Api.Tests.ApiTestHost;

namespace Mass.AddressWindow.Api.Tests;

// Variant 3: only a form identifier is given; the mock of the external application returns that form's letter as PDF.
public class FormLetterApiTests : IClassFixture<DefaultApiTestHost>
{
    private const string Url = "/api/letter-inspections/from-form";
    private const string FormId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private readonly HttpClient _client;

    public FormLetterApiTests(DefaultApiTestHost host)
    {
        _client = host.CreateClient();
    }

    [Fact]
    public async Task SingleWindow_FormDocument_IsFetchedAsPdfAndValid()
    {
        var response = await _client.PostAsJsonAsync(Url, new { envelope = "SingleWindow", formId = FormId, includePreview = false }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Body(response);
        Assert.Equal(ValidationOutcome.Valid, result.Status);
        Assert.Equal(DocumentSource.FormMock, result.Document.Source);
        Assert.Equal(DocumentFormat.Pdf, result.Document.SourceFormat);
        Assert.False(result.Document.ConvertedToPdf);
        Assert.Equal(RecipientMatch.NotChecked, result.Address!.RecipientMatch);
        Assert.Null(result.Label);
    }

    [Fact]
    public async Task DoubleWindow_FormDocument_HasReadableLabel()
    {
        var response = await _client.PostAsJsonAsync(Url, new { envelope = "DoubleWindow", formId = FormId, includePreview = false }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Body(response);
        Assert.Equal(ValidationOutcome.Valid, result.Status);
        Assert.True(result.Label!.Label!.NumberIsValid);
        Assert.Equal(RegisteredMailType.Domestic, result.Label.Label.Kind);
    }

    [Fact]
    public async Task SameFormId_GivesTheSameDocument()
    {
        var first = await Body(await _client.PostAsJsonAsync(Url, new { envelope = "SingleWindow", formId = FormId, includePreview = false }, Json));
        var second = await Body(await _client.PostAsJsonAsync(Url, new { envelope = "SingleWindow", formId = FormId.ToUpperInvariant(), includePreview = false }, Json));

        Assert.Equal(first.Document.FileName, second.Document.FileName);
        Assert.Equal(first.Address!.Address!.Lines, second.Address!.Address!.Lines);
    }

    [Theory]
    [InlineData("nie-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidFormId_Returns400(string formId)
    {
        var response = await _client.PostAsJsonAsync(Url, new { envelope = "SingleWindow", formId, includePreview = false }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(Json))!;
        Assert.NotNull(problem.Title);
    }
}
