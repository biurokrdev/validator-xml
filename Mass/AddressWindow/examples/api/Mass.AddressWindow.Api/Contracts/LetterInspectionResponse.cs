using Mass.AddressWindow.Api.Documents;
using Mass.AddressWindow.Pdf;

namespace Mass.AddressWindow.Api.Contracts;

public enum DocumentSource
{
    Upload,

    ExternalMock,

    FormMock,
}

public sealed record FindingDto(string Code, IssueSeverity Severity, string Message)
{
    public static FindingDto From(ValidationIssue issue) => new(issue.Code, issue.Severity, issue.Message);
}

public sealed record AreaDto(double Left, double Top, double Width, double Height)
{
    public static AreaDto From(RectangleMm r) => new(Round(r.Left), Round(r.Top), Round(r.Width), Round(r.Height));

    internal static double Round(double mm) => Math.Round(mm, 1);
}

public sealed record AddressDto(IReadOnlyList<string> Lines, AreaDto TextBounds, double? MinFontSizePt, double? MaxFontSizePt);

public sealed record LabelDto(
    AreaDto Bounds,
    int WidthPx,
    int HeightPx,
    string? Number,
    string? HumanReadableNumber,
    RegisteredMailType? Kind,
    bool NumberIsValid,
    string? Symbology,
    double? BarcodeModulePx);

public sealed record OverflowDto(double LeftMm, double TopMm, double RightMm, double BottomMm, string Description);

// Result for one required element: the recipient address or the R label, each judged on its own.
public sealed record ElementDto(
    ValidationOutcome Status,
    string WindowName,
    AreaDto WindowArea,
    bool Found,
    AddressDto? Address,
    LabelDto? Label,
    OverflowDto? Overflow,
    RecipientMatch RecipientMatch,
    IReadOnlyList<FindingDto> Reasons)
{
    public static ElementDto From(WindowCheckResult w) => new(
        w.Outcome,
        w.WindowName,
        AreaDto.From(w.WindowArea),
        w.Found,
        w.Block is { } b ? new AddressDto(b.Lines, AreaDto.From(b.TextBounds), b.MinFontSizePt, b.MaxFontSizePt) : null,
        w.Label is { } l ? new LabelDto(
            AreaDto.From(l.Bounds), l.WidthPx, l.HeightPx, l.Number, l.HumanReadableNumber, l.Type, l.NumberIsValid, l.Symbology,
            l.BarcodeModulePx is { } m ? Math.Round(m, 1) : null) : null,
        w.Overflow.Any
            ? new OverflowDto(w.Overflow.LeftMm, w.Overflow.TopMm, w.Overflow.RightMm, w.Overflow.BottomMm, w.Overflow.Describe())
            : null,
        w.RecipientMatch,
        w.Issues.Select(FindingDto.From).ToList());

    // A window missing from the result (scan without text) is inconclusive; the reason sits in documentReasons.
    public static ElementDto NotChecked(AddressWindowSpec window) => new(
        ValidationOutcome.Inconclusive, window.Name, AreaDto.From(window.Area), false, null, null, null, RecipientMatch.NotChecked, []);
}

public sealed record DocumentDto(string FileName, DocumentFormat SourceFormat, bool ConvertedToPdf, DocumentSource Source);

public sealed record ProfileDto(string Name, bool Confirmed, string Note);

public sealed record PageDto(int Number, int Count, double WidthMm, double HeightMm);

public sealed record PreviewDto(string DataUri, int WidthPx, int HeightPx, bool RenderedFromPdf);

public sealed record LetterInspectionResponse(
    ValidationOutcome Status,
    string Summary,
    EnvelopeType Envelope,
    DocumentDto Document,
    ProfileDto Profile,
    PageDto? Page,
    ElementDto? Address,
    ElementDto? Label,
    IReadOnlyList<FindingDto> DocumentReasons,
    PreviewDto? Preview)
{
    public static LetterInspectionResponse From(
        PdfAddressWindowValidationResult result, EnvelopeType envelope, EnvelopeLayout layout, ProfileDto profile, DocumentDto document)
    {
        var address = result.For(WindowRole.Recipient) is { } recipient ? ElementDto.From(recipient) : ElementDto.NotChecked(layout.RecipientWindow);
        var label = envelope != EnvelopeType.DoubleWindow ? null
            : result.For(WindowRole.Sender) is { } sender ? ElementDto.From(sender)
            : ElementDto.NotChecked(layout.SenderWindow!);

        return new LetterInspectionResponse(
            result.Outcome,
            result.Summary,
            envelope,
            document,
            profile,
            new PageDto(result.PageNumber, result.PageCount, AreaDto.Round(result.PageWidthMm), AreaDto.Round(result.PageHeightMm)),
            address,
            label,
            result.DocumentIssues.Select(FindingDto.From).ToList(),
            result.Preview is { } p ? new PreviewDto(p.DataUri, p.WidthPx, p.HeightPx, p.RenderedFromPdf) : null);
    }
}
