using Microsoft.EntityFrameworkCore;

namespace Mass.Infrastructure;

using Mass.Domain;

/// <summary>
/// Kontekst modułu Mass. Schemat bazy jest w Mass/001_create_registered_number_pool.sql - nie generować migracji.
/// Jeśli w aplikacji jest już własny DbContext, wystarczy dodać do niego <see cref="RegisteredNumbers"/>
/// i <c>ApplyConfiguration(new RegisteredNumberPoolConfiguration())</c>, a repozytorium przyjąć przez ten typ.
/// </summary>
public class MassDbContext(DbContextOptions<MassDbContext> options) : DbContext(options)
{
    public DbSet<RegisteredNumberPool> RegisteredNumbers => Set<RegisteredNumberPool>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new RegisteredNumberPoolConfiguration());
    }
}
