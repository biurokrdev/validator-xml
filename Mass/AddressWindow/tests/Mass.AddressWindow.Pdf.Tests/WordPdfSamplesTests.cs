namespace Mass.AddressWindow.Pdf.Tests;

public class WordPdfSamplesTests
{
    private readonly IPdfAddressWindowValidator _validator = new PdfAddressWindowValidator();

    private static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "pdf", name));

    [Fact]
    public void SingleWindowLetter_IsValidInSingleMode_AndLacksSenderInDoubleMode()
    {
        var pdf = Sample("01_C65_jedno_okienko_poprawny.pdf");

        var single = _validator.Validate(pdf, WindowMode.Single);
        var dbl = _validator.Validate(pdf, WindowMode.Double);

        Assert.True(single.IsValid, string.Join("\n", single.Issues));
        Assert.Equal(["Pan Jan Kowalski", "ul. Marszałkowska 142 m. 5", "00-061 Warszawa"], single.For(WindowRole.Recipient)!.Block!.Lines);
        Assert.False(dbl.IsValid);
        Assert.False(dbl.For(WindowRole.Sender)!.Found);
    }

    [Fact]
    public void TwoWindowLetter_IsValidInBothModes()
    {
        var result = _validator.Validate(Sample("02_C65_dwa_okienka_poprawny.pdf"), WindowMode.Double);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(["Urząd Gminy Wólka", "ul. Polna 1", "21-100 Lubartów"], result.For(WindowRole.Sender)!.Block!.Lines);
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
        var result = _validator.Validate(Sample("04_C65_ramka_i_adres_w_tresci.pdf"), WindowMode.Double);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.PositionEstimated);
        Assert.Equal(["Spółdzielnia Mieszkaniowa „Zacisze”", "ul. Lipowa 17", "20-020 Lublin"], result.For(WindowRole.Recipient)!.Block!.Lines);
    }
}
