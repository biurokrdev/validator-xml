namespace Mass.AddressWindow.Domain.Inspection;

/// <summary>Rodzaj koperty, do której trafi pismo. Decyduje, które okna są sprawdzane.</summary>
public enum EnvelopeType
{
    /// <summary>Jedno okienko: sprawdzany tylko adresat.</summary>
    SingleWindow = 1,

    /// <summary>Dwa okienka: adresat i nadawca.</summary>
    DoubleWindow = 2,
}

public enum WindowKind
{
    Recipient,

    Sender,
}
