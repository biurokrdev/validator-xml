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

/// <summary>Werdykt dla okna albo całego dokumentu.</summary>
public enum ValidationOutcome
{
    Valid,

    Invalid,

    /// <summary>Dokument nie pozwala wiarygodnie rozstrzygnąć (skan, nieczytelna grafika); powód jest w zgłoszeniach.</summary>
    Inconclusive,
}

/// <summary>Czy wykryty adres porównano z danymi odbiorcy i z jakim skutkiem.</summary>
public enum RecipientMatch
{
    /// <summary>Danych odbiorcy nie podano: wykryto blok przypominający adres, zgodność z odbiorcą nie była sprawdzana.</summary>
    NotChecked,

    Matched,

    Mismatch,
}

public enum WindowRole
{
    Recipient,

    Sender,
}
