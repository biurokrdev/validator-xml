namespace Mass.AddressWindow;

public static class EnvelopeLayouts
{
    public const double C65Width = 229;

    public const double C65Height = 114;

    public static EnvelopeWindow C65RecipientWindow { get; } = new(Width: 90, Height: 45, FromRight: 20, FromBottom: 15);

    public static EnvelopeWindow C65SenderWindow { get; } = new(Width: 70, Height: 30, FromLeft: 28, FromBottom: 20);

    public static EnvelopeLayout C65SingleWindow { get; } =
        EnvelopeLayout.FromEnvelope("C65, jedno okienko", C65Width, C65Height, C65RecipientWindow);

    public static EnvelopeLayout C65TwoWindows { get; } =
        EnvelopeLayout.FromEnvelope("C65, dwa okienka", C65Width, C65Height, C65RecipientWindow, C65SenderWindow);

    public static AddressWindowSpec RecipientDin5008B { get; } =
        new("okno adresata (DIN 5008 B)", new RectangleMm(20, 45, 85, 45));

    public static AddressWindowSpec RecipientDin5008A { get; } =
        new("okno adresata (DIN 5008 A)", new RectangleMm(20, 27, 85, 45));

    public static AddressWindowSpec SenderTopLeft { get; } =
        new("okno nadawcy", new RectangleMm(20, 12, 85, 28));

    public static EnvelopeLayout DlSingleWindowDin5008B { get; } =
        new("DL, jedno okienko po lewej (DIN 5008 B)", RecipientDin5008B);

    public static EnvelopeLayout DlSingleWindowDin5008A { get; } =
        new("DL, jedno okienko po lewej (DIN 5008 A)", RecipientDin5008A);

    public static EnvelopeLayout DlTwoWindowsLeft { get; } =
        new("DL, dwa okienka (adresat po lewej)", RecipientDin5008B, SenderTopLeft);
}
