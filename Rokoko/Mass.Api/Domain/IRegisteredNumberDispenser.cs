namespace Mass.Domain;

/// <summary>
/// Wydaje numery R do nadruku. Jedyny punkt, przez który kod nadrukowujący sięga po numer:
/// pobiera kolejny wolny numer, oddaje go do <c>work</c>, a po zakończeniu rozlicza.
/// Bezpieczny przy dowolnej liczbie równoległych wywołań i instancji aplikacji - dwa wywołania
/// nigdy nie dostaną tego samego numeru.
/// </summary>
public interface IRegisteredNumberDispenser
{
    /// <summary>
    /// Rezerwuje kolejny numer danego typu i wykonuje na nim <paramref name="work"/> (nadruk na dokument).
    /// Sukces: numer przechodzi w stan Użyty. Wyjątek z <paramref name="work"/>: rezerwacja jest zwalniana
    /// i wyjątek leci dalej. Rzuca <see cref="RegisteredNumberPoolExhaustedException"/>, gdy pula jest pusta.
    /// </summary>
    Task<TResult> UseNumberAsync<TResult>(
        RegisteredNumberPoolType type,
        string editor,
        Func<string, CancellationToken, Task<TResult>> work,
        CancellationToken ct = default);
}

/// <summary>Co zrobić z rezerwacją, której nikt nie rozliczył (proces padł w trakcie nadruku).</summary>
public enum StaleReservationAction
{
    /// <summary>Numer przepada. Bezpieczne: nie wiadomo, czy nie trafił już na dokument.</summary>
    Cancel = 0,

    /// <summary>Numer wraca do puli. Tylko gdy nadruk nie mógł się udać bez rozliczenia numeru.</summary>
    Release = 1
}

public sealed class RegisteredNumberDispenserOptions
{
    public const string Section = "RegisteredNumbers:Dispenser";

    /// <summary>Po jakim czasie rezerwacja jest uznawana za porzuconą. Musi być dłuższy niż najdłuższy nadruk.</summary>
    public TimeSpan StaleReservationAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Jak często szukać porzuconych rezerwacji.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    public StaleReservationAction StaleReservationAction { get; set; } = StaleReservationAction.Cancel;
}
