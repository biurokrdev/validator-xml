using System.Net;
using System.Net.Http.Json;
using System.Text;
using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Api.Documents;
using Microsoft.AspNetCore.Mvc;
using static Mass.AddressWindow.Api.Tests.ApiTestHost;

namespace Mass.AddressWindow.Api.Tests;

// Variant 1: the user uploads a document. Sample letters come from samples/ (DOCX built by the generator,
// PDF exported from Word); a DOCX goes to the mock conversion service, which returns the matching PDF.
public class UploadedLetterApiTests : IClassFixture<DefaultApiTestHost>
{
    private const string Url = "/api/letter-inspections/upload";

    private readonly HttpClient _client;

    public UploadedLetterApiTests(DefaultApiTestHost host)
    {
        _client = host.CreateClient();
    }

    private async Task<LetterInspectionResponse> Upload(string sample, string envelope)
    {
        var response = await _client.PostAsync(Url, UploadForm(Sample(sample), sample, envelope));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Body(response);
    }

    // 1. Correct address in a single window.
    [Fact]
    public async Task CorrectAddress_SingleWindow_IsValid()
    {
        var result = await Upload("01_C65_jedno_okienko_poprawny.pdf", "SingleWindow");

        Assert.Equal(ValidationOutcome.Valid, result.Status);
        Assert.Equal(ValidationOutcome.Valid, result.Address!.Status);
        Assert.Null(result.Label);
        Assert.Equal(RecipientMatch.NotChecked, result.Address.RecipientMatch);
        Assert.Equal(["Pan Jan Kowalski", "ul. Marszałkowska 142 m. 5", "00-061 Warszawa"], result.Address.Address!.Lines);
        Assert.Equal(DocumentFormat.Pdf, result.Document.SourceFormat);
        Assert.False(result.Document.ConvertedToPdf);
        Assert.False(result.Profile.Confirmed);
    }

    // 2. Address partly outside the window.
    [Fact]
    public async Task AddressStickingOut_IsInvalid_WithOverflow()
    {
        var result = await Upload("03_C65_bledy_adresu.pdf", "SingleWindow");

        Assert.Equal(ValidationOutcome.Invalid, result.Status);
        Assert.Equal(ValidationOutcome.Invalid, result.Address!.Status);
        Assert.True(result.Address.Found);
        Assert.InRange(result.Address.Overflow!.RightMm, 5, 15);
        Assert.Contains(result.Address.Reasons, r => r.Code == "ADDRESS_OUTSIDE_WINDOW");
    }

    // 3. No address: the two-window letter has a sender address where the label belongs and nothing else in that window.
    [Fact]
    public async Task MissingLabel_And_AddressOutsideProperArea_AreReported()
    {
        var result = await Upload("04_C65_ramka_i_adres_w_tresci.pdf", "DoubleWindow");

        Assert.Equal(ValidationOutcome.Invalid, result.Status);
        Assert.Equal(ValidationOutcome.Valid, result.Address!.Status);
        Assert.Equal(ValidationOutcome.Invalid, result.Label!.Status);
        Assert.False(result.Label.Found);
        Assert.Contains(result.Label.Reasons, r => r.Code == "LABEL_NOT_FOUND" && r.Message.Contains("musi być wstawiona jako grafika"));
    }

    // 4. Correct address and label in two windows.
    [Fact]
    public async Task CorrectAddressAndLabel_DoubleWindow_IsValid()
    {
        var result = await Upload("02_C65_dwa_okienka_poprawny.pdf", "DoubleWindow");

        Assert.Equal(ValidationOutcome.Valid, result.Status);
        Assert.Equal(ValidationOutcome.Valid, result.Address!.Status);
        Assert.Equal(ValidationOutcome.Valid, result.Label!.Status);
        Assert.Equal("00759007731512000621", result.Label.Label!.Number);
        Assert.Equal(RegisteredMailType.Domestic, result.Label.Label.Kind);
        Assert.True(result.Label.Label.NumberIsValid);
    }

    // 5. Label present but too large for the window, and a label with a wrong check digit.
    [Fact]
    public async Task OversizedLabel_IsInvalid_ButAddressStaysValid()
    {
        var result = await Upload("05_C65_nalepka_R_za_duza.pdf", "DoubleWindow");

        Assert.Equal(ValidationOutcome.Invalid, result.Status);
        Assert.Equal(ValidationOutcome.Valid, result.Address!.Status);
        Assert.Equal(ValidationOutcome.Invalid, result.Label!.Status);
        Assert.Contains(result.Label.Reasons, r => r.Code == "LABEL_TOO_LARGE");
        Assert.NotNull(result.Label.Overflow);
    }

    [Fact]
    public async Task LabelWithWrongCheckDigit_IsInvalid()
    {
        var result = await Upload("07_C65_nalepka_R_zla_cyfra_kontrolna.pdf", "DoubleWindow");

        Assert.Equal(ValidationOutcome.Invalid, result.Label!.Status);
        Assert.Contains(result.Label.Reasons, r => r.Code == "LABEL_NUMBER_INVALID");
        Assert.False(result.Label.Label!.NumberIsValid);
    }

    // 6. PDF upload: the file is checked without any further parameters.
    [Fact]
    public async Task PdfUpload_IsRecognisedByContent_EvenWithWrongExtension()
    {
        var response = await _client.PostAsync(Url, UploadForm(Sample("01_C65_jedno_okienko_poprawny.pdf"), "pismo.docx", "SingleWindow"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Body(response);
        Assert.Equal(DocumentFormat.Pdf, result.Document.SourceFormat);
        Assert.False(result.Document.ConvertedToPdf);
        Assert.Equal(ValidationOutcome.Valid, result.Status);
    }

    // 7. DOCX upload: real conversion, then validation of the rendered PDF.
    [Fact]
    public async Task DocxUpload_IsConvertedAndValidated()
    {
        var result = await Upload("02_C65_dwa_okienka_poprawny.docx", "DoubleWindow");

        Assert.Equal(DocumentFormat.Docx, result.Document.SourceFormat);
        Assert.True(result.Document.ConvertedToPdf);
        Assert.Equal(ValidationOutcome.Valid, result.Status);
        Assert.Equal(4, result.Address!.Address!.Lines.Count);
        Assert.Equal("00759007731512000621", result.Label!.Label!.Number);
    }

    [Fact]
    public async Task DocxUpload_WithAddressErrors_IsInvalidAfterConversion()
    {
        var result = await Upload("03_C65_bledy_adresu.docx", "SingleWindow");

        Assert.True(result.Document.ConvertedToPdf);
        Assert.Equal(ValidationOutcome.Invalid, result.Status);
        Assert.Contains(result.Address!.Reasons, r => r.Code == "ADDRESS_OUTSIDE_WINDOW");
    }

    // 8. Documents that cannot be analysed reliably.
    [Fact]
    public async Task ScanWithoutTextLayer_IsInconclusive_NotInvalid()
    {
        var response = await _client.PostAsync(Url, UploadForm(ScanLikePdf(), "skan.pdf", "DoubleWindow"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Body(response);
        Assert.Equal(ValidationOutcome.Inconclusive, result.Status);
        Assert.Equal(ValidationOutcome.Inconclusive, result.Address!.Status);
        Assert.Equal(ValidationOutcome.Inconclusive, result.Label!.Status);
        Assert.Contains(result.DocumentReasons, r => r.Code == "NO_TEXT_LAYER" && r.Severity == IssueSeverity.Inconclusive);
    }

    [Fact]
    public async Task CorruptedPdf_IsTechnicalError_NotAVerdict()
    {
        var response = await _client.PostAsync(Url, UploadForm("%PDF-1.7\nto nie jest prawdziwy dokument"u8.ToArray(), "zepsuty.pdf", "SingleWindow"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(Json))!;
        Assert.Equal("Dokumentu nie da się odczytać.", problem.Title);
    }

    [Fact]
    public async Task FileThatIsNeitherPdfNorDocx_Returns400()
    {
        var response = await _client.PostAsync(Url, UploadForm(Encoding.UTF8.GetBytes("zwykły tekst"), "notatka.pdf", "SingleWindow"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(Json))!;
        Assert.Contains("nie jest dokumentem PDF ani DOCX", problem.Detail);
    }

    [Fact]
    public async Task MissingEnvelope_Returns400()
    {
        var response = await _client.PostAsync(Url, UploadForm(Sample("01_C65_jedno_okienko_poprawny.pdf"), "pismo.pdf", envelope: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingFile_Returns400()
    {
        var response = await _client.PostAsync(Url, UploadForm(null, "", "SingleWindow"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // A PDF with only an image and no text, like a scanned letter.
    private static byte[] ScanLikePdf()
    {
        const string content = "q 500 0 0 700 50 70 cm /Im1 Do Q";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.3 841.9] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\nP\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:0000000000} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
