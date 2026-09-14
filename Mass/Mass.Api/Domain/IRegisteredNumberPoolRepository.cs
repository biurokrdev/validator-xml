namespace Mass.Domain;

/// <summary>Repozytorium puli numerów nadawczych przesyłek poleconych.</summary>
public interface IRegisteredNumberPoolRepository
{
    // ---- generowanie numerów z zakresu

    /// <summary>
    /// Generuje wszystkie R-ki z zakresu i zapisuje je jako dostępne. Numery już obecne w puli
    /// są pomijane, więc ponowny import tej samej rolki jest bezpieczny.
    /// </summary>
    Task<RangeImportResult> ImportRangeAsync(RegisteredNumberRange range, string editor, CancellationToken ct = default);

    /// <summary>
    /// Sprawdza zakres przed importem: które numery już są w puli (i w jakim stanie), a które są nowe.
    /// Nic nie zapisuje.
    /// </summary>
    Task<RangeCheckResult> CheckRangeAsync(RegisteredNumberRange range, int maxExistingListed = 100, CancellationToken ct = default);

    /// <summary>Lista numerów z filtrowaniem i stronicowaniem, posortowana po typie, numerze i prefiksie.</summary>
    Task<PagedResult<RegisteredNumberPool>> ListAsync(RegisteredNumberQuery query, CancellationToken ct = default);

    /// <summary>
    /// Pobiera najniższy dostępny numer danego typu i oznacza go jako zarezerwowany.
    /// Bezpieczne przy równoległych wywołaniach. Rzuca <see cref="RegisteredNumberPoolExhaustedException"/>, gdy pula jest pusta.
    /// </summary>
    Task<RegisteredNumberPool> AcquireNextAsync(RegisteredNumberPoolType type, string editor, CancellationToken ct = default);

    /// <summary>Liczba numerów w poszczególnych stanach dla danego typu.</summary>
    Task<PoolStatistics> GetStatisticsAsync(RegisteredNumberPoolType type, CancellationToken ct = default);

    // ---- walidacja

    /// <summary>Walidacja czysto formalna (format + cyfra kontrolna), bez dostępu do bazy.</summary>
    RegisteredNumberValidation Validate(string? fullNumber);

    /// <summary>Walidacja formalna plus sprawdzenie, czy numer istnieje w puli i w jakim jest stanie.</summary>
    Task<RegisteredNumberValidation> ValidateAsync(string? fullNumber, CancellationToken ct = default);

    // ---- weryfikacja użycia

    /// <summary>Stan użycia numeru: brak w puli, dostępny, zarezerwowany, użyty, anulowany.</summary>
    Task<RegisteredNumberUsageInfo> GetUsageAsync(string? fullNumber, CancellationToken ct = default);

    /// <summary><c>true</c> tylko gdy numer jest w puli i ma stan Użyty.</summary>
    Task<bool> IsUsedAsync(string? fullNumber, CancellationToken ct = default);

    /// <summary><c>true</c> gdy numer jest w puli i można go jeszcze przydzielić (stan Dostępny).</summary>
    Task<bool> IsAvailableAsync(string? fullNumber, CancellationToken ct = default);

    // ---- zmiany stanu konkretnego numeru

    /// <summary>Rezerwuje wskazany numer (Dostępny -> Zarezerwowany).</summary>
    Task<RegisteredNumberPool> ReserveAsync(string fullNumber, string editor, CancellationToken ct = default);

    /// <summary>Oznacza numer jako użyty (z Dostępny lub Zarezerwowany). Rzuca, gdy numeru nie ma w puli.</summary>
    Task<RegisteredNumberPool> MarkUsedAsync(string fullNumber, string editor, CancellationToken ct = default);

    /// <summary>Zwalnia rezerwację (Zarezerwowany -> Dostępny).</summary>
    Task<RegisteredNumberPool> ReleaseAsync(string fullNumber, string editor, CancellationToken ct = default);

    /// <summary>Anuluje numer (np. zniszczona nalepka). Numer użyty nie może być anulowany.</summary>
    Task<RegisteredNumberPool> CancelAsync(string fullNumber, string editor, CancellationToken ct = default);

    Task<RegisteredNumberPool?> FindAsync(string? fullNumber, CancellationToken ct = default);
}

/// <summary>Wynik importu zakresu.</summary>
public sealed record RangeImportResult(int Requested, int Added, int Skipped)
{
    public bool AnythingAdded => Added > 0;
}

/// <summary>Wynik sprawdzenia zakresu przed importem.</summary>
public sealed record RangeCheckResult(
    RegisteredNumberPoolType Type,
    string FirstFullNumber,
    string LastFullNumber,
    int Requested,
    int New,
    int AlreadyExisting,
    IReadOnlyList<RangeCheckExisting> Existing)
{
    /// <summary>Cały zakres jest już w puli - import nic nie doda.</summary>
    public bool IsFullyDuplicated => AlreadyExisting == Requested;

    public bool HasDuplicates => AlreadyExisting > 0;
}

/// <summary>Numer z zakresu, który już jest w puli.</summary>
public sealed record RangeCheckExisting(string FullNumber, RegisteredNumberPoolState State, string Editor, DateTime ChangeDate);

/// <summary>Kryteria listy numerów.</summary>
public sealed record RegisteredNumberQuery(
    RegisteredNumberPoolType? Type = null,
    RegisteredNumberPoolState? State = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 50)
{
    public const int MaxPageSize = 500;

    public int SafePage => Page < 1 ? 1 : Page;
    public int SafePageSize => PageSize < 1 ? 50 : Math.Min(PageSize, MaxPageSize);
}

/// <summary>Strona wyników.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Liczność puli w rozbiciu na stany.</summary>
public sealed record PoolStatistics(RegisteredNumberPoolType Type, int Available, int Reserved, int Used, int Cancelled)
{
    public int Total => Available + Reserved + Used + Cancelled;
}

/// <summary>Wynik walidacji numeru.</summary>
public sealed record RegisteredNumberValidation(
    string? Input,
    bool IsFormatValid,
    string? FullNumber,
    RegisteredNumberPoolType? Type,
    string? Error,
    bool? ExistsInPool = null,
    RegisteredNumberPoolState? State = null)
{
    /// <summary>Numer poprawny formalnie i (jeśli sprawdzano bazę) obecny w puli.</summary>
    public bool IsValid => IsFormatValid && ExistsInPool != false;

    public static RegisteredNumberValidation Invalid(string? input, string error)
        => new(input, false, null, null, error);
}

public enum RegisteredNumberUsage
{
    /// <summary>Numer formalnie niepoprawny (zły format lub cyfra kontrolna).</summary>
    InvalidNumber = 0,

    /// <summary>Numer poprawny, ale nie pochodzi z żadnej zaimportowanej puli.</summary>
    NotInPool = 1,

    Available = 2,
    Reserved = 3,
    Used = 4,
    Cancelled = 5
}

/// <summary>Informacja o użyciu numeru.</summary>
public sealed record RegisteredNumberUsageInfo(
    string? Input,
    string? FullNumber,
    RegisteredNumberUsage Usage,
    string? Editor,
    DateTime? ChangeDate,
    string? Error = null)
{
    public bool IsUsed => Usage == RegisteredNumberUsage.Used;
    public bool IsInPool => Usage >= RegisteredNumberUsage.Available;
}

/// <summary>Rzucany, gdy w puli nie ma już dostępnych numerów danego typu.</summary>
public sealed class RegisteredNumberPoolExhaustedException(RegisteredNumberPoolType type)
    : InvalidOperationException($"Brak dostępnych numerów nadawczych typu {type}. Zaimportuj nowy zakres.")
{
    public RegisteredNumberPoolType Type { get; } = type;
}

/// <summary>Rzucany, gdy numer jest formalnie poprawny, ale nie ma go w puli.</summary>
public sealed class RegisteredNumberNotFoundException(string fullNumber)
    : KeyNotFoundException($"Numer nadania {fullNumber} nie istnieje w puli.")
{
    public string FullNumber { get; } = fullNumber;
}

/// <summary>Rzucany przy niedozwolonym przejściu stanu (np. ponowne użycie numeru już użytego).</summary>
public sealed class RegisteredNumberStateException(string fullNumber, RegisteredNumberPoolState from, RegisteredNumberPoolState to)
    : InvalidOperationException($"Numer {fullNumber}: przejście {from} -> {to} jest niedozwolone.")
{
    public string FullNumber { get; } = fullNumber;
    public RegisteredNumberPoolState From { get; } = from;
    public RegisteredNumberPoolState To { get; } = to;
}
