using Mass.RLabel;
using SkiaSharp;

namespace Mass.AddressWindow.Pdf.Tests;

/// <summary>
/// Tryb dwóch okienek, układ domyślny C65: w oknie nadawcy (obszar strony x 28–79, y 64–79 mm, 1 mm odstępu)
/// sprawdzana jest nalepka R, czyli obraz rastrowy na stronie z generatora Mass.RLabel.
/// </summary>
public class RegisteredLabelPdfTests
{
    private const string DomesticNumber = "00759007731512000621";
    private const string InternationalNumber = "RR473124829PL";

    private static readonly string[] Recipient = ["Pan Jan Kowalski", "ul. Marszalkowska 142 m. 5", "00-061 Warszawa"];
    private static readonly RegisteredLabelRenderer Renderer = new();

    private readonly IPdfAddressWindowValidator _validator = new PdfAddressWindowValidator();

    private static byte[] Label(
        string number = DomesticNumber,
        MailType type = MailType.Domestic,
        double widthMm = 48,
        double heightMm = 12,
        bool validateCheckDigit = true,
        LabelImageFormat format = LabelImageFormat.Png) =>
        Renderer.Render(new RegisteredLabelRequest
        {
            Number = number,
            Type = type,
            WidthMm = widthMm,
            HeightMm = heightMm,
            Dpi = 600,
            ValidateCheckDigit = validateCheckDigit,
            Format = format,
        }).Bytes;

    private static MiniPdf Letter() => new MiniPdf()
        .Text(25, 25, 11, "Lubartow, 28 wrzesnia 2026 r.")
        .Text(121, 56, 10, Recipient)
        .Text(25, 100, 11, "Znak sprawy: OR.6220.14.2026", "", "Szanowny Panie,", "tresc pisma w kilku wierszach.");

    private static string[] Errors(WindowCheckResult window) =>
        window.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code).ToArray();

    [Fact]
    public void LabelInsideWindow_IsValid_AndNumberIsRead()
    {
        var result = _validator.Validate(Letter().Image(29.5, 65.5, 48, 12, Label()).Build(), WindowMode.Double);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        var window = result.For(WindowRole.Sender)!;
        Assert.Equal(WindowContent.RegisteredLabel, window.Content);
        Assert.Null(window.Block);
        var bounds = window.Label!.Bounds;
        Assert.Equal(29.5, bounds.Left, 1);
        Assert.Equal(65.5, bounds.Top, 1);
        Assert.Equal(48, bounds.Width, 1);
        Assert.Equal(12, bounds.Height, 1);
        Assert.Equal("okno adresata: OK; okno nadawcy: OK", result.Summary);
        Assert.True(result.Preview!.Png.Length > 0);

        Assert.Equal(DomesticNumber, window.Label.Number);
        Assert.Equal(RegisteredMailType.Domestic, window.Label.Type);
        Assert.Equal("GS1-128", window.Label.Symbology);
        Assert.True(window.Label.NumberIsValid);
        Assert.Equal(1134, window.Label.WidthPx);
    }

    [Fact]
    public void JpegLabel_IsReadThroughDctDecode()
    {
        var result = _validator.Validate(Letter().Jpeg(29.5, 65.5, 48, 12, Label(format: LabelImageFormat.Jpeg)).Build(), WindowMode.Double);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(DomesticNumber, result.For(WindowRole.Sender)!.Label!.Number);
    }

    [Fact]
    public void InternationalLabel_IsReadAsS10Number()
    {
        var pdf = Letter().Image(29.5, 65.5, 48, 12, Label(InternationalNumber, MailType.International)).Build();

        var window = _validator.Validate(pdf, WindowMode.Double).For(WindowRole.Sender)!;

        Assert.True(window.IsValid, string.Join("\n", window.Issues));
        Assert.Equal(InternationalNumber, window.Label!.Number);
        Assert.Equal(RegisteredMailType.International, window.Label.Type);
        Assert.Equal("Code 128", window.Label.Symbology);
    }

    [Fact]
    public void WrongCheckDigit_IsReported()
    {
        var pdf = Letter().Image(29.5, 65.5, 48, 12, Label("00759007731512000622", validateCheckDigit: false)).Build();

        var result = _validator.Validate(pdf, WindowMode.Double);

        Assert.False(result.IsValid);
        var window = result.For(WindowRole.Sender)!;
        Assert.Equal([IssueCodes.LabelNumberInvalid], Errors(window));
        Assert.False(window.Label!.NumberIsValid);
        Assert.Equal("okno adresata: OK; okno nadawcy: błąd", result.Summary);
    }

    [Fact]
    public void NoImageInWindow_IsReportedAsMissingLabel()
    {
        var result = _validator.Validate(Letter().Build(), WindowMode.Double);

        Assert.False(result.IsValid);
        Assert.True(result.For(WindowRole.Recipient)!.IsValid);
        var window = result.For(WindowRole.Sender)!;
        Assert.False(window.Found);
        Assert.Equal([IssueCodes.LabelNotFound], Errors(window));
        Assert.Equal("okno adresata: OK; okno nadawcy: nie znaleziono nalepki R", result.Summary);
    }

    [Fact]
    public void SenderAddressInsteadOfLabel_IsNotAccepted()
    {
        var pdf = Letter().Text(30, 66, 9, "Urzad Gminy Wolka", "ul. Polna 1", "21-100 Lubartow").Build();

        var window = _validator.Validate(pdf, WindowMode.Double).For(WindowRole.Sender)!;

        Assert.False(window.Found);
        Assert.Contains("musi być wstawiona jako grafika", Assert.Single(window.Issues).Message);
    }

    [Fact]
    public void ImageWithoutBarcode_IsUnreadable()
    {
        var window = _validator.Validate(Letter().Image(29.5, 65.5, 48, 12).Build(), WindowMode.Double).For(WindowRole.Sender)!;

        Assert.True(window.Found);
        Assert.Equal(ValidationOutcome.Inconclusive, window.Outcome);
        Assert.Contains(window.Issues, i => i.Code == IssueCodes.LabelUnreadable && i.Severity == IssueSeverity.Inconclusive);
        Assert.Null(window.Label!.Number);
    }

    [Fact]
    public void StandardLabel65x25_IsTooLargeForC65Window()
    {
        var window = _validator.Validate(Letter().Image(21, 59, 65, 25, Label(widthMm: 65, heightMm: 25)).Build(), WindowMode.Double)
            .For(WindowRole.Sender)!;

        Assert.True(window.Found);
        Assert.Equal([IssueCodes.LabelTooLarge], Errors(window));
        Assert.True(window.Overflow.Any);
        Assert.Equal(DomesticNumber, window.Label!.Number);
    }

    [Fact]
    public void LabelShiftedDown_ReportsSideAndDistance()
    {
        var result = _validator.Validate(Letter().Image(29.5, 70, 48, 12, Label()).Build(), WindowMode.Double);

        var window = result.For(WindowRole.Sender)!;
        Assert.Equal([IssueCodes.LabelOutsideWindow], Errors(window));
        Assert.Equal(3, window.Overflow.BottomMm, 1);
        Assert.Contains("wystaje u dołu o 3,0 mm", result.Summary);
    }

    [Fact]
    public void ImageElsewhereOnPage_IsNotTakenForLabel()
    {
        // A picture without a barcode (logo) outside the window is not a label.
        var window = _validator.Validate(Letter().Image(20, 10, 40, 15).Build(), WindowMode.Double)
            .For(WindowRole.Sender)!;

        Assert.False(window.Found);
    }

    [Fact]
    public void LabelDownsampledLikeWordPdfExport_IsStillRead()
    {
        using var original = SKBitmap.Decode(Label());
        using var small = original.Resize(new SKImageInfo(original.Width / 3, original.Height / 3), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var png = small.Encode(SKEncodedImageFormat.Png, 100);

        var window = _validator.Validate(Letter().Image(29.5, 65.5, 48, 12, png.ToArray()).Build(), WindowMode.Double).For(WindowRole.Sender)!;

        Assert.Equal(DomesticNumber, window.Label!.Number);
        Assert.Empty(Errors(window));
        Assert.Contains(window.Issues, i => i.Code == IssueCodes.LabelLowResolution);
    }

    [Fact]
    public void SingleMode_IgnoresLabelWindow()
    {
        var result = _validator.Validate(Letter().Build(), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Null(result.For(WindowRole.Sender));
    }
}
