using static Mass.AddressWindow.Tests.TestDocx;

namespace Mass.AddressWindow.Tests;

public class C65LayoutTests
{
    private readonly IAddressWindowValidator _validator = new AddressWindowValidator();

    // Adres nadawcy w oknie nadawcy to tryb opcjonalny; domyślnie jest tam nalepka R (RegisteredLabelWindowTests).
    private static readonly ValidationProfile SenderAddress = new() { SenderWindowContent = WindowContent.Address };

    private static string RecipientBox() => TextBox(117.5, 53.8, 72, 30, Recipient, name: "Adresat");

    private static string SenderBox(IEnumerable<string>? lines = null) =>
        TextBox(26.5, 63.8, 52, 15, lines ?? Sender, 9, name: "Nadawca");

    [Fact]
    public void C65TwoWindows_WindowsComputedFromEnvelopeSpecification()
    {
        var layout = EnvelopeLayouts.C65TwoWindows;

        Assert.Same(layout, ValidationProfile.Default.Layout);
        Assert.Equal(new RectangleMm(119, 54, 71, 30), Rounded(layout.RecipientWindow.Area));
        Assert.Equal(new RectangleMm(28, 64, 51, 15), Rounded(layout.SenderWindow!.Area));
        Assert.Equal(1, layout.RecipientWindow.ClearanceMm);
    }

    [Fact]
    public void C65SingleWindow_HasSameRecipientWindowAsTwoWindowVariant() =>
        Assert.Equal(EnvelopeLayouts.C65TwoWindows.RecipientWindow.Area, EnvelopeLayouts.C65SingleWindow.RecipientWindow.Area);

    [Fact]
    public void FromEnvelope_Centered_ShiftsWindowsByHalfOfPlay()
    {
        var layout = EnvelopeLayout.FromEnvelope(
            "C65 środek", 229, 114, EnvelopeLayouts.C65RecipientWindow, EnvelopeLayouts.C65SenderWindow, LetterPlay.Centered);

        Assert.Equal(new RectangleMm(109.5, 46.5, 90, 45), Rounded(layout.RecipientWindow.Area));
        Assert.Equal(new RectangleMm(18.5, 56.5, 70, 30), Rounded(layout.SenderWindow!.Area));
        Assert.Equal(3, layout.RecipientWindow.ClearanceMm);
    }

    [Fact]
    public void FromEnvelope_RejectsAmbiguousWindowPosition() =>
        Assert.Throws<ArgumentException>(() => EnvelopeLayout.FromEnvelope(
            "zły", 229, 114, new EnvelopeWindow(90, 45, FromLeft: 10, FromRight: 20, FromBottom: 15)));

    [Fact]
    public void FromEnvelope_RejectsWindowNeverFullyVisible() =>
        Assert.Throws<ArgumentException>(() => EnvelopeLayout.FromEnvelope(
            "za mały", 229, 114, new EnvelopeWindow(20, 45, FromRight: 20, FromBottom: 15)));

    [Fact]
    public void Single_AddressInRecipientWindow_IsValid()
    {
        var result = _validator.Validate(Create(RecipientBox()), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(Recipient, result.Windows[0].Block!.Lines);
    }

    [Fact]
    public void AddressVisibleOnlyWhenSheetIsCentered_IsRejected()
    {
        var result = _validator.Validate(Create(TextBox(109.5, 46.5, 82, 40, Recipient)), WindowMode.Single);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == IssueCodes.AddressOutsideWindow);
    }

    [Fact]
    public void AddressOnLeftSide_IsNotFound()
    {
        var result = _validator.Validate(Create(TextBox(21, 47, 82, 40, Recipient)), WindowMode.Single);

        Assert.Contains(result.Errors, e => e.Code == IssueCodes.WindowNotFound);
    }

    [Fact]
    public void Double_BothWindowsFilled_IsValid()
    {
        var result = _validator.Validate(Create(SenderBox() + RecipientBox()), WindowMode.Double, SenderAddress);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(Sender, result.For(WindowRole.Sender)!.Block!.Lines);
        Assert.Equal(Recipient, result.For(WindowRole.Recipient)!.Block!.Lines);
    }

    [Fact]
    public void Double_SenderLineTooWideForSenderWindow_IsReported()
    {
        string[] sender = ["Urząd Gminy Wólka", "ul. Generała Władysława Sikorskiego 1", "21-100 Lubartów"];

        var result = _validator.Validate(Create(SenderBox(sender) + RecipientBox()), WindowMode.Double, SenderAddress);

        Assert.False(result.IsValid);
        Assert.Contains(result.For(WindowRole.Sender)!.Issues, i => i.Code == IssueCodes.LineWraps);
    }

    [Fact]
    public void FlowParagraphsIndentedToRecipientWindow_AreDetected()
    {
        const string indent = "<w:ind w:left=\"5386\"/>";
        var body = Paragraphs([Recipient[0]], pPr: "<w:spacing w:before=\"1701\" w:after=\"0\"/>" + indent)
                   + Paragraphs(Recipient.Skip(1), pPr: indent);

        var result = _validator.Validate(Create(body), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.True(result.Windows[0].Block!.PositionEstimated);
    }

    [Fact]
    public void SenderFrameBesideIndentedRecipientParagraphs_DoesNotPushThem()
    {
        const string indent = "<w:ind w:left=\"5386\"/>";
        var body = Frame(29, 65, 49, 13, Sender, 9)
                   + Paragraphs([Recipient[0]], pPr: "<w:spacing w:before=\"1701\" w:after=\"0\"/>" + indent)
                   + Paragraphs(Recipient.Skip(1), pPr: indent);

        var result = _validator.Validate(Create(body), WindowMode.Double, SenderAddress);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(Recipient, result.For(WindowRole.Recipient)!.Block!.Lines);
    }

    private static RectangleMm Rounded(RectangleMm r) =>
        new(Math.Round(r.Left, 3), Math.Round(r.Top, 3), Math.Round(r.Width, 3), Math.Round(r.Height, 3));
}
