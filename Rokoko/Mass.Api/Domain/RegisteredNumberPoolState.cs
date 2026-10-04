namespace Mass.Domain;

/// <summary>
/// Stan pojedynczego numeru w puli. Zapisywany w bazie jako liczba (kolumna <c>state</c>).
/// </summary>
public enum RegisteredNumberPoolState
{
    /// <summary>Numer wolny, może zostać przydzielony przesyłce.</summary>
    Available = 0,

    /// <summary>Numer zarezerwowany dla przygotowywanej przesyłki, jeszcze nie nadany.</summary>
    Reserved = 1,

    /// <summary>Numer użyty - przesyłka nadana. Nie wraca do puli.</summary>
    Used = 2,

    /// <summary>Numer wycofany (np. uszkodzona nalepka z rolki). Nie wraca do puli.</summary>
    Cancelled = 3
}
