namespace Mass.AddressWindow.Pdf.Tests;

public class PdfValidatorTests
{
    // Układ domyślny C65: obszar adresata na stronie x 119–190, y 54–84 mm (tekst w x 120–189, y 55–83);
    // obszar nadawcy x 28–79, y 64–79 mm (tekst w x 29–78, y 65–78).
    private static readonly string[] Recipient = ["Pan Jan Kowalski", "ul. Marszalkowska 142 m. 5", "00-061 Warszawa"];
    private static readonly string[] Sender = ["Urzad Gminy Wolka", "ul. Polna 1", "21-100 Lubartow"];

    private readonly IPdfAddressWindowValidator _validator = new PdfAddressWindowValidator();

    private static readonly ValidationProfile SenderAddress = new() { SenderWindowContent = WindowContent.Address };

    private static byte[] Letter(double recipientLeft = 121, double recipientTop = 56, bool withSender = false) =>
        new MiniPdf()
            .Text(25, 25, 11, "Lubartow, 28 wrzesnia 2026 r.")
            .Text(recipientLeft, recipientTop, 10, Recipient)
            .Text(25, 100, 11, "Znak sprawy: OR.6220.14.2026", "", "Szanowny Panie,", "tresc pisma w kilku wierszach.")
            .Text(withSender ? 30 : 500, 66, 9, Sender)
            .Build();

    [Fact]
    public void Single_AddressInsideWindow_IsValidWithPreview()
    {
        var result = _validator.Validate(Letter(), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(210, result.PageWidthMm, 0.5);
        Assert.Equal(297, result.PageHeightMm, 0.5);
        var block = result.For(WindowRole.Recipient)!.Block!;
        Assert.Equal(AddressSourceKind.PdfText, block.Kind);
        Assert.Equal(Recipient, block.Lines);
        Assert.False(block.PositionEstimated);
        Assert.Equal(10, block.MinFontSizePt);
        Assert.False(result.For(WindowRole.Recipient)!.Overflow.Any);

        var preview = Assert.IsType<ValidationPreview>(result.Preview);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], preview.Png[..4]);
        Assert.StartsWith("data:image/png;base64,iVBOR", preview.DataUri);
        Assert.Equal(96, preview.Dpi);
        Assert.InRange(preview.WidthPx, 790, 800);
    }

    [Fact]
    public void AddressShiftedRight_ReportsOverflowInMillimetres()
    {
        var result = _validator.Validate(Letter(recipientLeft: 150), WindowMode.Single);

        Assert.False(result.IsValid);
        var window = result.For(WindowRole.Recipient)!;
        Assert.Contains(window.Issues, i => i.Code == IssueCodes.AddressOutsideWindow);
        Assert.True(window.Overflow.Any);
        Assert.InRange(window.Overflow.RightMm, 3, 10);
        Assert.Equal(0, window.Overflow.LeftMm);
        Assert.Contains("z prawej", result.Summary);
    }

    [Fact]
    public void AddressTooCloseToEdge_IsReported()
    {
        var result = _validator.Validate(Letter(recipientLeft: 119.2), WindowMode.Single);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == IssueCodes.AddressTooCloseToEdge);
        Assert.False(result.For(WindowRole.Recipient)!.Overflow.Any);
    }

    [Fact]
    public void Double_WithSender_IsValid()
    {
        var result = _validator.Validate(Letter(withSender: true), WindowMode.Double, SenderAddress);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(Sender, result.For(WindowRole.Sender)!.Block!.Lines);
    }

    [Fact]
    public void Double_WithoutSender_FailsOnlyForSender()
    {
        var result = _validator.Validate(Letter(), WindowMode.Double);

        Assert.False(result.IsValid);
        Assert.True(result.For(WindowRole.Recipient)!.IsValid);
        Assert.False(result.For(WindowRole.Sender)!.Found);
        Assert.Contains("nie znaleziono", result.Summary);
    }

    [Fact]
    public void PageWithoutText_ReportsNoTextLayer()
    {
        var result = _validator.Validate(new MiniPdf().Build(), WindowMode.Single);

        Assert.False(result.IsValid);
        Assert.True(result.IsDocumentReadable);
        Assert.Contains(result.Errors, e => e.Code == IssueCodes.NoTextLayer);
        Assert.Contains(result.Errors, e => e.Code == IssueCodes.WindowNotFound);
        Assert.NotNull(result.Preview);
    }

    [Fact]
    public void RotatedPage_WithTextDisplayedHorizontally_IsValid()
    {
        // Strona pozioma w przestrzeni użytkownika z /Rotate 90 wyświetla się jako pionowa A4.
        // Punkt wyświetlany (X, Ytop) odpowiada użytkownikowemu (x = Ytop, y = X); tekst musi biec w górę.
        const double ptPerMm = 72 / 25.4;
        var pdf = new MiniPdf(297, 210, rotate: 90);
        for (var i = 0; i < Recipient.Length; i++)
        {
            var displayTop = 56 + i * 4.2 + 2.5;
            pdf.RotatedText(displayTop * ptPerMm, 121 * ptPerMm, 10, Recipient[i]);
        }

        var result = _validator.Validate(pdf.Build(), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(210, result.PageWidthMm, 0.5);
        Assert.Equal(Recipient, result.For(WindowRole.Recipient)!.Block!.Lines);
    }

    [Fact]
    public void RotatedTextOnPortraitPage_IsRejected()
    {
        const double ptPerMm = 72 / 25.4;
        var pdf = new MiniPdf().RotatedText(125 * ptPerMm, (297 - 80) * ptPerMm, 10, "Pan Jan Kowalski 00-061 Warszawa");

        var result = _validator.Validate(pdf.Build(), WindowMode.Single);

        Assert.Contains(result.Errors, e => e.Code == IssueCodes.TextRotated);
    }

    [Fact]
    public void LandscapePage_WarnsAboutFormat()
    {
        var result = _validator.Validate(new MiniPdf(297, 210).Text(121, 56, 10, Recipient).Build(), WindowMode.Single);

        Assert.Contains(result.Warnings, w => w.Code == IssueCodes.UnexpectedPageFormat);
    }

    [Fact]
    public void NotAPdf_IsReportedAsUnreadable()
    {
        var result = _validator.Validate("to nie jest pdf"u8.ToArray(), WindowMode.Single);

        Assert.False(result.IsDocumentReadable);
        Assert.False(result.IsValid);
        Assert.Null(result.Preview);
        Assert.Equal(IssueCodes.InvalidDocument, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void PageNumberBeyondDocument_IsReportedAsUnreadable()
    {
        var result = _validator.Validate(Letter(), WindowMode.Single, options: new PdfValidationOptions { PageNumber = 2 });

        Assert.False(result.IsDocumentReadable);
        Assert.Contains("stron", result.Issues[0].Message);
    }

    [Fact]
    public void Base64_WithAndWithoutDataUriPrefix_IsAccepted()
    {
        var base64 = Convert.ToBase64String(Letter());

        Assert.True(_validator.ValidateBase64(base64, WindowMode.Single).IsValid);
        Assert.True(_validator.ValidateBase64("data:application/pdf;base64," + base64, WindowMode.Single).IsValid);
        Assert.Throws<ArgumentException>(() => _validator.ValidateBase64("%%%nie-base64%%%", WindowMode.Single));
    }

    [Fact]
    public void PreviewCanBeSkipped()
    {
        var result = _validator.Validate(Letter(), WindowMode.Single, options: new PdfValidationOptions { IncludePreview = false });

        Assert.True(result.IsValid);
        Assert.Null(result.Preview);
    }

    [Fact]
    public async Task ValidateAsync_ReadsStream()
    {
        using var stream = new MemoryStream(Letter());

        var result = await _validator.ValidateAsync(stream, WindowMode.Single);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Double_WithLayoutWithoutSenderWindow_Throws() =>
        Assert.Throws<ArgumentException>(() => _validator.Validate(
            Letter(), WindowMode.Double, new ValidationProfile { Layout = EnvelopeLayouts.C65SingleWindow }));

    [Fact]
    public void InvalidOptions_Throw() =>
        Assert.Throws<ArgumentException>(() => _validator.Validate(Letter(), WindowMode.Single, options: new PdfValidationOptions { PreviewDpi = 10 }));
}
