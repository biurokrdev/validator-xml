# Npgsql / EF Core type mapping reference

Read this when a property does not map the way you expect, a LINQ expression fails to translate,
or you need a Postgres-specific column type.

## Scalar defaults

| .NET type | Postgres type | Notes |
|---|---|---|
| `string` | `text` | Add `HasMaxLength(n)` for `varchar(n)` only when the limit is a business rule |
| `int` / `long` / `short` | `integer` / `bigint` / `smallint` | Identity via `UseIdentityByDefaultColumn()` (default) |
| `decimal` | `numeric` | Set precision: `HasPrecision(18, 2)` |
| `bool` | `boolean` | |
| `Guid` | `uuid` | Generate with `Guid.CreateVersion7()` (.NET 9) to keep index locality |
| `DateTime` (UTC) | `timestamp with time zone` | Non-UTC `DateTime` throws; convert at the boundary |
| `DateTime` (Unspecified) | `timestamp without time zone` | Use `.HasColumnType("timestamp without time zone")` deliberately |
| `DateTimeOffset` | `timestamp with time zone` | Offset is not stored; value is normalised to UTC |
| `DateOnly` / `TimeOnly` | `date` / `time` | Supported natively since Npgsql 6 |
| `TimeSpan` | `interval` | |
| `byte[]` | `bytea` | |

## Arrays

```csharp
public string[] Tags { get; set; } = [];
public List<int> Codes { get; set; } = new();
```

Map natively to `text[]` / `integer[]`. Translations: `Tags.Contains("x")` becomes
`'x' = ANY(tags)`, `Tags.Length` becomes `cardinality(tags)`, and
`x.Tags.Any(t => values.Contains(t))` becomes the overlap operator `tags && @values`.
Index with GIN when filtering by element: `HasIndex(x => x.Tags).HasMethod("gin")`.

## jsonb

Three options, from simplest to most flexible:

1. **POCO column**: `.Property(x => x.Metadata).HasColumnType("jsonb")` with
   `EnableDynamicJson()` on the data source (or `UseNpgsql(..., o => o.EnableDynamicJson())`).
   Querying inside the document is limited.
2. **Owned JSON entity** (EF Core 7+): `.OwnsOne(x => x.Address, b => b.ToJson())`. Properties of
   the owned type are queryable in LINQ and translated to `->>` operators.
3. **`JsonDocument` / `JsonElement`**: full control, query with
   `EF.Functions.JsonContains(x.Doc, """{"status":"open"}""")`, `JsonExists`, `JsonTypeof`.

Index: `HasIndex(x => x.Metadata).HasMethod("gin")`; for a path use raw SQL in the migration.

## Enums

- **Native Postgres enum**: `dataSourceBuilder.MapEnum<Status>()` plus
  `modelBuilder.HasPostgresEnum<Status>()`. Fast and typed, but adding a value needs a migration
  with `ALTER TYPE ... ADD VALUE`, which cannot run inside a transaction.
- **String conversion**: `.HasConversion<string>()`. Portable and readable; costs a few bytes per
  row. Prefer this unless the table is very large.
- Avoid storing the integer value; it makes the data unreadable and reordering the enum corrupts
  meaning.

## Ranges and intervals

`NpgsqlRange<DateTime>` maps to `tstzrange`; use `EF.Functions.Contains`/`Overlaps`.
Useful for bookings and validity periods with an exclusion constraint added via raw SQL.

## Full-text search

```csharp
builder.Property(x => x.SearchVector).HasColumnType("tsvector");
builder.HasGeneratedTsVectorColumn(x => x.SearchVector, "simple", x => new { x.Title, x.Body })
       .HasIndex(x => x.SearchVector).HasMethod("gin");
// query
ctx.Articles.Where(a => a.SearchVector.Matches(EF.Functions.PlainToTsQuery("simple", term)))
```

## Common translation failures

| Symptom | Cause | Fix |
|---|---|---|
| `could not be translated` on string compare | `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)` | `EF.Functions.ILike` or collation `.UseCollation("und-ci")` |
| `DateTime` kind error on insert | Local `DateTime` into `timestamptz` | `DateTime.SpecifyKind(x, Utc)` or use `DateTimeOffset` |
| `.Date` on `DateTime` fails | Not translatable to `timestamptz` | Compare ranges: `x >= day && x < day.AddDays(1)` |
| Cartesian explosion warning | Multiple collection `Include`s | `AsSplitQuery()` |
| `Contains` on a large list is slow | `IN (...)` with thousands of parameters | Pass an array parameter: `values.Contains(x.Id)` with `int[]` translates to `= ANY(@p)` |
