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

public sealed record ConversionProbeOutcome(IReadOnlyList<ConversionProbeResult> Probes, byte[]? RoundTripPackage);

public sealed class ConversionProbeRunner
{
    public const string SdkOpenProbe = "sdk-open";
    public const string SchemaProbe = "schema";
    public const string UploadGateProbe = "upload-gate";
    public const string EditorImportProbe = "editor-import";
    public const string RoundTripProbe = "round-trip";
    public const string PdfConversionProbe = "pdf-conversion";

    private static readonly Regex PageObjectPattern = new(@"/Type\s*/Page(?![s\w])", RegexOptions.Compiled);

    private readonly OpenXmlSchemaValidatorRunner _schemaValidator;
    private readonly IFileUploadSecurityService _uploadSecurity;
    private readonly IDocxToHtmlConverter _htmlConverter;
    private readonly IHtmlToDocxConverter _docxWriter;
    private readonly IDocxToPdfConversionService _pdfConverter;
    private readonly LibreOfficeConverterProbe _libreOffice;
    private readonly DocumentHealthOptions _options;
    private readonly ILogger<ConversionProbeRunner> _logger;

    public ConversionProbeRunner(
        OpenXmlSchemaValidatorRunner schemaValidator,
        IFileUploadSecurityService uploadSecurity,
        IDocxToHtmlConverter htmlConverter,
        IHtmlToDocxConverter docxWriter,
        IDocxToPdfConversionService pdfConverter,
        LibreOfficeConverterProbe libreOffice,
        IOptions<DocumentHealthOptions> options,
        ILogger<ConversionProbeRunner> logger)
    {
        _schemaValidator = schemaValidator;
        _uploadSecurity = uploadSecurity;
        _htmlConverter = htmlConverter;
        _docxWriter = docxWriter;
        _pdfConverter = pdfConverter;
        _libreOffice = libreOffice;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ConversionProbeOutcome> RunAsync(
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

        var (schemaProbe, sourceSchema) = RunSchemaValidation(documentBytes, findings, results[0].Status, cancellationToken);
        results.Add(schemaProbe);
        results.Add(RunUploadGate(documentBytes, findings));

        if (!includeConversionProbes)
        {
            return new ConversionProbeOutcome(results, null);
        }

        var (editorProbe, content) = await RunEditorImportAsync(documentBytes, findings, cancellationToken);
        results.Add(editorProbe);

        byte[]? roundTrip = null;

        if (_options.EnableRoundTripProbe)
        {
            var (roundTripProbe, package) = await RunRoundTripAsync(documentBytes, content, findings, cancellationToken);
            results.Add(roundTripProbe);
            roundTrip = package;

            if (package is not null)
            {
                CompareRoundTripSchema(package, sourceSchema, findings, cancellationToken);
            }
        }

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

        return new ConversionProbeOutcome(results, roundTrip);
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

    private (ConversionProbeResult Probe, SchemaValidationResult? Result) RunSchemaValidation(
        byte[] documentBytes,
        HealthFindingCollector findings,
        ProbeStatus sdkStatus,
        CancellationToken cancellationToken)
    {
        const string name = "Open XML SDK: walidacja schematu";
        const string description = "OpenXmlValidator (profil Microsoft 365) — niezgodności ze schematem OOXML; Word wiele z nich toleruje.";

        if (sdkStatus != ProbeStatus.Passed)
        {
            return (new ConversionProbeResult(SchemaProbe, name, description, ProbeStatus.Skipped, 0,
                "Pominięto — SDK nie otworzył pakietu.", null), null);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = _schemaValidator.Validate(documentBytes, "Microsoft365", cancellationToken);
            stopwatch.Stop();

            if (result.TotalCount == 0)
            {
                return (new ConversionProbeResult(SchemaProbe, name, description, ProbeStatus.Passed, stopwatch.ElapsedMilliseconds,
                    "Brak błędów schematu.", null), result);
            }

            var groups = result.Issues
                .GroupBy(issue => (issue.Code, issue.NodeName, issue.Description))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.Description, StringComparer.Ordinal)
                .ToList();

            var details = new StringBuilder();
            details.AppendLine($"Podsumowanie wg przyczyny ({groups.Count} grup, {result.Issues.Count} błędów{(result.TotalCount > result.Issues.Count ? $" z {result.TotalCount} — lista przycięta limitem StructureInspection:MaxSchemaIssues" : string.Empty)}):");

            foreach (var group in groups)
            {
                var first = group.First();
                details.AppendLine($"- {group.Count()}× [{first.Severity}] {group.Key.Code}{(group.Key.NodeName is null ? string.Empty : $" <{group.Key.NodeName}>")}: {group.Key.Description}");
                details.AppendLine($"    np. [{first.PartPath ?? "?"}]{(first.Path is null ? string.Empty : $" {first.Path}")}");
            }

            details.AppendLine();
            details.AppendLine($"Pełna lista ({result.Issues.Count}):");

            foreach (var issue in result.Issues)
            {
                details.AppendLine($"- [{issue.PartPath ?? "?"}] ({issue.Severity}, {issue.Code}{(issue.NodeName is null ? string.Empty : $", <{issue.NodeName}>")}) {issue.Description}{(issue.Path is null ? string.Empty : $" @ {issue.Path}")}");
            }

            var topGroups = string.Join("; ", groups.Take(5).Select(group =>
                $"{group.Count()}× {group.Key.Description}"));

            findings.Add(DocumentHealthCodes.SchemaErrors, StructureIssueSeverity.Info, HealthStage.Conversion,
                "Niezgodności ze schematem Open XML",
                $"OpenXmlValidator zgłosił {result.TotalCount} błędów w {groups.Count} grupach przyczyn. Word ignoruje większość z nich (nieznane atrybuty, kolejność elementów), ale rygorystyczne konwertery mogą odrzucić dokument. Najczęstsze: {topGroups}. Pełna lista (każdy błąd z częścią i ścieżką) w szczegółach próby „{name}” i w notatce dla programisty.",
                null, WordOpenImpact.None, PdfConversionImpact.Possible);

            return (new ConversionProbeResult(SchemaProbe, name, description, ProbeStatus.Warning, stopwatch.ElapsedMilliseconds,
                $"{result.TotalCount} błędów schematu w {groups.Count} grupach przyczyn — pełna lista w szczegółach.", details.ToString().TrimEnd()), result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();

            return (new ConversionProbeResult(SchemaProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"{exception.GetType().Name}: {exception.Message}", Describe(exception)), null);
        }
    }

    private void CompareRoundTripSchema(
        byte[] roundTripPackage,
        SchemaValidationResult? sourceSchema,
        HealthFindingCollector findings,
        CancellationToken cancellationToken)
    {
        SchemaValidationResult output;

        try
        {
            output = _schemaValidator.Validate(roundTripPackage, "Microsoft365", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Walidacja schematu wyniku round-tripu nie powiodła się (wynik diagnostyczny).");
            return;
        }

        if (output.TotalCount == 0)
        {
            return;
        }

        static (string Code, string? Node, string Description) Key(SchemaValidationIssue issue) => (issue.Code, issue.NodeName, issue.Description);

        var sourceCounts = (sourceSchema?.Issues ?? [])
            .GroupBy(Key)
            .ToDictionary(group => group.Key, group => group.Count());

        var introduced = output.Issues
            .GroupBy(Key)
            .Select(group => (group.Key, Count: group.Count(), Before: sourceCounts.GetValueOrDefault(group.Key), Sample: group.First()))
            .Where(group => group.Count > group.Before)
            .OrderByDescending(group => group.Count - group.Before)
            .ToList();

        if (introduced.Count == 0)
        {
            return;
        }

        var lines = introduced.Select(group =>
            $"- {group.Count - group.Before}× nowych ({group.Before} w źródle → {group.Count} po zapisie) {group.Key.Code}{(group.Key.Node is null ? string.Empty : $" <{group.Key.Node}>")}: {group.Key.Description} — np. [{group.Sample.PartPath ?? "?"}]{(group.Sample.Path is null ? string.Empty : $" {group.Sample.Path}")}");

        findings.Add(DocumentHealthCodes.AppRoundTripSchemaErrors, StructureIssueSeverity.Warning, HealthStage.Application,
            "Zapis z edytora wprowadza błędy schematu Open XML",
            $"Wynik round-tripu ma {output.TotalCount} błędów schematu (źródło: {sourceSchema?.TotalCount ?? 0}); {introduced.Count} grup przyczyn pochodzi z naszego writera:\n{string.Join("\n", lines)}",
            introduced[0].Sample.PartPath is null ? null : $"{introduced[0].Sample.PartPath}{(introduced[0].Sample.Path is null ? string.Empty : $" — {introduced[0].Sample.Path}")} (w WYNIKU zapisu)",
            WordOpenImpact.None, PdfConversionImpact.Possible,
            "Popraw HtmlToDocxConverter tak, żeby generował XML zgodny ze schematem (kolejność dzieci wg CT_*); strażnik: GeneratedPackageValidityTests. Word toleruje część takich błędów, rygorystyczne konwertery nie.",
            AppSupportLevel.Unsupported, "Błąd generuje nasz writer — nie dokument użytkownika.");
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

    private async Task<(ConversionProbeResult Probe, DocumentContent? Content)> RunEditorImportAsync(
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

                return (new ConversionProbeResult(EditorImportProbe, name, description, ProbeStatus.Warning, stopwatch.ElapsedMilliseconds,
                    "Pusty HTML.", null), null);
            }

            return (new ConversionProbeResult(EditorImportProbe, name, description, ProbeStatus.Passed, stopwatch.ElapsedMilliseconds,
                $"HTML {htmlLength} znaków, {content.Images.Count} obrazów, {content.Footnotes?.Count ?? 0} przypisów, {content.SectionHeadersFooters?.Count ?? 0} sekcji z własnymi nagłówkami.",
                null), content);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            findings.Add(DocumentHealthCodes.EditorImportFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                "Import do edytora przekroczył limit czasu",
                $"Konwerter DOCX→HTML nie zakończył pracy w {_options.ProbeTimeout.TotalSeconds:0} s. Otwarcie w edytorze i „Generuj PDF” zakończą się tak samo.",
                null, WordOpenImpact.None, PdfConversionImpact.Blocking,
                "Sprawdź rozmiar dokumentu i obrazów; zawieszenie na konkretnej konstrukcji wymaga analizy logów backendu.");

            return (new ConversionProbeResult(EditorImportProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"Limit czasu {_options.ProbeTimeout.TotalSeconds:0} s przekroczony (wątek konwertera może nadal pracować).", null), null);
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

            return (new ConversionProbeResult(EditorImportProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"{exception.GetType().Name}: {exception.Message}", Describe(exception)), null);
        }
    }

    private async Task<(ConversionProbeResult Probe, byte[]? Package)> RunRoundTripAsync(
        byte[] documentBytes,
        DocumentContent? content,
        HealthFindingCollector findings,
        CancellationToken cancellationToken)
    {
        const string name = "Edytor: round-trip DOCX → HTML → DOCX";
        const string description = "IHtmlToDocxConverter.ConvertPreservingPackage — zapis wyniku importu tą samą ścieżką co autosave (pass-through styles/theme/fontTable). Wynik jest ponownie badany: inwentarz konstrukcji i kondycja zapisanego pliku.";

        if (content is null)
        {
            return (new ConversionProbeResult(RoundTripProbe, name, description, ProbeStatus.Skipped, 0,
                "Pominięto — import do edytora nie dał treści.", null), null);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var package = await Task.Run(() =>
            {
                using var original = new MemoryStream(documentBytes, writable: false);
                return Write(content, original);
            }, cancellationToken).WaitAsync(_options.ProbeTimeout, cancellationToken);

            stopwatch.Stop();

            if (package.Length == 0)
            {
                throw new InvalidOperationException("Writer zwrócił pusty pakiet.");
            }

            if (!PassThroughApplied(documentBytes, package))
            {
                findings.Add(DocumentHealthCodes.RoundTripFallback, StructureIssueSeverity.Warning, HealthStage.Conversion,
                    "Pass-through pakietu nie zadziałał — zapis zregenerował style od zera",
                    "Wynik ConvertPreservingPackage nie zawiera oryginalnego styles.xml: PreserveOriginalParts rzucił na tym pakiecie i writer po cichu zwrócił pakiet zregenerowany. W v2 ginie pełny zestaw stylów, theme i fontTable oryginału (R-16) — użytkownik nie dostaje żadnego sygnału.",
                    null, WordOpenImpact.None, PdfConversionImpact.Possible,
                    "Uruchom HtmlToDocxConverter.PreserveOriginalParts na tym pakiecie pod debuggerem (catch połyka wyjątek) i obsłuż jego strukturę; do tego czasu dokument traci style przy każdym autosave.",
                    AppSupportLevel.Partial, "Fallback jest celowy (zapis nigdy nie może się wywalić), ale strata stylów jest cicha.");

                return (new ConversionProbeResult(RoundTripProbe, name, description, ProbeStatus.Warning, stopwatch.ElapsedMilliseconds,
                    $"Pakiet zapisany ({package.Length} B), ale BEZ pass-through — styles.xml oryginału nie przetrwał.", null), package);
            }

            return (new ConversionProbeResult(RoundTripProbe, name, description, ProbeStatus.Passed, stopwatch.ElapsedMilliseconds,
                $"Pakiet po round-tripie: {package.Length} B (źródło {documentBytes.Length} B). Różnice konstrukcji — patrz „Pokrycie przez naszą implementację”.", null), package);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            findings.Add(DocumentHealthCodes.RoundTripFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                "Zapis z edytora przekroczył limit czasu",
                $"Writer HTML→DOCX nie zakończył pracy w {_options.ProbeTimeout.TotalSeconds:0} s — autosave tego dokumentu zakończy się tak samo.",
                null, WordOpenImpact.None, PdfConversionImpact.Possible, null,
                AppSupportLevel.Unsupported, "Zawieszenie writera na tym dokumencie.");

            return (new ConversionProbeResult(RoundTripProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"Limit czasu {_options.ProbeTimeout.TotalSeconds:0} s przekroczony.", null), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogInformation(exception, "Próba round-trip zakończona wyjątkiem (wynik diagnostyczny).");
            findings.Add(DocumentHealthCodes.RoundTripFailed, StructureIssueSeverity.Error, HealthStage.Conversion,
                "Writer edytora nie zapisuje tego dokumentu",
                $"{exception.GetType().Name}: {exception.Message}. Autosave i „Pobierz” zakończą się tym samym błędem — dokument da się otworzyć, ale nie zapisać.",
                null, WordOpenImpact.None, PdfConversionImpact.Possible,
                "Wyjątek pochodzi z HtmlToDocxConverter (szczegóły próby) — konstrukcja HTML z readera, której writer nie przyjmuje.",
                AppSupportLevel.Unsupported, "Nasz writer rzuca na wyniku naszego readera.");

            return (new ConversionProbeResult(RoundTripProbe, name, description, ProbeStatus.Failed, stopwatch.ElapsedMilliseconds,
                $"{exception.GetType().Name}: {exception.Message}", Describe(exception)), null);
        }
    }

    private byte[] Write(DocumentContent content, Stream? original) =>
        _docxWriter.ConvertPreservingPackage(
            content.Html, original, content.Metadata, content.Header, content.Footer,
            content.Margins, content.PageSize, content.SectionHeadersFooters, content.Footnotes, content.Endnotes,
            content.FootnoteNumberFormat, content.EndnoteNumberFormat);

    private static bool PassThroughApplied(byte[] original, byte[] roundTrip)
    {
        try
        {
            var originalStyles = ReadStylesEntries(original);

            if (originalStyles.Count == 0)
            {
                return true;
            }

            var roundTripStyles = ReadStylesEntries(roundTrip);

            return originalStyles.Any(source => roundTripStyles.Any(target => target.AsSpan().SequenceEqual(source)));
        }
        catch
        {
            return true;
        }
    }

    private static List<byte[]> ReadStylesEntries(byte[] package)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(package, writable: false), System.IO.Compression.ZipArchiveMode.Read);
        var result = new List<byte[]>();

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');

            if (!name.StartsWith("word/styles", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result.Add(buffer.ToArray());
        }

        return result;
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
