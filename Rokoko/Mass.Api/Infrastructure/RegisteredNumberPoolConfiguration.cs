using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mass.Infrastructure;

using Mass.Domain;

/// <summary>
/// Mapowanie EF Core (Npgsql) na tabelę <c>mass.registered_number_pool</c>.
/// Schemat jest utrzymywany w SQL - nie generować migracji.
/// </summary>
public sealed class RegisteredNumberPoolConfiguration : IEntityTypeConfiguration<RegisteredNumberPool>
{
    public void Configure(EntityTypeBuilder<RegisteredNumberPool> b)
    {
        b.ToTable("registered_number_pool", "mass");

        b.HasKey(x => x.Id).HasName("pk_registered_number_pool");
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();

        b.Property(x => x.Type)
            .HasColumnName("type")
            .HasConversion<int>()
            .IsRequired();

        b.Property(x => x.Value)
            .HasColumnName("value")
            .IsRequired();

        b.Property(x => x.State)
            .HasColumnName("state")
            .HasConversion<int>()
            .IsRequired();

        b.Property(x => x.Editor)
            .HasColumnName("editor")
            .HasMaxLength(256)
            .IsRequired();

        b.Property(x => x.ChangeDate)
            .HasColumnName("change_date")
            .HasColumnType("timestamp with time zone")
            .IsRequired();                        // ustawia encja przy każdej zmianie

        // Krajowy (SSCC)
        b.Property(x => x.IacDigit).HasColumnName("iac_digit").HasColumnType("smallint");
        b.Property(x => x.KindDigit).HasColumnName("kind_digit").HasColumnType("smallint");

        // Zagraniczny (S10)
        b.Property(x => x.ServiceIndicator).HasColumnName("service_indicator").HasColumnType("char(2)").HasMaxLength(2);
        b.Property(x => x.CountryCode).HasColumnName("country_code").HasColumnType("char(2)").HasMaxLength(2);

        // Pełny numer liczy aplikacja (RegisteredNumberFormatter); baza pilnuje tylko unikalności.
        b.Property(x => x.FullNumber)
            .HasColumnName("full_number")
            .HasMaxLength(20)
            .IsRequired();

        b.HasIndex(x => x.FullNumber).IsUnique().HasDatabaseName("uq_registered_number_pool_full_number");
        b.HasIndex(x => new { x.Type, x.State, x.Value }).HasDatabaseName("ix_registered_number_pool_type_state_value");

        // Równoległe pobieranie "następnego wolnego numeru" - kolizje wykryje systemowa kolumna xmin.
        b.Property(x => x.RowVersion)
            .HasColumnName("xmin")
            .IsRowVersion();
    }
}
