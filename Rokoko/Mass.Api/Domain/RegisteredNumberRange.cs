namespace Mass.Domain;

/// <summary>
/// Zakres numerów nadawczych jednej puli (np. rolka nalepek z Elektronicznego Nadawcy).
/// Wszystkie numery w zakresie mają ten sam typ i te same prefiksy, różnią się tylko
/// 8-cyfrowym numerem seryjnym. Z zakresu generowane są kolejne R-ki.
/// </summary>
public sealed class RegisteredNumberRange
{
    /// <summary>Bezpiecznik przed omyłkowym importem milionów wierszy. Rolka PP to 1000 numerów.</summary>
    public const int MaxCount = 100_000;

    private RegisteredNumberRange() { }

    public RegisteredNumberPoolType Type { get; private init; }
    public int FirstSerial { get; private init; }
    public int LastSerial { get; private init; }
    public int Count => LastSerial - FirstSerial + 1;

    public short? IacDigit { get; private init; }
    public short? KindDigit { get; private init; }
    public string? ServiceIndicator { get; private init; }
    public string? CountryCode { get; private init; }

    public string FirstFullNumber => Format(FirstSerial);
    public string LastFullNumber => Format(LastSerial);

    // ---------------------------------------------------------------- fabryki

    /// <summary>Tak jak w Elektronicznym Nadawcy: pierwszy numer z rolki + ilość nalepek.</summary>
    public static RegisteredNumberRange FromFirstAndCount(string firstFullNumber, int count)
    {
        var first = ParseOrThrow(firstFullNumber, nameof(firstFullNumber));
        return Create(first, first.Serial + count - 1, count);
    }

    /// <summary>Pierwszy i ostatni numer zakresu (oba pełne, z cyfrą kontrolną).</summary>
    public static RegisteredNumberRange FromFirstAndLast(string firstFullNumber, string lastFullNumber)
    {
        var first = ParseOrThrow(firstFullNumber, nameof(firstFullNumber));
        var last = ParseOrThrow(lastFullNumber, nameof(lastFullNumber));

        if (first.Type != last.Type
            || first.IacDigit != last.IacDigit || first.KindDigit != last.KindDigit
            || first.ServiceIndicator != last.ServiceIndicator || first.CountryCode != last.CountryCode)
        {
            throw new ArgumentException(
                $"Numery {first.FullNumber} i {last.FullNumber} należą do różnych pul (inny typ lub prefiks).");
        }

        return Create(first, last.Serial, last.Serial - first.Serial + 1);
    }

    /// <summary>Zakres krajowy z części: IAC, S1 (1 lub 4), pierwszy numer seryjny, ilość.</summary>
    public static RegisteredNumberRange Domestic(short iacDigit, short kindDigit, int firstSerial, int count)
    {
        RegisteredNumberFormatter.TryParse(RegisteredNumberFormatter.FormatDomestic(iacDigit, kindDigit, firstSerial), out var first);
        return Create(first, firstSerial + count - 1, count);
    }

    /// <summary>Zakres zagraniczny z części: wskaźnik usługi (RR), pierwszy numer seryjny, ilość, kraj.</summary>
    public static RegisteredNumberRange International(string serviceIndicator, int firstSerial, int count, string countryCode = "PL")
    {
        RegisteredNumberFormatter.TryParse(RegisteredNumberFormatter.FormatInternational(serviceIndicator, firstSerial, countryCode), out var first);
        return Create(first, firstSerial + count - 1, count);
    }

    // ---------------------------------------------------------------- generowanie

    /// <summary>Pełny numer dla numeru seryjnego z tego zakresu.</summary>
    public string Format(int serial)
    {
        if (!Contains(serial))
            throw new ArgumentOutOfRangeException(nameof(serial), $"Numer {serial} jest poza zakresem {FirstSerial}-{LastSerial}.");

        return Type == RegisteredNumberPoolType.Domestic
            ? RegisteredNumberFormatter.FormatDomestic(IacDigit!.Value, KindDigit!.Value, serial)
            : RegisteredNumberFormatter.FormatInternational(ServiceIndicator!, serial, CountryCode!);
    }

    public bool Contains(int serial) => serial >= FirstSerial && serial <= LastSerial;

    /// <summary>Kolejne numery seryjne zakresu, rosnąco.</summary>
    public IEnumerable<int> Serials() => Enumerable.Range(FirstSerial, Count);

    /// <summary>Kolejne pełne R-ki zakresu, rosnąco.</summary>
    public IEnumerable<string> FullNumbers() => Serials().Select(Format);

    /// <summary>Encja puli dla wskazanego numeru seryjnego z tego zakresu.</summary>
    public RegisteredNumberPool CreateEntity(int serial, string editor)
    {
        if (!Contains(serial))
            throw new ArgumentOutOfRangeException(nameof(serial), $"Numer {serial} jest poza zakresem {FirstSerial}-{LastSerial}.");

        return Type == RegisteredNumberPoolType.Domestic
            ? RegisteredNumberPool.CreateDomestic(IacDigit!.Value, KindDigit!.Value, serial, editor)
            : RegisteredNumberPool.CreateInternational(ServiceIndicator!, serial, editor, CountryCode!);
    }

    public override string ToString() => $"{Type} {FirstFullNumber}..{LastFullNumber} ({Count})";

    // ---------------------------------------------------------------- pomocnicze

    private static RegisteredNumberRange Create(ParsedRegisteredNumber first, int lastSerial, int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "Zakres musi mieć co najmniej jeden numer.");
        if (count > MaxCount)
            throw new ArgumentOutOfRangeException(nameof(count), $"Zakres ma {count} numerów, limit to {MaxCount}.");
        if (lastSerial > RegisteredNumberFormatter.SerialMax)
            throw new ArgumentOutOfRangeException(nameof(count),
                $"Zakres wychodzi poza maksymalny numer seryjny {RegisteredNumberFormatter.SerialMax}.");
        if (first.Type == RegisteredNumberPoolType.Domestic && first.KindDigit is not (1 or 4))
            throw new ArgumentException($"Numer {first.FullNumber} ma S1 = {first.KindDigit}; przesyłka polecona ma 1 lub 4.");

        return new RegisteredNumberRange
        {
            Type = first.Type,
            FirstSerial = first.Serial,
            LastSerial = lastSerial,
            IacDigit = (short?)first.IacDigit,
            KindDigit = (short?)first.KindDigit,
            ServiceIndicator = first.ServiceIndicator,
            CountryCode = first.CountryCode
        };
    }

    private static ParsedRegisteredNumber ParseOrThrow(string fullNumber, string paramName)
    {
        if (!RegisteredNumberFormatter.TryParse(fullNumber, out var parsed, out var error))
            throw new ArgumentException($"Nieprawidłowy numer nadania \"{fullNumber}\": {error}", paramName);
        return parsed;
    }
}
