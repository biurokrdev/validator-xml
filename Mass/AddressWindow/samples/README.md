# Pliki do testów manualnych

Siedem pism A4 do koperty **C65 229×114 mm** (specyfikacje dostawcy: wzór 1760353344 z jednym okienkiem, wzór 1770196712 z dwoma). Każde otwiera się w Wordzie bez naprawiania i mieści na jednej stronie. W trybie dwóch okienek w oknie adresata sprawdzany jest adres, a w oknie nadawcy nalepka R listu poleconego: czy jest (jako grafika), czy mieści się w oknie i czy jej kod kreskowy zawiera poprawny numer nadawczy.

Czerwone przerywane prostokąty to **obszar strony widoczny w oknie przy każdym położeniu kartki w kopercie**. Walidator sprawdza dokładnie ten obszar. Złożona A4 ma w C65 luz 19 mm w poziomie i 15 mm w pionie, więc obszar jest mniejszy od samego okna. To kształty pomocnicze bez tekstu, walidator je pomija.

| Okno | Na kopercie | Obszar strony widoczny zawsze | Zawartość musi się zmieścić w (1 mm tolerancji) |
|---|---|---|---|
| adresata | 90×45 mm, 20 mm od prawej, 15 mm od dołu | x 119–190, y 54–84 mm | x 120–189, y 55–83 |
| nadawcy (nalepka R) | 70×30 mm, 28 mm od lewej, 20 mm od dołu | x 28–79, y 64–79 mm | x 29–78, y 65–78, czyli nalepka najwyżej 49×13 mm |

## Pliki i oczekiwane wyniki

### 01_C65_jedno_okienko_poprawny.docx

Koperta z jednym okienkiem. Adresat jest w polu tekstowym (zapisanym jak w Wordzie: DrawingML z kopią VML). Papier firmowy stoi w nagłówku po lewej, poza oknami. Nalepki R nie ma.

| Tryb | Wynik | Zgłoszenia |
|---|---|---|
| `Single` | **poprawny** | brak |
| `Double` | niepoprawny | `LABEL_NOT_FOUND` dla okna nadawcy |

Do sprawdzenia ręcznie: ten sam plik jest dobry dla koperty z jednym okienkiem i zły dla koperty z dwoma. Tryb wybiera klasa wywołująca.

### 02_C65_dwa_okienka_poprawny.docx

Koperta z dwoma okienkami. W oknie nadawcy jest nalepka R 48×12 mm: grafika z `Mass.RLabel` (600 DPI, PNG) zakotwiczona do strony w (29,5; 65,5) mm, z numerem krajowym 00759007731512000621 (GS1-128). Walidator odczytuje ten numer z kodu i potwierdza cyfrę kontrolną. Adresat (4 wiersze, firma) jest w polu tekstowym. Dane nadawcy są w nagłówku, poza oknami.

| Tryb | Wynik | Zgłoszenia |
|---|---|---|
| `Single` | **poprawny** | brak |
| `Double` | **poprawny** | brak |

### 03_C65_bledy_adresu.docx

Zbiór typowych błędów adresu w jednym piśmie. Pole adresata jest przesunięte w prawo i koniec najdłuższego wiersza wystaje poza okno, co widać w podglądzie. Adres ma 7 wierszy, a jeden z nich jest za długi. Kod pocztowy ma postać „01447” zamiast „01-447”, a tekst jest pisany 7 pt kursywą. Pole jest za niskie, więc Word ucina ostatni wiersz. Nalepki R nie ma.

| Tryb | Wynik | Zgłoszenia |
|---|---|---|
| `Single` | niepoprawny | `ADDRESS_OUTSIDE_WINDOW` (z prawej o ok. 10 mm), `TOO_MANY_LINES`, `LINE_TOO_LONG`, `POSTAL_CODE_LINE_MISSING` (z podpowiedzią „01447”), `FONT_TOO_SMALL`; ostrzeżenia `TEXT_OVERFLOWS_CONTAINER`, `LINE_WRAPS`, `DECORATED_TEXT` |
| `Double` | niepoprawny | jak wyżej + `LABEL_NOT_FOUND` dla okna nadawcy |

### 04_C65_ramka_i_adres_w_tresci.docx

Pismo bez pól tekstowych. Adresat to zwykłe akapity w treści, przesunięte do okna wcięciem z lewej (95 mm) i odstępem przed pierwszym akapitem. W oknie nadawcy jest adres nadawcy w ramce akapitu (Wstaw → Ramka), czyli tekst w miejscu, gdzie powinna być nalepka R.

| Tryb | Wynik | Zgłoszenia |
|---|---|---|
| `Single` | **poprawny** | – |
| `Double` | niepoprawny | `LABEL_NOT_FOUND` dla okna nadawcy, z dopiskiem, że w oknie jest tekst, a nalepka musi być grafiką |

Ramka nadawcy i adres adresata leżą na tej samej wysokości. Word nie przesuwa wciętego adresu, bo ramka nie zachodzi na obszar jego tekstu (sprawdzone w Wordzie). Walidator robi to samo. Pod adresem jest pusty akapit o stałej wysokości 30 mm, który prowadzi treść obok ramki. Gdyby zamiast niego ustawić duży „Odstęp przed” w akapicie „Znak sprawy”, Word przeniósłby ten akapit na prawo od ramki. Walidator odtwarza też to zachowanie. Pilnuje tego test `FrameWithTextWrapping_PushesOverlappingParagraphAside`.

### 05_C65_nalepka_R_za_duza.docx

Koperta z dwoma okienkami, adresat poprawny. Nalepka R ma standardowy rozmiar 65×25 mm (domyślny w `Mass.RLabel`), a obszar okna nadawcy widoczny zawsze ma 51×15 mm.

| Tryb | Wynik | Zgłoszenia |
|---|---|---|
| `Single` | **poprawny** | brak |
| `Double` | niepoprawny | `LABEL_TOO_LARGE` dla okna nadawcy: nalepka 65×25 mm, mieści się najwyżej 49×13 mm; numer jest mimo to odczytany |

### 06_C65_nalepka_R_zagraniczna.docx

Koperta z dwoma okienkami. Nalepka R 48×12 mm z numerem zagranicznym RR473124829PL (UPU S10, kod Code 128) i adresat zagraniczny z nazwą kraju wielkimi literami w ostatnim wierszu.

| Tryb | Wynik | Zgłoszenia |
|---|---|---|
| `Single` | **poprawny** | brak |
| `Double` | **poprawny** | brak; `Label.Number` = `RR473124829PL`, `Label.Type` = `International` |

### 07_C65_nalepka_R_zla_cyfra_kontrolna.docx

Jak 02, ale nalepka wygenerowana z numeru z literówką: 00759007731512000622, czyli cyfra kontrolna 2 zamiast 1 (generator wypuścił ją z wyłączoną kontrolą, `ValidateCheckDigit = false`). Położenie jest dobre.

| Tryb | Wynik | Zgłoszenia |
|---|---|---|
| `Single` | **poprawny** | brak |
| `Double` | niepoprawny | `LABEL_NUMBER_INVALID` dla okna nadawcy: „zła cyfra kontrolna: jest 2, powinna być 1” |

## Sugerowane testy manualne

1. **Czytelność komunikatów.** Uruchom walidację 03 w obu trybach i oceń, czy komunikaty mówią wprost, co poprawić.
2. **Przesunięcie pola.** W 01 przesuń pole adresata w lewo lub w górę o kilka mm (Formatuj kształt → Układ i właściwości → Położenie). Walidator powinien zgłosić `ADDRESS_TOO_CLOSE_TO_EDGE`, a po wyjściu poza czerwony obszar `ADDRESS_OUTSIDE_WINDOW`, podając stronę i odległość.
3. **Przesunięcie nalepki.** W 02 przesuń nalepkę R o kilka mm w dowolną stronę. Walidator powinien zgłosić `LABEL_TOO_CLOSE_TO_EDGE`, a po wyjściu poza czerwony obszar `LABEL_OUTSIDE_WINDOW`, podając stronę i odległość. Po powiększeniu nalepki ponad 49×13 mm powinno pojawić się `LABEL_TOO_LARGE`.
4. **Treść adresu.** W 02 dopisz wiersze, usuń myślnik z kodu pocztowego albo dopisz „POLSKA” pod kodem. Ostatnie jest dopuszczalne i nie powinno dać błędu.
5. **Dwa okienka.** Usuń z 02 nalepkę R albo wpisz w jej miejscu adres nadawcy w polu tekstowym. `Single` powinien przejść, a `Double` zwrócić `LABEL_NOT_FOUND` dla okna nadawcy.
7. **Treść nalepki.** Sprawdź 02 z oczekiwanym numerem `(00) 7 5900773 1 51200062 1` (powinien przejść) i z numerem `00759007731512000638` (`LABEL_NUMBER_MISMATCH`). W 02 zamień nalepkę na dowolne logo: `LABEL_UNREADABLE`. W 07 numer ma złą cyfrę kontrolną: `LABEL_NUMBER_INVALID`.
6. **Wydruk.** Wydrukuj 01 i 02 w skali 100%, złóż w trzy części i włóż do koperty C65. Przesuwaj kartkę w kopercie do wszystkich rogów. Czerwony obszar powinien być cały czas widoczny w okienku, a jego krawędzie powinny dotykać krawędzi okna przy skrajnych położeniach kartki.

## Wersje PDF

W `pdf/` jest tych samych siedem pism zapisanych do PDF przez Worda (Zapisz jako → PDF). Biblioteka waliduje tylko PDF, więc to one są używane w testach automatycznych; wersje DOCX służą do prób wariantu z konwersją w przykładowym API (mock zwraca PDF o tej samej nazwie) i do podglądu układu w Wordzie. Nalepka R jest w PDF zwykłym obrazem rastrowym i tak jest wykrywana. Word przy zapisie do PDF zmniejszył ją z 1134 px do 377 px szerokości (ok. 200 DPI), więc wersje PDF plików 02, 05, 06 i 07 mają dodatkowo ostrzeżenie `LABEL_LOW_RESOLUTION`; numer jest mimo to odczytany. Te same PDF-y zwraca mock usługi konwersji w przykładowym API, gdy wgrywany jest DOCX o tej samej nazwie.

Podglądy z naniesionymi oknami i wykrytym adresem (`pdf/preview/*_Single.png`, `*_Double.png`) generuje:

```
cd Mass/AddressWindow/samples/PdfPreviewGenerator
dotnet run
```

## Ponowne wygenerowanie

```
cd Mass/AddressWindow/samples/SampleGenerator
dotnet run
```

Generator nadpisuje pliki `.docx` w katalogu `samples/`. Wersje PDF trzeba potem zapisać ponownie z Worda (Zapisz jako → PDF).
