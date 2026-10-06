namespace Mass.AddressWindow;

public enum WindowMode
{
    Single = 1,

    Double = 2,
}

/// <summary>Co ma być widać w oknie koperty.</summary>
public enum WindowContent
{
    /// <summary>Blok adresowy (tekst): sprawdzane jest położenie i treść adresu.</summary>
    Address,

    /// <summary>
    /// Nalepka „R” listu poleconego (grafika): sprawdzane jest tylko, czy jest i czy mieści się w oknie.
    /// </summary>
    RegisteredLabel,
}

public enum WindowRole
{
    Recipient,

    Sender,
}
