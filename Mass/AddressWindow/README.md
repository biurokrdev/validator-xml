# Mass.AddressWindow

> Dokumentacja użytkownika i integracji (walidacja pism i nalepki R razem): [../docs/README.md](../docs/README.md).

Dwie biblioteki .NET 8 do sprawdzania, czy pismo nadaje się do koperty z okienkiem:

- **`Mass.AddressWindow`**: dokumenty Word (DOCX), bez zależności zewnętrznych.
- **`Mass.AddressWindow.Pdf`**: pliki PDF, z podglądem strony pokazującym okna i wykryty adres (sekcja [PDF](#pdf) niżej).

Obie sprawdzają tylko pierwszą stronę, bo tylko ona jest widoczna w okienku koperty, i używają tych samych układów kopert, reguł, kodów zgłoszeń i typów wyniku. Walidacja odpowiada na trzy pytania:

1. Czy w obszarze okna jest blok adresowy?
2. Czy tekst adresu mieści się w oknie, z zapasem na przesuwanie się kartki w kopercie?
3. Czy treść adresu spełnia reguły adresowania: liczba wierszy, długość wiersza, kod pocztowy NN-NNN w ostatnim wierszu, wielkość czcionki?

Tryb **jednego lub dwóch okienek** wybiera klasa wywołująca przy każdym wywołaniu (`WindowMode.Single` / `WindowMode.Double`).

W trybie dwóch okienek okna nie są sprawdzane tak samo. W **oknie adresata** szukany jest adres i dotyczą go wszystkie trzy pytania. W **oknie nadawcy** ma być **nalepka „R” listu poleconego** wstawiona jako grafika: sprawdzane jest tylko, czy jest i czy mieści się w oknie (sekcja [Okno nadawcy: nalepka R](#okno-nadawcy-nalepka-r)).

Biblioteka DOCX nie ma zależności zewnętrznych. DOCX jest czytany własnym parserem na `System.IO.Compression` i `System.Xml.Linq`, bez OpenXML SDK i bez bibliotek komercyjnych.

## Użycie (DOCX)

```csharp
using Mass.AddressWindow;

IAddressWindowValidator validator = new AddressWindowValidator();   // bezstanowy, można jako singleton

// koperta z jednym okienkiem: sprawdzany tylko adresat
var result = validator.ValidateFile(@"C:\pisma\wezwanie.docx", WindowMode.Single);

// koperta z dwoma okienkami: adresat i nadawca
var result2 = validator.Validate(docxBytes, WindowMode.Double);

if (!result.IsValid)
{
    foreach (var issue in result.Errors)
        Console.WriteLine($"{issue.Code} [{issue.Window}]: {issue.Message}");
}

var recipient = result.For(WindowRole.Recipient);
// recipient.Block.Lines, recipient.Block.TextBounds, recipient.Block.Kind (TextBox/Frame/TableCell/…)
```

Rejestracja w DI:

```csharp
services.AddSingleton<IAddressWindowValidator, AddressWindowValidator>();
```

Metody interfejsu `IAddressWindowValidator`:

| Metoda | Wejście |
|---|---|
| `Validate(Stream, WindowMode, ValidationProfile?)` | strumień DOCX (nie jest zamykany) |
| `Validate(byte[], WindowMode, ValidationProfile?)` | zawartość pliku |
| `ValidateFile(string, WindowMode, ValidationProfile?)` | ścieżka do pliku |
| `ValidateAsync(Stream, WindowMode, ValidationProfile?, CancellationToken)` | strumień bez możliwości przewijania (sieć, blob) |

Uszkodzony plik lub plik, który nie jest DOCX (np. `.doc`, dokument zaszyfrowany hasłem), nie rzuca wyjątku. Wynik ma wtedy `IsDocumentReadable == false` i błąd `INVALID_DOCUMENT`. Wyjątki są tylko dla błędów programisty: `null` na wejściu albo tryb `Double` z układem koperty bez okna nadawcy.

## Wynik

- `IsValid`: dokument odczytany, wszystkie wymagane okna znalezione i bez błędów.
- `Windows`: po jednym `WindowCheckResult` na sprawdzane okno (w trybie `Double` dwa). Każdy zawiera wykryty blok (`Block`), własne zgłoszenia i `Overflow`: o ile milimetrów tekst wystaje poza okno z każdej strony (`LeftMm`, `TopMm`, `RightMm`, `BottomMm`; `Any`, `MaxMm`, `Describe()` → „z prawej o 9,7 mm”).
- `Issues`, `Errors`, `Warnings`: zgłoszenia ze stałym kodem (`IssueCodes`) i komunikatem po polsku.

| Kod | Waga | Znaczenie |
|---|---|---|
| `WINDOW_NOT_FOUND` | błąd | w oknie nie ma tekstu (brak okienka adresowego) |
| `LABEL_NOT_FOUND` | błąd | w oknie nadawcy nie ma grafiki, czyli nalepki R; komunikat zaznacza, gdy zamiast niej jest tam tekst |
| `LABEL_TOO_LARGE` | błąd | nalepka R jest większa niż obszar okna; podaje rozmiar nalepki i największy dopuszczalny |
| `LABEL_OUTSIDE_WINDOW` | błąd | nalepka R ma dobry rozmiar, ale wychodzi poza okno; podaje, z której strony i o ile mm |
| `LABEL_TOO_CLOSE_TO_EDGE` | błąd | nalepka R jest w oknie, ale bliżej krawędzi niż `ClearanceMm` |
| `ADDRESS_OUTSIDE_WINDOW` | błąd | tekst wychodzi poza okno; komunikat podaje, z której strony i o ile mm |
| `ADDRESS_TOO_CLOSE_TO_EDGE` | błąd | tekst jest w oknie, ale bliżej krawędzi niż `ClearanceMm` |
| `TEXT_ROTATED` | błąd | tekst obrócony lub pionowy |
| `TOO_FEW_LINES` / `TOO_MANY_LINES` | błąd | liczba wierszy poza zakresem |
| `LINE_TOO_LONG` | błąd | wiersz dłuższy niż `MaxCharactersPerLine` |
| `POSTAL_CODE_LINE_MISSING` | błąd | brak „NN-NNN Miejscowość” w ostatnim wierszu; podpowiada, gdy znajdzie np. „00061” |
| `TEXT_AFTER_POSTAL_CODE_LINE` | błąd | kod pocztowy nie jest w ostatnim wierszu |
| `FONT_TOO_SMALL` | błąd | czcionka mniejsza niż `MinFontSizePt` |
| `ADDRESS_EMPTY` | błąd | blok adresowy bez tekstu |
| `INVALID_DOCUMENT` | błąd | plik nie jest poprawnym DOCX/PDF |
| `NO_TEXT_LAYER` | błąd | (PDF) strona bez warstwy tekstowej: skan albo tekst zamieniony na krzywe |
| `TEXT_OVERFLOWS_CONTAINER` | ostrzeżenie | tekst wyższy niż pole tekstowe o stałej wysokości (Word utnie wiersze) |
| `LINE_WRAPS` | ostrzeżenie | wiersz prawdopodobnie się zawinie |
| `OTHER_TEXT_IN_WINDOW` | ostrzeżenie | w oknie widać jeszcze inny tekst |
| `FONT_TOO_LARGE`, `DECORATED_TEXT`, `NON_LEFT_ALIGNMENT`, `UNUSUAL_CHARACTERS`, `EMPTY_LINE_INSIDE` | ostrzeżenie | typografia i czytelność maszynowa |
| `MERGE_FIELDS_PRESENT` | ostrzeżenie | szablon korespondencji seryjnej («Pole»); błędy kodu pocztowego są wtedy obniżane do ostrzeżeń |
| `UNEXPECTED_PAGE_FORMAT` | ostrzeżenie | strona nie jest A4 w pionie |
| `POSITION_ESTIMATED` | informacja | położenie wyliczone w przybliżeniu (patrz niżej) |

## Okno nadawcy: nalepka R

W trybie `Double` okno nadawcy nie służy do adresu nadawcy, tylko do nalepki R listu poleconego (np. grafiki z biblioteki `Mass.RLabel`). Walidator nie sprawdza tam żadnych danych adresowych. Odpowiada na dwa pytania:

1. Czy w oknie jest nalepka, czyli grafika: obraz wstawiony do DOCX albo obraz rastrowy na stronie PDF?
2. Czy cała nalepka mieści się w obszarze okna, z tym samym zapasem na przesuwanie się kartki co adres?

```csharp
var result = validator.Validate(pdfBytes, WindowMode.Double);

var labelWindow = result.For(WindowRole.Sender)!;
labelWindow.Content;        // WindowContent.RegisteredLabel
labelWindow.Found;          // czy w oknie jest grafika
labelWindow.Label?.Bounds;  // położenie i rozmiar nalepki na stronie, w mm
labelWindow.Overflow;       // o ile mm nalepka wystaje z każdej strony
labelWindow.Block;          // zawsze null: w tym oknie nie szukamy adresu
```

Za nalepkę uznawana jest grafika, która najlepiej pokrywa się z oknem. Grafika poza oknem (np. logo w nagłówku) nie jest brana pod uwagę. Jeśli obok nalepki w oknie widać tekst, wynik ma ostrzeżenie `OTHER_TEXT_IN_WINDOW`. Adres nadawcy wpisany w oknie zamiast nalepki daje `LABEL_NOT_FOUND`.

**Rozmiar nalepki a koperta C65.** Okno nadawcy ma na kopercie 70×30 mm, ale przy luzie kartki 19×15 mm zawsze widoczny jest tylko obszar 51×15 mm strony, a po odjęciu 1 mm odstępu nalepka może mieć najwyżej **49×13 mm**. Domyślna nalepka z `Mass.RLabel` ma 65×25 mm i w tym układzie zawsze dostanie `LABEL_TOO_LARGE`. Trzeba ją wygenerować mniejszą (`WidthMm`, `HeightMm`) albo zmienić założenie o luzie kartki (`LetterPlay.Centered`, mniej bezpieczne). Przy 48 mm szerokości najwęższa kreska kodu ma ok. 0,17 mm, mniej niż 0,25 mm zalecane dla skanerów, więc taki rozmiar warto sprawdzić na wydruku.

**Ograniczenia.** Walidator rozpoznaje nalepkę po tym, że jest grafiką w oknie; nie odczytuje kodu kreskowego ani numeru i nie odróżni nalepki R od innego obrazka w tym miejscu. W DOCX obsługiwane są obrazy DrawingML (także w grupach kształtów); dla obrazu wstawionego „w tekście” położenie jest szacowane (`POSITION_ESTIMATED`). Starsze obrazy VML (`v:imagedata`) nie są wykrywane. W PDF wykrywane są obrazy rastrowe; nalepka narysowana wektorowo nie zostanie znaleziona.

**Adres nadawcy zamiast nalepki.** Poprzednie zachowanie (adres nadawcy sprawdzany regułami `SenderRules`) jest dostępne przez profil:

```csharp
var profile = new ValidationProfile { SenderWindowContent = WindowContent.Address };
```

## Konfiguracja

Wszystko jest w `ValidationProfile`. Można go przekazać w konstruktorze walidatora (profil domyślny) albo w pojedynczym wywołaniu.

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

Obszar widoczny zawsze jest mniejszy od okna, bo luz kartki w C65 jest duży. Mieści się w nim do 6 wierszy 10 pt adresata i do 3 wierszy 9 pt nadawcy. Jeśli pisma są kopertowane maszynowo i kartka zawsze leży na środku, można przyjąć `LetterPlay.Centered`: okno jest wtedy przesunięte o połowę luzu, a odstęp od krawędzi wynosi 3 mm. To założenie jest mniej bezpieczne.

Współrzędne okien na stronie można też podać wprost (`new EnvelopeLayout(name, new AddressWindowSpec(name, new RectangleMm(...)))`), jak w układach DIN 5008 z oknem adresata po lewej: `DlSingleWindowDin5008B`, `DlSingleWindowDin5008A`, `DlTwoWindowsLeft`.

Do okna można dodać `ElementNameHint` (nazwa pola tekstowego, tag kontrolki zawartości albo zakładka). Element o tej nazwie zostanie wtedy uznany za blok adresowy, nawet jeśli leży poza oknem.

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

## Jak wykrywany jest blok adresowy

Parser przegląda pierwszą stronę: treść dokumentu i nagłówek pierwszej strony (z uwzględnieniem „Inna pierwsza strona”). Zbiera bloki tekstu z:

| Źródło | Dokładność położenia |
|---|---|
| pole tekstowe DrawingML (`wps`), także w grupach kształtów | dokładna przy kotwiczeniu do strony lub marginesu |
| pole tekstowe VML (starsze dokumenty) | j.w. |
| ramka akapitu (`w:framePr`) | dokładna przy kotwiczeniu do strony lub marginesu |
| komórka tabeli pływającej (`w:tblpPr`) | dokładna dla wierszy o stałej wysokości |
| zwykłe akapity i tabele w przepływie tekstu | **szacowana** |

Tekst w przepływie dzielony jest na bloki przy pustym akapicie, dużym odstępie przed lub po akapicie i przy zmianie wyrównania (np. data po prawej). Ramki i kształty zakotwiczone do strony z oblewaniem tekstem są traktowane jak przeszkody: akapit, który na nie zachodzi (łącznie z odstępem przed nim), trafia obok obiektu albo pod niego, tak jak w Wordzie.

Dla każdego okna wybierany jest blok o najlepszym wyniku „część wspólna z oknem × udział bloku leżący w oknie”. Dzięki temu pole z adresem wygrywa z długą treścią pisma, która tylko przecina okno. Jeśli okno ma `ElementNameHint`, pierwszeństwo ma blok o tej nazwie. Kształty z `mc:AlternateContent` liczone są raz: wersja DrawingML jest brana pod uwagę, a zapasowa VML pomijana.

**Ograniczenie:** DOCX nie przechowuje wyniku składu tekstu. Wysokość i szerokość tekstu biblioteka szacuje z wielkości czcionki, interlinii, wcięć i średniej szerokości znaku (`AverageCharacterWidthEm`, `LineHeightFactor` w profilu). Dla pól tekstowych, ramek i tabel pływających daje to dokładność rzędu ułamka milimetra na krawędziach kontenera. Dla tekstu w zwykłym przepływie dokumentu błąd może sięgać kilku milimetrów, dlatego wynik ma wtedy informację `POSITION_ESTIMATED`. Szablony pism do kopert z okienkiem powinny trzymać adres w polu tekstowym albo ramce zakotwiczonej do strony, bo wtedy walidacja jest wiarygodna.

Nie są obsługiwane: kształty w grupach VML (`v:group`), kanwy rysunku, pliki `.doc` i dokumenty zaszyfrowane.

## PDF

`Mass.AddressWindow.Pdf` sprawdza gotowe pliki PDF (np. pisma wygenerowane z szablonu przed wysyłką do drukarni). Wejście to bajty, Base64, strumień albo ścieżka; wynik to ta sama walidacja co dla DOCX plus podgląd strony w PNG.

```csharp
using Mass.AddressWindow;
using Mass.AddressWindow.Pdf;

IPdfAddressWindowValidator validator = new PdfAddressWindowValidator();   // bezstanowy, singleton

var result = validator.ValidateBase64(pdfBase64, WindowMode.Double);       // przyjmuje też "data:application/pdf;base64,..."

result.IsValid;                                  // wszystkie okna OK
result.Summary;                                  // "okno adresata: błąd (wystaje z prawej o 9,7 mm); okno nadawcy: nie znaleziono adresu"
result.For(WindowRole.Recipient)!.Overflow;      // WindowOverflow: LeftMm/TopMm/RightMm/BottomMm
result.Issues;                                   // te same kody i komunikaty co dla DOCX
result.Preview!.DataUri;                         // "data:image/png;base64,..." do <img src>
result.Preview!.Png;                             // bajty PNG
```

Metody `IPdfAddressWindowValidator`: `Validate(byte[])`, `ValidateBase64(string)`, `Validate(Stream)`, `ValidateFile(string)`, `ValidateAsync(Stream)`; każda przyjmuje `WindowMode`, opcjonalny `ValidationProfile` (układ koperty, reguły) i `PdfValidationOptions`:

| Opcja | Domyślnie | Uwagi |
|---|---|---|
| `PageNumber` | 1 | strona do sprawdzenia |
| `IncludePreview` | `true` | `false` pomija renderowanie (szybciej, gdy podgląd nie jest potrzebny) |
| `PreviewDpi` | 96 | 36–600; przy 96 DPI strona A4 ma 794×1123 px, ok. 100–150 KB PNG |

**Podgląd** (`ValidationPreview`): strona wyrenderowana z PDF z naniesionymi obszarami okien (zielone = OK, czerwone = błąd lub brak adresu), niebieską ramką wykrytego adresu i czerwonym zaznaczeniem części adresu, która wystaje poza okno, z podpisem „adres wystaje: z prawej o 9,7 mm”. Obszary okien to obszar strony widoczny w oknie przy każdym położeniu kartki (patrz „Luz kartki w kopercie”). `RenderedFromPdf == false` oznacza, że renderowanie PDFium się nie powiodło i podgląd jest schematem z samych prostokątów liter.

**Jak to działa.** Pozycje liter czyta [PdfPig](https://github.com/UglyToad/PdfPig) (Apache 2.0, czysty C#). Obrys każdej litery liczony jest z jej punktu początkowego, szerokości znaku zapisanej w PDF (tablica `/Widths`) i wielkości czcionki (0,8 em nad linią bazową, 0,92 em dla wielkich liter z ogonkami i kreskami, 0,22 em pod nią). Celowo nie są używane obrysy glifów z fontu: PDF-y z Worda i wielu innych programów nie mają osadzonych fontów, a wtedy PdfPig na Windows bierze font z systemu, a na Linuksie (bez fontów) podstawia ramkę całego fontu, co dawało różne wyniki na obu systemach. Metryki z PDF są identyczne wszędzie. Litery są łączone w wiersze (wspólna linia bazowa, przerwa powyżej 3 em rozdziela kolumny, np. nadawcę i datę), a wiersze w bloki (odstęp do 0,6 wysokości wiersza i wspólny zakres w poziomie). Dalej działa ten sam wybór bloku i te same reguły co dla DOCX. Położenie jest dokładne (bez `POSITION_ESTIMATED`), bo PDF zawiera wynik składu. Obrót strony (`/Rotate`) jest uwzględniany; tekst obrócony względem strony daje `TEXT_ROTATED`. Podgląd renderuje [PDFtoImage](https://github.com/sungaila/PDFtoImage) (MIT) na silniku PDFium (BSD) z natywnymi bibliotekami dla Windows, Linux (glibc i musl), macOS; rysowanie nakładek robi SkiaSharp (MIT) z osadzonym fontem Liberation Sans (OFL).

**Linux / GCP.** Nic nie trzeba instalować w obrazie: PDFium i SkiaSharp (wariant `NoDependencies`) mają natywne biblioteki w pakietach NuGet, a font jest w DLL. Sprawdzone w kontenerze `mcr.microsoft.com/dotnet/sdk:8.0` (Debian 12, bez fontów i bez fontconfig): wszystkie testy przechodzą, a podglądy renderują się tak samo jak na Windows.

**Ograniczenia.** Skan albo PDF z tekstem zamienionym na krzywe nie ma warstwy tekstowej: wynik to `NO_TEXT_LAYER` + `WINDOW_NOT_FOUND`, a nie fałszywe „OK”. Podkreślenie w PDF to osobna linia, nie atrybut tekstu, więc nie jest wykrywane. PDF zaszyfrowany hasłem daje `INVALID_DOCUMENT`.

## Struktura

```
AddressWindow/
├─ src/Mass.AddressWindow/
│  ├─ IAddressWindowValidator.cs      interfejs publiczny (DOCX)
│  ├─ AddressWindowValidator.cs       implementacja DOCX
│  ├─ Model/                          WindowMode, EnvelopeLayout(s), AddressContentRules, ValidationProfile, RectangleMm
│  ├─ Results/                        wynik, zgłoszenia, kody, WindowOverflow
│  ├─ Docx/                           własny parser DOCX: pakiet ZIP, style, tekst, geometria pierwszej strony
│  └─ Rules/                          wspólny silnik: wybór bloku dla okna, reguły geometrii i treści (IAddressCandidate), okno nalepki R (LabelWindowChecker)
├─ src/Mass.AddressWindow.Pdf/
│  ├─ IPdfAddressWindowValidator.cs   interfejs publiczny (PDF)
│  ├─ PdfAddressWindowValidator.cs    implementacja PDF
│  ├─ Text/                           PdfPig: litery → wiersze → bloki, geometria strony
│  └─ Preview/                        render strony (PDFium) + nakładki (SkiaSharp)
├─ tests/Mass.AddressWindow.Tests/    xUnit; dokumenty testowe generowane w kodzie (TestDocx)
├─ tests/Mass.AddressWindow.Pdf.Tests/ xUnit; minimalne PDF-y generowane w kodzie (MiniPdf) + PDF-y z Worda z samples/pdf
└─ samples/                           5 pism DOCX i ich PDF-y do testów manualnych + generatory (patrz samples/README.md)
```

```
dotnet test Mass/AddressWindow/Mass.AddressWindow.sln
```
