using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using D2ViewerEditor.Application.Common.Security;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public sealed class ConversionProbeRunner
{
    public const string SdkOpenProbe = "sdk-open";
    public const string SchemaProbe = "schema";
    public const string UploadGateProbe = "upload-gate";
    public const string EditorImportProbe = "editor-import";
    public const string PdfConversionProbe = "pdf-conversion";

    private static readonly Regex PageObjectPattern = new(@"/Type\s*/Page(?![s\w])", RegexOptions.Compiled);

    private readonly OpenXmlSchemaValidatorRunner _schemaValidator;
    private readonly IFileUploadSecurityService _uploadSecurity;
    private readonly IDocxToHtmlConverter _htmlConverter;
    private readonly IDocxToPdfConversionService _pdfConverter;
    private readonly LibreOfficeConverterProbe _libreOffice;
    private readonly DocumentHealthOptions _options;
    private readonly ILogger<ConversionProbeRunner> _logger;

    public ConversionProbeRunner(
        OpenXmlSchemaValidatorRunner schemaValidator,
        IFileUploadSecurityService uploadSecurity,
        IDocxToHtmlConverter htmlConverter,
        IDocxToPdfConversionService pdfConverter,
        LibreOfficeConverterProbe libreOffice,
        IOptions<DocumentHealthOptions> options,
        ILogger<ConversionProbeRunner> logger)
    {
        _schemaValidator = schemaValidator;
        _uploadSecurity = uploadSecurity;
        _htmlConverter = htmlConverter;
        _pdfConverter = pdfConverter;
        _libreOffice = libreOffice;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConversionProbeResult>> RunAsync(
        byte[] documentBytes,
        string fileName,
        HealthFindingCollector findings,
        bool includeConversionProbes,
        CancellationToken cancellationToken)
    {
        var results = new List<ConversionProbeResult>
        {
            RunSdkOpen(documentBytes, findings),
        };

        results.Add(RunSchemaValidation(documentBytes, findings, results[0].Status, cancellationToken));
        results.Add(RunUploadGate(documentBytes, findings));

        if (!includeConversionProbes)
        {
            return results;
        }

        results.Add(await RunEditorImportAsync(documentBytes, findings, cancellationToken));
        results.Add(await RunPdfConversionAsync(documentBytes, fileName, findings, cancellationToken));

        var libreOffice = await _libreOffice.RunAsync(documentBytes, cancellationToken);
        results.Add(libreOffice);

        if (libreOffice.Status == ProbeStatus.Failed)
        {
            findings.Add(DocumentHealthCodes.LibreOfficeFailed, StructureIssueSeverity.Warning, HealthStage.Conversion,
                "LibreOffice nie skonwertował dokumentu",
                $"{libreOffice.Message} Niezależny silnik odrzucił dokument — jeśli produkcyjna usługa DOCX→PDF też oparta jest na LibreOffice, to jest bezpośrednia przyczyna.",
                null, WordOpenImpact.None, PdfConversionImpact.Likely,
                "Przeczytaj stderr w szczegółach próby; usuń wskazaną konstrukcję albo zapisz dokument ponownie w Wordzie.");
        }

        return results;
    }

    private static ConversionProbeResult RunSdkOpen(byte[] documentBytes, HealthFindingCollector findings)
    {
        const string name = "Open XML SDK: otwarcie pakietu";
        const string description = "WordprocessingDocument.Open + odczyt MainDocumentPart.Document.Body — warstwa, na której opiera się większość konwerterów .NET.";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var stream = new MemoryStream(documentBytes, writable: false);
            using var document = WordprocessingDocument.Open(stream, isEditable: false);
            var mainPart = document.MainDocumentPart;

            if (mainPart is null)
            {
                stopwatch.Stop();
                findings.Add(DocumentHealthCodes.SdkOpenFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                    "Open XML SDK nie znalazł głównej części",
                    "Pakiet otworzył się jako OPC, ale nie ma MainDocumentPart (relationship officeDocument). Każdy konwerter oparty na SDK zakończy się błędem. O otwarciu w Wordzie decydują ustalenia etapów Pakiet OPC / XML.",
                    null, WordOpenImpact.None, PdfConversionImpact.Blocking);

                return new ConversionProbeResult(SdkOpenProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                    "MainDocumentPart == null.", null);
            }

            var body = mainPart.Document?.Body;
            var blocks = body?.ChildElements.Count ?? 0;
            stopwatch.Stop();

            if (body is null)
            {
                findings.Add(DocumentHealthCodes.SdkOpenFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                    "Open XML SDK nie odczytał w:body",
                    "SDK otworzył pakiet, ale model dokumentu nie ma treści (Document.Body == null).",
                    null, WordOpenImpact.None, PdfConversionImpact.Likely);

                return new ConversionProbeResult(SdkOpenProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                    "Document.Body == null.", null);
            }

            return new ConversionProbeResult(SdkOpenProbe, name, description, ProbeStatus.Passed, stopwatch.ElapsedMilliseconds,
                $"Pakiet otwarty; w:body ma {blocks} elementów najwyższego poziomu, {document.Parts.Count()} części głównych.", null);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            findings.Add(DocumentHealthCodes.SdkOpenFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                "Open XML SDK nie otworzył pakietu",
                $"{exception.GetType().Name}: {exception.Message}. Konwertery i biblioteki oparte na Open XML SDK (w tym edytor tej aplikacji) zakończą się tym samym błędem. Word bywa bardziej tolerancyjny niż SDK — o otwarciu w Wordzie decydują ustalenia etapów Plik / Pakiet OPC / XML.",
                null, WordOpenImpact.None, PdfConversionImpact.Likely,
                "Przyczyna leży zwykle w ustaleniach etapów Pakiet OPC / XML powyżej.");

            return new ConversionProbeResult(SdkOpenProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"{exception.GetType().Name}: {exception.Message}", Describe(exception));
        }
    }

    private ConversionProbeResult RunSchemaValidation(
        byte[] documentBytes,
        HealthFindingCollector findings,
        ProbeStatus sdkStatus,
        CancellationToken cancellationToken)
    {
        const string name = "Open XML SDK: walidacja schematu";
        const string description = "OpenXmlValidator (profil Microsoft 365) — niezgodności ze schematem OOXML; Word wiele z nich toleruje.";

        if (sdkStatus != ProbeStatus.Passed)
        {
            return new ConversionProbeResult(SchemaProbe, name, description, ProbeStatus.Skipped, 0,
                "Pominięto — SDK nie otworzył pakietu.", null);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = _schemaValidator.Validate(documentBytes, "Microsoft365", cancellationToken);
            stopwatch.Stop();

            if (result.TotalCount == 0)
            {
                return new ConversionProbeResult(SchemaProbe, name, description, ProbeStatus.Passed, stopwatch.ElapsedMilliseconds,
                    "Brak błędów schematu.", null);
            }

            var sample = string.Join("\n", result.Issues.Take(8).Select(issue =>
                $"- [{issue.PartPath ?? "?"}] {issue.Description}{(issue.Path is null ? string.Empty : $" @ {issue.Path}")}"));

            findings.Add(DocumentHealthCodes.SchemaErrors, StructureIssueSeverity.Info, HealthStage.Conversion,
                "Niezgodności ze schematem Open XML",
                $"OpenXmlValidator zgłosił {result.TotalCount} błędów. Word ignoruje większość z nich (nieznane atrybuty, kolejność elementów), ale rygorystyczne konwertery mogą odrzucić dokument. Pełna lista: Walidator struktury → zakładka Schemat.",
                null, WordOpenImpact.None, PdfConversionImpact.Possible);

            return new ConversionProbeResult(SchemaProbe, name, description, ProbeStatus.Warning, stopwatch.ElapsedMilliseconds,
                $"{result.TotalCount} błędów schematu (pierwsze {Math.Min(8, result.Issues.Count)} w szczegółach).", sample);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();

            return new ConversionProbeResult(SchemaProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"{exception.GetType().Name}: {exception.Message}", Describe(exception));
        }
    }

    private ConversionProbeResult RunUploadGate(byte[] documentBytes, HealthFindingCollector findings)
    {
        const string name = "Bramka bezpieczeństwa uploadu";
        const string description = "IFileUploadSecurityService.ValidateDocxStructure — ta sama kontrola, którą przechodzi plik przy wgrywaniu do edytora i przez External API.";
        var stopwatch = Stopwatch.StartNew();
        var validation = _uploadSecurity.ValidateDocxStructure(documentBytes);
        stopwatch.Stop();

        if (validation.IsValid)
        {
            return new ConversionProbeResult(UploadGateProbe, name, description, ProbeStatus.Passed, stopwatch.ElapsedMilliseconds,
                "Plik przechodzi bramkę uploadu.", null);
        }

        findings.Add(DocumentHealthCodes.UploadGateRejected, StructureIssueSeverity.Error, HealthStage.Conversion,
            $"Aplikacja odrzuci ten plik ({validation.Code})",
            $"Bramka uploadu zwraca: {validation.Error} Dokument nie trafi do edytora ani do „Generuj PDF” — w tej aplikacji konwersja nie zostanie nawet rozpoczęta.",
            null, WordOpenImpact.None, PdfConversionImpact.Blocking,
            "Usuń przyczynę odrzucenia (makra, niedozwolone wpisy, uszkodzone archiwum) i wgraj ponownie.");

        return new ConversionProbeResult(UploadGateProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
            $"{validation.Code}: {validation.Error}", null);
    }

    private async Task<ConversionProbeResult> RunEditorImportAsync(
        byte[] documentBytes,
        HealthFindingCollector findings,
        CancellationToken cancellationToken)
    {
        const string name = "Edytor: import DOCX → HTML";
        const string description = "IDocxToHtmlConverter — ścieżka otwarcia dokumentu w edytorze; „Generuj PDF” startuje z jej wyniku.";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var content = await Task.Run(() =>
            {
                using var stream = new MemoryStream(documentBytes, writable: false);
                return _htmlConverter.Convert(stream);
            }, cancellationToken).WaitAsync(_options.ProbeTimeout, cancellationToken);

            stopwatch.Stop();
            var htmlLength = content.Html?.Length ?? 0;

            if (htmlLength == 0)
            {
                findings.Add(DocumentHealthCodes.EditorImportFailed, StructureIssueSeverity.Warning, HealthStage.Conversion,
                    "Edytor zaimportował pusty dokument",
                    "Konwerter DOCX→HTML zwrócił pusty HTML — treść nie została odczytana (w aplikacji: DOCUMENT_CONTENT_EMPTY).",
                    null, WordOpenImpact.None, PdfConversionImpact.Blocking);

                return new ConversionProbeResult(EditorImportProbe, name, description, ProbeStatus.Warning, stopwatch.ElapsedMilliseconds,
                    "Pusty HTML.", null);
            }

            return new ConversionProbeResult(EditorImportProbe, name, description, ProbeStatus.Passed, stopwatch.ElapsedMilliseconds,
                $"HTML {htmlLength} znaków, {content.Images.Count} obrazów, {content.Footnotes?.Count ?? 0} przypisów, {content.SectionHeadersFooters?.Count ?? 0} sekcji z własnymi nagłówkami.",
                null);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            findings.Add(DocumentHealthCodes.EditorImportFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                "Import do edytora przekroczył limit czasu",
                $"Konwerter DOCX→HTML nie zakończył pracy w {_options.ProbeTimeout.TotalSeconds:0} s. Otwarcie w edytorze i „Generuj PDF” zakończą się tak samo.",
                null, WordOpenImpact.None, PdfConversionImpact.Blocking,
                "Sprawdź rozmiar dokumentu i obrazów; zawieszenie na konkretnej konstrukcji wymaga analizy logów backendu.");

            return new ConversionProbeResult(EditorImportProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"Limit czasu {_options.ProbeTimeout.TotalSeconds:0} s przekroczony (wątek konwertera może nadal pracować).", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogInformation(exception, "Próba importu do edytora zakończona wyjątkiem (wynik diagnostyczny).");
            findings.Add(DocumentHealthCodes.EditorImportFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                "Edytor nie importuje tego dokumentu",
                $"{exception.GetType().Name}: {exception.Message}. Otwarcie w edytorze zakończy się błędem, więc „Generuj PDF” nie ma z czego wystartować.",
                null, WordOpenImpact.None, PdfConversionImpact.Blocking,
                "Komunikat wyjątku wskazuje konstrukcję; porównaj z ustaleniami etapów Pakiet/XML/Struktura.");

            return new ConversionProbeResult(EditorImportProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"{exception.GetType().Name}: {exception.Message}", Describe(exception));
        }
    }

    private async Task<ConversionProbeResult> RunPdfConversionAsync(
        byte[] documentBytes,
        string fileName,
        HealthFindingCollector findings,
        CancellationToken cancellationToken)
    {
        var converterName = _pdfConverter.GetType().Name;
        var isMock = converterName.Contains("Mock", StringComparison.OrdinalIgnoreCase);
        var name = $"Konwerter DOCX → PDF ({converterName})";
        var description = isMock
            ? "Zarejestrowany klient usługi DOCX→PDF to ATRAPA (ADR-0111): składa PDF z tekstu dokumentu i nie odzwierciedla produkcyjnego konwertera. Po podpięciu klienta HTTP ta próba pokaże odpowiedź prawdziwej usługi."
            : "Zarejestrowany klient usługi DOCX→PDF — ten sam, którego używa „Generuj PDF”.";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _pdfConverter.ConvertAsync(documentBytes, fileName, cancellationToken)
                .WaitAsync(_options.ProbeTimeout, cancellationToken);
            stopwatch.Stop();

            var pdf = result.PdfBytes;
            var validPdf = pdf.Length > 8 && Encoding.ASCII.GetString(pdf, 0, 5) == "%PDF-" && HasEofMarker(pdf);
            var pages = validPdf ? PageObjectPattern.Matches(Encoding.Latin1.GetString(pdf)).Count : 0;

            if (!validPdf)
            {
                findings.Add(DocumentHealthCodes.PdfOutputInvalid, StructureIssueSeverity.Warning, HealthStage.Conversion,
                    "Konwerter zwrócił dane niebędące poprawnym PDF",
                    $"Wynik ma {pdf.Length} B, ale nie zaczyna się od %PDF- albo nie kończy znacznikiem %%EOF. Podgląd PDF w aplikacji nie wyrenderuje takiego pliku.",
                    null, WordOpenImpact.None, PdfConversionImpact.Likely);

                return new ConversionProbeResult(PdfConversionProbe, name, description, ProbeStatus.Warning, stopwatch.ElapsedMilliseconds,
                    $"Odpowiedź {pdf.Length} B bez sygnatury PDF.", null);
            }

            return new ConversionProbeResult(PdfConversionProbe, name, description, ProbeStatus.Passed, stopwatch.ElapsedMilliseconds,
                $"PDF {pdf.Length} B, ~{pages} stron, plik '{result.FileName}'.{(isMock ? " (atrapa — nie dowodzi zgodności z produkcyjnym konwerterem)" : string.Empty)}",
                null);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            findings.Add(DocumentHealthCodes.PdfConversionFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                "Konwerter DOCX→PDF przekroczył limit czasu",
                $"Usługa nie odpowiedziała w {_options.ProbeTimeout.TotalSeconds:0} s.",
                null, WordOpenImpact.None, PdfConversionImpact.Blocking);

            return new ConversionProbeResult(PdfConversionProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"Limit czasu {_options.ProbeTimeout.TotalSeconds:0} s przekroczony.", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogInformation(exception, "Próba konwersji DOCX→PDF zakończona wyjątkiem (wynik diagnostyczny).");
            findings.Add(DocumentHealthCodes.PdfConversionFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                "Konwerter DOCX→PDF zwrócił błąd",
                $"{exception.GetType().Name}: {exception.Message}. To dosłowna odpowiedź zarejestrowanego klienta konwersji — „Generuj PDF” zakończy się tym samym błędem.",
                null, WordOpenImpact.None, PdfConversionImpact.Blocking,
                "Porównaj komunikat z ustaleniami etapów powyżej; jeśli usługa nie podaje przyczyny, kieruj się próbą LibreOffice.");

            return new ConversionProbeResult(PdfConversionProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"{exception.GetType().Name}: {exception.Message}", Describe(exception));
        }
    }

    private static bool HasEofMarker(byte[] pdf)
    {
        var tailLength = Math.Min(pdf.Length, 2048);
        var tail = Encoding.ASCII.GetString(pdf, pdf.Length - tailLength, tailLength);

        return tail.Contains("%%EOF", StringComparison.Ordinal);
    }

    private static string Describe(Exception exception)
    {
        var builder = new StringBuilder();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            builder.Append(current.GetType().FullName).Append(": ").AppendLine(current.Message);
        }

        var stack = exception.StackTrace;

        if (!string.IsNullOrEmpty(stack))
        {
            var lines = stack.Split('\n').Take(6);
            builder.AppendLine().AppendLine(string.Join("\n", lines).TrimEnd());
        }

        return builder.ToString().Trim();
    }
}
