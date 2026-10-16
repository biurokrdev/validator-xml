namespace Mass.AddressWindow;

public sealed class ValidationProfile
{
    public EnvelopeLayout Layout { get; init; } = EnvelopeLayouts.C65TwoWindows;

    public AddressContentRules RecipientRules { get; init; } = AddressContentRules.Recipient;

    /// <summary>
    /// Wiersze adresu odbiorcy, który ma być w oknie adresata. Gdy podane, walidator szuka tego adresu na całej
    /// stronie i zgłasza, gdy leży poza oknem albo różni się treścią. Bez nich potwierdza tylko, że w oknie
    /// jest blok przypominający adres.
    /// </summary>
    public IReadOnlyList<string>? ExpectedRecipient { get; init; }

    public AddressContentRules SenderRules { get; init; } = AddressContentRules.Sender;

    /// <summary>
    /// Co jest sprawdzane w oknie nadawcy w trybie <see cref="WindowMode.Double"/>. Domyślnie nalepka R;
    /// <see cref="WindowContent.Address"/> przywraca sprawdzanie adresu nadawcy według <see cref="SenderRules"/>.
    /// </summary>
    public WindowContent SenderWindowContent { get; init; } = WindowContent.RegisteredLabel;

    /// <summary>Co sprawdzać w treści nalepki R (kod kreskowy, numer). Patrz <see cref="RegisteredLabelRules"/>.</summary>
    public RegisteredLabelRules LabelRules { get; init; } = RegisteredLabelRules.Default;

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
    }
}
