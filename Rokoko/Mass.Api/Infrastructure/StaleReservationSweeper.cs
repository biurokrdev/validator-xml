using Microsoft.Extensions.Options;

namespace Mass.Infrastructure;

using Mass.Domain;

/// <summary>
/// Sprząta rezerwacje, których nikt nie rozliczył: proces padł między pobraniem numeru a oznaczeniem
/// go jako użyty. Bez tego takie numery wisiałyby w stanie Zarezerwowany na zawsze.
/// </summary>
public sealed class StaleReservationSweeper(
    IServiceScopeFactory scopes,
    IOptions<RegisteredNumberDispenserOptions> options,
    ILogger<StaleReservationSweeper> log) : BackgroundService
{
    private const string Editor = "sweeper";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        using var timer = new PeriodicTimer(o.SweepInterval);

        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IRegisteredNumberPoolRepository>();

                var expired = await repo.ExpireStaleReservationsAsync(
                    DateTime.UtcNow - o.StaleReservationAfter, o.StaleReservationAction, Editor, stoppingToken);

                if (expired > 0)
                    log.LogWarning("Porzucone rezerwacje numerów R: {Count} ({Action}).", expired, o.StaleReservationAction);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // M.in. DbUpdateConcurrencyException, gdy numer rozliczono w trakcie sprzątania - następny obieg go pominie.
                log.LogError(ex, "Sprzątanie porzuconych rezerwacji nie powiodło się.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
