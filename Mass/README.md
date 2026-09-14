# Mass - pule numerów nadawczych przesyłek poleconych (R)

| Element | Cel |
|------|-----|
| `001_create_registered_number_pool.sql` | Schemat `mass` i tabela `mass.registered_number_pool` z indeksami. Idempotentny. |
| `001_create_registered_number_pool.rollback.sql` | Wycofuje powyższe. **Usuwa dane.** |
| `Mass.Api/` | REST API .NET 8 (administracja numerami) + domena i repozytorium EF Core. |
| `mass-ui/` | Front Angular 22 (lista, zasilenie przedziałem, sprawdzenie numeru) + testy Playwright. |
| `TESTS.md` | Specyfikacja testów manualnych i automatycznych. |

## Szybki start (bez PostgreSQL)

```bash
# API na bazie InMemory (appsettings.Development.json), port 5080, Swagger pod /swagger
cd Mass.Api && dotnet run

# front, port 4200, proxy /api -> 5080
cd mass-ui && npm install && npm start

# testy
cd mass-ui && npm test          # Vitest
cd mass-ui && npm run e2e       # Playwright (API musi działać)
```

Na produkcji ustaw `Database:Provider = Postgres` i `ConnectionStrings:Mass` (poza repo), po wcześniejszym uruchomieniu skryptu SQL.

## REST API (`Mass.Api`, prefiks `/api/registered-numbers`)

| Metoda | Ścieżka | Cel | Kody |
|---|---|---|---|
| GET | `?type=&state=&search=&page=&pageSize=` | lista ze stronicowaniem (max 500/str.) | 200 |
| GET | `/stats` | liczności obu pul wg stanów | 200 |
| GET | `/{fullNumber}` | szczegóły numeru | 200, 400 (zły numer), 404 (spoza puli) |
| POST | `/validate` `{number}` | format, cyfra kontrolna, obecność w puli, stan | 200 |
| POST | `/ranges/check` `{firstNumber, count \| lastNumber}` | **weryfikacja przed zasileniem**: ile nowych, ile już mamy (z listą) | 200, 400 |
| POST | `/ranges/import` `{firstNumber, count \| lastNumber}` | zasilenie puli; istniejące pomija | 200, 400 |
| POST | `/{fullNumber}/state` `{action: Reserve\|Use\|Release\|Cancel}` | zmiana stanu | 200, 400, 404, 409 (niedozwolone przejście / konflikt) |
| POST | `/acquire` `{type}` | pobierz i zarezerwuj kolejny wolny numer | 200, 409 (pula pusta) |

Autor zmiany: nagłówek `X-Editor` (docelowo tożsamość użytkownika; API nie ma jeszcze uwierzytelniania). Błędy w formacie RFC 7807 (`ProblemDetails`). CORS dla originów z `Cors:Origins`.

Struktura projektu: `Domain/` (encja, enumy, formater, zakres, kontrakt repozytorium), `Infrastructure/` (DbContext, konfiguracja EF, repozytorium), `Contracts/` (DTO), `Controllers/`.

## Front (`mass-ui`)

Jedna strona bez routera. Trzy sekcje są zdefiniowane jako `ng-template` w `app.html`; przycisk w nagłówku ustawia aktywną sekcję, a `ngTemplateOutlet` renderuje tylko ją (pozostałych nie ma w DOM). Domyślnie widoczna jest lista.

| Sekcja | Przycisk | Funkcje |
|---|---|---|
| Lista | `Lista` | panel stanu puli pod tytułem („Zostało N numerów do wykorzystania”, per typ: dostępne, zarezerwowane, użyte, anulowane, razem), filtry (typ, stan, fragment numeru), stronicowanie, akcje zmiany stanu zależne od stanu, „Pobierz kolejny numer” |
| Zasilenie | `Zasilenie` | pierwszy numer + ilość albo ostatni numer → **Sprawdź** (duplikaty, tabela istniejących) → **Zasil** (aktywne tylko po aktualnym sprawdzeniu i gdy jest co dodać) |
| Sprawdź numer | `Sprawdź numer` | walidacja pojedynczego numeru i jego stan w puli |

Pole „Edytor” w nagłówku trafia do `X-Editor`. Serwis: `src/app/core/registered-numbers.service.ts`, modele: `src/app/core/models.ts`, komponenty sekcji: `src/app/features/`. Elementy mają `data-testid` pod Playwright.

## Dwie rodziny numerów R

| | Krajowy (`type = 1`) | Zagraniczny (`type = 2`) |
|---|---|---|
| Standard | GS1 SSCC, kod GS1-128 | UPU S10 |
| Długość | 20 cyfr | 13 znaków |
| Budowa | `00` + IAC (1-9) + `5900773` + S1 + 8 cyfr + kontrolna | `RA..RZ` + 8 cyfr + kontrolna + kod kraju |
| Cyfra kontrolna | GS1 mod 10 z 17 cyfr (bez `00`) | ważony mod 11, wagi 8 6 4 2 3 5 9 7 |
| Przykład | `00759007731512000621` | `RR473124829PL` |
| Kolumny części | `iac_digit`, `kind_digit` | `service_indicator`, `country_code` |

S1 (`kind_digit`) wg GS1 Polska: **1 lub 4 = przesyłka polecona**, 2 = list z zadeklarowaną wartością, 3 = paczka, 5 = pobraniowa, 6 = e-przesyłka, 8 = biznesowa. Aplikacja przyjmuje tylko 1 i 4 (R-ki); rozszerzenie: `RegisteredNumberPool.CreateDomestic` i `RegisteredNumberRange`.

## Tabela `mass.registered_number_pool`

Jeden wiersz = jeden numer nadania. Pula (rolka 1000 nalepek z Elektronicznego Nadawcy) to zbiór wierszy o tym samym `type` i prefiksach.

| Kolumna | Typ | Uwagi | Model C# |
|---------|-----|-------|----------|
| `id` | `uuid` | PK | `Guid Id` |
| `type` | `integer` | 1 krajowy, 2 zagraniczny | `RegisteredNumberPoolType Type` |
| `value` | `integer` | 8-cyfrowy numer przesyłki, 0..99999999 | `int Value` |
| `state` | `integer` | 0 dostępny, 1 zarezerwowany, 2 użyty, 3 anulowany | `RegisteredNumberPoolState State` |
| `editor` | `varchar(256)` | | `string Editor` |
| `change_date` | `timestamptz` | ustawia encja | `DateTime ChangeDate` |
| `iac_digit` | `smallint` | tylko krajowy, 1-9 | `short? IacDigit` |
| `kind_digit` | `smallint` | tylko krajowy, 1 lub 4 | `short? KindDigit` |
| `service_indicator` | `char(2)` | tylko zagraniczny, `R[A-Z]` | `string? ServiceIndicator` |
| `country_code` | `char(2)` | tylko zagraniczny, np. `PL` | `string? CountryCode` |
| `full_number` | `varchar(20)` | liczy aplikacja, unikalna | `string FullNumber` |

Baza jest celowo prosta: przechowuje dane, pilnuje kluczy, zakresów i unikalności `full_number`. Cyfry kontrolne, składanie numeru, reguły przejść stanów i datę zmiany obsługuje aplikacja (`RegisteredNumberFormatter`, `RegisteredNumberPool`).

## Uruchomienie
```bash
psql -h <host> -U <admin> -d <db> -f 001_create_registered_number_pool.sql
```
Wymaga PostgreSQL 13+ (`gen_random_uuid()`). Uprawnienia dla konta aplikacji nadaj poza skryptem, np. `GRANT SELECT, INSERT, UPDATE ON mass.registered_number_pool TO <login_aplikacji>` i `GRANT USAGE ON SCHEMA mass TO <login_aplikacji>`.

## Klasy .NET (`Mass.Api/Domain`, `Mass.Api/Infrastructure`)

| Plik | Rola |
|------|------|
| `RegisteredNumberPoolType.cs`, `RegisteredNumberPoolState.cs` | Enumy zapisywane jako liczby. |
| `RegisteredNumberFormatter.cs` | Cyfry kontrolne, składanie i rozkładanie numerów (`TryParse` z opisem błędu). |
| `RegisteredNumberRange.cs` | Zakres jednej puli: z pierwszego numeru i ilości, z pierwszego i ostatniego, albo z części. Generuje kolejne R-ki. |
| `RegisteredNumberPool.cs` | Encja: jeden numer, fabryki, przejścia stanów. |
| `IRegisteredNumberPoolRepository.cs` | Kontrakt repozytorium + rekordy wyników i wyjątki. |
| `RegisteredNumberPoolRepository.cs` | Implementacja na EF Core + Npgsql (lista, sprawdzenie zakresu, import, pobieranie, zmiany stanu). |
| `RegisteredNumberPoolConfiguration.cs`, `MassDbContext.cs` | Mapowanie EF Core. |

### Repozytorium

```csharp
var repo = new RegisteredNumberPoolRepository(db);

// 1. Generowanie R-ek z zakresu (EN podaje pierwszy numer z rolki i ilość nalepek)
var range  = RegisteredNumberRange.FromFirstAndCount("00759007731512000621", 1000);
var result = await repo.ImportRangeAsync(range, "import");        // Added / Skipped, ponowny import jest bezpieczny

var intl   = RegisteredNumberRange.FromFirstAndLast("RR473124829PL", "RR473134825PL");
await repo.ImportRangeAsync(intl, "import");

// 2. Pobranie kolejnego numeru do nadania (Dostępny -> Zarezerwowany), bezpieczne równolegle
var number = await repo.AcquireNextAsync(RegisteredNumberPoolType.Domestic, "app");
// ... przesyłka nadana:
await repo.MarkUsedAsync(number.FullNumber, "app");               // -> Użyty
// ... albo rezerwacja porzucona:
await repo.ReleaseAsync(number.FullNumber, "app");                // -> Dostępny

// 3. Walidacja
var v = repo.Validate("(00) 7 5900773 1 51200062 1");             // format + cyfra kontrolna, bez bazy
var v2 = await repo.ValidateAsync("RR473124829PL");               // + czy jest w puli i w jakim stanie

// 4. Weryfikacja użycia
var usage = await repo.GetUsageAsync("00759007731512000621");     // InvalidNumber / NotInPool / Available / Reserved / Used / Cancelled
bool used = await repo.IsUsedAsync("00759007731512000621");
```

Reguły stanów: `Available -> Reserved -> Used`, `Reserved -> Available` (release), `Available -> Used` (nalepka użyta bez rezerwacji), `Available|Reserved -> Cancelled`. Numer użyty nie zmienia już stanu.

`AcquireNextAsync` na PostgreSQL używa `SELECT ... FOR UPDATE SKIP LOCKED`, więc wiele instancji aplikacji pobiera różne numery bez czekania. Na innych providerach (testy InMemory/SQLite) działa zwykły `OrderBy`, a kolizje wykrywa token współbieżności `RowVersion` mapowany na `xmin`.

Wpięcie w DI:
```csharp
services.AddDbContext<MassDbContext>(o => o.UseNpgsql(connectionString));
services.AddScoped<IRegisteredNumberPoolRepository, RegisteredNumberPoolRepository>();
```
