namespace Mass.Domain;

/// <summary>
/// Rodzina numeru nadawczego przesyłki poleconej. Wartości są zapisywane w bazie jako liczby
/// (kolumna <c>type</c>) - nie zmieniać przypisanych liczb.
/// </summary>
public enum RegisteredNumberPoolType
{
    /// <summary>Krajowy numer SSCC/GS1, 20 cyfr: (00) + IAC + 5900773 + S1 + numer + cyfra kontrolna.</summary>
    Domestic = 1,

    /// <summary>Zagraniczny numer UPU S10, 13 znaków: RA..RZ + numer + cyfra kontrolna + kod kraju.</summary>
    International = 2
}
