using Mass.AddressWindow.Domain.Inspection;
using Mass.AddressWindow.Domain.Letters;
using Mass.AddressWindow.Pdf;

namespace Mass.AddressWindow.Infrastructure.Inspection;

/// <summary>
/// Adapter biblioteki Mass.AddressWindow.Pdf. Tylko tu typy biblioteki są tłumaczone na model domeny,
/// więc reszta aplikacji nie zależy od jej kształtu.
/// </summary>
internal sealed class PdfAddressWindowInspector(IPdfAddressWindowValidator validator) : IAddressWindowInspector
{
    public LetterInspection Inspect(PdfLetter letter, EnvelopeType envelope, bool includePreview)
    {
        ArgumentNullException.ThrowIfNull(letter);

        var result = validator.Validate(
            letter.Content,
            ToMode(envelope),
            options: new PdfValidationOptions { IncludePreview = includePreview });

        if (!result.IsDocumentReadable)
        {
            return LetterInspection.Unreadable(letter.FileName, envelope, result.Summary, result.Issues.Select(ToFinding).ToArray());
        }

        // Biblioteka zwraca zgłoszenia dokumentu przed zgłoszeniami okien, na jednej liście.
        var documentIssueCount = result.Issues.Count - result.Windows.Sum(w => w.Issues.Count);

        return LetterInspection.Completed(
            letter.FileName,
            envelope,
            result.Summary,
            new PageInfo(result.PageNumber, result.PageCount, Round(result.PageWidthMm), Round(result.PageHeightMm)),
            result.Windows.Select(ToWindow).ToArray(),
            result.Issues.Take(documentIssueCount).Select(ToFinding).ToArray(),
            result.Preview is { } p ? new PagePreview(p.Png, p.WidthPx, p.HeightPx, p.RenderedFromPdf) : null);
    }

    private static WindowMode ToMode(EnvelopeType envelope) => envelope switch
    {
        EnvelopeType.SingleWindow => WindowMode.Single,
        EnvelopeType.DoubleWindow => WindowMode.Double,
        _ => throw new ArgumentOutOfRangeException(nameof(envelope), envelope, "Nieznany rodzaj koperty."),
    };

    private static WindowInspection ToWindow(WindowCheckResult window) => new(
        window.Role == WindowRole.Sender ? WindowKind.Sender : WindowKind.Recipient,
        window.WindowName,
        ToArea(window.WindowArea),
        window.ClearanceMm,
        window.Content == WindowContent.RegisteredLabel ? WindowContentKind.RegisteredLabel : WindowContentKind.Address,
        window.Block is { } block
            ? new AddressBlock(block.Lines.ToArray(), ToArea(block.TextBounds), block.MinFontSizePt, block.MaxFontSizePt)
            : null,
        window.Label is { } label ? new RegisteredLabel(ToArea(label.Bounds), label.PositionEstimated) : null,
        window.Overflow.Any
            ? new OverflowMm(window.Overflow.LeftMm, window.Overflow.TopMm, window.Overflow.RightMm, window.Overflow.BottomMm, window.Overflow.Describe())
            : OverflowMm.None,
        window.Issues.Select(ToFinding).ToArray());

    private static Finding ToFinding(ValidationIssue issue) => new(
        issue.Code,
        issue.Severity switch
        {
            IssueSeverity.Error => FindingSeverity.Error,
            IssueSeverity.Warning => FindingSeverity.Warning,
            _ => FindingSeverity.Info,
        },
        issue.Message);

    private static AreaMm ToArea(RectangleMm r) => new(Round(r.Left), Round(r.Top), Round(r.Width), Round(r.Height));

    private static double Round(double mm) => Math.Round(mm, 1);
}
