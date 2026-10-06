namespace Mass.AddressWindow.Pdf.Tests;

public class WordPdfSamplesTests
{
    private readonly IPdfAddressWindowValidator _validator = new PdfAddressWindowValidator();

    private static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "pdf", name));

    [Fact]
    public void SingleWindowLetter_IsValidInSingleMode_AndLacksLabelInDoubleMode()
    {
        var pdf = Sample("01_C65_jedno_okienko_poprawny.pdf");

        var single = _validator.Validate(pdf, WindowMode.Single);
        var dbl = _validator.Validate(pdf, WindowMode.Double);

        Assert.True(single.IsValid, string.Join("\n", single.Issues));
        Assert.Equal(["Pan Jan Kowalski", "ul. Marszałkowska 142 m. 5", "00-061 Warszawa"], single.For(WindowRole.Recipient)!.Block!.Lines);
        Assert.False(dbl.IsValid);
        Assert.False(dbl.For(WindowRole.Sender)!.Found);
        Assert.Contains(dbl.For(WindowRole.Sender)!.Issues, i => i.Code == IssueCodes.LabelNotFound);
    }

    [Fact]
    public void TwoWindowLetter_IsValidInBothModes()
    {
        var pdf = Sample("02_C65_dwa_okienka_poprawny.pdf");

        var result = _validator.Validate(pdf, WindowMode.Double);

        Assert.True(_validator.Validate(pdf, WindowMode.Single).IsValid);
        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        // Nalepka R 48×12 mm wstawiona w Wordzie jako grafika w (29,5; 65,5) mm.
        var label = result.For(WindowRole.Sender)!.Label!.Bounds;
        Assert.Equal(29.5, label.Left, 1);
        Assert.Equal(65.5, label.Top, 1);
        Assert.Equal(48, label.Width, 0);
        Assert.Equal(12, label.Height, 0);
        Assert.Equal(4, result.For(WindowRole.Recipient)!.Block!.Lines.Count);
        Assert.True(result.Preview!.RenderedFromPdf);
    }

    [Fact]
    public void FaultyLetter_ReportsSameProblemsAsDocx()
    {
        var result = _validator.Validate(Sample("03_C65_bledy_adresu.pdf"), WindowMode.Double);

        Assert.False(result.IsValid);
        var recipient = result.For(WindowRole.Recipient)!;
        var codes = recipient.Issues.Select(i => i.Code).ToList();
        Assert.Contains(IssueCodes.AddressOutsideWindow, codes);
        Assert.Contains(IssueCodes.TooManyLines, codes);
        Assert.Contains(IssueCodes.LineTooLong, codes);
        Assert.Contains(IssueCodes.PostalCodeLineMissing, codes);
        Assert.Contains(IssueCodes.FontTooSmall, codes);
        Assert.Contains(IssueCodes.DecoratedText, codes);
        Assert.InRange(recipient.Overflow.RightMm, 5, 15);
        Assert.False(result.For(WindowRole.Sender)!.Found);
    }

    [Fact]
    public void FrameAndFlowLetter_HasExactPositionsInPdf()
    {
        var result = _validator.Validate(Sample("04_C65_ramka_i_adres_w_tresci.pdf"), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.PositionEstimated);
        Assert.Equal(["Spółdzielnia Mieszkaniowa „Zacisze”", "ul. Lipowa 17", "20-020 Lublin"], result.For(WindowRole.Recipient)!.Block!.Lines);
    }

    [Fact]
    public void FrameAndFlowLetter_HasSenderAddressWhereLabelShouldBe()
    {
        var result = _validator.Validate(Sample("04_C65_ramka_i_adres_w_tresci.pdf"), WindowMode.Double);

        Assert.False(result.IsValid);
        Assert.True(result.For(WindowRole.Recipient)!.IsValid);
        var labelWindow = result.For(WindowRole.Sender)!;
        Assert.False(labelWindow.Found);
        Assert.Contains("musi być wstawiona jako grafika", Assert.Single(labelWindow.Issues).Message);
    }

    [Fact]
    public void StandardSizeLabel_IsTooLargeForLabelWindow()
    {
        var result = _validator.Validate(Sample("05_C65_nalepka_R_za_duza.pdf"), WindowMode.Double);

        Assert.False(result.IsValid);
        Assert.True(result.For(WindowRole.Recipient)!.IsValid);
        var window = result.For(WindowRole.Sender)!;
        Assert.True(window.Found);
        Assert.Contains(window.Issues, i => i.Code == IssueCodes.LabelTooLarge);
        Assert.Equal(65, window.Label!.Bounds.Width, 0);
        Assert.Equal(25, window.Label.Bounds.Height, 0);
    }
}
