# Mass.AddressWindow

> Dokumentacja użytkownika i integracji (walidacja pism i nalepki R razem): [../docs/README.md](../docs/README.md).
> Przykładowa aplikacja (API + ekran), która używa tej biblioteki: [examples/README.md](examples/README.md).

Biblioteka .NET 8 do sprawdzania, czy pismo w PDF nadaje się do koperty z okienkiem. Sprawdza tylko pierwszą stronę, bo tylko ona jest widoczna w okienku koperty, i odpowiada na trzy pytania:

1. Czy w obszarze okna jest blok adresowy?
2. Czy tekst adresu mieści się w oknie, z zapasem na przesuwanie się kartki w kopercie?
3. Czy treść adresu spełnia reguły adresowania: liczba wierszy, długość wiersza, kod pocztowy NN-NNN w ostatnim wierszu, wielkość czcionki?

Tryb **jednego lub dwóch okienek** wybiera klasa wywołująca przy każdym wywołaniu (`WindowMode.Single` / `WindowMode.Double`).

W trybie dwóch okienek okna nie są sprawdzane tak samo. W **oknie adresata** szukany jest adres i dotyczą go wszystkie trzy pytania. W **oknie nadawcy** ma być **nalepka „R” listu poleconego** wstawiona jako grafika: sprawdzane jest, czy jest, czy mieści się w oknie i czy jej kod kreskowy zawiera poprawny numer nadawczy, krajowy albo zagraniczny (sekcja [Okno nadawcy: nalepka R](#okno-nadawcy-nalepka-r)).

Biblioteka przyjmuje tylko PDF. Dokument Word trzeba najpierw zapisać do PDF; przykładowe API wysyła go w tym celu do usługi konwersji (zamockowanej).

## Użycie

```csharp
using Mass.AddressWindow;
using Mass.AddressWindow.Pdf;

IPdfAddressWindowValidator validator = new PdfAddressWindowValidator();   // bezstanowy, można jako singleton

// koperta z jednym okienkiem: sprawdzany tylko adresat
var result = validator.ValidateFile(@"C:\pisma\wezwanie.pdf", WindowMode.Single);

// koperta z dwoma okienkami: adresat i nalepka R
var result2 = validator.Validate(pdfBytes, WindowMode.Double);

if (!result.IsValid)
{
    foreach (var issue in result.Errors)
        Console.WriteLine($"{issue.Code} [{issue.Window}]: {issue.Message}");
}

result.Summary;                                  // "okno adresata: błąd (wystaje z prawej o 9,7 mm); okno nadawcy: nie znaleziono nalepki R"
result.Preview!.DataUri;                         // "data:image/png;base64,..." do <img src>

var recipient = result.For(WindowRole.Recipient)!;
recipient.Block!.Lines;                          // wiersze wykrytego adresu
recipient.Overflow;                              // o ile mm tekst wystaje z każdej strony
```

Rejestracja w DI:

```csharp
services.AddSingleton<IPdfAddressWindowValidator, PdfAddressWindowValidator>();
```

Metody `IPdfAddressWindowValidator`: `Validate(byte[])`, `ValidateBase64(string)` (przyjmuje też `data:application/pdf;base64,...`), `Validate(Stream)`, `ValidateFile(string)`, `ValidateAsync(Stream)`. Każda przyjmuje `WindowMode`, opcjonalny `ValidationProfile` (układ koperty, reguły, oczekiwane dane) i `PdfValidationOptions`:

| Opcja | Domyślnie | Uwagi |
|---|---|---|
| `PageNumber` | 1 | strona do sprawdzenia |
| `IncludePreview` | `true` | `false` pomija renderowanie (szybciej, gdy podgląd nie jest potrzebny) |
| `PreviewDpi` | 96 | 36–600; przy 96 DPI strona A4 ma 794×1123 px, ok. 100–150 KB PNG |

Uszkodzony plik lub plik, który nie jest PDF (np. zaszyfrowany hasłem), nie rzuca wyjątku. Wynik ma wtedy `IsDocumentReadable == false` i zgłoszenie `INVALID_DOCUMENT`. Wyjątki są tylko dla błędów programisty: `null` na wejściu albo tryb `Double` z układem koperty bez okna nadawcy.

Aplikacja hostująca nie może mieć włączonego `InvariantGlobalization`: biblioteka formatuje milimetry w kulturze `pl-PL`.

## Wynik

- `Outcome`: `Valid`, `Invalid` albo `Inconclusive`. Niepoprawny, gdy którekolwiek okno ma błąd albo brak wymaganej treści; nierozstrzygnięty, gdy plik jest nieczytelny, strona nie ma warstwy tekstowej albo grafika w oknie nalepki nie ma czytelnego kodu (zgłoszenia o wadze `Inconclusive`); inaczej poprawny. `IsValid` to `Outcome == Valid`. To samo dla każdego okna (`WindowCheckResult.Outcome`).
- `RecipientMatch` (okno adresata): `NotChecked` bez `ValidationProfile.ExpectedRecipient` (wykryto blok przypominający adres), inaczej `Matched` albo `Mismatch`.
- `Windows`: po jednym `WindowCheckResult` na sprawdzane okno (w trybie `Double` dwa). Każdy zawiera wykryty blok (`Block`) albo nalepkę (`Label`), własne zgłoszenia i `Overflow`: o ile milimetrów treść wystaje poza okno z każdej strony (`LeftMm`, `TopMm`, `RightMm`, `BottomMm`; `Any`, `MaxMm`, `Describe()` → „z prawej o 9,7 mm”).
- `Issues`, `Errors`, `Warnings`, `DocumentIssues`: zgłoszenia ze stałym kodem (`IssueCodes`) i komunikatem po polsku.
- `Summary`, `Preview`, `PageNumber`, `PageCount`, `PageWidthMm`, `PageHeightMm`: opis dla człowieka i dane strony.

| Kod | Waga | Znaczenie |
|---|---|---|
| `WINDOW_NOT_FOUND` | błąd | w oknie nie ma tekstu, a na stronie nie ma bloku przypominającego adres |
| `ADDRESS_NOT_FOUND` | błąd | podano `ExpectedRecipient`, a żaden blok tekstu na stronie nie zawiera jego wierszy |
| `ADDRESS_MISMATCH` | błąd | adres w dokumencie nie zawiera wszystkich wierszy `ExpectedRecipient` |
| `LABEL_NOT_FOUND` | błąd | w oknie nadawcy nie ma grafiki, czyli nalepki R; komunikat zaznacza, gdy zamiast niej jest tam tekst |
| `LABEL_TOO_LARGE` | błąd | nalepka R jest większa niż obszar okna; podaje rozmiar nalepki i największy dopuszczalny |
| `LABEL_OUTSIDE_WINDOW` | błąd | nalepka R ma dobry rozmiar, ale wychodzi poza okno; podaje, z której strony i o ile mm, albo w którym innym oknie leży |
| `LABEL_TOO_CLOSE_TO_EDGE` | błąd | nalepka R jest w oknie, ale bliżej krawędzi niż `ClearanceMm` |
| `LABEL_UNREADABLE` | nierozstrzygnięte | z grafiki w oknie nie udało się odczytać kodu Code 128 / GS1-128 (obraz bez kodu, zbyt mały), więc nie wiadomo, czy to nalepka |
| `LABEL_NUMBER_INVALID` | błąd | tekst z kodu nie jest numerem nadawczym albo ma złą cyfrę kontrolną; komunikat podaje, jaka cyfra jest, a jaka powinna być |
| `LABEL_TYPE_NOT_ALLOWED` | błąd | numer jest krajowy, a reguły dopuszczają tylko zagraniczny (albo odwrotnie) |
| `LABEL_NUMBER_MISMATCH` | błąd | numer na nalepce różni się od oczekiwanego (`RegisteredLabelRules.ExpectedNumber`) |
| `LABEL_BARCODE_NOT_GS1` | ostrzeżenie | numer krajowy zakodowany jako zwykły Code 128 bez FNC1; skanery nie rozpoznają go jako SSCC |
| `LABEL_LOW_RESOLUTION` | ostrzeżenie | najwęższa kreska kodu ma w obrazie mniej niż 2,5 px: obraz został zmniejszony (Word przy zapisie do PDF zmniejsza obrazy do ok. 200 DPI) i wydruk może być nieostry |
| `ADDRESS_OUTSIDE_WINDOW` | błąd | tekst wychodzi poza okno (komunikat podaje, z której strony i o ile mm) albo leży w całości poza nim, także w drugim oknie koperty |
| `ADDRESS_TOO_CLOSE_TO_EDGE` | błąd | tekst jest w oknie, ale bliżej krawędzi niż `ClearanceMm` |
| `TEXT_ROTATED` | błąd | tekst obrócony lub pionowy |
| `TOO_FEW_LINES` / `TOO_MANY_LINES` | błąd | liczba wierszy poza zakresem |
| `LINE_TOO_LONG` | błąd | wiersz dłuższy niż `MaxCharactersPerLine` |
| `POSTAL_CODE_LINE_MISSING` | błąd | brak „NN-NNN Miejscowość” w ostatnim wierszu; podpowiada, gdy znajdzie np. „00061” |
| `TEXT_AFTER_POSTAL_CODE_LINE` | błąd | kod pocztowy nie jest w ostatnim wierszu |
| `FONT_TOO_SMALL` | błąd | czcionka mniejsza niż `MinFontSizePt` |
| `ADDRESS_EMPTY` | błąd | blok adresowy bez tekstu |
| `INVALID_DOCUMENT` | nierozstrzygnięte | plik nie jest poprawnym PDF |
| `NO_TEXT_LAYER` | nierozstrzygnięte | strona bez warstwy tekstowej: skan albo tekst zamieniony na krzywe; okna nie są sprawdzane |
| `OTHER_TEXT_IN_WINDOW` | ostrzeżenie | w oknie widać jeszcze inny tekst |
| `FONT_TOO_LARGE`, `DECORATED_TEXT`, `NON_LEFT_ALIGNMENT`, `UNUSUAL_CHARACTERS`, `EMPTY_LINE_INSIDE` | ostrzeżenie | typografia i czytelność maszynowa |
| `MERGE_FIELDS_PRESENT` | ostrzeżenie | szablon korespondencji seryjnej («Pole»); błędy kodu pocztowego są wtedy obniżane do ostrzeżeń |
| `UNEXPECTED_PAGE_FORMAT` | ostrzeżenie | strona nie jest A4 w pionie |

## Okno nadawcy: nalepka R

W trybie `Double` okno nadawcy nie służy do adresu nadawcy, tylko do nalepki R listu poleconego: grafiki z biblioteki `Mass.RLabel` albo innej nalepki z kodem Code 128 / GS1-128. Walidator nie sprawdza tam żadnych danych adresowych. Odpowiada na trzy pytania:

1. **Czy nalepka jest?** Szuka obrazu rastrowego, który najlepiej pokrywa się z oknem; gdy w oknie nie ma obrazu, przegląda też inne obrazy strony o proporcjach nalepki, żeby nalepkę w niewłaściwym oknie zgłosić jako leżącą poza oknem, a nie jako brak. Tekst w oknie, np. adres nadawcy, nie liczy się jako nalepka i daje `LABEL_NOT_FOUND`.
2. **Czy się mieści?** Cała nalepka musi leżeć w obszarze okna, z tym samym zapasem na przesuwanie się kartki co adres: `LABEL_TOO_LARGE`, `LABEL_OUTSIDE_WINDOW`, `LABEL_TOO_CLOSE_TO_EDGE`.
3. **Czy to właściwa nalepka?** Z obrazu odczytywany jest kod kreskowy, a z niego numer nadawczy. Numer musi mieć poprawną budowę i cyfrę kontrolną (krajowy: SSCC 20 cyfr, mod 10; zagraniczny: UPU S10 13 znaków, ważony mod 11, tak samo jak w `Mass.RLabel`), dopuszczalny rodzaj i, jeśli podano, zgadzać się z oczekiwanym numerem: `LABEL_UNREADABLE`, `LABEL_NUMBER_INVALID`, `LABEL_TYPE_NOT_ALLOWED`, `LABEL_NUMBER_MISMATCH`.

```csharp
var result = validator.Validate(pdfBytes, WindowMode.Double);

var labelWindow = result.For(WindowRole.Sender)!;
labelWindow.Content;                 // WindowContent.RegisteredLabel
labelWindow.Found;                   // czy w oknie jest grafika
labelWindow.Overflow;                // o ile mm nalepka wystaje z każdej strony
labelWindow.Block;                   // zawsze null: w tym oknie nie szukamy adresu

var label = labelWindow.Label!;      // null, gdy grafiki nie znaleziono
label.Bounds;                        // położenie i rozmiar nalepki na stronie, w mm
label.Number;                        // "00759007731512000621" albo "RR473124829PL"; null, gdy kodu nie udało się odczytać
label.HumanReadableNumber;           // "(00)75900773 1 51200062 1", jak pod kodem na nalepce
label.Type;                          // RegisteredMailType.Domestic / International
label.NumberIsValid;                 // budowa i cyfra kontrolna
label.Symbology;                     // "GS1-128" (krajowy) albo "Code 128" (zagraniczny)
label.WidthPx; label.BarcodeModulePx; // rozmiar obrazu i szerokość najwęższej kreski w pikselach
```

Reguły treści nalepki są w `ValidationProfile.LabelRules` (`RegisteredLabelRules`):

| Właściwość | Domyślnie | Znaczenie |
|---|---|---|
| `ReadBarcode` | `true` | `false` sprawdza tylko obecność i położenie, bez odczytu kodu |
| `AllowDomestic`, `AllowInternational` | `true`, `true` | dopuszczalne rodzaje numeru; niedopuszczalny daje `LABEL_TYPE_NOT_ALLOWED` |
| `ExpectedNumber` | `null` | numer, który ma być na nalepce, w dowolnym zapisie (spacje, nawiasy, małe litery są pomijane); inny daje `LABEL_NUMBER_MISMATCH` |
| `MinBarcodeModulePx` | 2,5 | poniżej tej szerokości najwęższej kreski (w pikselach obrazu) zgłaszane jest `LABEL_LOW_RESOLUTION` |

```csharp
// pismo dla konkretnej przesyłki: na nalepce musi być numer, który wydała pula, a adres musi być adresem odbiorcy
var profile = new ValidationProfile
{
    ExpectedRecipient = ["Pani Anna Zielińska", "ul. Długa 12/3", "20-020 Lublin"],
    LabelRules = new RegisteredLabelRules { ExpectedNumber = "(00) 7 5900773 1 51200062 1", AllowInternational = false },
};
var result = validator.Validate(pdfBytes, WindowMode.Double, profile);
```

**Jak czytany jest kod.** Obraz nalepki (PNG lub JPEG, przez SkiaSharp) jest zamieniany na skalę szarości, a kod Code 128 jest odczytywany z kilkudziesięciu wierszy obrazu dwiema metodami: z szerokości kresek mierzonych między pikselami, a gdy to zawiedzie, z próbkowania zaczernienia modułami, które znosi rozmycie. Odczyt jest przyjmowany dopiero po sprawdzeniu sumy kontrolnej Code 128, a wynik to odczyt powtórzony w największej liczbie wierszy. Dzięki temu czytelne są nalepki, które Word przy zapisie do PDF zmniejszył do ok. 200 DPI (najwęższa kreska 1,7 px); taki obraz dostaje jednak ostrzeżenie `LABEL_LOW_RESOLUTION`, bo wydrukowany kod może być nieostry.

**Rozmiar nalepki a koperta C65.** Okno nadawcy ma na kopercie 70×30 mm, ale przy luzie kartki 19×15 mm zawsze widoczny jest tylko obszar 51×15 mm strony, a po odjęciu 1 mm odstępu nalepka może mieć najwyżej **49×13 mm**. Domyślna nalepka z `Mass.RLabel` ma 65×25 mm i w tym układzie zawsze dostanie `LABEL_TOO_LARGE`. Trzeba ją wygenerować mniejszą (`WidthMm`, `HeightMm`) albo zmienić założenie o luzie kartki (`LetterPlay.Centered`, mniej bezpieczne). Przy 48 mm szerokości najwęższa kreska kodu ma ok. 0,21 mm przy 600 DPI, mniej niż 0,25 mm zalecane dla skanerów, więc taki rozmiar warto sprawdzić na wydruku.

**Ograniczenia.** Walidator czyta kod kreskowy, ale nie tekst wydrukowany pod nim, i nie sprawdza, czy numer istnieje w puli numerów ani czy nie został już użyty; to rola puli (`Rokoko/Mass.Api`). Rozpoznaje Code 128 i GS1-128 na obrazie rastrowym: nalepka narysowana w PDF wektorowo albo wpisana czcionką z kodem kreskowym nie zostanie znaleziona. Inna grafika w oknie nadawcy (np. logo) jest traktowana jak nalepka i kończy się `LABEL_UNREADABLE`.

**Adres nadawcy zamiast nalepki.** Poprzednie zachowanie (adres nadawcy sprawdzany regułami `SenderRules`) jest dostępne przez profil:

```csharp
var profile = new ValidationProfile { SenderWindowContent = WindowContent.Address };
```

## Konfiguracja

Wszystko jest w `ValidationProfile`. Można go przekazać w konstruktorze walidatora (profil domyślny) albo w pojedynczym wywołaniu. `ExpectedRecipient` (wiersze adresu odbiorcy) włącza szukanie konkretnego adresu na całej stronie i porównanie treści; bez niego walidator potwierdza tylko blok przypominający adres.

### Układ koperty

Domyślny układ to **koperta C65 229×114 mm** według specyfikacji dostawcy:

| Koperta | Okno adresata | Okno nadawcy |
|---|---|---|
| C65, jedno okienko (wzór 1760353344): `EnvelopeLayouts.C65SingleWindow` | 90×45 mm, 20 mm od prawej, 15 mm od dołu | – |
| C65, dwa okienka (wzór 1770196712): `EnvelopeLayouts.C65TwoWindows` (**domyślny**) | 90×45 mm, 20 mm od prawej, 15 mm od dołu | 70×30 mm, 28 mm od lewej, 20 mm od dołu |

Okno adresata jest takie samo w obu kopertach. Tryb `Single` na profilu domyślnym sprawdza więc poprawnie pisma do obu rodzajów kopert.

Okna podaje się tak jak w specyfikacji koperty, a biblioteka przelicza je na pierwszą stronę pisma (`EnvelopeLayout.FromEnvelope`):

```csharp
var layout = EnvelopeLayout.FromEnvelope(
    "C65, dwa okienka",
    envelopeWidth: 229, envelopeHeight: 114,
    recipientWindow: new EnvelopeWindow(Width: 90, Height: 45, FromRight: 20, FromBottom: 15),
    senderWindow:    new EnvelopeWindow(Width: 70, Height: 30, FromLeft: 28, FromBottom: 20));

var result = validator.Validate(stream, WindowMode.Double, new ValidationProfile { Layout = layout });
```

**Luz kartki w kopercie.** Złożona A4 ma 210×99 mm, a C65 ma 229×114 mm. Kartka może się więc przesunąć o 19 mm w poziomie i 15 mm w pionie. Domyślne założenie `LetterPlay.AnyPosition` sprawdza tylko ten obszar strony, który jest widoczny w oknie przy każdym położeniu kartki. Dla C65 wychodzi:

| Okno | Okno na kopercie | Obszar strony widoczny zawsze | Tekst musi się zmieścić w (1 mm tolerancji druku) |
|---|---|---|---|
| adresata | 90×45 mm | x 119–190, y 54–84 mm (71×30) | x 120–189, y 55–83 |
| nadawcy | 70×30 mm | x 28–79, y 64–79 mm (51×15) | x 29–78, y 65–78 |

Obszar widoczny zawsze jest mniejszy od okna, bo luz kartki w C65 jest duży. Mieści się w nim do 6 wierszy 10 pt adresata i nalepka 49×13 mm. Jeśli pisma są kopertowane maszynowo i kartka zawsze leży na środku, można przyjąć `LetterPlay.Centered`: okno jest wtedy przesunięte o połowę luzu, a odstęp od krawędzi wynosi 3 mm. To założenie jest mniej bezpieczne.

Współrzędne okien na stronie można też podać wprost (`new EnvelopeLayout(name, new AddressWindowSpec(name, new RectangleMm(...)))`), jak w układach DIN 5008 z oknem adresata po lewej: `DlSingleWindowDin5008B`, `DlSingleWindowDin5008A`, `DlTwoWindowsLeft`.

### Reguły treści

Domyślne reguły treści (`AddressContentRules`). Kolumna „Nadawca” dotyczy tylko profilu z `SenderWindowContent = WindowContent.Address`; domyślnie w oknie nadawcy jest nalepka R i reguł treści tam nie ma.

| Reguła | Adresat | Nadawca |
|---|---|---|
| wiersze | 3–6 | 2–5 |
| znaków w wierszu | ≤ 40 | ≤ 40 |
| czcionka | 8–14 pt | 8–14 pt |
| kod NN-NNN + miejscowość w ostatnim wierszu | tak | tak |
| adres zagraniczny (kraj WIELKIMI LITERAMI w ostatnim wierszu) | dozwolony | dozwolony |

Limity wzorowane są na zaleceniach Poczty Polskiej dla przesyłek sortowanych automatycznie. Przed wdrożeniem porównaj je z aktualną wersją wytycznych PP i z umową nadawczą. Każdy limit można nadpisać.

## Jak to działa

**Tekst.** Pozycje liter czyta [PdfPig](https://github.com/UglyToad/PdfPig) (Apache 2.0, czysty C#). Obrys każdej litery liczony jest z jej punktu początkowego, szerokości znaku zapisanej w PDF (tablica `/Widths`) i wielkości czcionki (0,8 em nad linią bazową, 0,92 em dla wielkich liter z ogonkami i kreskami, 0,22 em pod nią). Celowo nie są używane obrysy glifów z fontu: PDF-y z Worda i wielu innych programów nie mają osadzonych fontów, a wtedy PdfPig na Windows bierze font z systemu, a na Linuksie podstawia ramkę całego fontu, co dawało różne wyniki na obu systemach. Metryki z PDF są identyczne wszędzie. Litery są łączone w wiersze (wspólna linia bazowa, przerwa powyżej 3 em rozdziela kolumny, np. nadawcę i datę), a wiersze w bloki (odstęp do 0,6 wysokości wiersza i wspólny zakres w poziomie). Obrót strony (`/Rotate`) jest uwzględniany; tekst obrócony względem strony daje `TEXT_ROTATED`.

**Wybór bloku.** Dla okna adresata wybierany jest blok przypominający adres (2–8 krótkich wierszy, kod pocztowy albo kraj w ostatnim) o najlepszym wyniku „część wspólna z oknem × udział bloku leżący w oknie”; dzięki temu adres wygrywa z długą treścią pisma, która tylko przecina okno. Gdy w oknie nie ma takiego bloku, szukany jest na całej stronie i zgłaszany jako leżący poza oknem, z nazwą okna, w którym faktycznie jest. Z `ExpectedRecipient` szukany jest blok zawierający wiersze odbiorcy.

**Podgląd** (`ValidationPreview`): strona wyrenderowana z PDF przez [PDFtoImage](https://github.com/sungaila/PDFtoImage) (MIT) na silniku PDFium (BSD), z naniesionymi obszarami okien (zielone = OK, czerwone = błąd lub brak, żółte = nierozstrzygnięte), niebieską ramką wykrytego adresu lub nalepki i czerwonym zaznaczeniem części, która wystaje poza okno. Nakładki rysuje SkiaSharp (MIT) z osadzonym fontem Liberation Sans (OFL). `RenderedFromPdf == false` oznacza, że renderowanie się nie powiodło i podgląd jest schematem z samych prostokątów liter.

**Linux / GCP.** Nic nie trzeba instalować w obrazie: PDFium i SkiaSharp mają natywne biblioteki w pakietach NuGet, a font jest w DLL. Sprawdzone w kontenerze `mcr.microsoft.com/dotnet/sdk:8.0` (Debian 12, bez fontów i bez fontconfig).

**Ograniczenia.** Skan albo PDF z tekstem zamienionym na krzywe nie ma warstwy tekstowej: wynik jest nierozstrzygnięty z `NO_TEXT_LAYER`, a nie fałszywe „OK”. Podkreślenie w PDF to osobna linia, nie atrybut tekstu, więc nie jest wykrywane. PDF zaszyfrowany hasłem daje `INVALID_DOCUMENT`.

## Struktura

```
AddressWindow/
├─ Mass.AddressWindow.sln              biblioteka, testy, generatory przykładów
├─ src/Mass.AddressWindow/
│  ├─ Model/                           WindowMode, EnvelopeLayout(s), AddressContentRules, RegisteredLabelRules, ValidationProfile, RectangleMm
│  ├─ Results/                         wynik, zgłoszenia, kody, WindowOverflow
│  ├─ Rules/                           wybór bloku dla okna (WindowChecker), reguły geometrii i treści, okno nalepki R (LabelWindowChecker); Labels/: dekoder Code 128, numer nadawczy
│  └─ Pdf/                             IPdfAddressWindowValidator, PdfAddressWindowValidator; Text/: PdfPig → wiersze → bloki, obrazy; Preview/: render strony + nakładki
├─ tests/Mass.AddressWindow.Tests/     xUnit; minimalne PDF-y generowane w kodzie (MiniPdf) + PDF-y z Worda z samples/pdf
├─ samples/                            7 pism DOCX i ich PDF-y do testów manualnych + generatory (patrz samples/README.md)
└─ examples/                           przykładowa aplikacja: API .NET (PDF/DOCX, mock aplikacji zewnętrznej) i ekran Angular (examples/README.md)
```

```
dotnet test Mass/AddressWindow/Mass.AddressWindow.sln              # biblioteka
dotnet test Mass/AddressWindow/examples/Mass.AddressWindow.Examples.sln   # przykładowe API
```
