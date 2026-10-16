namespace Mass.AddressWindow;

public sealed class AddressContentRules
{
    public int MinLines { get; init; } = 3;

    public int MaxLines { get; init; } = 6;

    public int MaxCharactersPerLine { get; init; } = 40;

    public double MinFontSizePt { get; init; } = 8;

    public double MaxFontSizePt { get; init; } = 14;

    public bool RequirePostalCodeLine { get; init; } = true;

    public bool AllowForeignAddress { get; init; } = true;

    public bool WarnOnNonLeftAlignment { get; init; } = true;

    public bool WarnOnItalicOrUnderline { get; init; } = true;

    public static AddressContentRules Recipient { get; } = new();

    public static AddressContentRules Sender { get; } = new() { MinLines = 2, MaxLines = 5 };
}
