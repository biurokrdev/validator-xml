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
| `envelope` | `SingleWindow` (domyślnie, sprawdzany adres adresata) albo `DoubleWindow` (adres adresata i nalepka R w oknie nadawcy) |
| `includePreview` | `true` (domyślnie) albo `false`: bez podglądu PNG, szybciej |

```
curl -F "file=@pismo.pdf" -F envelope=DoubleWindow http://localhost:5090/api/letter-inspections
```

| Odpowiedź | Kiedy |
|---|---|
| `200` + wynik | plik przyjęty do sprawdzenia, także gdy pismo jest niepoprawne (`isValid: false`) albo PDF okazał się uszkodzony lub zaszyfrowany (`isReadable: false`, zgłoszenie `INVALID_DOCUMENT`) |
| `400` + `ProblemDetails` | brak pliku, plik pusty, za duży, bez nagłówka PDF albo nieznany rodzaj koperty |

Wynik: `isValid`, `isReadable`, `summary`, `page`, `windows[]`, `documentFindings[]` i `preview.dataUri` (PNG do `<img src>`). Każde okno ma `content`, `found`, `isValid`, `overflow` w mm z gotowym opisem i `findings[]`. Kody zgłoszeń są takie same jak w bibliotece (tabela w `../README.md`).

Przy dwóch okienkach okna różnią się tym, czego się w nich szuka:

| Okno | `content` | Co jest sprawdzane | Co jest w wyniku |
|---|---|---|---|
| adresata (`kind: Recipient`) | `Address` | położenie i treść adresu | `address.lines`, rozmiar czcionki |
| nadawcy (`kind: Sender`) | `RegisteredLabel` | tylko czy jest nalepka R (grafika) i czy mieści się w oknie; żadnych danych adresowych | `label.bounds`: położenie i rozmiar nalepki w mm |

Do okna nadawcy koperty C65 mieści się nalepka najwyżej 49×13 mm; standardowa 65×25 mm daje `LABEL_TOO_LARGE` (szczegóły w `../README.md`, sekcja „Okno nadawcy: nalepka R”).

Układ koperty to domyślny profil biblioteki, czyli C65. Inny układ ustawia się w jednym miejscu: `Infrastructure/DependencyInjection.cs`, w konstruktorze `PdfAddressWindowValidator`.

API nie może mieć włączonego `InvariantGlobalization`: biblioteka formatuje milimetry w kulturze `pl-PL` i bez danych ICU rzuca wyjątek przy opisie adresu wystającego poza okno.

## Testy

```
dotnet test Mass/AddressWindow/Mass.AddressWindow.sln     # biblioteka + API
cd Mass/AddressWindow/app/ui && npm test                  # komponenty (Vitest)
```
