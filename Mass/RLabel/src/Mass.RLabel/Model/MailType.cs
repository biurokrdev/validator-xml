namespace Mass.RLabel;

/// <summary>Rodzaj przesyłki poleconej, a zarazem rodzina numeru nadawczego.</summary>
public enum MailType
{
    /// <summary>Krajowa: numer GS1 SSCC, 20 cyfr (np. <c>00759007731512000621</c>), kod kreskowy GS1-128.</summary>
    Domestic,

    /// <summary>Zagraniczna: numer UPU S10, 13 znaków (np. <c>RR473124829PL</c>), kod kreskowy Code 128.</summary>
    International,
}

/// <summary>Format pliku wynikowego.</summary>
public enum LabelImageFormat
{
    /// <summary>PNG, bezstratny. Zalecany do druku i do kodów kreskowych.</summary>
    Png,

    /// <summary>JPEG. Stratny; przy jakości poniżej ok. 85 krawędzie kresek kodu mogą się rozmywać.</summary>
    Jpeg,
}
