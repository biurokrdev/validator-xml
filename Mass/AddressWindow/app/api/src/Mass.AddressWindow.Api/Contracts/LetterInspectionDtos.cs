using System.ComponentModel.DataAnnotations;
using Mass.AddressWindow.Domain.Inspection;

namespace Mass.AddressWindow.Api.Contracts;

/// <summary>
/// Formularz multipart/form-data: plik PDF i rodzaj koperty. Przy dwóch okienkach w oknie adresata
/// sprawdzany jest adres, a w oknie nadawcy nalepka R listu poleconego (czy jest i czy się mieści).
/// </summary>
public sealed class InspectLetterRequest
{
    [Required(ErrorMessage = "Nie przesłano pliku PDF.")]
    public IFormFile? File { get; set; }

    public EnvelopeType Envelope { get; set; } = EnvelopeType.SingleWindow;

    /// <summary>false pomija renderowanie podglądu strony (szybciej).</summary>
    public bool IncludePreview { get; set; } = true;
}

public sealed record FindingDto(string Code, FindingSeverity Severity, string Message)
{
    public static FindingDto From(Finding f) => new(f.Code, f.Severity, f.Message);
}

public sealed record AddressDto(IReadOnlyList<string> Lines, AreaMm TextBounds, double? MinFontSizePt, double? MaxFontSizePt);

/// <summary>Nalepka R wykryta w oknie: położenie i rozmiar grafiki na stronie.</summary>
public sealed record LabelDto(AreaMm Bounds, bool PositionEstimated);

public sealed record OverflowDto(double LeftMm, double TopMm, double RightMm, double BottomMm, string Description);

public sealed record WindowDto(
    WindowKind Kind,
    string Name,
    bool Found,
    bool IsValid,
    AreaMm Area,
    double ClearanceMm,
    WindowContentKind Content,
    AddressDto? Address,
    LabelDto? Label,
    OverflowDto? Overflow,
    IReadOnlyList<FindingDto> Findings)
{
    public static WindowDto From(WindowInspection w) => new(
        w.Kind,
        w.Name,
        w.Found,
        w.IsValid,
        w.Area,
        w.ClearanceMm,
        w.Content,
        w.Address is { } a ? new AddressDto(a.Lines, a.TextBounds, a.MinFontSizePt, a.MaxFontSizePt) : null,
        w.Label is { } l ? new LabelDto(l.Bounds, l.PositionEstimated) : null,
        w.Overflow.Any
            ? new OverflowDto(w.Overflow.Left, w.Overflow.Top, w.Overflow.Right, w.Overflow.Bottom, w.Overflow.Description)
            : null,
        w.Findings.Select(FindingDto.From).ToList());
}

public sealed record PreviewDto(string DataUri, int WidthPx, int HeightPx, bool RenderedFromPdf);

public sealed record LetterInspectionResponse(
    string FileName,
    EnvelopeType Envelope,
    bool IsValid,
    bool IsReadable,
    string Summary,
    PageInfo? Page,
    IReadOnlyList<WindowDto> Windows,
    IReadOnlyList<FindingDto> DocumentFindings,
    PreviewDto? Preview)
{
    public static LetterInspectionResponse From(LetterInspection i) => new(
        i.FileName,
        i.Envelope,
        i.IsValid,
        i.IsReadable,
        i.Summary,
        i.Page,
        i.Windows.Select(WindowDto.From).ToList(),
        i.DocumentFindings.Select(FindingDto.From).ToList(),
        i.Preview is { } p
            ? new PreviewDto("data:image/png;base64," + Convert.ToBase64String(p.Png), p.WidthPx, p.HeightPx, p.RenderedFromPdf)
            : null);
}
