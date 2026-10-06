using Mass.AddressWindow.Pdf.Preview;
using Mass.AddressWindow.Pdf.Text;
using Mass.AddressWindow.Rules;
using UglyToad.PdfPig;

namespace Mass.AddressWindow.Pdf;

public sealed class PdfAddressWindowValidator : IPdfAddressWindowValidator
{
    private readonly ValidationProfile _defaultProfile;

    public PdfAddressWindowValidator(ValidationProfile? defaultProfile = null)
    {
        _defaultProfile = defaultProfile ?? ValidationProfile.Default;
    }

    public PdfAddressWindowValidationResult Validate(byte[] pdf, WindowMode mode, ValidationProfile? profile = null, PdfValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        profile ??= _defaultProfile;
        options ??= PdfValidationOptions.Default;
        profile.EnsureUsableFor(mode);
        options.Validate();

        PdfPageText text;
        int pageCount;
        try
        {
            using var document = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true });
            pageCount = document.NumberOfPages;
            if (options.PageNumber > pageCount)
            {
                return Unreadable(mode, $"Dokument ma {pageCount} stron, a walidacja dotyczy strony {options.PageNumber}.", options);
            }

            text = PdfTextExtractor.Extract(document.GetPage(options.PageNumber));
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            return Unreadable(mode, $"Nie udało się odczytać pliku PDF: {ex.Message}", options);
        }

        var documentIssues = new List<ValidationIssue>();
        if (!text.HasText)
        {
            documentIssues.Add(new ValidationIssue(
                IssueCodes.NoTextLayer, IssueSeverity.Error,
                "Strona nie zawiera warstwy tekstowej (skan albo tekst zamieniony na krzywe). Nie da się sprawdzić położenia adresu."));
        }

        if (!text.Geometry.IsA4Portrait)
        {
            documentIssues.Add(new ValidationIssue(
                IssueCodes.UnexpectedPageFormat, IssueSeverity.Warning,
                $"Strona ma {GeometryRules.Mm(text.Geometry.WidthMm)} × {GeometryRules.Mm(text.Geometry.HeightMm)}, "
                + $"a układ „{profile.Layout.Name}” zakłada A4 w pionie. Położenie okien może się nie zgadzać."));
        }

        var candidates = text.Candidates;
        var claimed = new HashSet<IAddressCandidate>();
        var windows = new List<WindowCheckResult>
        {
            WindowChecker.Check(WindowRole.Recipient, profile.Layout.RecipientWindow, profile.RecipientRules, candidates, claimed),
        };
        if (mode == WindowMode.Double)
        {
            windows.Add(profile.SenderWindowContent == WindowContent.RegisteredLabel
                ? LabelWindowChecker.Check(WindowRole.Sender, profile.Layout.SenderWindow!, text.Images, candidates, claimed)
                : WindowChecker.Check(WindowRole.Sender, profile.Layout.SenderWindow!, profile.SenderRules, candidates, claimed));
        }

        var validation = new AddressWindowValidationResult(mode, isDocumentReadable: true, windows, documentIssues);
        var preview = options.IncludePreview
            ? PreviewRenderer.Render(pdf, options.PageNumber - 1, text, windows, options.PreviewDpi)
            : null;

        return new PdfAddressWindowValidationResult(validation, preview, text.Geometry.WidthMm, text.Geometry.HeightMm, pageCount, options.PageNumber);
    }

    public PdfAddressWindowValidationResult ValidateBase64(string pdfBase64, WindowMode mode, ValidationProfile? profile = null, PdfValidationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfBase64);
        var payload = pdfBase64.AsSpan().Trim();
        var comma = payload.IndexOf(',');
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
        {
            payload = payload[(comma + 1)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload.ToString());
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Podany tekst nie jest poprawnym Base64.", nameof(pdfBase64), ex);
        }

        return Validate(bytes, mode, profile, options);
    }

    public PdfAddressWindowValidationResult Validate(Stream pdf, WindowMode mode, ValidationProfile? profile = null, PdfValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var buffer = new MemoryStream();
        pdf.CopyTo(buffer);
        return Validate(buffer.ToArray(), mode, profile, options);
    }

    public PdfAddressWindowValidationResult ValidateFile(string path, WindowMode mode, ValidationProfile? profile = null, PdfValidationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Validate(File.ReadAllBytes(path), mode, profile, options);
    }

    public async Task<PdfAddressWindowValidationResult> ValidateAsync(
        Stream pdf,
        WindowMode mode,
        ValidationProfile? profile = null,
        PdfValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var buffer = new MemoryStream();
        await pdf.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Validate(buffer.ToArray(), mode, profile, options);
    }

    private static PdfAddressWindowValidationResult Unreadable(WindowMode mode, string reason, PdfValidationOptions options) =>
        new(AddressWindowValidationResult.Unreadable(mode, reason), null, 0, 0, 0, options.PageNumber);
}
