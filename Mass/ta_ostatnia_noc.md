# Mass (pula numerów R): pakiet dla testera automatyzującego

Esencja z `ku_ku_lafila.md` przycięta pod rozmowę z testerem automatyzującym i pod założenie zadań w Azure Boards. Pełne tabele danych, algorytmy krok po kroku i 20 przypadków biznesowych z uzasadnieniem są w `ku_ku_lafila.md`; ten plik ma wystarczyć do rozpoczęcia pracy bez czytania tamtego.

---

## 1. O czym rozmawiamy (5 zdań)

Mass to pula numerów nadawczych listów poleconych (R-ek) Poczty Polskiej. Operator zasila pulę rolką 1000 nalepek, wpisując pierwszy numer z rolki i ilość, a system generuje resztę. Aplikacje nadawcze pobierają z puli kolejne wolne numery i zmieniają ich stan: dostępny, zarezerwowany, użyty, anulowany. Najgroźniejsze błędy to: wydanie tego samego numeru dwóm listom, zła cyfra kontrolna (Poczta nie rozpozna numeru) i wpuszczenie do puli rolki innego rodzaju przesyłki. Testy automatyczne mają pilnować dokładnie tych trzech rzeczy plus poprawności formularza zasilania.

---

## 2. Środowisko i to, co już jest w repo

| Element | Wartość |
|---|---|
| API | `cd Mass.Api && dotnet run` → `http://localhost:5080`, Swagger `/swagger`, health `/health` |
| Baza | `Database:Provider = InMemory` w `appsettings.Development.json`; dane znikają po restarcie. PostgreSQL 13+ ze skryptem `001_create_registered_number_pool.sql` tylko do testów współbieżności i unikalności. |
| Front | `cd mass-ui && npm start` → `http://localhost:4200`, proxy `/api` → 5080. Playwright sam startuje front przez `webServer`. |
| Testy | `npx playwright test` (21 testów w `mass-ui/e2e/*.spec.ts`), `npm test` (Vitest). Brak `npm run lint`. |
| Zmienne | `E2E_BASE_URL` (domyślnie `http://localhost:4200`), `E2E_API_URL` (domyślnie `http://localhost:5080`) |
| Autor zmian | nagłówek `X-Editor` (UI: pole „Edytor” w nagłówku). Bez nagłówka API zapisuje `api`. Brak uwierzytelniania. |

**Helpery gotowe do użycia** (`mass-ui/e2e/helpers/`):

- `numbers.ts`: `gs1CheckDigit`, `s10CheckDigit`, `domestic(iac, kind, serial)`, `international(serial, svc, cc)`, `corruptCheckDigit(fullNumber)`, `uniqueSerialBase()`, `domesticRange(count, kind)`, `internationalRange(count)`. To jest lustro algorytmu z C#. Nie pisz własnych.
- `api.ts`: seedowanie danych i odczyt stanu backendu przez REST, do przygotowania warunków wstępnych i asercji po akcji w UI.

**Zasady, których trzymają się istniejące testy** (i nowe też mają):

1. Każdy test generuje własny losowy przedział (`uniqueSerialBase()`), nie czyścimy bazy, testy są niezależne od siebie i od poprzednich uruchomień.
2. Warunki wstępne przez API, UI tylko dla tego, co jest przedmiotem testu, stan po akcji potwierdzany przez API.
3. Selektory wyłącznie `data-testid`. Asercje tekstów po polsku, tak jak w UI.
4. `workers: 1`, bo statystyki i „Pobierz kolejny numer” zależą od wspólnego stanu puli.
5. `beforeAll` sprawdza `/health`; brak API kończy test czytelnym komunikatem, nie timeoutem.

**Selektory formularza „Zasilenie”:** `import-first`, `import-mode` (select: `count` | `last`), `import-count`, `import-last`, `import-check`, `import-submit`, `import-reset`, `import-error`, `check-result`, `check-first`, `check-last`, `check-requested`, `check-new`, `check-existing`, `check-ok`, `check-duplicated-some`, `check-duplicated-all`, `check-existing-table`, `import-result`, `import-added`, `import-skipped`.

---

## 3. Domena w pigułce (to, co trzeba wiedzieć, żeby pisać asercje)

### Numer krajowy (GS1 SSCC, 20 cyfr)

`00` + IAC (1-9) + `5900773` + S1 + serial (8 cyfr) + cyfra kontrolna. Przykład `00759007731512000621` = IAC 7, S1 1, serial 51200062, kontrolna 1. Na nalepce: `(00) 7 5900773 1 51200062 1`.

Cyfra kontrolna: z 17 cyfr (bez `00`), od prawej wagi 3,1,3,1…, suma, `(10 − suma mod 10) mod 10`. Tylko S1 = 1 lub 4 jest przesyłką poleconą; inne S1 aplikacja odrzuca.

### Numer zagraniczny (UPU S10, 13 znaków)

Wskaźnik `RA`…`RZ` + serial (8 cyfr) + cyfra kontrolna + kod kraju (2 litery). Przykład `RR473124829PL` = serial 47312482, kontrolna 9.

Cyfra kontrolna: wagi `8 6 4 2 3 5 9 7`, suma, `11 − (suma mod 11)`; wynik 10 → `0`, wynik 11 → `5`. Przykłady wyjątków: `RR473124850PL` (10 → 0), `RR473124885PL` (11 → 5).

### Reguły, które mają być pokryte asercjami

| Reguła | Skąd | Komunikat / kod |
|---|---|---|
| Normalizacja wpisu: spacje, myślniki, `(00)`, małe litery | formatter | wynik pokazuje numer kanoniczny |
| Zła cyfra kontrolna odrzuca przed zapisem | formatter | „Błędna cyfra kontrolna GS1: jest X, powinna być Y.” / „…S10: …” |
| 20 cyfr bez prefiksu PP | formatter | „20 cyfr, ale prefiks nie jest numerem SSCC Poczty Polskiej…” |
| Inna długość / śmieci | formatter | „Nieznany format: oczekiwano 20 cyfr SSCC lub 13 znaków UPU S10…” |
| S1 spoza {1,4} | zakres | „…ma S1 = N; przesyłka polecona ma 1 lub 4.” |
| Pierwszy i ostatni z różnych pul (typ, IAC, S1, wskaźnik, kraj) | zakres | „…należą do różnych pul (inny typ lub prefiks).” |
| Ostatni < pierwszy | zakres | „Zakres musi mieć co najmniej jeden numer.” |
| Ilość > 100 000 | zakres | „Zakres ma N numerów, limit to 100000.” |
| Serial > 99999999 | zakres | „Zakres wychodzi poza maksymalny numer seryjny 99999999.” |
| `count` i `lastNumber` naraz | API | 400 „Podaj dokładnie jedno z: count albo lastNumber.” |
| Ponowny import pomija istniejące | repozytorium | `added` / `skipped` w odpowiedzi |
| Przejścia stanów: Available→Reserved→Used, Reserved→Available, Available→Used, Available/Reserved→Cancelled; Used jest końcowy | encja | 409 „Przejście stanu niedozwolone.” |
| Numer spoza puli przy zmianie stanu | repozytorium | 404 „Numer nie istnieje w puli.” |
| Pula pusta przy `/acquire` | repozytorium | 409 „Pula numerów wyczerpana.” |
| Równoległa zmiana tego samego wiersza | EF `xmin` | 409 „Numer został w międzyczasie zmieniony przez kogoś innego.” |
| Pobieranie kolejnego: najniższy dostępny serial danego typu | repozytorium | `FOR UPDATE SKIP LOCKED` na PostgreSQL |

### API (`/api/registered-numbers`)

| Metoda | Ścieżka | Ciało | Kody |
|---|---|---|---|
| GET | `?type=&state=&search=&page=&pageSize=` | | 200 (pageSize obcinany do 500) |
| GET | `/stats` | | 200 |
| GET | `/{fullNumber}` | | 200, 400, 404 |
| POST | `/validate` | `{number}` | 200 |
| POST | `/ranges/check` | `{firstNumber, count \| lastNumber}` | 200, 400 |
| POST | `/ranges/import` | `{firstNumber, count \| lastNumber}` | 200, 400 |
| POST | `/{fullNumber}/state` | `{action: Reserve\|Use\|Release\|Cancel}` | 200, 400, 404, 409 |
| POST | `/acquire` | `{type: Domestic\|International}` | 200, 409 |

---

## 4. Zadania do Azure Boards

Struktura: 1 Epic → 4 Features → PBI (jeden na przypadek biznesowy) → Test Case pod PBI. Poniżej gotowe treści do wklejenia. Pola: **Tytuł**, **Opis** (w formie „Jako… chcę… żeby…”), **Kryteria akceptacji** (Given/When/Then), **Dane testowe** (konkretne numery), **Wskazówki automatyzacji**, **Tagi**, **Priorytet** (1 = blokuje wdrożenie, 2 = przed produkcją, 3 = następna iteracja).

Numery w danych testowych są policzone i poprawne. W automatach używaj `domesticRange()` / `internationalRange()` zamiast stałych, a stałe z tego pliku traktuj jako przykład oczekiwanej postaci i do asercji komunikatów.

### Epic: Mass, automatyzacja testów puli numerów R

Opis: Pokrycie testami automatycznymi (Playwright + API) formularza zasilania puli, cyklu życia numeru i walidacji numerów, tak żeby każdy merge był chroniony przed trzema krytycznymi błędami: duplikat numeru, zła cyfra kontrolna, rolka złego rodzaju przesyłki.

Definicja ukończenia Epica: wszystkie PBI P1 i P2 mają zielone testy w `mass-ui/e2e`, raport Playwright w CI, sekcja 2.6 z `TESTS.md` przejściona ręcznie na PostgreSQL i odnotowana w PBI-20.

---

### Feature A: Zasilanie puli (formularz „Zasilenie” + `/ranges/*`)

#### PBI-01 Zasilenie puli pełną rolką

**Opis.** Jako operator kancelarii chcę wpisać pierwszy numer z rolki i ilość, żeby system wygenerował i zapisał wszystkie numery z rolki z poprawnymi cyframi kontrolnymi.

**Kryteria akceptacji.**
- Given pusta pula dla danego prefiksu, When wpiszę pierwszy numer i ilość 1000 i kliknę „Sprawdź”, Then widzę typ, przedział od-do, 1000 w przedziale, 1000 nowych, 0 w puli i zielony komunikat, a „Zasil” jest aktywne.
- When kliknę „Zasil”, Then komunikat: dodano 1000, pominięto 0.
- Then API `GET ?search=<prefiks>` zwraca 1000 pozycji w stanie `Available` z edytorem z nagłówka.
- Then ostatni numer w wyniku sprawdzenia równa się oczekiwanemu (policzonemu helperem).
- To samo dla numeru zagranicznego.

**Dane testowe.**

| Typ | Pierwszy numer | Ilość | Oczekiwany ostatni |
|---|---|---|---|
| krajowy IAC 1 S1 1 | `00159007731100010009` | 1000 | `00159007731100019996` |
| krajowy IAC 3 S1 4 | `00359007734300030000` | 1000 | `00359007734300039997` |
| zagraniczny RR PL | `RR473124829PL` | 1000 | `RR473134812PL` |
| zagraniczny RB PL | `RB700000000PL` | 100 | `RB700000999PL` |

**Automatyzacja.** Istnieje E2E-IMP-02 i E2E-IMP-03 (`import-range.spec.ts`), rozszerzyć o asercję ostatniego numeru (`check-last` vs `range.numbers.at(-1)`) i o S1 = 4 (`domesticRange(1000, 4)`). Weryfikacja liczby przez `api.ts` (lista z `search`), nie przez przewijanie tabeli.

**Tagi.** mass, import, p1. **Priorytet.** 1.

#### PBI-02 Normalizacja zapisu numeru

**Opis.** Jako operator chcę wkleić numer tak, jak jest wydrukowany na nalepce (z nawiasem, spacjami, myślnikami, małymi literami), żeby nie musieć go ręcznie czyścić.

**Kryteria akceptacji.**
- Given numer w dowolnym z wariantów poniżej, When „Sprawdź” z ilością 1, Then `check-first` pokazuje numer kanoniczny, bez błędu.

**Dane testowe.**

| Wpis | Oczekiwany kanoniczny |
|---|---|
| `(00) 7 5900773 1 51200062 1` | `00759007731512000621` |
| `00-7-5900773-1-51200062-1` | `00759007731512000621` |
| `  00759007731512000621  ` | `00759007731512000621` |
| `rr 473124829 pl` | `RR473124829PL` |
| `RR-47312482-9-PL` | `RR473124829PL` |

**Automatyzacja.** Test parametryzowany (`for … of` po tablicy wariantów) w `import-range.spec.ts`. Wariant z nawiasem już jest w E2E-IMP-02; dodać pozostałe. Numery generuj helperem, a warianty buduj z kanonicznego przez funkcję formatującą w teście.

**Tagi.** mass, import, p2. **Priorytet.** 2.

#### PBI-03 Ponowne zasilenie tym samym przedziałem (duplikaty)

**Opis.** Jako operator chcę, żeby ponowne zasilenie rolką, która już częściowo jest w puli, dodało tylko brakujące numery i pokazało mi, które już są, żeby dwie osoby nie zdublowały numerów, a dosypanie końcówki rolki było możliwe.

**Kryteria akceptacji.**
- Given w puli jest 5 pierwszych numerów przedziału (seed przez API), When „Sprawdź” z tym samym pierwszym numerem i ilością 7, Then 2 nowe, 5 w puli, żółte ostrzeżenie `check-duplicated-some`, tabela `check-existing-table` z 5 wierszami (numer, stan, edytor), „Zasil” aktywne.
- When „Zasil”, Then dodano 2, pominięto 5; API zwraca 7 pozycji.
- Given cały przedział w puli, When „Sprawdź”, Then `check-duplicated-all`, 0 nowych, „Zasil” nieaktywne.
- Nakładka na początku przedziału i przedział przylegający dają odpowiednio 2/2 i 3/0.

**Dane testowe** (krajowe IAC 5, S1 1, serial 55000000…55000011, kolejność kroków obowiązkowa):

| Krok | Pierwszy numer | Ilość / ostatni | Oczekiwane nowe / w puli |
|---|---|---|---|
| 1 | `00559007731550000009` | 5 | 5 / 0 |
| 2 | `00559007731550000009` | 7 | 2 / 5 |
| 3 | `00559007731550000016` | 3 | 0 / 3 (Zasil nieaktywne) |
| 4 | `00559007731550000054` | 4 | 2 / 2 |
| 5 | `00559007731550000092` | 3 | 3 / 0 |
| 6 | `00559007731550000009` | ostatni `00559007731550000115` | 0 / 12 |

**Automatyzacja.** E2E-IMP-04 i E2E-IMP-05 pokrywają kroki 2 i 3. Dodać kroki 4-6 jako jeden test sekwencyjny na jednym `domesticRange(12)`, seedując krok 1 przez API i weryfikując `added`/`skipped` z odpowiedzi `/ranges/import`.

**Tagi.** mass, import, duplicates, p1. **Priorytet.** 1.

#### PBI-04 Podział rolki na transze

**Opis.** Jako operator chcę zasilić jedną rolkę w dwóch krokach (np. 600 + 400), żeby móc rozdzielić nalepki między działy bez dziury ani duplikatu na styku.

**Kryteria akceptacji.**
- Given rolka 1000 numerów, When zasilę pierwsze 600 w trybie „ilość”, a potem od numeru 601. do ostatniego w trybie „ostatni numer”, Then drugi import ma 400 nowych i 0 w puli, a API zwraca łącznie 1000 pozycji dla prefiksu.
- Kontrola styku: import 600 + import od numeru 600. (o jeden za wcześnie) daje 1 w puli.

**Dane testowe.** `domesticRange(1000)`; drugi start = `range.numbers[600]`, ostatni = `range.numbers[999]`. Przykład stałych: pierwszy `00659007731610000007`, 601. = `dom(6,1,61000600)`, ostatni `00659007731610009994`.

**Automatyzacja.** Nowy test w `import-range.spec.ts`, oba importy przez API, asercja liczby przez API. UI opcjonalnie.

**Tagi.** mass, import, p2. **Priorytet.** 2.

#### PBI-05 Zasilenie parą pierwszy-ostatni i odrzucenie par z różnych pul

**Opis.** Jako operator chcę podać zakres „od-do” z opakowania rolki, żeby nie liczyć ilości, a system ma odrzucić parę numerów z różnych rolek.

**Kryteria akceptacji.**
- Given para z tej samej puli, When „Sprawdź” w trybie „ostatni numer”, Then liczba w przedziale = oczekiwana; para pierwszy = ostatni daje 1.
- Given para z różnych pul (typ, IAC, S1, wskaźnik, kraj), Then `import-error` zawiera „należą do różnych pul”.
- Given ostatni < pierwszy, Then „Zakres musi mieć co najmniej jeden numer.”
- Given ostatni ze złą cyfrą kontrolną, Then „Błędna cyfra kontrolna GS1: jest 9, powinna być 8.”

**Dane testowe.**

| Pierwszy | Ostatni | Oczekiwane |
|---|---|---|
| `00459007731450000003` | `00459007731450000096` | 10 w przedziale |
| `RR350000001DE` | `RR350000001DE` | 1 w przedziale |
| `00759007731512000621` | `RR473124829PL` | błąd: różne pule (typ) |
| `00159007731100010009` | `00259007731100010105` | błąd: różne pule (IAC) |
| `00159007731100010009` | `00159007734100010109` | błąd: różne pule (S1) |
| `RR600000007PL` | `RB600000109PL` | błąd: różne pule (wskaźnik) |
| `RR600000007PL` | `RR600000109DE` | błąd: różne pule (kraj) |
| `00559007731550000054` | `00559007731550000009` | błąd: co najmniej jeden numer |
| `00759007731512000621` | `00759007731512000639` | błąd: cyfra kontrolna jest 9, powinna być 8 |

**Automatyzacja.** E2E-IMP-03 i E2E-IMP-08 są. Dodać test parametryzowany po tabeli błędów (5 par „różne pule” + 2 pozostałe). Pary buduj helperem: `domestic(iac, kind, serial)` z różnymi `iac`/`kind`, `international(serial, svc, cc)` z różnymi `svc`/`cc`.

**Tagi.** mass, import, validation, p1. **Priorytet.** 1.

#### PBI-06 Odrzucenie numeru z literówką i śmieci

**Opis.** Jako operator chcę, żeby system odrzucił numer ze złą cyfrą kontrolną lub złym formatem zanim cokolwiek zapisze, żeby literówka nie wygenerowała 1000 numerów z nieistniejącej rolki.

**Kryteria akceptacji.**
- Given numer z tabeli, When „Sprawdź”, Then `import-error` zawiera oczekiwany fragment, `check-result` nie istnieje, „Zasil” nieaktywne.
- Given puste pole, Then „Sprawdź” nieaktywne.

**Dane testowe.**

| Wpis | Oczekiwany fragment komunikatu |
|---|---|
| `00759007731512000622` | Błędna cyfra kontrolna GS1: jest 2, powinna być 1 |
| `RR473124820PL` | Błędna cyfra kontrolna S10: jest 0, powinna być 9 |
| `00112345678901234560` | prefiks nie jest numerem SSCC Poczty Polskiej |
| `00059007731512000622` | prefiks nie jest numerem SSCC Poczty Polskiej |
| `0075900773151200062` | Nieznany format |
| `007590077315120006210` | Nieznany format |
| `abc` | Nieznany format |
| `QR473124829PL` | Nieznany format |
| `RR473124829P` | Nieznany format |
| `RR47312489PL` | Nieznany format |

**Automatyzacja.** E2E-IMP-06 pokrywa złą cyfrę GS1. Dodać test parametryzowany po tabeli. Złą cyfrę generuj przez `corruptCheckDigit()`, oczekiwaną cyfrę wyciągaj z numeru poprawnego (ostatni znak / 11. znak), żeby asercja nie była stałą.

**Tagi.** mass, import, validation, p1. **Priorytet.** 1.

#### PBI-07 Odrzucenie rolki innego rodzaju przesyłki (S1)

**Opis.** Jako administrator chcę, żeby do puli listów poleconych nie dało się wprowadzić rolki paczkowej, pobraniowej ani innej, żeby list nie dostał numeru z niewłaściwej usługi.

**Kryteria akceptacji.**
- Given krajowy numer z S1 ∉ {1, 4} i poprawną cyfrą kontrolną, When „Sprawdź”, Then `import-error` zawiera „ma S1 = N; przesyłka polecona ma 1 lub 4”.
- Given S1 = 4, Then zasilenie działa jak dla S1 = 1.

**Dane testowe.**

| Numer | S1 |
|---|---|
| `00459007732490000008` | 2 |
| `00459007733490000012` | 3 |
| `00459007735490000023` | 5 |
| `00459007736490000037` | 6 |
| `00459007738490000055` | 8 |
| `00359007734300030000` | 4 (ma przejść) |

**Automatyzacja.** `domestic(iac, kind as any, serial)` dla kind 2,3,5,6,8; pętla po wartościach. Brak istniejącego testu.

**Tagi.** mass, import, validation, p1. **Priorytet.** 1.

#### PBI-08 Limity zakresu

**Opis.** Jako administrator chcę, żeby pomyłkowe „1000000” zamiast „1000” albo zakres wychodzący poza numerację były odrzucone, żeby nie zalać bazy nieistniejącymi numerami.

**Kryteria akceptacji.**
- Ilość 100001 → błąd limitu; 100000 → przechodzi sprawdzenie (nie klikać „Zasil” w automacie).
- Serial 99999990 + ilość 20 → „Zakres wychodzi poza maksymalny numer seryjny 99999999”.
- Ilość 0 / ujemna → „Sprawdź” nieaktywne; API → 400.
- Ciało z `count` i `lastNumber` naraz → 400 „Podaj dokładnie jedno z: count albo lastNumber.”

**Dane testowe.** `00859007731010000007` (limit ilości), `00959007731999999900` (koniec numeracji), `RR999999995PL` z ilością 2.

**Automatyzacja.** Limity ilości i seriali przez API (`/ranges/check`, `expect(status).toBe(400)` i `detail` z komunikatem), ilość 0/-5 przez UI (stan przycisku `import-check`). Nie wykonywać importu 100 000 w automacie.

**Tagi.** mass, import, limits, p2. **Priorytet.** 2.

---

### Feature B: Cykl życia numeru (`/acquire`, `/{fullNumber}/state`)

#### PBI-09 Pobieranie kolejnego numeru przez aplikację nadawczą

**Opis.** Jako system nadawczy chcę dostać kolejny wolny numer danego typu i mieć go od razu zarezerwowany, żeby dwa listy nigdy nie dostały tego samego numeru.

**Kryteria akceptacji.**
- Given w puli N dostępnych numerów danego typu, When `POST /acquire {type}`, Then odpowiedź zawiera najniższy dostępny serial, stan `Reserved`, edytor z nagłówka.
- When powtórzę, Then następny serial.
- Given UI: kafelek typu → „Pobierz kolejny numer”, Then komunikat „Pobrano i zarezerwowano numer …” i wiersz w stanie Zarezerwowany.
- PostgreSQL: 20 równoległych `POST /acquire` → 20 różnych numerów, 0 błędów 500.

**Dane testowe.** `internationalRange(5)` seedowany przez API (typ zagraniczny ma mniej ruchu z innych testów).

**Automatyzacja.** E2E-STA-05 pokrywa UI. Dodać test API: seed 5, acquire ×5, asercja rosnących seriali i braku duplikatów w zbiorze. Test równoległy tylko z tagiem `@postgres`, pomijany na InMemory (`test.skip(process.env.E2E_DB !== 'postgres')`).

**Tagi.** mass, lifecycle, concurrency, p1. **Priorytet.** 1.

#### PBI-10 Przejścia stanów

**Opis.** Jako administrator chcę, żeby numer przechodził tylko przez dozwolone stany, żeby numer użyty nigdy nie wrócił do puli.

**Kryteria akceptacji.**
- Dozwolone: Available→Reserved (`Reserve`), Reserved→Used (`Use`), Available→Used (`Use`), Reserved→Available (`Release`), Available→Cancelled, Reserved→Cancelled.
- Zakazane (409 „Przejście stanu niedozwolone”): Used→cokolwiek, Cancelled→cokolwiek, Available→Release.
- Nieznana akcja (`Explode`) → 400. Numer spoza puli → 404.
- UI pokazuje tylko dozwolone przyciski; dla Used i Cancelled „brak”.

**Dane testowe.** Macierz przejść:

| Stan początkowy | Reserve | Use | Release | Cancel |
|---|---|---|---|---|
| Available | 200 | 200 | 409 | 200 |
| Reserved | 409 | 200 | 200 | 200 |
| Used | 409 | 409 | 409 | 409 |
| Cancelled | 409 | 409 | 409 | 409 |

**Automatyzacja.** E2E-STA-01…03 pokrywają ścieżki dozwolone w UI. Dodać test API: dla każdej komórki macierzy osobny numer z `domesticRange(16)`, doprowadzony do stanu początkowego przez API, potem akcja i asercja kodu. 16 wywołań, jeden test.

**Tagi.** mass, lifecycle, p1. **Priorytet.** 1.

#### PBI-11 Anulowanie uszkodzonej nalepki i użycie poza systemem

**Opis.** Jako operator chcę anulować numer z uszkodzonej nalepki (z potwierdzeniem) oraz oznaczyć jako użyty numer naklejony bez rezerwacji, żeby pula odzwierciedlała fizyczne nalepki.

**Kryteria akceptacji.**
- „Anuluj” → okno potwierdzenia; odrzucenie nie zmienia stanu; potwierdzenie → Cancelled, brak akcji, nieliczony w „Zostało N numerów”.
- „Oznacz jako użyty” na Available → Used bez rezerwacji.

**Automatyzacja.** E2E-STA-03 pokrywa anulowanie. Dodać asercję statystyk po anulowaniu (`/stats` przed i po: `available` −1, `cancelled` +1) oraz Available→Used w UI.

**Tagi.** mass, lifecycle, p2. **Priorytet.** 2.

#### PBI-12 Konflikt dwóch operatorów

**Opis.** Jako administrator chcę, żeby jednoczesna zmiana tego samego numeru przez dwie osoby skończyła się czytelnym błędem u drugiej, żeby nikt nie nadpisał cudzej zmiany po cichu.

**Kryteria akceptacji.**
- Given dwie karty z tym samym numerem Available, When A „Oznacz jako użyty”, a potem B „Zarezerwuj”, Then B dostaje czerwony komunikat 409 z opisem `Used -> Reserved`, a po odświeżeniu widzi Used.

**Automatyzacja.** E2E-STA-04 istnieje (dwa konteksty przeglądarki). Na PostgreSQL dodatkowo wariant z prawdziwą kolizją `xmin` (dwa żądania API bez odświeżenia) z tagiem `@postgres`.

**Tagi.** mass, lifecycle, concurrency, p2. **Priorytet.** 2.

#### PBI-13 Pula wyczerpana

**Opis.** Jako system nadawczy chcę dostać jednoznaczny błąd, gdy nie ma już wolnych numerów, żeby nie wpaść w pętlę ponawiania.

**Kryteria akceptacji.**
- Given wszystkie numery typu są Used/Cancelled, When `POST /acquire`, Then 409 „Pula numerów wyczerpana”; w UI przycisk nieaktywny.

**Automatyzacja.** Trudne na wspólnej puli (inne testy dosypują numery). Rozwiązanie: uruchamiać jako pierwszy test w osobnym projekcie Playwright na świeżym API, albo tylko manualnie (`TESTS.md` TM-STA-11). Do decyzji z testerem.

**Tagi.** mass, lifecycle, p2. **Priorytet.** 2.

---

### Feature C: Weryfikacja, lista, statystyki

#### PBI-14 Sprawdzenie pojedynczego numeru

**Opis.** Jako pracownik obsługujący reklamację chcę wpisać numer z pisma i zobaczyć, czy jest nasz, w jakim stanie, kto i kiedy go zmienił.

**Kryteria akceptacji.**
- Numer w puli → zielony, typ, stan, edytor, data.
- Numer poprawny spoza puli → żółty „poprawny, ale nie ma go w puli”.
- Zła cyfra kontrolna → czerwony z „jest X, powinna być Y”.
- Śmieci → czerwony „Nieznany format”.
- Zapis z małych liter i spacji → znormalizowany, wynik jak dla kanonicznego.

**Dane testowe.** `RR473124820PL` (S10 jest 0, powinna być 9), `00112345678901234560` (obcy prefiks), `rr 473124829 pl` (normalizacja), dowolny z `domesticRange(1)` seedowany / nieseedowany.

**Automatyzacja.** E2E-CHK-01…04 pokrywają. Dodać przypadek obcego prefiksu (TM-CHK-04, dziś nieautomatyzowany).

**Tagi.** mass, check, p2. **Priorytet.** 2.

#### PBI-15 Statystyki puli

**Opis.** Jako kierownik chcę widzieć, ile numerów zostało per typ, żeby wiedzieć, kiedy zamówić rolkę.

**Kryteria akceptacji.**
- Kafelki (dostępne, zarezerwowane, użyte, anulowane, razem) per typ równe wynikom `/stats`.
- Po każdej zmianie stanu w UI kafelki się aktualizują (różnica dokładnie o 1 w odpowiednich polach).

**Automatyzacja.** E2E-LST-04 istnieje. Dodać asercję delty: `/stats` przed → akcja w UI → `/stats` po → porównanie z kafelkami.

**Tagi.** mass, list, p2. **Priorytet.** 2.

#### PBI-16 Filtry, wyszukiwanie, stronicowanie

**Opis.** Jako operator chcę odfiltrować numery po typie, stanie i fragmencie numeru oraz przechodzić po stronach, żeby znaleźć rolkę lub numery zawieszone w rezerwacji.

**Kryteria akceptacji.**
- Filtr typ / stan / fragment (także `(00) 7 5900773`) zwraca tylko pasujące; „Wyczyść” przywraca pełną listę; brak dopasowań → „Brak numerów spełniających kryteria”.
- Strona 25/25, „Następna” / „Poprzednia”, licznik „2 / N”, na ostatniej „Następna” nieaktywne.
- API: `pageSize=1000` obcięte do 500.

**Automatyzacja.** E2E-LST-01…03 pokrywają. Dodać asercję `pageSize` przez API i fragment ze spacjami.

**Tagi.** mass, list, p3. **Priorytet.** 3.

#### PBI-17 Ślad audytowy (edytor, data zmiany)

**Opis.** Jako audytor chcę wiedzieć, kto i kiedy zmienił stan numeru, żeby rozliczyć listy polecone z Pocztą.

**Kryteria akceptacji.**
- Każda operacja zapisuje `editor` z nagłówka X-Editor i `changeDate` (UTC, nie starszy niż start testu).
- Wywołanie bez X-Editor → `editor = "api"`.

**Automatyzacja.** Test API: import bez nagłówka → `editor === 'api'`; zmiana stanu z nagłówkiem `e2e-<losowy>` → `editor` równy, `changeDate >= testStart`. E2E-STA-01 częściowo pokrywa edytora.

**Tagi.** mass, audit, p2. **Priorytet.** 2.

---

### Feature D: Zagranica i wdrożenie

#### PBI-18 Rolki zagraniczne z różnymi wskaźnikami i krajami

**Opis.** Jako administrator chcę zasilać rolki RA…RZ i z kodami kraju innymi niż PL, żeby nowa rolka z Poczty nie wymagała zmiany kodu, a każda kombinacja była osobną pulą.

**Kryteria akceptacji.**
- Import `RA…PL`, `RB…PL`, `RZ…PL`, `RR…DE`, `RR…CZ` działa; typ w wyniku „Zagraniczny”.
- Para RR/RB lub PL/DE w trybie „ostatni numer” → błąd „różne pule”.
- `/acquire {type: International}` zwraca najniższy serial niezależnie od wskaźnika (do potwierdzenia z biznesem: czy pobieranie ma rozróżniać wskaźnik; dziś nie rozróżnia).

**Dane testowe.** `RA201000002PL` ×1000, `RB202000009PL` ×100, `RZ207000005PL` ×1000, `RR301000005DE` ×100, `RR302000001CZ` ×100.

**Automatyzacja.** `international(serial, svc, cc)` w pętli po `[['RA','PL'],['RB','PL'],['RZ','PL'],['RR','DE'],['RR','CZ']]`, import przez API, asercja `type` i liczby.

**Tagi.** mass, import, international, p3. **Priorytet.** 3.

#### PBI-19 Wyjątki cyfry kontrolnej S10 (10 → 0, 11 → 5)

**Opis.** Jako administrator chcę mieć pewność, że numery zagraniczne trafiające w przypadki specjalne algorytmu S10 są generowane i walidowane poprawnie.

**Kryteria akceptacji.**
- `RR473124850PL` i `RR473124885PL` przechodzą walidację (`/validate` → poprawny).
- `RR473124855PL` → „jest 5, powinna być 0”.
- Import 1000 numerów od `RR473124829PL` zawiera oba numery specjalne dokładnie w tej postaci.

**Automatyzacja.** Test API na `/validate` i na `/ranges/import` + `GET /{fullNumber}` dla obu numerów. Jednostkowo w Vitest: `s10CheckDigit('47312485') === 0`, `s10CheckDigit('47312488') === 5`.

**Tagi.** mass, validation, algorithm, p2. **Priorytet.** 2.

#### PBI-20 Przebieg na PostgreSQL przed wdrożeniem

**Opis.** Jako zespół chcemy przejść komplet testów na PostgreSQL ze skryptem produkcyjnym, żeby różnice między InMemory a produkcją wyszły przed wdrożeniem.

**Kryteria akceptacji.**
- Skrypt `001_create_registered_number_pool.sql` uruchomiony na czystej bazie, `GET /health` → `database: "Postgres"`.
- Pełny `npx playwright test` zielony.
- Ręcznie: `INSERT` duplikatu `full_number` → błąd `uq_registered_number_pool_full_number`; `type = 3` → błąd CHECK; `ab -n 20 -c 10 POST /acquire` → 20 różnych numerów.
- Testy z tagiem `@postgres` (PBI-09, PBI-12) zielone.

**Automatyzacja.** Docelowo Testcontainers w osobnym projekcie NUnit z `WebApplicationFactory<Program>` (Program jest już `partial`). Na teraz: pipeline z usługą PostgreSQL i zmienną `E2E_DB=postgres`.

**Tagi.** mass, postgres, release, p1. **Priorytet.** 1 przed wdrożeniem.

---

## 5. Kolejność i podział pracy

| Sprint / etap | PBI | Uwaga |
|---|---|---|
| 1 | 01, 03, 05, 06, 07 | rozszerzenie istniejących specyfikacji import-range, najwięcej wartości za najmniej pracy |
| 2 | 09, 10, 19 | nowe testy API (szybkie, bez UI), macierz stanów, algorytm S10 |
| 3 | 02, 04, 08, 11, 14, 15, 17 | domknięcie P2 |
| 4 | 12 (@postgres), 13, 16, 18, 20 | PostgreSQL w pipeline, decyzja co do PBI-13 |

**Definicja ukończenia pojedynczego PBI:** test w `mass-ui/e2e` lub test API, nazwa testu z ID PBI w tytule (`test('PBI-06 odrzuca …')`), zielony 3 razy z rzędu lokalnie, brak `test.only`, brak stałych numerów poza asercjami komunikatów, wpis w tabeli pokrycia w `TESTS.md` sekcja 3.2.

---

## 6. Szybki generator numerów (dla testera, poza Playwright)

```js
const gs1 = d => { let s = 0; for (let i = 0; i < d.length; i++) s += +d[d.length - 1 - i] * (i % 2 ? 1 : 3); return (10 - s % 10) % 10; };
const s10 = s => { const w = [8, 6, 4, 2, 3, 5, 9, 7]; let t = 0; for (let i = 0; i < 8; i++) t += +s[i] * w[i]; const c = 11 - t % 11; return c === 10 ? 0 : c === 11 ? 5 : c; };
const dom  = (iac, s1, serial) => { const b = `${iac}5900773${s1}${String(serial).padStart(8, '0')}`; return `00${b}${gs1(b)}`; };
const intl = (serial, svc = 'RR', cc = 'PL') => { const s = String(serial).padStart(8, '0'); return `${svc}${s}${s10(s)}${cc}`; };

dom(7, 1, 51200062);   // 00759007731512000621
intl(47312482);        // RR473124829PL
```

Jeśli czegoś brakuje w tym pliku, pełna wersja z 71 dodatkowymi numerami i uzasadnieniem biznesowym każdego przypadku jest w `ku_ku_lafila.md`.
