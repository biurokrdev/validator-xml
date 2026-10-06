using static Mass.AddressWindow.Tests.TestDocx;

namespace Mass.AddressWindow.Tests;

/// <summary>
/// Tryb dwóch okienek, układ domyślny C65: w oknie nadawcy (obszar strony x 28–79, y 64–79 mm, 1 mm odstępu)
/// sprawdzana jest nalepka R, czyli grafika. Mieści się w nim najwyżej 49×13 mm.
/// </summary>
public class RegisteredLabelWindowTests
{
    private readonly IAddressWindowValidator _validator = new AddressWindowValidator();

    private static string RecipientBox() => TextBox(117.5, 53.8, 72, 30, Recipient, name: "Adresat");

    private WindowCheckResult LabelWindow(string body) =>
        _validator.Validate(Create(body + RecipientBox()), WindowMode.Double).For(WindowRole.Sender)!;

    private static string[] Errors(WindowCheckResult window) =>
        window.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code).ToArray();

    [Fact]
    public void LabelInsideWindow_IsValid()
    {
        var result = _validator.Validate(Create(Picture(29.5, 65.5, 48, 12) + RecipientBox()), WindowMode.Double);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        var window = result.For(WindowRole.Sender)!;
        Assert.Equal(WindowContent.RegisteredLabel, window.Content);
        Assert.True(window.Found);
        Assert.Null(window.Block);
        Assert.Equal(new RectangleMm(29.5, 65.5, 48, 12), window.Label!.Bounds);
        Assert.Equal("NalepkaR", window.Label.Name);
        Assert.False(window.Label.PositionEstimated);
        Assert.False(window.Overflow.Any);
        Assert.Equal(WindowContent.Address, result.For(WindowRole.Recipient)!.Content);
    }

    [Fact]
    public void NoGraphicInWindow_IsReportedAsMissingLabel()
    {
        var window = LabelWindow("");

        Assert.False(window.Found);
        Assert.False(window.IsValid);
        Assert.Equal([IssueCodes.LabelNotFound], Errors(window));
    }

    [Fact]
    public void SenderAddressInsteadOfLabel_IsNotAccepted()
    {
        var window = LabelWindow(TextBox(26.5, 63.8, 52, 15, Sender, 9, name: "Nadawca"));

        Assert.False(window.Found);
        var error = Assert.Single(window.Issues, i => i.Code == IssueCodes.LabelNotFound);
        Assert.Contains("musi być wstawiona jako grafika", error.Message);
    }

    [Fact]
    public void GraphicOutsideWindow_IsNotTakenForLabel()
    {
        // Logo w lewym górnym rogu, poza oknem.
        var window = LabelWindow(Picture(20, 10, 40, 15, "Logo"));

        Assert.False(window.Found);
        Assert.Equal([IssueCodes.LabelNotFound], Errors(window));
    }

    [Fact]
    public void StandardLabel65x25_IsTooLargeForC65Window()
    {
        var window = LabelWindow(Picture(21, 59, 65, 25));

        Assert.True(window.Found);
        Assert.False(window.IsValid);
        Assert.Equal([IssueCodes.LabelTooLarge], Errors(window));
        Assert.Contains("65,0 × 25,0 mm", window.Issues[0].Message);
        Assert.Contains("49,0 × 13,0 mm", window.Issues[0].Message);
        Assert.True(window.Overflow.Any);
    }

    [Fact]
    public void LabelShiftedRight_ReportsSideAndDistance()
    {
        var window = LabelWindow(Picture(35, 65.5, 48, 12));

        Assert.Equal([IssueCodes.LabelOutsideWindow], Errors(window));
        Assert.Equal(4, window.Overflow.RightMm);
        Assert.Equal(0, window.Overflow.LeftMm);
        Assert.Contains("z prawej o 4,0 mm", window.Issues[0].Message);
    }

    [Fact]
    public void LabelTouchingWindowEdge_IsTooCloseToEdge()
    {
        var window = LabelWindow(Picture(28.2, 65.5, 48, 12));

        Assert.Equal([IssueCodes.LabelTooCloseToEdge], Errors(window));
        Assert.False(window.Overflow.Any);
    }

    [Fact]
    public void LabelInsideGroup_IsPositionedWithinGroup()
    {
        var window = LabelWindow(GroupedPicture(25, 60, 60, 25, dx: 4.5, dy: 5.5, w: 48, h: 12));

        Assert.True(window.IsValid, string.Join("\n", window.Issues));
        Assert.Equal(new RectangleMm(29.5, 65.5, 48, 12), Rounded(window.Label!.Bounds));
    }

    [Fact]
    public void InlineLabel_IsFoundWithEstimatedPosition()
    {
        // Strona testowa ma marginesy 25 mm; akapit z grafiką zaczyna się 41 mm niżej, wcięty o 5 mm.
        var body = InlinePicture(40, 10, $"<w:spacing w:before=\"{Twips(41)}\" w:after=\"0\"/><w:ind w:left=\"{Twips(5)}\"/>");

        var window = LabelWindow(body);

        Assert.True(window.Found);
        Assert.True(window.Label!.PositionEstimated);
        Assert.Contains(window.Issues, i => i.Code == IssueCodes.PositionEstimated && i.Severity == IssueSeverity.Info);
    }

    [Fact]
    public void TextNextToLabel_IsWarning()
    {
        var window = LabelWindow(Picture(29.5, 65.5, 30, 12) + TextBox(60, 64, 18, 12, ["nr 12/2026"], 8, name: "Dopisek"));

        Assert.True(window.IsValid, string.Join("\n", window.Issues));
        var warning = Assert.Single(window.Issues, i => i.Severity == IssueSeverity.Warning);
        Assert.Equal(IssueCodes.OtherTextInWindow, warning.Code);
    }

    [Fact]
    public void SingleMode_DoesNotLookForLabel()
    {
        var result = _validator.Validate(Create(RecipientBox()), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Null(result.For(WindowRole.Sender));
    }

    [Fact]
    public void SenderWindowContentAddress_RestoresSenderAddressCheck()
    {
        var profile = new ValidationProfile { SenderWindowContent = WindowContent.Address };
        var body = TextBox(26.5, 63.8, 52, 15, Sender, 9, name: "Nadawca") + RecipientBox();

        var result = _validator.Validate(Create(body), WindowMode.Double, profile);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        var window = result.For(WindowRole.Sender)!;
        Assert.Equal(WindowContent.Address, window.Content);
        Assert.Equal(Sender, window.Block!.Lines);
        Assert.Null(window.Label);
    }

    private static RectangleMm Rounded(RectangleMm r) =>
        new(Math.Round(r.Left, 1), Math.Round(r.Top, 1), Math.Round(r.Width, 1), Math.Round(r.Height, 1));
}
