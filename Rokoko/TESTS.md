# Mass - specyfikacja testów panelu administracji numerami R

Zakres: API `Mass.Api` (`/api/registered-numbers`) i front `mass-ui` (lista, zasilenie, sprawdzenie numeru).

## 1. Środowisko i dane testowe

| Element | Wartość |
|---|---|
| API | `dotnet run --project Mass.Api` → `http://localhost:5080` (Swagger: `/swagger`) |
| Baza do testów | `Database:Provider = InMemory` (appsettings.Development.json) - bez PostgreSQL, dane znikają po restarcie. Do testów unikalności w bazie i `FOR UPDATE SKIP LOCKED` wymagany PostgreSQL 13+ ze skryptem `001_create_registered_number_pool.sql`. |
| Front | `cd mass-ui && npm start` → `http://localhost:4200` (proxy `/api` → 5080) |
| Autor zmian | pole „Edytor” w nagłówku UI (nagłówek `X-Editor`) |

Numery testowe (wszystkie z poprawną cyfrą kontrolną):

| Nazwa | Numer | Uwagi |
|---|---|---|
| D1 | `00759007731512000621` | krajowy, IAC 7, S1 1, serial 51200062 |
| D1+1 | `00759007731512000638` | kolejny po D1 |
| D1+2 | `00759007731512000645` | |
| D1+4 | `00759007731512000669` | ostatni w przedziale D1 x5 |
| D-bad | `00759007731512000622` | D1 z błędną cyfrą kontrolną |
| D-obcy | `00112345678901234560` | 20 cyfr, ale nie prefiks Poczty Polskiej |
| I1 | `RR473124829PL` | zagraniczny, serial 47312482 |
| I1+2 | `RR473124846PL` | ostatni w przedziale I1 x3 |
| I-bad | `RR473124820PL` | I1 z błędną cyfrą kontrolną |

Przy powtarzaniu testów na tej samej bazie użyj innego przedziału (inne IAC lub serial), bo pula pamięta numery.

## 2. Testy manualne

Format: ID · warunki wstępne · kroki · oczekiwany wynik.

### 2.1 Zasilenie puli przedziałem (ekran „Zasilenie”)

| ID | Warunki | Kroki | Oczekiwany wynik |
|---|---|---|---|
| TM-IMP-01 | pusta pula | Otwórz „Zasilenie”. | „Sprawdź” i „Zasil” nieaktywne. Po wpisaniu D1 „Sprawdź” aktywne, „Zasil” nadal nieaktywne. |
| TM-IMP-02 | D1 nie w puli | Pierwszy numer D1, ilość 5, „Sprawdź”. | Wynik: typ Krajowy, przedział D1…D1+4, 5 w przedziale, 5 nowych, 0 w puli, zielony komunikat. „Zasil” aktywne. |
| TM-IMP-03 | po TM-IMP-02 | „Zasil”. | Komunikat: dodano 5, pominięto 0. Na liście 5 numerów w stanie Dostępny z edytorem z nagłówka. |
| TM-IMP-04 | po TM-IMP-03 | D1, ilość 7, „Sprawdź”. | 2 nowe, 5 już w puli, żółte ostrzeżenie, tabela 5 istniejących z ich stanem i edytorem. „Zasil” aktywne. Po „Zasil”: dodano 2, pominięto 5. |
| TM-IMP-05 | po TM-IMP-03 | D1, ilość 3, „Sprawdź”. | „Cały przedział jest już w puli”, 0 nowych, „Zasil” nieaktywne. |
| TM-IMP-06 | - | Pierwszy numer I1, tryb „ostatni numer”, ostatni I1+2, „Sprawdź”, „Zasil”. | Typ Zagraniczny, 3 w przedziale, dodano 3. |
| TM-IMP-07 | - | Pierwszy numer D-bad, „Sprawdź”. | Czerwony błąd „Błędna cyfra kontrolna GS1: jest 2, powinna być 1”. Brak wyniku sprawdzenia, „Zasil” nieaktywne. |
| TM-IMP-08 | - | Pierwszy numer D1, ostatni I1. | Błąd: numery należą do różnych pul. |
| TM-IMP-09 | - | Pierwszy numer `(00) 7 5900773 1 51200062 1` (spacje i nawias), ilość 1, „Sprawdź”. | Numer znormalizowany do D1; sprawdzenie działa. |
| TM-IMP-10 | - | Po „Sprawdź” zmień ilość. | Podpowiedź „Przedział zmienił się po sprawdzeniu”, „Zasil” nieaktywne aż do ponownego sprawdzenia. |
| TM-IMP-11 | - | Ilość 100001. | Błąd limitu (100 000). |
| TM-IMP-12 | - | Ilość i ostatni numer jednocześnie (przez Swagger). | HTTP 400 „Podaj dokładnie jedno z: count albo lastNumber”. |
| TM-IMP-13 | - | Numer krajowy z S1 = 3 (paczka) jako pierwszy. | Błąd: przesyłka polecona ma S1 = 1 lub 4. |

### 2.2 Lista numerów (ekran „Lista”)

| ID | Warunki | Kroki | Oczekiwany wynik |
|---|---|---|---|
| TM-LST-01 | pula z numerami | Otwórz „Lista”. | Kafelki statystyk dla obu typów (dostępne, zarezerwowane, użyte, anulowane, razem). Tabela z numerem, typem, stanem, edytorem, datą, akcjami. |
| TM-LST-02 | - | Filtr typ = Zagraniczny. | Tylko numery `R…PL`. Podsumowanie „Znaleziono N numerów”. |
| TM-LST-03 | - | Filtr stan = Użyty. | Tylko wiersze z odznaką „Użyty”. |
| TM-LST-04 | - | Fragment numeru `0075900773`. | Tylko krajowe z IAC 7. Fragment ze spacjami/„(00)” działa tak samo. |
| TM-LST-05 | >25 numerów | Na stronie 25, „Następna”. | Zmiana strony, licznik „2 / N”, „Poprzednia” aktywne, na ostatniej „Następna” nieaktywne. |
| TM-LST-06 | - | „Wyczyść”. | Filtry puste, pełna lista. |
| TM-LST-07 | brak dopasowań | Fragment `ZZZ`. | „Brak numerów spełniających kryteria”. |
| TM-LST-08 | API zatrzymane | Odśwież listę. | Czerwony komunikat „Brak połączenia z API”, brak wyjątku w konsoli. |

### 2.3 Zmiana stanu numeru

| ID | Warunki | Kroki | Oczekiwany wynik |
|---|---|---|---|
| TM-STA-01 | D1 Dostępny | „Zarezerwuj”. | Stan Zarezerwowany, edytor = wartość z pola „Edytor”, data zmiany zaktualizowana, komunikat sukcesu. |
| TM-STA-02 | D1 Zarezerwowany | „Oznacz jako użyty”. | Stan Użyty, brak przycisków akcji („brak”). |
| TM-STA-03 | D1+1 Dostępny | „Oznacz jako użyty” bez rezerwacji. | Stan Użyty (nalepka użyta ręcznie). |
| TM-STA-04 | D1+2 Zarezerwowany | „Zwolnij”. | Stan Dostępny. |
| TM-STA-05 | D1+3 Dostępny | „Anuluj”, w oknie „Anuluj” (odrzuć). | Bez zmian. |
| TM-STA-06 | D1+3 Dostępny | „Anuluj”, potwierdź. | Stan Anulowany, brak akcji. |
| TM-STA-07 | D1 Użyty | Przez Swagger `POST /{D1}/state {"action":"Cancel"}`. | HTTP 409 „Przejście stanu niedozwolone”. |
| TM-STA-08 | D1 Dostępny, dwie karty | Karta A i B pokazują D1 Dostępny. W A „Oznacz jako użyty”. W B „Zarezerwuj”. | W B czerwony komunikat 409 z opisem przejścia `Used -> Reserved`. Po odświeżeniu B stan Użyty. |
| TM-STA-09 | numer spoza puli | Swagger `POST /{D-obcy-poprawny}/state`. | HTTP 404 „Numer nie istnieje w puli”. |
| TM-STA-10 | pula zagraniczna ma ≥1 dostępny | Kafelek Zagraniczny → „Pobierz kolejny numer”. | Komunikat „Pobrano i zarezerwowano numer R…PL” z najniższym dostępnym; w tabeli stan Zarezerwowany. |
| TM-STA-11 | pula zagraniczna bez dostępnych | Jak wyżej. | Przycisk nieaktywny; przez Swagger `POST /acquire` → 409 „Pula numerów wyczerpana”. |

### 2.4 Sprawdzenie numeru (ekran „Sprawdź numer”)

| ID | Warunki | Kroki | Oczekiwany wynik |
|---|---|---|---|
| TM-CHK-01 | D1 Użyty | Wpisz D1, „Sprawdź”. | Zielony: numer w puli, typ Krajowy, stan Użyty. |
| TM-CHK-02 | - | Numer poprawny spoza puli. | Żółty: poprawny, ale nie ma go w puli. |
| TM-CHK-03 | - | I-bad. | Czerwony: „Błędna cyfra kontrolna S10: jest 0, powinna być 9”. |
| TM-CHK-04 | - | D-obcy. | Czerwony: prefiks nie jest numerem SSCC Poczty Polskiej. |
| TM-CHK-05 | - | `abc`. | Czerwony: „Nieznany format…”. |
| TM-CHK-06 | - | `rr 473124829 pl` (małe litery, spacje). | Znormalizowany do I1, wynik jak dla I1. |

### 2.5 API bezpośrednio (Swagger / curl)

| ID | Wywołanie | Oczekiwany wynik |
|---|---|---|
| TM-API-01 | `GET /health` | `{status:"ok", database:"InMemory"\|"Postgres"}` |
| TM-API-02 | `GET /api/registered-numbers?pageSize=1000` | `pageSize` obcięte do 500. |
| TM-API-03 | `GET /api/registered-numbers/{D-bad}` | 400 z opisem cyfry kontrolnej. |
| TM-API-04 | `POST /ranges/import` bez `firstNumber` | 400 (walidacja modelu). |
| TM-API-05 | `POST /ranges/import` bez nagłówka `X-Editor` | Edytor = `api`. |
| TM-API-06 | `POST /{D1}/state {"action":"Explode"}` | 400. |
| TM-API-07 | CORS: żądanie z `http://localhost:4200` | Nagłówki CORS obecne; z innego originu brak. |

### 2.6 Tylko PostgreSQL (po wdrożeniu skryptu SQL)

| ID | Kroki | Oczekiwany wynik |
|---|---|---|
| TM-DB-01 | Import 5 numerów, `SELECT full_number, state FROM mass.registered_number_pool`. | 5 wierszy z numerami jak w UI, `state = 0`. |
| TM-DB-02 | `INSERT` ręcznie duplikatu istniejącego `full_number`. | Błąd unikalności `uq_registered_number_pool_full_number`. |
| TM-DB-03 | `INSERT` z `type = 3` albo `state = 9`. | Błąd CHECK. |
| TM-DB-04 | Zmiana stanu przez UI, sprawdź `change_date` i `editor`. | Zaktualizowane przez aplikację (UTC). |
| TM-DB-05 | Dwa równoległe `POST /acquire` (np. `ab -n 20 -c 10`). | Każde wywołanie dostaje inny numer, brak 500. |
| TM-DB-06 | Dwie karty: w obu ten sam numer Dostępny, w A „Zarezerwuj”, w B „Oznacz jako użyty”. | W B 409 (token `xmin`), po odświeżeniu stan z A. |

## 3. Testy automatyczne (Playwright)

Pliki: `mass-ui/e2e/*.spec.ts`, konfiguracja `mass-ui/playwright.config.ts`, helpery `mass-ui/e2e/helpers/`.

### 3.1 Zasady

- Każdy scenariusz generuje własny, losowy przedział numerów (helper `numbers.ts` liczy cyfry kontrolne GS1 i S10), więc testy są niezależne od siebie i od poprzednich uruchomień. Nie czyścimy bazy.
- Dane wejściowe przygotowuje się przez API (`helpers/api.ts`), a UI testuje się tylko dla tego, co jest przedmiotem scenariusza. Stan backendu po akcji w UI jest potwierdzany przez API.
- Selektory wyłącznie `data-testid`. Teksty asercji po polsku, zgodnie z UI.
- Sekwencyjnie (`workers: 1`), bo statystyki i „Pobierz kolejny numer” zależą od wspólnego stanu puli.
- `test.beforeAll` sprawdza `/health`; brak API kończy test czytelnym komunikatem, a nie timeoutem.

### 3.2 Pokrycie

| Spec | Test | Pokrywa |
|---|---|---|
| `import-range.spec.ts` | E2E-IMP-01 | TM-IMP-01 |
| | E2E-IMP-02 | TM-IMP-02, 03, 09 |
| | E2E-IMP-03 | TM-IMP-06 |
| | E2E-IMP-04 | TM-IMP-04 (duplikaty częściowe, tabela istniejących, import pomija) |
| | E2E-IMP-05 | TM-IMP-05 (całość zduplikowana, blokada „Zasil”) |
| | E2E-IMP-06 | TM-IMP-07 |
| | E2E-IMP-07 | TM-IMP-10 |
| | E2E-IMP-08 | TM-IMP-08 |
| `numbers-list.spec.ts` | E2E-LST-01 | TM-LST-01, 04 |
| | E2E-LST-02 | TM-LST-02, 03, 06 |
| | E2E-LST-03 | TM-LST-05 |
| | E2E-LST-04 | statystyki |
| | E2E-STA-01 | TM-STA-01, 02 (+ edytor z nagłówka) |
| | E2E-STA-02 | TM-STA-04 |
| | E2E-STA-03 | TM-STA-05, 06 |
| | E2E-STA-04 | TM-STA-08 (konflikt 409) |
| | E2E-STA-05 | TM-STA-10 |
| `number-check.spec.ts` | E2E-CHK-01..04 | TM-CHK-01, 02, 03, 05, 06 |

Nieautomatyzowane (świadomie): TM-LST-08 (API wyłączone), TM-API-*, TM-DB-* (wymagają PostgreSQL lub narzędzi spoza przeglądarki). Kandydaci na testy integracyjne NUnit z `WebApplicationFactory<Program>` i Testcontainers.

### 3.3 Uruchomienie

```bash
# terminal 1 - API w trybie InMemory
cd Mass/Mass.Api
dotnet run            # ASPNETCORE_ENVIRONMENT=Development -> Provider=InMemory, port 5080

# terminal 2 - testy (front startuje automatycznie przez webServer)
cd Mass/mass-ui
npx playwright test               # wszystkie
npx playwright test import-range  # jeden plik
npx playwright test --ui          # tryb interaktywny
npx playwright show-report        # raport HTML po uruchomieniu w CI
```

Zmienne: `E2E_BASE_URL` (domyślnie `http://localhost:4200`), `E2E_API_URL` (domyślnie `http://localhost:5080`).

### 3.4 Kryteria akceptacji

- 100% scenariuszy E2E zielone na InMemory przed każdym merge.
- Przed wdrożeniem na środowisko z PostgreSQL: pełny przebieg E2E na bazie testowej ze skryptem SQL plus sekcja 2.6 manualnie.
