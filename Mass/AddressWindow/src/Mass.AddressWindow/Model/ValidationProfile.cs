namespace Mass.AddressWindow;

public sealed class ValidationProfile
{
    public EnvelopeLayout Layout { get; init; } = EnvelopeLayouts.C65TwoWindows;

    public AddressContentRules RecipientRules { get; init; } = AddressContentRules.Recipient;

    public AddressContentRules SenderRules { get; init; } = AddressContentRules.Sender;

    /// <summary>
    /// Co jest sprawdzane w oknie nadawcy w trybie <see cref="WindowMode.Double"/>. Domyślnie nalepka R:
    /// czy jest i czy mieści się w oknie. <see cref="WindowContent.Address"/> przywraca sprawdzanie
    /// adresu nadawcy według <see cref="SenderRules"/>.
    /// </summary>
    public WindowContent SenderWindowContent { get; init; } = WindowContent.RegisteredLabel;

    public double AverageCharacterWidthEm { get; init; } = 0.55;

    public double LineHeightFactor { get; init; } = 1.17;

    public static ValidationProfile Default { get; } = new();

    internal void EnsureUsableFor(WindowMode mode)
    {
        if (Layout is null)
        {
            throw new ArgumentException("Profil nie ma układu koperty (Layout).");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Nieznany tryb okienek.");
        }

        if (mode == WindowMode.Double && !Layout.SupportsDoubleWindow)
        {
            throw new ArgumentException(
                $"Układ koperty „{Layout.Name}” nie definiuje okna nadawcy, więc nie obsługuje trybu dwóch okienek.",
                nameof(mode));
        }

        if (AverageCharacterWidthEm <= 0 || LineHeightFactor <= 0)
        {
            throw new ArgumentException("AverageCharacterWidthEm i LineHeightFactor muszą być dodatnie.");
        }
    }
}
