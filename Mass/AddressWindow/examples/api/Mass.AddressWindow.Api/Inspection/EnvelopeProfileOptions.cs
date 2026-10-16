namespace Mass.AddressWindow.Api.Inspection;

// Which envelope geometry the library uses and whether the business has confirmed its dimensions.
// Until Confirmed is true the result must be presented as a demonstration, not as proof of fit.
public sealed class EnvelopeProfileOptions
{
    public const string Section = "Envelope";

    public string Profile { get; set; } = "C65TwoWindows";

    public bool Confirmed { get; set; }

    public string Note { get; set; } =
        "Profil demonstracyjny: wymiary okienek i założenie o składaniu kartki wymagają potwierdzenia biznesowego.";

    public EnvelopeLayout Layout => Profile switch
    {
        "C65TwoWindows" => EnvelopeLayouts.C65TwoWindows,
        "C65SingleWindow" => EnvelopeLayouts.C65SingleWindow,
        "DlTwoWindowsLeft" => EnvelopeLayouts.DlTwoWindowsLeft,
        "DlSingleWindowDin5008B" => EnvelopeLayouts.DlSingleWindowDin5008B,
        "DlSingleWindowDin5008A" => EnvelopeLayouts.DlSingleWindowDin5008A,
        _ => throw new InvalidOperationException($"Nieznany profil koperty w konfiguracji: „{Profile}”."),
    };
}
