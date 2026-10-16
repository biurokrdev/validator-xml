# Walidacja pisma (koperty z okienkami): przykładowa aplikacja

Przykład użycia biblioteki `Mass.AddressWindow`: API .NET i jeden ekran w Angularze. Aplikacja odpowiada na dwa pytania: czy wymagane treści (adres odbiorcy, a przy kopercie z dwoma okienkami także etykieta R listu poleconego) znajdują się w odpowiednich okienkach koperty i czy całe mieszczą się w dostępnych obszarach. Walidację wykonuje biblioteka; aplikacja dokłada przyjmowanie plików, mock aplikacji zewnętrznej (generowanie pisma z danych i konwersja DOCX do PDF) i ekran.

Dwa warianty:

| Wariant | Wejście | Co wiemy o odbiorcy |
|---|---|---|
| **Wczytać plik** | PDF albo DOCX wybrany przez użytkownika | nic: walidacja potwierdza tylko, że w okienku jest blok przypominający adres (`recipientMatch: NotChecked`) |
| **Ręcznie podać dane** | dane odbiorcy i (dla dwóch okienek) numer R; dokument zwraca mock aplikacji zewnętrznej | dane podane: walidacja szuka tego adresu i tego numeru w dokumencie (`recipientMatch: Matched / Mismatch`, `LABEL_NUMBER_MISMATCH`) |

| **Sprawdzić konkretny forms** | identyfikator formularza (GUID); dokument formularza zwraca mock aplikacji zewnętrznej | nic: jak przy wczytanym pliku; numer R z nalepki jest sprawdzany co do budowy i cyfry kontrolnej |

Wszystkie warianty idą przez ten sam proces: DOCX → PDF (zewnętrzna usługa konwersji) → biblioteka walidacyjna → wynik.

```
examples/
├─ Mass.AddressWindow.Examples.sln
├─ api/
│  ├─ Mass.AddressWindow.Api/              .NET 8, jeden projekt
│  │  ├─ Documents/                        LetterDocument: format rozpoznawany po zawartości, limit rozmiaru
│  │  ├─ Conversion/                       IDocxToPdfConverter: port na zewnętrzną usługę konwersji
│  │  ├─ External/                         LetterOrder, IExternalLetterSource, MockExternalSystem: jeden mock aplikacji zewnętrznej (generuje pismo w PDF przez SkiaSharp albo w DOCX; konwertuje DOCX do PDF)
│  │  ├─ Inspection/                       LetterInspectionService: oba warianty → biblioteka → odpowiedź; EnvelopeProfileOptions
│  │  ├─ Contracts/                        żądania i odpowiedź JSON (enumy biblioteki: ValidationOutcome, IssueSeverity, RecipientMatch, RegisteredMailType)
│  │  └─ Controllers/                      dwa endpointy; Program.cs mapuje błędy techniczne na ProblemDetails
│  └─ Mass.AddressWindow.Api.Tests/        xUnit przez WebApplicationFactory na plikach z ../../samples (DOCX i PDF)
└─ ui/                                     Angular 22, jeden ekran
```

## Uruchomienie

```
cd Mass/AddressWindow/examples/api
dotnet run --project Mass.AddressWindow.Api          # http://localhost:5090, Swagger pod /swagger

cd Mass/AddressWindow/examples/ui
npm install
npm start                                            # http://localhost:4300, /api idzie przez proxy do :5090
```

Do prób: `Mass/AddressWindow/samples` (DOCX) i `samples/pdf` (oczekiwane wyniki w `samples/README.md`).

### Aplikacja zewnętrzna i konwersja DOCX

Produkcyjnie dwie rzeczy robi zewnętrzna aplikacja przez HTTP: generuje pismo z danych odbiorcy (wariant 2) i renderuje DOCX do PDF (oba warianty). W przykładzie zastępuje ją jeden mock, `MockExternalSystem`, za dwoma portami:

| Port | Co robi mock | Gdzie podłączyć prawdziwą usługę |
|---|---|---|
| `IExternalLetterSource` | `FetchAsync`: buduje prawdziwe pismo z podanym adresem i numerem R, jako PDF (SkiaSharp) albo DOCX; `FetchFormAsync`: pismo wyliczone z GUID formularza, zawsze PDF | implementacja wysyłająca zamówienie lub identyfikator formularza do aplikacji zewnętrznej |
| `IDocxToPdfConverter` | pisma, które sam wygenerował, renderuje do PDF z tej samej treści; dla wgranego DOCX zwraca gotowy PDF o tej samej nazwie z `samples/pdf`; inny DOCX → `502` | implementacja wysyłająca DOCX do usługi konwersji i odbierająca PDF |

Mock niczego nie udaje poza tym: nie waliduje DOCX bez PDF i nie zgaduje wyniku. Jedyna zależność środowiskowa to dane ICU (bez `InvariantGlobalization`), bo biblioteka formatuje milimetry w `pl-PL`.

## Konfiguracja (`appsettings.json`)

| Sekcja | Klucz | Znaczenie |
|---|---|---|
| `Envelope` | `Profile` | układ koperty z biblioteki: `C65TwoWindows` (domyślny), `C65SingleWindow`, `DlTwoWindowsLeft`, `DlSingleWindowDin5008B`, `DlSingleWindowDin5008A` |
| | `Confirmed`, `Note` | `false` = profil demonstracyjny; wynik niesie `profile.confirmed: false` i tekst `Note` (ekran ich nie pokazuje; są dla integracji). Wymiary okienek C65 i założenie o składaniu kartki na trzy pochodzą ze specyfikacji w bibliotece i **wymagają potwierdzenia biznesowego** |
| `External:Mock` | `LetterFormat` | `Pdf` albo `Docx`: format pisma zwracanego przez mock w wariancie 2, zawsze ten sam |
| | `ConversionEnabled` | `false` symuluje niedostępną usługę konwersji: DOCX dostaje `503` |
| | `PdfDirectory` | katalog z gotowymi PDF-ami dla wgrywanych DOCX, dopasowanie po nazwie pliku; puste = `samples/pdf` kopiowane obok API |

Geometrii nie edytuje się w UI. Inny układ niż gotowe profile dodaje się w `EnvelopeLayouts` biblioteki (`EnvelopeLayout.FromEnvelope` z wymiarami koperty i okien).

## API

### Wariant 1: `POST /api/letter-inspections/upload` (`multipart/form-data`)

| Pole | Wartość |
|---|---|
| `file` | PDF albo DOCX, do 20 MB; format rozpoznawany po zawartości (nagłówek `%PDF-`, pakiet ZIP z `word/document.xml`), nie po nazwie |
| `envelope` | `SingleWindow` albo `DoubleWindow` (wymagane) |
| `includePreview` | `true` (domyślnie) albo `false`: bez podglądu PNG |

```
curl -F "file=@pismo.docx" -F envelope=DoubleWindow http://localhost:5090/api/letter-inspections/upload
```

### Wariant 2: `POST /api/letter-inspections/from-recipient` (`application/json`)

```json
{ "envelope": "DoubleWindow", "source": "ERP-2026/114", "recipientLines": ["Pani Anna Zielińska", "ul. Długa 12/3", "20-020 Lublin"], "registeredNumber": "RR473124829PL", "includePreview": false }
```

`source`: dowolny tekst przekazywany aplikacji zewnętrznej (opcjonalny; API nie nadaje mu znaczenia). `recipientLines`: 2–6 niepustych wierszy. `registeredNumber`: wymagany dla `DoubleWindow`, ignorowany dla `SingleWindow`. API wywołuje `IExternalLetterSource` (mock) i otrzymany dokument przepuszcza przez ten sam proces co wariant 1, z podanymi danymi jako oczekiwanymi.

### Wariant 3: `POST /api/letter-inspections/from-form` (`application/json`)

```json
{ "envelope": "DoubleWindow", "formId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "includePreview": false }
```

`formId`: poprawny GUID; inny tekst daje `400`. API prosi `IExternalLetterSource.FetchFormAsync` o dokument formularza (mock: pismo wyliczone z identyfikatora, ten sam GUID zawsze daje to samo pismo, zwracane jako PDF) i sprawdza go jak wczytany plik. Gdyby aplikacja zewnętrzna zwróciła DOCX, przeszedłby przez konwersję jak w wariancie 1.

### Odpowiedzi

| Kod | Kiedy |
|---|---|
| `200` + wynik | dokument sprawdzony; werdykt (także negatywny albo nierozstrzygnięty) jest w polu `status` |
| `400` | brak pliku, plik pusty lub za duży, ani PDF, ani DOCX, brak koperty, zły adres odbiorcy, brak numeru R dla dwóch okienek |
| `422` | plik wygląda na PDF, ale nie da się go odczytać (uszkodzony, zaszyfrowany) |
| `502` | usługa konwersji nie zwróciła PDF (szczegół w `detail`) albo aplikacja zewnętrzna nie zwróciła dokumentu |
| `503` | usługa konwersji DOCX niedostępna lub wyłączona |

Błędy techniczne to `ProblemDetails` z polskim tytułem; nigdy nie udają wyniku walidacji.

### Wynik

```json
{
  "status": "Invalid",
  "summary": "okno adresata: OK; okno nadawcy: błąd (wystaje z lewej o 7,0 mm, …)",
  "envelope": "DoubleWindow",
  "document": { "fileName": "05_C65_nalepka_R_za_duza.pdf", "sourceFormat": "Pdf", "convertedToPdf": false, "source": "Upload" },
  "profile": { "name": "C65, dwa okienka", "confirmed": false, "note": "Profil demonstracyjny C65: …" },
  "page": { "number": 1, "count": 1, "widthMm": 210, "heightMm": 297 },
  "address": { "status": "Valid", "windowName": "okno adresata", "windowArea": { "left": 119, "top": 54, "width": 71, "height": 30 }, "found": true,
               "address": { "lines": ["Pan Jan Kowalski", "ul. Marszałkowska 142 m. 5", "00-061 Warszawa"], "textBounds": { "left": 120.2, "top": 55.7, "width": 43.5, "height": 11.7 }, "minFontSizePt": 10, "maxFontSizePt": 10 },
               "label": null, "overflow": null, "recipientMatch": "NotChecked", "reasons": [] },
  "label":   { "status": "Invalid", "windowName": "okno nadawcy", "windowArea": { "left": 28, "top": 64, "width": 51, "height": 15 }, "found": true,
               "address": null, "label": { "bounds": { "left": 21, "top": 59, "width": 65, "height": 25 }, "widthPx": 511, "heightPx": 196, "number": "00759007731512000621", "humanReadableNumber": "(00)75900773 1 51200062 1", "kind": "Domestic", "numberIsValid": true, "symbology": "GS1-128", "barcodeModulePx": 2 },
               "overflow": { "leftMm": 7, "topMm": 5, "rightMm": 7, "bottomMm": 5, "description": "z lewej o 7,0 mm, z prawej o 7,0 mm, u góry o 5,0 mm, u dołu o 5,0 mm" },
               "recipientMatch": "NotChecked",
               "reasons": [ { "code": "LABEL_TOO_LARGE", "severity": "Error", "message": "Nalepka R ma 65,0 × 25,0 mm, a okno nadawcy mieści najwyżej 49,0 × 13,0 mm …" },
                            { "code": "LABEL_LOW_RESOLUTION", "severity": "Warning", "message": "…" } ] },
  "documentReasons": [],
  "preview": null
}
```

| Pole | Znaczenie |
|---|---|
| `status` | `Valid`, `Invalid` albo `Inconclusive`. Niepoprawny, gdy którykolwiek element jest niepoprawny; nierozstrzygnięty, gdy dokument nie pozwala wiarygodnie ocenić (skan bez warstwy tekstowej, nieczytelna grafika w oknie nalepki); inaczej poprawny |
| `address` | okno adresowe: `status`, `found`, wykryte `address.lines` i `textBounds`, `overflow` (o ile mm wystaje), `recipientMatch`, `reasons` |
| `label` | okno etykiety R (`null` dla jednego okienka): `status`, `found`, `label.number` z kodu kreskowego, `kind`, `numberIsValid`, `bounds`, `overflow`, `reasons` |
| `reasons[]`, `documentReasons[]` | `code` (stały, [lista](../../docs/zgloszenia.md)), `severity` (`Error`, `Inconclusive`, `Warning`, `Info`), `message` po polsku |
| `document` | nazwa i format pliku źródłowego, czy był konwertowany, skąd pochodzi (`Upload`, `ExternalMock`, `FormMock`) |
| `profile` | użyty układ koperty i czy jest potwierdzony biznesowo |
| `windowArea`, `textBounds`, `bounds` | dane geometryczne w mm od lewego górnego rogu strony, do diagnostyki |

## Zasady walidacji w skrócie

- **Jedno okienko:** blok adresowy musi być w okienku adresowym i cały mieścić się w obszarze widocznym przy każdym położeniu kartki; treść musi spełniać reguły adresowania.
- **Dwa okienka:** jak wyżej plus etykieta R: grafika z czytelnym kodem kreskowym z poprawnym numerem nadawczym, cała w swoim okienku. Każdy element oceniany osobno; całość poprawna tylko, gdy oba są poprawne.
- **Sam tekst w okienku to nie adres**, a sama litera R albo numer to nie etykieta: adres musi mieć budowę adresu (reguły treści), etykieta musi mieć odczytany kod kreskowy. Granice sprawdzane są dla całego bloku i całej grafiki, nie dla fragmentu wyciętego z okienka.
- **Treść poza właściwym okienkiem** (także zamienione miejsca adresu i etykiety) jest wykrywana na całej stronie i zgłaszana jako leżąca poza oknem, z nazwą okna, w którym faktycznie jest.
- **Bez danych odbiorcy** (wariant 1) wynik mówi „wykryto blok przypominający adres”, nie „adres zgodny z odbiorcą”.
- **Nie da się rozstrzygnąć** (skan, nieczytelna grafika, plik nieczytelny po konwersji): wynik `Inconclusive` z przyczyną, nigdy „poprawny”.

## Frontend

Jeden ekran: typ koperty (domyślnie jedno okienko), wybór czynności (domyślnie wczytanie pliku), formularz wariantu z przyciskiem „Wyczyść” (przywraca domyślne ustawienia i puste pola). Po uruchomieniu sprawdzenia formularz znika; wraca po „Weryfikuj kolejny” (koperta i czynność zostają, pola się czyszczą) albo „Wróć do formularza” po błędzie technicznym (wpisane dane zostają). Dalej krótki status (`Sprawdzanie dokumentu…`, `Konwersja dokumentu i sprawdzanie…`, `Pobieranie dokumentu i sprawdzanie…`), wynik: status całości ze znakiem ✓ ✗ ?, podsumowanie, blok dla adresu i dla etykiety z przyczynami, uwagi i dane geometryczne zwinięte, podgląd strony zwinięty. W trakcie przetwarzania nie da się uruchomić drugiego żądania.

## Testy

```
dotnet test Mass/AddressWindow/examples/Mass.AddressWindow.Examples.sln   # API, kilka sekund
cd Mass/AddressWindow/examples/ui && npm test                             # komponenty (Vitest)
```
