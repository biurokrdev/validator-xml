using System.Collections.Concurrent;
using System.Security.Cryptography;
using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Api.Conversion;
using Mass.AddressWindow.Api.Documents;
using Microsoft.Extensions.Options;

namespace Mass.AddressWindow.Api.External;

public sealed class MockExternalOptions
{
    public const string Section = "External:Mock";

    // Which document the simulated external application returns for an order; fixed by configuration, never random.
    public DocumentFormat LetterFormat { get; set; } = DocumentFormat.Pdf;

    // false simulates the conversion service being down: every DOCX gets 503, PDF keeps working.
    public bool ConversionEnabled { get; set; } = true;

    // Ready PDF renders for uploaded DOCX files, matched by file name. Empty = samples/pdf shipped with the API.
    public string PdfDirectory { get; set; } = "";
}

// Stands in for the external application, which in production does three things over HTTP: produces the letter for
// an order, returns the letter of a stored form by its identifier, and renders a DOCX to PDF. Letters it produced
// itself it can render (the same content drawn straight as PDF);
// for an uploaded DOCX it returns the ready PDF of the same name from PdfDirectory, the way a real service would
// return its render. Any other DOCX is reported as a conversion failure, never validated by guesswork.
public sealed class MockExternalSystem(IOptions<MockExternalOptions> options) : IExternalLetterSource, IDocxToPdfConverter
{
    private readonly MockExternalOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, byte[]> _renders = new();

    public Task<LetterDocument> FetchAsync(LetterOrder order, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var baseName = "pismo-" + (order.RegisteredNumber is { } n ? LetterContent.SafeFileName(n) : "bez-numeru");
        try
        {
            if (_options.LetterFormat == DocumentFormat.Pdf)
            {
                return Task.FromResult(LetterDocument.Create(baseName + ".pdf", LetterPdfWriter.Write(order)));
            }

            var docx = LetterDocxWriter.Write(order);
            _renders[Hash(docx)] = LetterPdfWriter.Write(order);
            return Task.FromResult(LetterDocument.Create(baseName + ".docx", docx));
        }
        catch (Exception ex) when (ex is not DocumentRejectedException)
        {
            throw new ExternalSourceException($"Aplikacja zewnętrzna (mock) nie zwróciła dokumentu: {ex.Message}", ex);
        }
    }

    // A form is fetched by GUID alone, so the mock derives a deterministic letter from the identifier: the same GUID
    // always yields the same recipient. Forms come back as PDF; a DOCX would still go through conversion.
    public Task<LetterDocument> FetchFormAsync(FormOrder form, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = form.FormId.ToByteArray();
        var recipient = Recipient.Create(["Pan Jan Kowalski", $"ul. Formularzowa {bytes[0] % 200 + 1}", "00-061 Warszawa"]);
        var number = form.Envelope == EnvelopeType.DoubleWindow ? "00759007731512000621" : null;
        var order = LetterOrder.Create(form.Envelope, recipient, number);
        try
        {
            return Task.FromResult(LetterDocument.Create($"formularz-{form.FormId}.pdf", LetterPdfWriter.Write(order)));
        }
        catch (Exception ex) when (ex is not DocumentRejectedException)
        {
            throw new ExternalSourceException($"Aplikacja zewnętrzna (mock) nie zwróciła dokumentu formularza: {ex.Message}", ex);
        }
    }

    public async Task<byte[]> ConvertAsync(LetterDocument docx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.ConversionEnabled)
        {
            throw new DocumentConversionException("Usługa konwersji DOCX do PDF jest wyłączona w konfiguracji.", converterUnavailable: true);
        }

        if (_renders.TryRemove(Hash(docx.Content), out var rendered))
        {
            return rendered;
        }

        var path = Path.Combine(PdfDirectory, Path.ChangeExtension(Path.GetFileName(docx.FileName), ".pdf"));
        if (!File.Exists(path))
        {
            throw new DocumentConversionException(
                $"Usługa konwersji (mock) nie ma gotowego PDF dla „{docx.FileName}”. Mock zna tylko pisma z katalogu samples; "
                + "dla innych dokumentów podłącz prawdziwą usługę konwersji (IDocxToPdfConverter).");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private string PdfDirectory => string.IsNullOrWhiteSpace(_options.PdfDirectory)
        ? Path.Combine(AppContext.BaseDirectory, "samples", "pdf")
        : _options.PdfDirectory;

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
}
