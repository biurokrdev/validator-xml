namespace Mass.AddressWindow.Pdf;

public sealed class ValidationPreview
{
    internal ValidationPreview(byte[] png, int widthPx, int heightPx, int dpi, bool renderedFromPdf)
    {
        Png = png;
        WidthPx = widthPx;
        HeightPx = heightPx;
        Dpi = dpi;
        RenderedFromPdf = renderedFromPdf;
    }

    public byte[] Png { get; }

    public string Base64 => Convert.ToBase64String(Png);

    public string DataUri => "data:image/png;base64," + Base64;

    public string ContentType => "image/png";

    public int WidthPx { get; }

    public int HeightPx { get; }

    public int Dpi { get; }

    public bool RenderedFromPdf { get; }
}

public sealed class PdfAddressWindowValidationResult
{
    internal PdfAddressWindowValidationResult(
        AddressWindowValidationResult validation,
        ValidationPreview? preview,
        double pageWidthMm,
        double pageHeightMm,
        int pageCount,
        int pageNumber)
    {
        Validation = validation;
        Preview = preview;
        PageWidthMm = pageWidthMm;
        PageHeightMm = pageHeightMm;
        PageCount = pageCount;
        PageNumber = pageNumber;
    }

    public AddressWindowValidationResult Validation { get; }

    public ValidationPreview? Preview { get; }

    public double PageWidthMm { get; }

    public double PageHeightMm { get; }

    public int PageCount { get; }

    public int PageNumber { get; }

    public WindowMode Mode => Validation.Mode;

    public bool IsDocumentReadable => Validation.IsDocumentReadable;

    public bool IsValid => Validation.IsValid;

    public IReadOnlyList<WindowCheckResult> Windows => Validation.Windows;

    public IReadOnlyList<ValidationIssue> Issues => Validation.Issues;

    public IEnumerable<ValidationIssue> Errors => Validation.Errors;

    public IEnumerable<ValidationIssue> Warnings => Validation.Warnings;

    public WindowCheckResult? For(WindowRole role) => Validation.For(role);

    public string Summary
    {
        get
        {
            if (!IsDocumentReadable)
            {
                return Issues.FirstOrDefault()?.Message ?? "Nie udało się odczytać pliku PDF.";
            }

            var parts = Windows.Select(w => w.Found
                ? $"{w.WindowName}: {(w.IsValid ? "OK" : "błąd")}{(w.Overflow.Any ? $" (wystaje {w.Overflow.Describe()})" : "")}"
                : $"{w.WindowName}: nie znaleziono adresu");
            return string.Join("; ", parts);
        }
    }
}
