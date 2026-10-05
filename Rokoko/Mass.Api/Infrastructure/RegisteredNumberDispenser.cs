using Microsoft.EntityFrameworkCore;

namespace Mass.Infrastructure;

using Mass.Domain;

/// <summary>
/// Singleton. Każda operacja na bazie idzie we własnym zakresie DI (własny DbContext), więc z jednej
/// instancji może korzystać wiele wątków naraz. Nadruk odbywa się poza transakcją: numer jest w tym
/// czasie w stanie Zarezerwowany, a nie pod blokadą wiersza.
/// </summary>
public sealed class RegisteredNumberDispenser(IServiceScopeFactory scopes, ILogger<RegisteredNumberDispenser> log)
    : IRegisteredNumberDispenser
{
    private readonly SemaphoreSlim _acquireGate = new(1, 1);

    public async Task<TResult> UseNumberAsync<TResult>(
        RegisteredNumberPoolType type,
        string editor,
        Func<string, CancellationToken, Task<TResult>> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var number = await AcquireAsync(type, editor, ct);

        TResult result;
        try
        {
            result = await work(number, ct);
        }
        catch
        {
            // Bez tokenu anulowania: numer ma wrócić do puli także wtedy, gdy przerwano właśnie przez anulowanie.
            try
            {
                await WithRepositoryAsync(r => r.ReleaseAsync(number, editor, CancellationToken.None));
            }
            catch (Exception releaseError)
            {
                log.LogError(releaseError, "Nie udało się zwolnić numeru {Number}; zostaje zarezerwowany do czasu sprzątania.", number);
            }

            throw;
        }

        // Nadruk już jest na dokumencie. Jeśli ten zapis padnie, numer zostaje Zarezerwowany i sprzątanie go anuluje.
        await WithRepositoryAsync(r => r.MarkUsedAsync(number, editor, CancellationToken.None));
        return result;
    }

    private async Task<string> AcquireAsync(RegisteredNumberPoolType type, string editor, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRegisteredNumberPoolRepository>();

        // PostgreSQL rozdziela numery sam (FOR UPDATE SKIP LOCKED), także między instancjami aplikacji.
        // Inne providery (InMemory w dev/e2e) nie blokują wierszy, więc w obrębie procesu pobrania idą pojedynczo.
        var gate = !scope.ServiceProvider.GetRequiredService<MassDbContext>().Database.IsNpgsql();
        if (gate) await _acquireGate.WaitAsync(ct);
        try
        {
            return (await repo.AcquireNextAsync(type, editor, ct)).FullNumber;
        }
        finally
        {
            if (gate) _acquireGate.Release();
        }
    }

    private async Task WithRepositoryAsync(Func<IRegisteredNumberPoolRepository, Task> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<IRegisteredNumberPoolRepository>());
    }
}
