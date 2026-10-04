using System.ComponentModel.DataAnnotations;
using Mass.Domain;

namespace Mass.Api.Contracts;

/// <summary>Pojedynczy numer nadania w odpowiedziach API.</summary>
public sealed record RegisteredNumberDto(
    Guid Id,
    string FullNumber,
    RegisteredNumberPoolType Type,
    int Serial,
    RegisteredNumberPoolState State,
    string Editor,
    DateTime ChangeDate,
    short? IacDigit,
    short? KindDigit,
    string? ServiceIndicator,
    string? CountryCode)
{
    public static RegisteredNumberDto From(RegisteredNumberPool e) => new(
        e.Id, e.FullNumber, e.Type, e.Value, e.State, e.Editor, e.ChangeDate,
        e.IacDigit, e.KindDigit, e.ServiceIndicator, e.CountryCode);
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

/// <summary>
/// Zakres do sprawdzenia/importu. Podaj <see cref="FirstNumber"/> oraz <see cref="Count"/> ALBO <see cref="LastNumber"/>.
/// Numery w pełnym zapisie z cyfrą kontrolną, dozwolone spacje i "(00)".
/// </summary>
public sealed record RangeRequest(
    [Required] string FirstNumber,
    [Range(1, RegisteredNumberRange.MaxCount)] int? Count,
    string? LastNumber)
{
    public RegisteredNumberRange ToRange()
    {
        var hasCount = Count is > 0;
        var hasLast = !string.IsNullOrWhiteSpace(LastNumber);

        if (hasCount == hasLast)
            throw new ArgumentException("Podaj dokładnie jedno z: count albo lastNumber.");

        return hasCount
            ? RegisteredNumberRange.FromFirstAndCount(FirstNumber, Count!.Value)
            : RegisteredNumberRange.FromFirstAndLast(FirstNumber, LastNumber!);
    }
}

public sealed record RangeCheckResponse(
    RegisteredNumberPoolType Type,
    string FirstNumber,
    string LastNumber,
    int Requested,
    int New,
    int AlreadyExisting,
    bool HasDuplicates,
    bool IsFullyDuplicated,
    IReadOnlyList<RangeCheckExistingDto> Existing)
{
    public static RangeCheckResponse From(RangeCheckResult r) => new(
        r.Type, r.FirstFullNumber, r.LastFullNumber, r.Requested, r.New, r.AlreadyExisting,
        r.HasDuplicates, r.IsFullyDuplicated,
        r.Existing.Select(e => new RangeCheckExistingDto(e.FullNumber, e.State, e.Editor, e.ChangeDate)).ToList());
}

public sealed record RangeCheckExistingDto(string FullNumber, RegisteredNumberPoolState State, string Editor, DateTime ChangeDate);

public sealed record RangeImportResponse(
    RegisteredNumberPoolType Type,
    string FirstNumber,
    string LastNumber,
    int Requested,
    int Added,
    int Skipped);

public sealed record ValidateRequest([Required] string Number);

public sealed record ValidationResponse(
    string? Input,
    bool IsFormatValid,
    bool IsValid,
    string? FullNumber,
    RegisteredNumberPoolType? Type,
    bool? ExistsInPool,
    RegisteredNumberPoolState? State,
    string? Error)
{
    public static ValidationResponse From(RegisteredNumberValidation v)
        => new(v.Input, v.IsFormatValid, v.IsValid, v.FullNumber, v.Type, v.ExistsInPool, v.State, v.Error);
}

/// <summary>Akcja zmiany stanu konkretnego numeru.</summary>
public enum StateAction
{
    Reserve,
    Use,
    Release,
    Cancel
}

public sealed record ChangeStateRequest([Required] StateAction Action);

public sealed record AcquireRequest([Required] RegisteredNumberPoolType Type);

public sealed record PoolStatisticsDto(RegisteredNumberPoolType Type, int Available, int Reserved, int Used, int Cancelled, int Total)
{
    public static PoolStatisticsDto From(PoolStatistics s) => new(s.Type, s.Available, s.Reserved, s.Used, s.Cancelled, s.Total);
}
