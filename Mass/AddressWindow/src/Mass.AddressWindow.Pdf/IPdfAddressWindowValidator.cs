namespace Mass.AddressWindow.Pdf;

public interface IPdfAddressWindowValidator
{
    PdfAddressWindowValidationResult Validate(byte[] pdf, WindowMode mode, ValidationProfile? profile = null, PdfValidationOptions? options = null);

    PdfAddressWindowValidationResult ValidateBase64(string pdfBase64, WindowMode mode, ValidationProfile? profile = null, PdfValidationOptions? options = null);

    PdfAddressWindowValidationResult Validate(Stream pdf, WindowMode mode, ValidationProfile? profile = null, PdfValidationOptions? options = null);

    PdfAddressWindowValidationResult ValidateFile(string path, WindowMode mode, ValidationProfile? profile = null, PdfValidationOptions? options = null);

    Task<PdfAddressWindowValidationResult> ValidateAsync(
        Stream pdf,
        WindowMode mode,
        ValidationProfile? profile = null,
        PdfValidationOptions? options = null,
        CancellationToken cancellationToken = default);
}
