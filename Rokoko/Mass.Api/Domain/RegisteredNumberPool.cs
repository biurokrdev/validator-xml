namespace Mass.Domain;

/// <summary>
/// Pojedynczy numer nadawczy przesyłki poleconej w puli. Mapowany na <c>mass.registered_number_pool</c>.
/// Tworzony wyłącznie przez <see cref="CreateDomestic"/> / <see cref="CreateInternational"/>,
/// żeby części numeru zawsze były spójne z typem (bazę i tak pilnują CHECK-i).
/// </summary>
public sealed class RegisteredNumberPool
{
    private RegisteredNumberPool() { }   // dla EF Core

    public Guid Id { get; private set; }
    public RegisteredNumberPoolType Type { get; private set; }

    /// <summary>8-cyfrowy indywidualny numer przesyłki (bez prefiksów i cyfry kontrolnej).</summary>
    public int Value { get; private set; }

    public RegisteredNumberPoolState State { get; private set; }
    public string Editor { get; private set; } = string.Empty;

    /// <summary>Data ostatniej zmiany (UTC), ustawiana przez encję przy każdym przejściu.</summary>
    public DateTime ChangeDate { get; private set; }

    // Krajowy (SSCC)
    public short? IacDigit { get; private set; }
    public short? KindDigit { get; private set; }

    // Zagraniczny (S10)
    public string? ServiceIndicator { get; private set; }
    public string? CountryCode { get; private set; }

    /// <summary>Pełny numer z cyfrą kontrolną, liczony przez RegisteredNumberFormatter. Unikalny w bazie.</summary>
    public string FullNumber { get; private set; } = string.Empty;

    /// <summary>Token współbieżności mapowany na systemową kolumnę xmin PostgreSQL.</summary>
    public uint RowVersion { get; private set; }

    // ---------------------------------------------------------------- fabryki

    public static RegisteredNumberPool CreateDomestic(short iacDigit, short kindDigit, int serial, string editor)
    {
        if (kindDigit is not (1 or 4))
            throw new ArgumentOutOfRangeException(nameof(kindDigit), "Przesyłka polecona ma S1 = 1 lub 4.");

        var fullNumber = RegisteredNumberFormatter.FormatDomestic(iacDigit, kindDigit, serial);

        return new RegisteredNumberPool
        {
            Id = Guid.NewGuid(),
            Type = RegisteredNumberPoolType.Domestic,
            Value = serial,
            State = RegisteredNumberPoolState.Available,
            Editor = RequireEditor(editor),
            ChangeDate = DateTime.UtcNow,
            IacDigit = iacDigit,
            KindDigit = kindDigit,
            FullNumber = fullNumber
        };
    }

    public static RegisteredNumberPool CreateInternational(string serviceIndicator, int serial, string editor, string countryCode = "PL")
    {
        var fullNumber = RegisteredNumberFormatter.FormatInternational(serviceIndicator, serial, countryCode);

        return new RegisteredNumberPool
        {
            Id = Guid.NewGuid(),
            Type = RegisteredNumberPoolType.International,
            Value = serial,
            State = RegisteredNumberPoolState.Available,
            Editor = RequireEditor(editor),
            ChangeDate = DateTime.UtcNow,
            ServiceIndicator = serviceIndicator,
            CountryCode = countryCode,
            FullNumber = fullNumber
        };
    }

    /// <summary>Tworzy wpis z pełnego numeru (np. odczytanego z nalepki). Zwraca null przy błędnym numerze.</summary>
    public static RegisteredNumberPool? FromFullNumber(string fullNumber, string editor)
    {
        if (!RegisteredNumberFormatter.TryParse(fullNumber, out var p)) return null;

        return p.Type == RegisteredNumberPoolType.Domestic
            ? CreateDomestic((short)p.IacDigit!.Value, (short)p.KindDigit!.Value, p.Serial, editor)
            : CreateInternational(p.ServiceIndicator!, p.Serial, editor, p.CountryCode!);
    }

    // ---------------------------------------------------------------- przejścia stanów

    /// <summary>Dostępny -> Zarezerwowany.</summary>
    public void Reserve(string editor) => Transition(RegisteredNumberPoolState.Reserved, editor, RegisteredNumberPoolState.Available);

    /// <summary>Dostępny lub Zarezerwowany -> Użyty (nalepka mogła zostać naklejona bez wcześniejszej rezerwacji).</summary>
    public void MarkUsed(string editor) => Transition(RegisteredNumberPoolState.Used, editor, RegisteredNumberPoolState.Available, RegisteredNumberPoolState.Reserved);

    /// <summary>Zarezerwowany -> Dostępny (rezerwacja porzucona).</summary>
    public void Release(string editor) => Transition(RegisteredNumberPoolState.Available, editor, RegisteredNumberPoolState.Reserved);

    public void Cancel(string editor)
    {
        if (State == RegisteredNumberPoolState.Used)
            throw new RegisteredNumberStateException(FullNumber, State, RegisteredNumberPoolState.Cancelled);
        State = RegisteredNumberPoolState.Cancelled;
        Editor = RequireEditor(editor);
        ChangeDate = DateTime.UtcNow;
    }

    private void Transition(RegisteredNumberPoolState to, string editor, params RegisteredNumberPoolState[] allowedFrom)
    {
        if (Array.IndexOf(allowedFrom, State) < 0)
            throw new RegisteredNumberStateException(FullNumber, State, to);
        State = to;
        Editor = RequireEditor(editor);
        ChangeDate = DateTime.UtcNow;
    }

    private static string RequireEditor(string editor)
    {
        if (string.IsNullOrWhiteSpace(editor)) throw new ArgumentException("Editor jest wymagany.", nameof(editor));
        if (editor.Length > 256) throw new ArgumentException("Editor ma maksymalnie 256 znaków.", nameof(editor));
        return editor.Trim();
    }
}
