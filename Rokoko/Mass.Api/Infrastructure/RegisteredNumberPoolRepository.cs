using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mass.Infrastructure;

using Mass.Domain;

/// <summary>
/// Repozytorium puli R-ek oparte na EF Core + Npgsql.
/// Nie otwiera własnego zakresu transakcji poza <see cref="AcquireNextAsync"/>; jeśli wywołujący
/// prowadzi transakcję na tym samym DbContext, repozytorium w niej pracuje.
/// </summary>
public sealed class RegisteredNumberPoolRepository(MassDbContext db) : IRegisteredNumberPoolRepository
{
    private const int ImportBatchSize = 1_000;

    private readonly MassDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    // =============================================================== generowanie z zakresu

    public async Task<RangeImportResult> ImportRangeAsync(RegisteredNumberRange range, string editor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(range);

        // Numery już obecne w tej puli (ten sam typ i prefiksy) - pomijamy, import jest idempotentny.
        var existing = (await QueryPool(range)
                .Where(x => x.Value >= range.FirstSerial && x.Value <= range.LastSerial)
                .Select(x => x.Value)
                .ToListAsync(ct))
            .ToHashSet();

        var added = 0;
        var batch = new List<RegisteredNumberPool>(ImportBatchSize);

        foreach (var serial in range.Serials())
        {
            if (existing.Contains(serial)) continue;

            batch.Add(range.CreateEntity(serial, editor));

            if (batch.Count == ImportBatchSize)
            {
                added += await FlushAsync(batch, ct);
            }
        }

        added += await FlushAsync(batch, ct);

        return new RangeImportResult(range.Count, added, range.Count - added);
    }

    public async Task<RangeCheckResult> CheckRangeAsync(RegisteredNumberRange range, int maxExistingListed = 100, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(range);

        var inRange = QueryPool(range)
            .Where(x => x.Value >= range.FirstSerial && x.Value <= range.LastSerial);

        var existingCount = await inRange.CountAsync(ct);

        var listed = await inRange
            .OrderBy(x => x.Value)
            .Take(Math.Max(0, maxExistingListed))
            .Select(x => new RangeCheckExisting(x.FullNumber, x.State, x.Editor, x.ChangeDate))
            .ToListAsync(ct);

        return new RangeCheckResult(
            range.Type, range.FirstFullNumber, range.LastFullNumber,
            range.Count, range.Count - existingCount, existingCount, listed);
    }

    public async Task<PagedResult<RegisteredNumberPool>> ListAsync(RegisteredNumberQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<RegisteredNumberPool> q = _db.RegisteredNumbers.AsNoTracking();

        if (query.Type is { } type) q = q.Where(x => x.Type == type);
        if (query.State is { } state) q = q.Where(x => x.State == state);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Szukamy po fragmencie pełnego numeru; zapis "(00) 7 5900773 ..." sprowadzamy do samych znaków.
            var term = query.Search.Trim().ToUpperInvariant().Replace(" ", "").Replace("(00)", "00").Replace("-", "");
            q = q.Where(x => x.FullNumber.Contains(term));
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderBy(x => x.Type).ThenBy(x => x.Value).ThenBy(x => x.FullNumber)
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .ToListAsync(ct);

        return new PagedResult<RegisteredNumberPool>(items, query.SafePage, query.SafePageSize, total);
    }

    public async Task<RegisteredNumberPool> AcquireNextAsync(RegisteredNumberPoolType type, string editor, CancellationToken ct = default)
    {
        // Własna transakcja tylko, gdy wywołujący żadnej nie prowadzi.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        IDbContextTransaction? tx = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            var next = await SelectNextAvailableAsync(type, ct)
                       ?? throw new RegisteredNumberPoolExhaustedException(type);

            next.Reserve(editor);
            await _db.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
            return next;
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    public async Task<PoolStatistics> GetStatisticsAsync(RegisteredNumberPoolType type, CancellationToken ct = default)
    {
        var counts = await _db.RegisteredNumbers
            .Where(x => x.Type == type)
            .GroupBy(x => x.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Of(RegisteredNumberPoolState s) => counts.FirstOrDefault(c => c.State == s)?.Count ?? 0;

        return new PoolStatistics(type,
            Of(RegisteredNumberPoolState.Available),
            Of(RegisteredNumberPoolState.Reserved),
            Of(RegisteredNumberPoolState.Used),
            Of(RegisteredNumberPoolState.Cancelled));
    }

    // =============================================================== walidacja

    public RegisteredNumberValidation Validate(string? fullNumber)
    {
        if (!RegisteredNumberFormatter.TryParse(fullNumber, out var parsed, out var error))
            return RegisteredNumberValidation.Invalid(fullNumber, error!);

        return new RegisteredNumberValidation(fullNumber, true, parsed.FullNumber, parsed.Type, null);
    }

    public async Task<RegisteredNumberValidation> ValidateAsync(string? fullNumber, CancellationToken ct = default)
    {
        var formal = Validate(fullNumber);
        if (!formal.IsFormatValid) return formal;

        var entity = await FindByFullNumberAsync(formal.FullNumber!, track: false, ct);

        return entity is null
            ? formal with { ExistsInPool = false, Error = "Numer jest poprawny, ale nie pochodzi z zaimportowanej puli." }
            : formal with { ExistsInPool = true, State = entity.State };
    }

    // =============================================================== weryfikacja użycia

    public async Task<RegisteredNumberUsageInfo> GetUsageAsync(string? fullNumber, CancellationToken ct = default)
    {
        var formal = Validate(fullNumber);
        if (!formal.IsFormatValid)
            return new RegisteredNumberUsageInfo(fullNumber, null, RegisteredNumberUsage.InvalidNumber, null, null, formal.Error);

        var entity = await FindByFullNumberAsync(formal.FullNumber!, track: false, ct);
        if (entity is null)
            return new RegisteredNumberUsageInfo(fullNumber, formal.FullNumber, RegisteredNumberUsage.NotInPool, null, null);

        return new RegisteredNumberUsageInfo(fullNumber, entity.FullNumber, ToUsage(entity.State), entity.Editor, entity.ChangeDate);
    }

    public async Task<bool> IsUsedAsync(string? fullNumber, CancellationToken ct = default)
        => (await GetUsageAsync(fullNumber, ct)).Usage == RegisteredNumberUsage.Used;

    public async Task<bool> IsAvailableAsync(string? fullNumber, CancellationToken ct = default)
        => (await GetUsageAsync(fullNumber, ct)).Usage == RegisteredNumberUsage.Available;

    // =============================================================== zmiany stanu

    public Task<RegisteredNumberPool> ReserveAsync(string fullNumber, string editor, CancellationToken ct = default)
        => MutateAsync(fullNumber, e => e.Reserve(editor), ct);

    public Task<RegisteredNumberPool> MarkUsedAsync(string fullNumber, string editor, CancellationToken ct = default)
        => MutateAsync(fullNumber, e => e.MarkUsed(editor), ct);

    public Task<RegisteredNumberPool> ReleaseAsync(string fullNumber, string editor, CancellationToken ct = default)
        => MutateAsync(fullNumber, e => e.Release(editor), ct);

    public Task<RegisteredNumberPool> CancelAsync(string fullNumber, string editor, CancellationToken ct = default)
        => MutateAsync(fullNumber, e => e.Cancel(editor), ct);

    public async Task<RegisteredNumberPool?> FindAsync(string? fullNumber, CancellationToken ct = default)
    {
        if (!RegisteredNumberFormatter.TryParse(fullNumber, out var parsed)) return null;
        return await FindByFullNumberAsync(parsed.FullNumber, track: true, ct);
    }

    // =============================================================== pomocnicze

    private async Task<RegisteredNumberPool> MutateAsync(string fullNumber, Action<RegisteredNumberPool> action, CancellationToken ct)
    {
        if (!RegisteredNumberFormatter.TryParse(fullNumber, out var parsed, out var error))
            throw new ArgumentException($"Nieprawidłowy numer nadania \"{fullNumber}\": {error}", nameof(fullNumber));

        var entity = await FindByFullNumberAsync(parsed.FullNumber, track: true, ct)
                     ?? throw new RegisteredNumberNotFoundException(parsed.FullNumber);

        action(entity);
        await _db.SaveChangesAsync(ct);   // DbUpdateConcurrencyException, gdy ktoś zmienił wiersz równolegle (xmin)
        return entity;
    }

    private Task<RegisteredNumberPool?> FindByFullNumberAsync(string fullNumber, bool track, CancellationToken ct)
    {
        IQueryable<RegisteredNumberPool> q = _db.RegisteredNumbers;
        if (!track) q = q.AsNoTracking();
        return q.FirstOrDefaultAsync(x => x.FullNumber == fullNumber, ct);
    }

    /// <summary>Wiersze tej samej puli co zakres: ten sam typ i te same prefiksy (NULL-e porównywane jako równe).</summary>
    private IQueryable<RegisteredNumberPool> QueryPool(RegisteredNumberRange range)
        => _db.RegisteredNumbers.Where(x =>
            x.Type == range.Type
            && x.IacDigit == range.IacDigit
            && x.KindDigit == range.KindDigit
            && x.ServiceIndicator == range.ServiceIndicator
            && x.CountryCode == range.CountryCode);

    private async Task<RegisteredNumberPool?> SelectNextAvailableAsync(RegisteredNumberPoolType type, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
        {
            // FOR UPDATE SKIP LOCKED: równoległe instancje dostają różne wiersze zamiast czekać lub kolidować.
            // Zapytanie nie jest komponowane dalej, więc EF wysyła je dosłownie.
            var rows = await _db.RegisteredNumbers
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM   mass.registered_number_pool
                    WHERE  type = {(int)type} AND state = {(int)RegisteredNumberPoolState.Available}
                    ORDER  BY value, full_number
                    LIMIT  1
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            return rows.FirstOrDefault();
        }

        // Inne providery (testy InMemory/SQLite): bez blokady wiersza, kolizje wykrywa token współbieżności.
        return await _db.RegisteredNumbers
            .Where(x => x.Type == type && x.State == RegisteredNumberPoolState.Available)
            .OrderBy(x => x.Value).ThenBy(x => x.FullNumber)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<int> FlushAsync(List<RegisteredNumberPool> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return 0;

        _db.RegisteredNumbers.AddRange(batch);
        var saved = await _db.SaveChangesAsync(ct);

        // Zapisane encje nie są już potrzebne w trackerze - import może liczyć dziesiątki tysięcy wierszy.
        foreach (var e in batch) _db.Entry(e).State = EntityState.Detached;
        batch.Clear();

        return saved;
    }

    private static RegisteredNumberUsage ToUsage(RegisteredNumberPoolState state) => state switch
    {
        RegisteredNumberPoolState.Available => RegisteredNumberUsage.Available,
        RegisteredNumberPoolState.Reserved => RegisteredNumberUsage.Reserved,
        RegisteredNumberPoolState.Used => RegisteredNumberUsage.Used,
        RegisteredNumberPoolState.Cancelled => RegisteredNumberUsage.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Nieznany stan numeru.")
    };
}
