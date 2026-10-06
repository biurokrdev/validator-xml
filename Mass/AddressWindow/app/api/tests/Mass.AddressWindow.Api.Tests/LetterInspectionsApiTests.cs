using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Domain.Inspection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Mass.AddressWindow.Api.Tests;

public class LetterInspectionsApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "pdf", name));

    private static MultipartFormDataContent Form(byte[]? file, string fileName, string? envelope, bool? includePreview = null)
    {
        var form = new MultipartFormDataContent();
        if (file is not null)
        {
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(part, "file", fileName);
        }

        if (envelope is not null)
        {
            form.Add(new StringContent(envelope), "envelope");
        }

        if (includePreview is not null)
        {
            form.Add(new StringContent(includePreview.Value ? "true" : "false"), "includePreview");
        }

        return form;
    }

    private async Task<LetterInspectionResponse> Inspect(string sample, string envelope, bool? includePreview = null)
    {
        var response = await _client.PostAsync("/api/letter-inspections", Form(Sample(sample), sample, envelope, includePreview));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LetterInspectionResponse>(Json))!;
    }

    [Fact]
    public async Task SingleWindowLetter_IsValidForSingleWindowEnvelope()
    {
        var result = await Inspect("01_C65_jedno_okienko_poprawny.pdf", "SingleWindow");

        Assert.True(result.IsValid, result.Summary);
        Assert.True(result.IsReadable);
        Assert.Equal("01_C65_jedno_okienko_poprawny.pdf", result.FileName);
        var recipient = Assert.Single(result.Windows);
        Assert.Equal(WindowKind.Recipient, recipient.Kind);
        Assert.Equal(["Pan Jan Kowalski", "ul. Marszałkowska 142 m. 5", "00-061 Warszawa"], recipient.Address!.Lines);
        Assert.Null(recipient.Overflow);
        Assert.StartsWith("data:image/png;base64,", result.Preview!.DataUri);
    }

    [Fact]
    public async Task SingleWindowLetter_LacksRegisteredLabelForDoubleWindowEnvelope()
    {
        var result = await Inspect("01_C65_jedno_okienko_poprawny.pdf", "DoubleWindow");

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Windows.Count);
        var sender = result.Windows.Single(w => w.Kind == WindowKind.Sender);
        Assert.False(sender.Found);
        Assert.Equal(WindowContentKind.RegisteredLabel, sender.Content);
        Assert.Contains(sender.Findings, f => f.Code == "LABEL_NOT_FOUND" && f.Severity == FindingSeverity.Error);
        Assert.Contains("nie znaleziono nalepki R", result.Summary);
    }

    [Fact]
    public async Task LetterWithRegisteredLabel_IsValidForDoubleWindowEnvelope()
    {
        var result = await Inspect("02_C65_dwa_okienka_poprawny.pdf", "DoubleWindow");

        Assert.True(result.IsValid, result.Summary);
        var recipient = result.Windows.Single(w => w.Kind == WindowKind.Recipient);
        Assert.Equal(WindowContentKind.Address, recipient.Content);
        Assert.NotNull(recipient.Address);
        var labelWindow = result.Windows.Single(w => w.Kind == WindowKind.Sender);
        Assert.True(labelWindow.Found);
        Assert.Null(labelWindow.Address);
        Assert.Equal(48, labelWindow.Label!.Bounds.Width, 0);
        Assert.Equal(12, labelWindow.Label.Bounds.Height, 0);
        Assert.Null(labelWindow.Overflow);
    }

    [Fact]
    public async Task StandardSizeRegisteredLabel_DoesNotFitTheWindow()
    {
        var result = await Inspect("05_C65_nalepka_R_za_duza.pdf", "DoubleWindow");

        Assert.False(result.IsValid);
        var labelWindow = result.Windows.Single(w => w.Kind == WindowKind.Sender);
        Assert.True(labelWindow.Found);
        Assert.Contains(labelWindow.Findings, f => f.Code == "LABEL_TOO_LARGE");
        Assert.NotNull(labelWindow.Overflow);
    }

    [Fact]
    public async Task FaultyLetter_ReportsOverflowAndContentErrors()
    {
        var result = await Inspect("03_C65_bledy_adresu.pdf", "SingleWindow");

        Assert.False(result.IsValid);
        var recipient = Assert.Single(result.Windows);
        Assert.True(recipient.Found);
        Assert.InRange(recipient.Overflow!.RightMm, 5, 15);
        Assert.Contains("z prawej", recipient.Overflow.Description);
        var codes = recipient.Findings.Select(f => f.Code).ToList();
        Assert.Contains("ADDRESS_OUTSIDE_WINDOW", codes);
        Assert.Contains("POSTAL_CODE_LINE_MISSING", codes);
    }

    [Fact]
    public async Task EnvelopeDefaultsToSingleWindow_AndPreviewCanBeSkipped()
    {
        var response = await _client.PostAsync(
            "/api/letter-inspections",
            Form(Sample("02_C65_dwa_okienka_poprawny.pdf"), "pismo.pdf", envelope: null, includePreview: false));
        var result = (await response.Content.ReadFromJsonAsync<LetterInspectionResponse>(Json))!;

        Assert.Equal(EnvelopeType.SingleWindow, result.Envelope);
        Assert.True(result.IsValid, result.Summary);
        Assert.Null(result.Preview);
        Assert.Equal(1, result.Page!.Number);
    }

    [Fact]
    public async Task CorruptedPdf_IsReportedAsUnreadable_NotAsHttpError()
    {
        var corrupted = Encoding.ASCII.GetBytes("%PDF-1.7\nto nie jest prawdziwy dokument");

        var response = await _client.PostAsync("/api/letter-inspections", Form(corrupted, "zepsuty.pdf", "SingleWindow"));
        var result = (await response.Content.ReadFromJsonAsync<LetterInspectionResponse>(Json))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(result.IsReadable);
        Assert.False(result.IsValid);
        Assert.Empty(result.Windows);
        Assert.Contains(result.DocumentFindings, f => f.Code == "INVALID_DOCUMENT");
    }

    [Fact]
    public async Task FileThatIsNotPdf_Returns400WithReason()
    {
        var response = await _client.PostAsync(
            "/api/letter-inspections", Form(Encoding.UTF8.GetBytes("zwykły tekst"), "notatka.txt", "SingleWindow"));
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(Json))!;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Plik nie jest dokumentem PDF.", problem.Detail);
    }

    [Fact]
    public async Task MissingFile_Returns400()
    {
        var response = await _client.PostAsync("/api/letter-inspections", Form(file: null, "", "SingleWindow"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Nie przes", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownEnvelope_Returns400()
    {
        var response = await _client.PostAsync(
            "/api/letter-inspections", Form(Sample("01_C65_jedno_okienko_poprawny.pdf"), "pismo.pdf", "Triple"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
