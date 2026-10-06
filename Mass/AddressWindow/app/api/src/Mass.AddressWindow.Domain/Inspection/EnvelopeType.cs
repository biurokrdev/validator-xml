namespace Mass.AddressWindow.Domain.Inspection;

/// <summary>Rodzaj koperty, do której trafi pismo. Decyduje, które okna są sprawdzane.</summary>
public enum EnvelopeType
{
    /// <summary>Jedno okienko: sprawdzany tylko adresat.</summary>
    SingleWindow = 1,

    /// <summary>Dwa okienka: adresat i okno nadawcy, w którym ma być nalepka R listu poleconego.</summary>
    DoubleWindow = 2,
}

public enum WindowKind
{
    Recipient,

    Sender,
}

/// <summary>Czego szukamy w oknie.</summary>
public enum WindowContentKind
{
    /// <summary>Adres: sprawdzane jest położenie i treść.</summary>
    Address,

    /// <summary>Nalepka R (grafika): sprawdzane jest tylko, czy jest i czy mieści się w oknie.</summary>
    RegisteredLabel,
}
