namespace Mass.AddressWindow.Pdf;

public sealed class PdfValidationOptions
{
    public static PdfValidationOptions Default { get; } = new();

    public int PageNumber { get; init; } = 1;

    public bool IncludePreview { get; init; } = true;

    public int PreviewDpi { get; init; } = 96;

    internal void Validate()
    {
        if (PageNumber < 1)
        {
            throw new ArgumentException("Numer strony musi być co najmniej 1.", nameof(PageNumber));
        }

        if (PreviewDpi is < 36 or > 600)
        {
            throw new ArgumentException($"Rozdzielczość podglądu musi być w zakresie 36–600 DPI, podano {PreviewDpi}.", nameof(PreviewDpi));
        }
    }
}
