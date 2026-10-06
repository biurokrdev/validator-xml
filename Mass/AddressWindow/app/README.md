# Sprawdzenie okienka adresowego: aplikacja

Aplikacja do biblioteki `Mass.AddressWindow.Pdf`: użytkownik wczytuje pismo w PDF, plik idzie do API, API sprawdza pierwszą stronę biblioteką i odsyła wynik, a frontend pokazuje werdykt, wykryte adresy, zgłoszenia i podgląd strony z oknami koperty.

```
app/
├─ api/                                    .NET 8, warstwy DDD
│  ├─ src/Mass.AddressWindow.Domain/         model: PdfLetter, LetterInspection, WindowInspection, Finding; port IAddressWindowInspector
│  ├─ src/Mass.AddressWindow.Application/    przypadek użycia InspectLetterHandler
│  ├─ src/Mass.AddressWindow.Infrastructure/ adapter portu na IPdfAddressWindowValidator (jedyne miejsce, które zna bibliotekę)
│  ├─ src/Mass.AddressWindow.Api/            kontroler, kontrakty JSON, mapowanie wyjątków na ProblemDetails
│  └─ tests/Mass.AddressWindow.Api.Tests/    xUnit: domena + API (WebApplicationFactory) na PDF-ach z samples/pdf
└─ ui/                                     Angular 22 (standalone, sygnały), bez biblioteki UI
```

Zależności idą do środka: Api → Application → Domain ← Infrastructure → biblioteka. Domena nie zna ani ASP.NET, ani biblioteki.

## Uruchomienie

```
cd Mass/AddressWindow/app/api
dotnet run --project src/Mass.AddressWindow.Api      # http://localhost:5090, Swagger pod /swagger

cd Mass/AddressWindow/app/ui
npm install
npm start                                            # http://localhost:4300, /api idzie przez proxy do :5090
```

Do prób nadają się pliki z `Mass/AddressWindow/samples/pdf` (oczekiwane wyniki: `samples/README.md`).

## API

`POST /api/letter-inspections`, `multipart/form-data`:

| Pole | Wartość |
|---|---|
| `file` | plik PDF, do 20 MB |
| `envelope` | `SingleWindow` (domyślnie, sprawdzany adresat) albo `DoubleWindow` (adresat i nadawca) |
| `includePreview` | `true` (domyślnie) albo `false`: bez podglądu PNG, szybciej |

```
curl -F "file=@pismo.pdf" -F envelope=DoubleWindow http://localhost:5090/api/letter-inspections
```

| Odpowiedź | Kiedy |
|---|---|
| `200` + wynik | plik przyjęty do sprawdzenia, także gdy pismo jest niepoprawne (`isValid: false`) albo PDF okazał się uszkodzony lub zaszyfrowany (`isReadable: false`, zgłoszenie `INVALID_DOCUMENT`) |
| `400` + `ProblemDetails` | brak pliku, plik pusty, za duży, bez nagłówka PDF albo nieznany rodzaj koperty |

Wynik: `isValid`, `isReadable`, `summary`, `page`, `windows[]` (dla każdego okna: `found`, `isValid`, `address.lines`, `overflow` w mm z gotowym opisem, `findings[]`), `documentFindings[]` i `preview.dataUri` (PNG do `<img src>`). Kody zgłoszeń są takie same jak w bibliotece (tabela w `../README.md`).

Układ koperty to domyślny profil biblioteki, czyli C65. Inny układ ustawia się w jednym miejscu: `Infrastructure/DependencyInjection.cs`, w konstruktorze `PdfAddressWindowValidator`.

API nie może mieć włączonego `InvariantGlobalization`: biblioteka formatuje milimetry w kulturze `pl-PL` i bez danych ICU rzuca wyjątek przy opisie adresu wystającego poza okno.

## Testy

```
dotnet test Mass/AddressWindow/Mass.AddressWindow.sln     # biblioteka + API
cd Mass/AddressWindow/app/ui && npm test                  # komponenty (Vitest)
```
