using Mass.RLabel;

namespace Mass.AddressWindow.Pdf.Tests;

/// <summary>
/// Werdykt trójstanowy, dane odbiorcy i treść poza właściwym oknem. Układ domyślny C65: okno adresata
/// x 119–190, y 54–84 mm; okno nadawcy (nalepka R) x 28–79, y 64–79 mm.
/// </summary>
public class OutcomeAndRecipientTests
{
    private static readonly string[] Recipient = ["Pan Jan Kowalski", "ul. Marszalkowska 142 m. 5", "00-061 Warszawa"];
    private static readonly string[] Expected = ["Pan Jan Kowalski", "ul. Marszałkowska 142 m. 5", "00-061 Warszawa"];
    private static readonly byte[] Label = new RegisteredLabelRenderer().Render(new RegisteredLabelRequest
    {
        Number = "00759007731512000621",
        Type = MailType.Domestic,
        WidthMm = 48,
        HeightMm = 12,
        Dpi = 600,
    }).Bytes;

    private readonly IPdfAddressWindowValidator _validator = new PdfAddressWindowValidator();

    private static MiniPdf Letter(double recipientLeft = 121, double recipientTop = 56) => new MiniPdf()
        .Text(25, 25, 11, "Lubartow, 28 wrzesnia 2026 r.")
        .Text(recipientLeft, recipientTop, 10, Recipient)
        .Text(25, 100, 11, "Znak sprawy: OR.6220.14.2026", "", "Szanowny Panie,", "tresc pisma w kilku wierszach.");

    private static ValidationProfile Expecting(params string[] lines) => new() { ExpectedRecipient = lines };

    // ------------------------------------------------------------ werdykt

    [Fact]
    public void CorrectLetter_IsValid_WithoutRecipientConfirmation()
    {
        var result = _validator.Validate(Letter().Image(29.5, 65.5, 48, 12, Label).Build(), WindowMode.Double);

        Assert.Equal(ValidationOutcome.Valid, result.Outcome);
        Assert.Equal(RecipientMatch.NotChecked, result.For(WindowRole.Recipient)!.RecipientMatch);
    }

    [Fact]
    public void InvalidLabel_MakesWholeLetterInvalid_EvenWithValidAddress()
    {
        var result = _validator.Validate(Letter().Build(), WindowMode.Double);

        Assert.Equal(ValidationOutcome.Valid, result.For(WindowRole.Recipient)!.Outcome);
        Assert.Equal(ValidationOutcome.Invalid, result.For(WindowRole.Sender)!.Outcome);
        Assert.Equal(ValidationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void UnreadableGraphicInLabelWindow_IsInconclusive_NotInvalid()
    {
        var result = _validator.Validate(Letter().Image(29.5, 65.5, 48, 12).Build(), WindowMode.Double);

        var window = result.For(WindowRole.Sender)!;
        Assert.Equal(ValidationOutcome.Inconclusive, window.Outcome);
        Assert.Contains(window.Issues, i => i.Code == IssueCodes.LabelUnreadable && i.Severity == IssueSeverity.Inconclusive);
        Assert.DoesNotContain(window.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal(ValidationOutcome.Inconclusive, result.Outcome);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ScanWithoutTextLayer_IsInconclusive_WithReason()
    {
        var result = _validator.Validate(new MiniPdf().Image(20, 20, 170, 250).Build(), WindowMode.Double);

        Assert.Equal(ValidationOutcome.Inconclusive, result.Outcome);
        Assert.Empty(result.Windows);
        var reason = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.NoTextLayer, reason.Code);
        Assert.Equal(IssueSeverity.Inconclusive, reason.Severity);
        Assert.Contains("warstwy tekstowej", result.Summary);
    }

    [Fact]
    public void CorruptedFile_IsInconclusive()
    {
        var result = _validator.Validate("%PDF-1.7 nic wiecej"u8.ToArray(), WindowMode.Single);

        Assert.False(result.IsDocumentReadable);
        Assert.Equal(ValidationOutcome.Inconclusive, result.Outcome);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.InvalidDocument && i.Severity == IssueSeverity.Inconclusive);
    }

    // ------------------------------------------------------------ dane odbiorcy

    [Fact]
    public void ExpectedRecipient_Matched_DespiteDiacriticsAndCase()
    {
        var result = _validator.Validate(Letter().Build(), WindowMode.Single, Expecting("PAN JAN KOWALSKI", "ul. Marszałkowska 142 m. 5", "00-061 Warszawa"));

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(RecipientMatch.Matched, result.For(WindowRole.Recipient)!.RecipientMatch);
    }

    [Fact]
    public void ExpectedRecipient_DifferentStreet_IsMismatch()
    {
        var result = _validator.Validate(Letter().Build(), WindowMode.Single, Expecting("Pan Jan Kowalski", "ul. Polna 7", "00-061 Warszawa"));

        var window = result.For(WindowRole.Recipient)!;
        Assert.Equal(RecipientMatch.Mismatch, window.RecipientMatch);
        var issue = Assert.Single(window.Issues, i => i.Code == IssueCodes.AddressMismatch);
        Assert.Contains("ul. Polna 7", issue.Message);
        Assert.Equal(ValidationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void ExpectedRecipient_PrintedOutsideWindow_IsFoundAndReportedOutside()
    {
        // Address printed at the left margin, nothing in the recipient window.
        var result = _validator.Validate(Letter(recipientLeft: 25, recipientTop: 56).Build(), WindowMode.Single, Expecting(Expected));

        var window = result.For(WindowRole.Recipient)!;
        Assert.True(window.Found);
        Assert.Equal(RecipientMatch.Matched, window.RecipientMatch);
        var issue = Assert.Single(window.Issues, i => i.Code == IssueCodes.AddressOutsideWindow);
        Assert.Contains("w całości poza oknem", issue.Message);
        Assert.Equal(ValidationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void ExpectedRecipient_AbsentFromPage_IsAddressNotFound()
    {
        var result = _validator.Validate(Letter().Build(), WindowMode.Single, Expecting("Firma Nowa sp. z o.o.", "ul. Inna 1", "30-001 Krakow"));

        var window = result.For(WindowRole.Recipient)!;
        Assert.False(window.Found);
        Assert.Contains(window.Issues, i => i.Code == IssueCodes.AddressNotFound);
        Assert.Equal(ValidationOutcome.Invalid, result.Outcome);
    }

    // ------------------------------------------------------------ treść w niewłaściwym oknie

    [Fact]
    public void SwappedAddressAndLabel_AreReportedInWrongWindows()
    {
        // Address where the label should be, label where the address should be.
        var pdf = new MiniPdf()
            .Text(30, 66, 9, Recipient)
            .Image(121, 60, 48, 12, Label)
            .Text(25, 100, 11, "Znak sprawy: OR.6220.14.2026", "", "Szanowny Panie,", "tresc pisma w kilku wierszach.")
            .Build();

        var result = _validator.Validate(pdf, WindowMode.Double);

        var address = result.For(WindowRole.Recipient)!;
        Assert.True(address.Found);
        Assert.Contains(address.Issues, i => i.Code == IssueCodes.AddressOutsideWindow && i.Message.Contains("w okno nadawcy, czyli w niewłaściwym oknie"));

        var label = result.For(WindowRole.Sender)!;
        Assert.True(label.Found);
        Assert.Equal("00759007731512000621", label.Label!.Number);
        Assert.Contains(label.Issues, i => i.Code == IssueCodes.LabelOutsideWindow && i.Message.Contains("w okno adresata, czyli w niewłaściwym oknie"));
        Assert.Equal(ValidationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void AddressLikeBlockOutsideWindow_IsReportedOutside_NotAsMissing()
    {
        var result = _validator.Validate(Letter(recipientLeft: 25, recipientTop: 56).Build(), WindowMode.Single);

        var window = result.For(WindowRole.Recipient)!;
        Assert.True(window.Found);
        Assert.Equal(Recipient, window.Block!.Lines);
        Assert.Contains(window.Issues, i => i.Code == IssueCodes.AddressOutsideWindow);
        Assert.DoesNotContain(window.Issues, i => i.Code == IssueCodes.WindowNotFound);
    }

    [Fact]
    public void BodyTextCrossingWindow_IsNotTakenForAddress_WhenRealAddressIsElsewhere()
    {
        var pdf = new MiniPdf()
            .Text(25, 56, 10, Recipient)
            .Text(25, 74, 11, "Szanowny Panie, w nawiazaniu do Pana wniosku uprzejmie informuje, ze sprawa zostala przekazana do rozpatrzenia.")
            .Build();

        var window = _validator.Validate(pdf, WindowMode.Single).For(WindowRole.Recipient)!;

        Assert.Equal(Recipient, window.Block!.Lines);
        Assert.Contains(window.Issues, i => i.Code == IssueCodes.AddressOutsideWindow);
    }
}
