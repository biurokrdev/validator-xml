using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using static Mass.AddressWindow.Api.Tests.ApiTestHost;

namespace Mass.AddressWindow.Api.Tests;

// 8. Conversion problems are technical errors: no validation runs on the DOCX and nothing is reported as verified.
public class ConversionFailureApiTests
{
    private const string Url = "/api/letter-inspections/upload";

    [Fact]
    public async Task ConversionServiceDown_Returns503_ForDocx_ButStillAcceptsPdf()
    {
        using var host = new ApiTestHost(("External:Mock:ConversionEnabled", "false"));
        var client = host.CreateClient();

        var docx = await client.PostAsync(Url, UploadForm(Sample("02_C65_dwa_okienka_poprawny.docx"), "pismo.docx", "DoubleWindow"));
        var pdf = await client.PostAsync(Url, UploadForm(Sample("02_C65_dwa_okienka_poprawny.pdf"), "pismo.pdf", "DoubleWindow"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, docx.StatusCode);
        var problem = (await docx.Content.ReadFromJsonAsync<ProblemDetails>(Json))!;
        Assert.Equal("Konwersja DOCX do PDF jest niedostępna.", problem.Title);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
    }

    [Fact]
    public async Task ConversionServiceReturnsNothing_Returns502_WithoutVerdict()
    {
        using var host = new ApiTestHost();

        var response = await host.CreateClient().PostAsync(Url, UploadForm(Sample("02_C65_dwa_okienka_poprawny.docx"), "nieznane-pismo.docx", "DoubleWindow"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(Json))!;
        Assert.Equal("Konwersja DOCX do PDF nie powiodła się.", problem.Title);
        Assert.Contains("nieznane-pismo.docx", problem.Detail);
    }

    [Fact]
    public async Task MockDocxWithConversionServiceDown_Returns503()
    {
        using var host = new ApiTestHost(("External:Mock:ConversionEnabled", "false"), ("External:Mock:LetterFormat", "Docx"));
        var order = new { envelope = "SingleWindow", recipientLines = new[] { "Pan Jan Kowalski", "ul. Polna 1", "00-001 Warszawa" }, includePreview = false };

        var response = await host.CreateClient().PostAsJsonAsync("/api/letter-inspections/from-recipient", order, Json);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
