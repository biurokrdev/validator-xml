---
name: dotnet-ef-core-postgres
description: >-
  Design, query and migrate a PostgreSQL database from .NET using Entity Framework Core and Npgsql:
  DbContext and entity configuration, migrations, efficient LINQ queries, transactions, concurrency,
  and the Npgsql-specific features (jsonb, arrays, enums, snake_case naming). Use this skill whenever
  the user works on a DbContext, an entity, a migration, a repository, a query that is slow or returns
  too much data, "N+1", "AsNoTracking", "dotnet ef", Npgsql, or any persistence code in a .NET project
  that talks to Postgres, even if they just say "add a table" or "save this to the database".
---

# EF Core + PostgreSQL (Npgsql) in .NET

This skill produces persistence code that is correct under load, migrates safely in CI/CD and keeps
the database schema readable. Most EF Core bugs are silent: the code compiles, tests on an in-memory
provider pass, and production hits N+1 queries, client-side evaluation or lost updates. The workflow
below is ordered so that those problems are caught before the code is written.

## Workflow

1. **Read the existing DbContext and conventions first.** Look for `OnModelCreating`,
   `IEntityTypeConfiguration<T>` classes, naming conventions (`UseSnakeCaseNamingConvention`),
   and how the context is registered (`AddDbContext`, `AddDbContextPool`, `AddDbContextFactory`).
   New code that ignores existing conventions produces a migration full of renames.
2. **Model the entity, then the configuration, then the migration.** Keep mapping in
   `IEntityTypeConfiguration<T>` classes (one per entity) rather than data annotations, so the
   domain class stays free of persistence concerns and the mapping is discoverable in one place.
3. **Write the query against the real provider.** Use the Npgsql provider in tests
   (Testcontainers or a local Postgres), not the InMemory provider. InMemory accepts queries that
   Npgsql cannot translate and ignores constraints, so it proves nothing about production.
4. **Generate and read the migration.** Run `dotnet ef migrations add <Name>` and open the file.
   Check for unexpected drops, renames and type changes before committing. A migration that drops a
   column is a data-loss change and needs an explicit decision.
5. **Verify.** Apply the migration to a disposable database, run the integration tests, and
   inspect the generated SQL for the new queries (`LogTo` or `ToQueryString()`).

## Modelling

- Use `IEntityTypeConfiguration<T>` and `modelBuilder.ApplyConfigurationsFromAssembly(...)`.
- Prefer `required` properties and non-nullable reference types; EF maps them to `NOT NULL`.
- Make the primary key explicit (`HasKey`) and choose the key type deliberately: `int`/`long` with
  identity for internal tables, `Guid` (ideally UUIDv7 / `Guid.CreateVersion7()`) when ids leak
  to clients or are generated in distributed code.
- Define every relationship from the principal side with `HasMany/WithOne/HasForeignKey` and set
  `OnDelete` explicitly. The default cascade behaviour differs between required and optional
  relationships and is a frequent source of surprise.
- Add indexes for every column used in `Where`, `Join` or `OrderBy` on large tables
  (`HasIndex(...)`, unique where the domain requires it). Postgres does not index foreign keys
  automatically.
- Use value objects via `OwnsOne` or value converters; avoid primitive obsession for money, dates
  with a time zone and identifiers.
- Timestamps: store `timestamptz` and use `DateTimeOffset` or UTC `DateTime`. Npgsql rejects
  non-UTC `DateTime` for `timestamptz` since version 6, so decide on UTC at the boundary.
- Concurrency: add `IsRowVersion()` on a `uint xmin` shadow property
  (`.Property<uint>("xmin").IsRowVersion()`) for optimistic concurrency on Postgres.

## Npgsql-specific features worth using

| Need | Mapping |
|---|---|
| Semi-structured data | `jsonb` column: `.HasColumnType("jsonb")` with a POCO or `JsonDocument`; query with `EF.Functions.JsonContains` |
| Lists of scalars | Postgres arrays: `string[]` / `List<int>` map natively, queries translate `Contains` to `= ANY(...)` |
| Enums | `MapEnum<T>()` on the data source for native Postgres enums, or a value converter to `string` for portability |
| Case-insensitive search | `EF.Functions.ILike(x.Name, pattern)` |
| Full-text search | `NpgsqlTsVector` column with a GIN index |
| snake_case schema | `EFCore.NamingConventions` package: `.UseSnakeCaseNamingConvention()` |

## Querying

- Read-only queries: `AsNoTracking()` (or `AsNoTrackingWithIdentityResolution()` when the result
  graph has repeated entities). Tracking costs memory and time and only matters when you intend to
  `SaveChanges`.
- Project early: `Select` into a DTO instead of loading entities and mapping in memory. This also
  removes the need for `Include` in most read paths.
- When you do need a graph, use `Include` / `ThenInclude` and `AsSplitQuery()` for multiple
  collection includes to avoid the cartesian explosion. Never iterate a collection navigation in a
  loop without loading it first; that is the classic N+1.
- Paginate with `OrderBy` + `Skip/Take` on an indexed column, or keyset pagination for large
  tables (`Where(x => x.Id > lastId).Take(n)`).
- Bulk updates and deletes: `ExecuteUpdateAsync` / `ExecuteDeleteAsync` (EF Core 7+) run a single
  SQL statement and bypass the change tracker.
- Watch for client evaluation: EF Core throws when it cannot translate, but a `ToList()` placed too
  early silently pulls the whole table. Keep `IQueryable` until the final materialisation.
- Always pass the `CancellationToken` to async EF calls.

## Transactions and SaveChanges

- A single `SaveChangesAsync` is already atomic. Open an explicit transaction
  (`Database.BeginTransactionAsync`) only when several `SaveChanges` calls or raw SQL must commit
  together.
- When using `EnableRetryOnFailure`, wrap manual transactions in
  `CreateExecutionStrategy().ExecuteAsync(...)`; otherwise a retried transaction re-runs half-committed
  work.
- Catch `DbUpdateConcurrencyException` where optimistic concurrency is enabled and decide: reload
  and retry, or surface a 409 to the caller.
- Catch `UniqueConstraintException` style errors by inspecting `PostgresException.SqlState`
  (`23505` is unique violation) rather than pre-checking with a query; the pre-check races.

## Migrations

- One migration per logical change, named for the change (`AddInvoiceDueDate`), never `Update1`.
- Review the generated `Up`/`Down`. Rename detection is heuristic: a renamed property often appears
  as drop + add, which loses data. Replace with `RenameColumn` by hand.
- Data migrations go through `migrationBuilder.Sql(...)` inside the same migration, idempotently.
- Large tables: creating an index locks the table. Use
  `migrationBuilder.Sql("CREATE INDEX CONCURRENTLY ...")` with `suppressTransaction: true`.
- Apply migrations in CI/CD with `dotnet ef database update` or a generated idempotent script
  (`dotnet ef migrations script --idempotent`), not with `Database.Migrate()` on app startup when
  more than one instance starts at once (Cloud Run, Kubernetes), because concurrent migrators
  deadlock or double-apply.
- Keep the `Migrations` folder in source control and never edit an applied migration; add a new
  one.

## Registration and configuration

```csharp
builder.Services.AddDbContextPool<AppDbContext>((sp, options) =>
    options
        .UseNpgsql(connectionString, npgsql =>
        {
            npgsql.EnableRetryOnFailure(3);
            npgsql.MigrationsAssembly("MyApp.Infrastructure");
        })
        .UseSnakeCaseNamingConvention()
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .EnableDetailedErrors(builder.Environment.IsDevelopment())
        .EnableSensitiveDataLogging(builder.Environment.IsDevelopment()));
```

- Connection strings come from configuration or a secret manager, never source. On Cloud SQL use the
  Unix socket (`Host=/cloudsql/<project>:<region>:<instance>`) or the Cloud SQL connector.
- `AddDbContextPool` is cheaper per request but the context must not hold per-request state
  (for example an injected `ICurrentUser`). Use `AddDbContext` in that case.
- Register `NpgsqlDataSource` once when you need `MapEnum`, `EnableDynamicJson` or custom type
  handlers; pass the data source to `UseNpgsql`.

## Testing persistence

- Use Testcontainers (`Testcontainers.PostgreSql`) to spin up a real Postgres per test class; apply
  migrations with `MigrateAsync()` in the fixture. See the `dotnet-testing` skill for the fixture
  pattern.
- Use `Respawn` or a transaction-per-test to reset state between tests.
- Assert the SQL for critical queries with `query.ToQueryString()` when the shape of the SQL is the
  requirement (for example that an index can be used).

## Examples

**Example 1**
Input: "Add an `Invoice` table with lines; each invoice has a number unique per customer."
Output: `Invoice` and `InvoiceLine` entities, `InvoiceConfiguration` with
`HasIndex(x => new { x.CustomerId, x.Number }).IsUnique()`, `HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade)`,
a migration named `AddInvoices`, and a note that the unique violation surfaces as `PostgresException`
with SqlState `23505`.

**Example 2**
Input: "This endpoint listing orders with their items is slow."
Output: Replace `Include(o => o.Items)` + in-memory mapping with a projection to `OrderListItemDto`,
add `AsNoTracking()`, page with keyset on `Id`, and show the SQL before and after via
`ToQueryString()`.

## References

- `references/npgsql-type-mapping.md` — read when mapping a non-trivial Postgres type
  (jsonb, arrays, ranges, enums, tsvector, interval) or when a query fails to translate.
