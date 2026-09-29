# Numery nadawcze listów poleconych (R-ki): budowa, algorytm generowania i dane testowe do zasilania puli

Dokument opisuje, z czego składa się numer nadawczy przesyłki poleconej, jak aplikacja Mass liczy cyfry kontrolne i generuje kolejne numery z rolki, oraz daje gotowy zestaw danych testowych do formularza „Zasilenie”. Wszystkie numery w tabelach zostały policzone tym samym algorytmem, który jest w kodzie (`RegisteredNumberFormatter.cs` i `e2e/helpers/numbers.ts`), i zgadzają się z numerami wzorcowymi z `TESTS.md`.

---

## 1. Z czego składa się numer

Poczta Polska używa dwóch niezależnych rodzin numerów. Aplikacja rozpoznaje rodzinę po długości i wzorcu zapisu.

### 1.1 Numer krajowy: GS1 SSCC, 20 cyfr

Przykład: `00759007731512000621`, na nalepce drukowany jako `(00) 7 5900773 1 51200062 1`.

| Pozycje | Fragment | Nazwa | Znaczenie | Wartości |
|---|---|---|---|---|
| 1-2 | `00` | Identyfikator aplikacji GS1 (AI) | Mówi czytnikowi, że dalej jest SSCC. Nie bierze udziału w liczeniu cyfry kontrolnej. | zawsze `00` |
| 3 | `7` | IAC (cyfra rozszerzenia) | Rozróżnia równoległe pule tego samego nadawcy. Każdy IAC to osobna pula. | `1`-`9` |
| 4-10 | `5900773` | Prefiks firmy GS1 | `590` = Polska, `0773` = Poczta Polska S.A. Stały. | zawsze `5900773` |
| 11 | `1` | S1 (rodzaj przesyłki) | Kod usługi wg GS1 Polska. | `1` lub `4` = polecona; `2` = z zadeklarowaną wartością; `3` = paczka; `5` = pobraniowa; `6` = e-przesyłka; `8` = biznesowa |
| 12-19 | `51200062` | Numer seryjny | Właściwy numer przesyłki. Kolejne nalepki na rolce mają kolejne numery. | `00000000`-`99999999` |
| 20 | `1` | Cyfra kontrolna | Liczona z 17 cyfr (pozycje 3-19) metodą GS1 mod 10. | `0`-`9` |

Aplikacja Mass przyjmuje wyłącznie S1 = 1 lub 4 (przesyłki polecone). Pozostałe kody są odrzucane z komunikatem „przesyłka polecona ma S1 = 1 lub 4”.

W bazie numer krajowy jest rozłożony na kolumny: `iac_digit`, `kind_digit`, `value` (serial) oraz pełny `full_number`.

### 1.2 Numer zagraniczny: UPU S10, 13 znaków

Przykład: `RR473124829PL`.

| Pozycje | Fragment | Nazwa | Znaczenie | Wartości |
|---|---|---|---|---|
| 1-2 | `RR` | Wskaźnik usługi | Dwie litery. Pierwsza `R` oznacza przesyłkę poleconą (registered), druga rozróżnia rodzaj/pulę. | `RA`-`RZ` |
| 3-10 | `47312482` | Numer seryjny | 8 cyfr, kolejne na rolce. | `00000000`-`99999999` |
| 11 | `9` | Cyfra kontrolna | Liczona z 8 cyfr serialu ważoną sumą mod 11 (S10). | `0`-`9` |
| 12-13 | `PL` | Kod kraju | ISO 3166-1 alpha-2 kraju nadania. | dwie wielkie litery, u nas `PL` |

W bazie: `service_indicator`, `country_code`, `value` (serial), `full_number`.

### 1.3 Co identyfikuje pulę

Pula (rolka nalepek) to zbiór numerów o tym samym typie i tych samych prefiksach, różniących się tylko numerem seryjnym:

- krajowa: ten sam IAC i to samo S1,
- zagraniczna: ten sam wskaźnik usługi i ten sam kod kraju.

Dwa numery z różnymi prefiksami nie mogą tworzyć jednego przedziału. Formularz zgłasza wtedy błąd „należą do różnych pul”.

---

## 2. Algorytm: jak liczona jest cyfra kontrolna

### 2.1 GS1 mod 10 (numer krajowy)

Bierzemy 17 cyfr bez początkowego `00`, czyli IAC + prefiks + S1 + serial. Idąc **od prawej**, cyfry na pozycjach nieparzystych mnożymy przez 3, na parzystych przez 1. Sumujemy. Cyfra kontrolna to liczba, którą trzeba dodać do sumy, żeby otrzymać wielokrotność 10.

Przykład dla `75900773151200062` (numer `00759007731512000621`):

| Pozycja od lewej | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 16 | 17 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Cyfra | 7 | 5 | 9 | 0 | 0 | 7 | 7 | 3 | 1 | 5 | 1 | 2 | 0 | 0 | 0 | 6 | 2 |
| Waga | 3 | 1 | 3 | 1 | 3 | 1 | 3 | 1 | 3 | 1 | 3 | 1 | 3 | 1 | 3 | 1 | 3 |
| Iloczyn | 21 | 5 | 27 | 0 | 0 | 7 | 21 | 3 | 3 | 5 | 3 | 2 | 0 | 0 | 0 | 6 | 6 |

Suma iloczynów = **109**. Najbliższa wielokrotność 10 w górę to 110, więc cyfra kontrolna = 110 − 109 = **1**. Wzór: `(10 − suma mod 10) mod 10`. Końcowe `mod 10` zamienia wynik 10 na 0, gdy suma sama jest wielokrotnością 10.

Skoro 17 cyfr to liczba nieparzysta, pierwsza cyfra od lewej (IAC) zawsze ma wagę 3.

### 2.2 UPU S10 ważony mod 11 (numer zagraniczny)

Bierzemy 8 cyfr serialu. Każdą mnożymy przez stałą wagę z tabeli `8 6 4 2 3 5 9 7` (od lewej). Sumujemy, liczymy resztę z dzielenia przez 11 i odejmujemy ją od 11. Dwa wyjątki: wynik 10 daje cyfrę `0`, wynik 11 (reszta 0) daje cyfrę `5`.

Przykład dla serialu `47312482` (numer `RR473124829PL`):

| Pozycja | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|---|---|---|---|---|---|---|---|---|
| Cyfra | 4 | 7 | 3 | 1 | 2 | 4 | 8 | 2 |
| Waga | 8 | 6 | 4 | 2 | 3 | 5 | 9 | 7 |
| Iloczyn | 32 | 42 | 12 | 2 | 6 | 20 | 72 | 14 |

Suma = **200**. 200 mod 11 = 2. 11 − 2 = **9**. Cyfra kontrolna = 9.

Przykłady wyjątków (z tej samej rolki):

| Serial | Suma | Reszta mod 11 | 11 − reszta | Cyfra kontrolna | Pełny numer |
|---|---|---|---|---|---|
| `47312485` | 221 | 1 | 10 | **0** | `RR473124850PL` |
| `47312488` | 242 | 0 | 11 | **5** | `RR473124885PL` |

### 2.3 Dlaczego cyfra kontrolna ma znaczenie w testach

Cyfra kontrolna wyłapuje pojedynczą pomyłkę w przepisaniu i większość zamian sąsiednich cyfr. Formularz odrzuca numer ze złą cyfrą kontrolną, zanim w ogóle spojrzy do bazy, i mówi, jaka cyfra jest, a jaka powinna być. Dlatego do testów nie da się wpisać „dowolnych 20 cyfr”: numer musi być policzony.

---

## 3. Algorytm: jak generowane są numery z rolki

Użytkownik dostaje z Elektronicznego Nadawcy rolkę nalepek (zwykle 1000 sztuk) i wpisuje w formularz **pierwszy numer z rolki** oraz **ilość** (albo **ostatni numer**). Aplikacja robi kolejno:

1. **Normalizacja wpisu.** Usuwa spacje i myślniki, zamienia małe litery na wielkie, `(00)` zamienia na `00`. Dzięki temu `(00) 7 5900773 1 51200062 1` i `00759007731512000621` to ten sam numer.
2. **Rozpoznanie typu** po wzorcu: 20 cyfr zaczynających się od `00`, cyfry 1-9 i `5900773` to numer krajowy; `R` + litera + 8 cyfr + cyfra + 2 litery to zagraniczny. Inaczej: „Nieznany format”.
3. **Weryfikacja cyfry kontrolnej** wpisanego numeru według sekcji 2. Błąd zatrzymuje proces z opisem.
4. **Rozłożenie na części**: typ, prefiksy (IAC + S1 albo wskaźnik + kraj) i numer seryjny.
5. **Wyznaczenie zakresu seriali.** Przy trybie „ilość”: od `serial` do `serial + ilość − 1`. Przy trybie „ostatni numer”: ostatni numer przechodzi kroki 1-4, prefiksy muszą być identyczne, a zakres to od pierwszego do ostatniego serialu.
6. **Bezpieczniki zakresu**: co najmniej 1 numer, nie więcej niż 100 000 (rolka to 1000, limit chroni przed omyłkowym importem milionów wierszy), ostatni serial nie może przekroczyć 99999999, a dla krajowych S1 musi być 1 lub 4.
7. **Generowanie pełnych numerów.** Dla każdego serialu z zakresu: prefiksy bez zmian, serial dopełniony zerami do 8 cyfr, cyfra kontrolna **liczona na nowo** dla każdego numeru. Cyfra kontrolna nie rośnie „o jeden”, jest funkcją całego numeru (patrz sekcja 4.2, gdzie kolejne numery kończą się na 1, 8, 5, 2, 9).
8. **Sprawdzenie** (`/ranges/check`): które z wygenerowanych numerów już są w bazie. Wynik: ile w przedziale, ile nowych, ile już w puli, z listą istniejących i ich stanem.
9. **Zasilenie** (`/ranges/import`): wstawienie tylko nowych numerów w stanie „Dostępny” z edytorem z nagłówka. Istniejące są pomijane, więc ponowny import tej samej rolki jest bezpieczny.

Z tak zasilonej puli aplikacje pobierają kolejne numery (`/acquire`): zawsze najniższy dostępny serial danego typu, atomowo, w stanie „Zarezerwowany”.

---

## 4. Dane testowe do formularza „Zasilenie”

Pola formularza: **Pierwszy numer**, **Tryb** (`ilość` / `ostatni numer`), **Ilość** (domyślnie 1000), **Ostatni numer**, przyciski **Sprawdź**, **Zasil**, **Wyczyść**. Przed pracą wpisz coś w pole **Edytor** w nagłówku (np. `tester`).

Pula pamięta numery między testami do restartu API (InMemory) lub trwale (PostgreSQL). Sekcje używają rozłącznych prefiksów, żeby scenariusze nie kolidowały. Po wyczerpaniu zestawu zmień IAC lub serial i policz numer snippetem z sekcji 5.

### 4.1 Pełne rolki (happy path)

Jak z Elektronicznego Nadawcy: pierwszy numer z rolki i ilość nalepek. Tryb `ilość`, wklej pierwszy numer, wpisz ilość, „Sprawdź”, „Zasil”. Oczekiwane: N w przedziale, N nowych, 0 w puli, po zasileniu dodano N, pominięto 0.

| ID | Typ | Prefiksy | Pierwszy numer | Ilość | Oczekiwany ostatni numer | Uwagi |
|---|---|---|---|---|---|---|
| ROLL-D1 | Krajowy | IAC 1, S1 1 | `00159007731100010009` | 1000 | `00159007731100019996` | podstawowa rolka |
| ROLL-D2 | Krajowy | IAC 2, S1 1 | `00259007731200020004` | 1000 | `00259007731200029991` | inny IAC = osobna pula |
| ROLL-D3 | Krajowy | IAC 3, S1 4 | `00359007734300030000` | 1000 | `00359007734300039997` | S1 = 4 też jest polecona |
| ROLL-D4 | Krajowy | IAC 7, S1 4 | `00759007734512000622` | 1000 | `00759007734512010614` | ten sam IAC i serial co D1, inne S1: nie może pokazać duplikatów |
| ROLL-I1 | Zagraniczny | RR, PL | `RR473124829PL` | 1000 | `RR473134812PL` | podstawowa rolka |
| ROLL-I2 | Zagraniczny | RR, PL | `RR600000007PL` | 500 | `RR600004998PL` | ilość inna niż domyślna |
| ROLL-I3 | Zagraniczny | RB, PL | `RB700000000PL` | 100 | `RB700000999PL` | wskaźnik inny niż RR |
| ROLL-I4 | Zagraniczny | RR, DE | `RR800000002DE` | 50 | `RR800000492DE` | kod kraju inny niż PL |

Wariant w trybie `ostatni numer`: te same wiersze, w „Ostatni numer” wklej oczekiwany ostatni numer. Liczba w przedziale musi równać się kolumnie „Ilość”.

### 4.2 Numery wzorcowe z TESTS.md (krajowe IAC 7, S1 1)

Pięć kolejnych numerów. Zwróć uwagę, że cyfry kontrolne to 1, 8, 5, 2, 9, nie kolejne liczby.

| Nazwa | Serial | Numer |
|---|---|---|
| D1 | 51200062 | `00759007731512000621` |
| D1+1 | 51200063 | `00759007731512000638` |
| D1+2 | 51200064 | `00759007731512000645` |
| D1+3 | 51200065 | `00759007731512000652` |
| D1+4 | 51200066 | `00759007731512000669` |

### 4.3 Scenariusze duplikatów (krajowe IAC 5, S1 1)

Dwanaście kolejnych numerów, serial 55000000…55000011. Kroki wykonuj po kolei na tej samej bazie.

| Serial | Numer | Serial | Numer |
|---|---|---|---|
| 55000000 | `00559007731550000009` | 55000006 | `00559007731550000061` |
| 55000001 | `00559007731550000016` | 55000007 | `00559007731550000078` |
| 55000002 | `00559007731550000023` | 55000008 | `00559007731550000085` |
| 55000003 | `00559007731550000030` | 55000009 | `00559007731550000092` |
| 55000004 | `00559007731550000047` | 55000010 | `00559007731550000108` |
| 55000005 | `00559007731550000054` | 55000011 | `00559007731550000115` |

| Krok | Pierwszy numer | Tryb | Wartość | Oczekiwany wynik | Jak wprowadzić |
|---|---|---|---|---|---|
| DUP-01 pierwsze zasilenie | `00559007731550000009` | ilość | 5 | 5 nowych, 0 w puli; „Zasil”: dodano 5, pominięto 0 | Wklej numer, tryb `ilość`, „Ilość” 5, „Sprawdź”, „Zasil”. |
| DUP-02 nakładka na końcu | `00559007731550000009` | ilość | 7 | 2 nowe, 5 w puli, żółte ostrzeżenie, tabela 5 istniejących; „Zasil”: dodano 2, pominięto 5 | Ten sam numer, zmień „Ilość” na 7, „Sprawdź” (dopiero wtedy „Zasil” się odblokuje), „Zasil”. |
| DUP-03 cały przedział w puli | `00559007731550000016` | ilość | 3 | „Cały przedział jest już w puli”, 0 nowych, „Zasil” nieaktywne | Wklej numer serialu 55000001, „Ilość” 3, „Sprawdź”. Nie klikaj „Zasil”. |
| DUP-04 nakładka na początku | `00559007731550000054` | ilość | 4 | 2 nowe (55000007, 55000008), 2 w puli (55000005, 55000006) | Wklej numer serialu 55000005, „Ilość” 4, „Sprawdź”, „Zasil”. |
| DUP-05 przedział przylegający | `00559007731550000092` | ilość | 3 | 3 nowe, 0 w puli | Wklej numer serialu 55000009, „Ilość” 3, „Sprawdź”, „Zasil”. |
| DUP-06 cały ciąg | `00559007731550000009` | ostatni numer | `00559007731550000115` | 12 w przedziale, 0 nowych, 12 w puli | Tryb `ostatni numer`, w „Ostatni numer” wklej numer serialu 55000011, „Sprawdź”. „Zasil” nieaktywne. |
| DUP-07 jeden numer | `00559007731550000108` | ilość | 1 | 1 w przedziale, 0 nowych, 1 w puli | Wklej numer serialu 55000010, „Ilość” 1, „Sprawdź”. |

Ciąg zagraniczny (RR, PL, serial 48000000…48000005):

| Serial | Numer |
|---|---|
| 48000000 | `RR480000008PL` |
| 48000001 | `RR480000011PL` |
| 48000002 | `RR480000025PL` |
| 48000003 | `RR480000039PL` |
| 48000004 | `RR480000042PL` |
| 48000005 | `RR480000056PL` |

| Krok | Pierwszy numer | Tryb | Wartość | Oczekiwany wynik | Jak wprowadzić |
|---|---|---|---|---|---|
| DUP-I1 | `RR480000008PL` | ostatni numer | `RR480000025PL` | Typ Zagraniczny, 3 w przedziale, dodano 3 | Tryb `ostatni numer`, „Ostatni numer” = serial 48000002, „Sprawdź”, „Zasil”. |
| DUP-I2 | `RR480000011PL` | ilość | 5 | 3 nowe, 2 w puli | Wklej numer serialu 48000001, „Ilość” 5, „Sprawdź”; „Zasil” dodaje 3. |

### 4.4 Warianty zapisu tego samego numeru (normalizacja)

Wklej wpis dokładnie w podanej postaci, tryb `ilość`, „Ilość” 1, „Sprawdź”. W wyniku ma być numer znormalizowany.

| Wpis w polu | Znormalizowany | Co sprawdza |
|---|---|---|
| `(00) 7 5900773 1 51200062 1` | `00759007731512000621` | zapis z nalepki: nawias i spacje |
| `00 7 5900773 1 51200062 1` | `00759007731512000621` | spacje bez nawiasu |
| `00-7-5900773-1-51200062-1` | `00759007731512000621` | myślniki |
| `  00759007731512000621  ` | `00759007731512000621` | spacje na końcach |
| `rr 473124829 pl` | `RR473124829PL` | małe litery i spacje |
| `RR-47312482-9-PL` | `RR473124829PL` | myślniki, cyfra kontrolna oddzielona |
| `rr473124829pl` | `RR473124829PL` | małe litery bez spacji |

### 4.5 Wyjątki cyfry kontrolnej S10 (wynik 10 → 0, wynik 11 → 5)

Numery z tej samej rolki co ROLL-I1, na których algorytm trafia w przypadki specjalne. Tryb `ilość`, „Ilość” 1, „Sprawdź”: mają przejść jako poprawne.

| Serial | Numer | Przypadek |
|---|---|---|
| 47312485 | `RR473124850PL` | 11 − 1 = 10, cyfra kontrolna `0` |
| 47312488 | `RR473124885PL` | reszta 0, 11 − 0 = 11, cyfra kontrolna `5` |

Kontrola negatywna: `RR473124855PL` (ten sam serial co pierwszy, cyfra `5` zamiast `0`) ma dać „Błędna cyfra kontrolna S10: jest 5, powinna być 0”.

### 4.6 Wartości graniczne

| ID | Pierwszy numer | Tryb | Wartość | Oczekiwany wynik | Jak wprowadzić |
|---|---|---|---|---|---|
| BND-01 serial minimalny | `00959007731000000007` (serial 00000000) | ilość | 1 | OK, 1 w przedziale | Wklej numer, „Ilość” 1, „Sprawdź”. |
| BND-02 serial maksymalny | `00959007731999999993` (serial 99999999) | ilość | 1 | OK, 1 w przedziale | Wklej numer, „Ilość” 1, „Sprawdź”. |
| BND-03 do końca zakresu | `00959007731999999955` (serial 99999995) | ilość | 5 | OK, ostatni = `00959007731999999993` | Wklej numer, „Ilość” 5, „Sprawdź”; ostatni w wyniku równy BND-02. |
| BND-04 poza zakres seriali | `00959007731999999900` (serial 99999990) | ilość | 20 | Błąd „Zakres wychodzi poza maksymalny numer seryjny 99999999” | Wklej numer, „Ilość” 20, „Sprawdź”. |
| BND-05 limit ilości | `00859007731010000007` | ilość | 100000 | OK, ostatni = `00859007731010999998` | Wklej numer, „Ilość” 100000, „Sprawdź”. „Zasil” tylko na bazie testowej (100 000 wierszy). |
| BND-06 ponad limit | `00859007731010000007` | ilość | 100001 | Błąd limitu 100 000 | Wklej numer, „Ilość” 100001, „Sprawdź”. |
| BND-07 ilość 0 | dowolny poprawny | ilość | 0 | „Sprawdź” nieaktywne; Swagger 400 | Wpisz 0 w „Ilość”, sprawdź stan przycisku. |
| BND-08 ilość ujemna | dowolny poprawny | ilość | -5 | jak BND-07 | Wpisz -5 w „Ilość”. |
| BND-09 ilość ułamkowa | dowolny poprawny | ilość | 2.5 | pole odrzuca / Swagger 400 | Spróbuj wpisać 2.5 albo 2,5. |
| BND-10 zagraniczny min | `RR000000005PL` | ilość | 1 | OK | Wklej numer, „Ilość” 1, „Sprawdź”. |
| BND-11 zagraniczny max | `RR999999995PL` | ilość | 2 | Błąd: poza maksymalny numer seryjny | Wklej numer, „Ilość” 2, „Sprawdź” (z 1 przechodzi). |
| BND-12 ostatni = pierwszy | `00559007731550000009` | ostatni numer | `00559007731550000009` | 1 w przedziale | Ten sam numer w obu polach, tryb `ostatni numer`. |

### 4.7 Dane negatywne: pierwszy numer

Wklej wartość w „Pierwszy numer”, tryb `ilość`, „Ilość” 1, „Sprawdź”. Oczekuj czerwonego komunikatu, braku wyniku i nieaktywnego „Zasil”.

| ID | Pierwszy numer | Oczekiwany błąd | Jak powstał |
|---|---|---|---|
| NEG-01 zła cyfra kontrolna GS1 | `00759007731512000622` | „Błędna cyfra kontrolna GS1: jest 2, powinna być 1.” | D1 z ostatnią cyfrą 1 → 2 |
| NEG-02 zła cyfra kontrolna S10 | `RR473124820PL` | „Błędna cyfra kontrolna S10: jest 0, powinna być 9.” | I1 z 11. znakiem 9 → 0 |
| NEG-03 S1 = 3 (paczka) | `00759007733512000625` | „…ma S1 = 3; przesyłka polecona ma 1 lub 4.” | cyfra kontrolna poprawna, zły rodzaj |
| NEG-04 S1 = 2 (zadeklarowana wartość) | `00759007732512000628` | „…ma S1 = 2; …” | jak wyżej |
| NEG-05 S1 = 0 | `00759007730512000624` | „…ma S1 = 0; …” | jak wyżej |
| NEG-06 obcy prefiks GS1 | `00112345678901234560` | „20 cyfr, ale prefiks nie jest numerem SSCC Poczty Polskiej…” | 20 cyfr bez `5900773` |
| NEG-07 IAC = 0 | `00059007731512000622` | jak NEG-06 | IAC spoza 1-9 |
| NEG-08 19 cyfr | `0075900773151200062` | „Nieznany format…” | D1 bez ostatniej cyfry |
| NEG-09 21 cyfr | `007590077315120006210` | „Nieznany format…” | D1 z dopisanym 0 |
| NEG-10 litery | `abc` | „Nieznany format…” | |
| NEG-11 zły wskaźnik | `QR473124829PL` | „Nieznany format…” | Q zamiast R |
| NEG-12 kod kraju 1 litera | `RR473124829P` | „Nieznany format…” | I1 bez ostatniej litery |
| NEG-13 7 cyfr serialu | `RR47312489PL` | „Nieznany format…” | 12 znaków |
| NEG-14 pusty | (puste) | „Sprawdź” nieaktywne; Swagger 400 | |
| NEG-15 tylko spacje | `   ` | „Numer jest pusty.” lub „Sprawdź” nieaktywne | |

### 4.8 Dane negatywne: tryb „ostatni numer”

Pierwszy numer w „Pierwszy numer”, tryb `ostatni numer`, drugi numer w „Ostatni numer”, „Sprawdź”. Oczekuj czerwonego komunikatu.

| ID | Pierwszy numer | Ostatni numer | Oczekiwany błąd | Co się różni |
|---|---|---|---|---|
| RNG-01 krajowy vs zagraniczny | `00759007731512000621` | `RR473124829PL` | „…należą do różnych pul (inny typ lub prefiks).” | typ |
| RNG-02 inny IAC | `00159007731100010009` | `00259007731100010105` | jak RNG-01 | 3. cyfra (IAC 1 vs 2) |
| RNG-03 inne S1 | `00159007731100010009` | `00159007734100010109` | jak RNG-01 | 11. cyfra (S1 1 vs 4) |
| RNG-04 inny wskaźnik | `RR600000007PL` | `RB600000109PL` | jak RNG-01 | RR vs RB |
| RNG-05 inny kraj | `RR600000007PL` | `RR600000109DE` | jak RNG-01 | PL vs DE |
| RNG-06 ostatni < pierwszy | `00559007731550000054` | `00559007731550000009` | „Zakres musi mieć co najmniej jeden numer.” | kolejność |
| RNG-07 ostatni ze złą cyfrą kontrolną | `00759007731512000621` | `00759007731512000639` | „Błędna cyfra kontrolna GS1: jest 9, powinna być 8.” | D1+1 z 8 → 9 |
| RNG-08 ostatni pusty | `00759007731512000621` | (puste) | „Sprawdź” nieaktywne | |

### 4.9 Żądania do Swaggera / curl

Endpointy: `POST /api/registered-numbers/ranges/check` (weryfikacja) i `POST /api/registered-numbers/ranges/import` (zasilenie). W Swaggerze (`http://localhost:5080/swagger`): „Try it out”, wklej ciało, „Execute”.

```json
{ "firstNumber": "00159007731100010009", "count": 1000 }
```
```json
{ "firstNumber": "RR480000008PL", "lastNumber": "RR480000025PL" }
```
```json
{ "firstNumber": "00559007731550000009", "count": 5, "lastNumber": "00559007731550000047" }
```
Trzecie ciało → 400 „Podaj dokładnie jedno z: count albo lastNumber.” Z UI nie da się tego wywołać, formularz wysyła tylko jedno pole.

```bash
curl -s -X POST http://localhost:5080/api/registered-numbers/ranges/check \
  -H "Content-Type: application/json" -H "X-Editor: tester" \
  -d '{"firstNumber":"00159007731100010009","count":1000}'
```

---

## 5. Policz własny numer

Wklej do konsoli przeglądarki (F12 → Console) albo do `node`, potem wywołaj `dom(...)` lub `intl(...)`.

```js
// GS1 mod 10: od prawej, wagi 3,1,3,1...
const gs1 = d => { let s = 0; for (let i = 0; i < d.length; i++) s += +d[d.length - 1 - i] * (i % 2 ? 1 : 3); return (10 - s % 10) % 10; };
// UPU S10: wagi 8 6 4 2 3 5 9 7, 11 - (suma mod 11); 10 -> 0, 11 -> 5
const s10 = s => { const w = [8, 6, 4, 2, 3, 5, 9, 7]; let t = 0; for (let i = 0; i < 8; i++) t += +s[i] * w[i]; const c = 11 - t % 11; return c === 10 ? 0 : c === 11 ? 5 : c; };

const dom  = (iac, s1, serial) => { const b = `${iac}5900773${s1}${String(serial).padStart(8, '0')}`; return `00${b}${gs1(b)}`; };
const intl = (serial, svc = 'RR', cc = 'PL') => { const s = String(serial).padStart(8, '0'); return `${svc}${s}${s10(s)}${cc}`; };

dom(7, 1, 51200062);      // 00759007731512000621
intl(47312482);           // RR473124829PL
intl(47312485);           // RR473124850PL  (przypadek 10 -> 0)
intl(47312488);           // RR473124885PL  (przypadek 11 -> 5)

// cała rolka:
Array.from({ length: 1000 }, (_, i) => dom(1, 1, 10001000 + i));
```

---

## 6. Dodatkowe przykłady do testów

Wszystkie numery poprawne (chyba że sekcja mówi inaczej), policzone tym samym algorytmem co wyżej. Prefiksy i seriale są rozłączne z sekcją 4, więc można je używać równolegle na tej samej bazie. Każdą tabelę można też wykorzystać jako źródło do sekcji „Sprawdź numer”.

### 6.1 Krajowe rolki, tryb „ilość”

Wklej pierwszy numer, wpisz ilość, „Sprawdź”, „Zasil”. Oczekiwany ostatni numer musi zgadzać się z wynikiem sprawdzenia.

| ID | IAC | S1 | Pierwszy numer | Ilość | Oczekiwany ostatni numer |
|---|---|---|---|---|---|
| EX-D01 | 4 | 1 | `00459007731400010007` | 1000 | `00459007731400019994` |
| EX-D02 | 4 | 4 | `00459007734400020007` | 1000 | `00459007734400029994` |
| EX-D03 | 6 | 1 | `00659007731610000007` | 1000 | `00659007731610009994` |
| EX-D04 | 6 | 4 | `00659007734620000005` | 250 | `00659007734620002498` |
| EX-D05 | 8 | 1 | `00859007731815000004` | 1000 | `00859007731815009991` |
| EX-D06 | 8 | 4 | `00859007734825000002` | 100 | `00859007734825000996` |
| EX-D07 | 9 | 1 | `00959007731910000005` | 1000 | `00959007731910009992` |
| EX-D08 | 9 | 4 | `00959007734920000003` | 10 | `00959007734920000096` |
| EX-D09 | 1 | 4 | `00159007734140000009` | 500 | `00159007734140004991` |
| EX-D10 | 2 | 4 | `00259007734240000005` | 1000 | `00259007734240009992` |
| EX-D11 | 3 | 1 | `00359007731310000009` | 25 | `00359007731310000245` |
| EX-D12 | 5 | 4 | `00559007734540000003` | 1000 | `00559007734540009990` |

### 6.2 Zagraniczne rolki, tryb „ilość”

| ID | Wskaźnik | Kraj | Pierwszy numer | Ilość | Oczekiwany ostatni numer |
|---|---|---|---|---|---|
| EX-I01 | RR | PL | `RR101000000PL` | 1000 | `RR101009998PL` |
| EX-I02 | RR | PL | `RR102000006PL` | 500 | `RR102004997PL` |
| EX-I03 | RA | PL | `RA201000002PL` | 1000 | `RA201009995PL` |
| EX-I04 | RB | PL | `RB202000009PL` | 100 | `RB202000998PL` |
| EX-I05 | RC | PL | `RC203000005PL` | 50 | `RC203000495PL` |
| EX-I06 | RD | PL | `RD204000001PL` | 1000 | `RD204009990PL` |
| EX-I07 | RE | PL | `RE205000008PL` | 25 | `RE205000246PL` |
| EX-I08 | RL | PL | `RL206000004PL` | 1000 | `RL206009992PL` |
| EX-I09 | RR | DE | `RR301000005DE` | 100 | `RR301000994DE` |
| EX-I10 | RR | CZ | `RR302000001CZ` | 100 | `RR302000995CZ` |
| EX-I11 | RR | GB | `RR303000008GB` | 10 | `RR303000095GB` |
| EX-I12 | RZ | PL | `RZ207000005PL` | 1000 | `RZ207009999PL` |

### 6.3 Pary pierwszy-ostatni, tryb „ostatni numer”

Wklej oba numery, „Sprawdź”. Liczba w przedziale musi równać się kolumnie „Oczekiwana ilość”.

| ID | Pierwszy numer | Ostatni numer | Oczekiwana ilość |
|---|---|---|---|
| EX-R01 | `00459007731450000003` | `00459007731450000096` | 10 |
| EX-R02 | `00459007731450010002` | `00459007731450010996` | 100 |
| EX-R03 | `00659007734650000006` | `00659007734650000006` | 1 |
| EX-R04 | `00859007731850000007` | `00859007731850009994` | 1000 |
| EX-R05 | `00959007734950000004` | `00959007734950000042` | 5 |
| EX-R06 | `RR150000006PL` | `RR150000099PL` | 10 |
| EX-R07 | `RR150010003PL` | `RR150019991PL` | 1000 |
| EX-R08 | `RB250000009PL` | `RB250000499PL` | 50 |
| EX-R09 | `RR350000001DE` | `RR350000001DE` | 1 |
| EX-R10 | `RA150020005PL` | `RA150020116PL` | 12 |
| EX-R11 | `00259007731250000001` | `00259007731250000193` | 20 |
| EX-R12 | `RZ150030008PL` | `RZ150030025PL` | 3 |

### 6.4 Pojedyncze numery poprawne

Do zasilenia z ilością 1, do „Sprawdź numer” (oczekiwane: poprawny, nie ma go w puli, dopóki nie zasilisz) oraz jako baza do sekcji 6.5.

| ID | Numer | Typ |
|---|---|---|
| EX-S01 | `00459007731470000014` | krajowy, IAC 4, S1 1 |
| EX-S02 | `00459007734470000022` | krajowy, IAC 4, S1 4 |
| EX-S03 | `00659007731670000030` | krajowy, IAC 6, S1 1 |
| EX-S04 | `00659007734670000048` | krajowy, IAC 6, S1 4 |
| EX-S05 | `00859007731870000056` | krajowy, IAC 8, S1 1 |
| EX-S06 | `00859007734870000064` | krajowy, IAC 8, S1 4 |
| EX-S07 | `00959007731970000076` | krajowy, IAC 9, S1 1 |
| EX-S08 | `00959007734970000084` | krajowy, IAC 9, S1 4 |
| EX-S09 | `RR170000019PL` | zagraniczny RR PL |
| EX-S10 | `RA170000022PL` | zagraniczny RA PL |
| EX-S11 | `RB170000036PL` | zagraniczny RB PL |
| EX-S12 | `RR170000040DE` | zagraniczny RR DE |
| EX-S13 | `RR170000053FR` | zagraniczny RR FR |
| EX-S14 | `RZ170000067PL` | zagraniczny RZ PL |

### 6.5 Błędne cyfry kontrolne

Te same numery co w 6.4 z cyfrą kontrolną powiększoną o 1. Wklej jako pierwszy numer, „Sprawdź”: oczekiwany czerwony komunikat z podaną parą „jest / powinna być”.

| ID | Numer | Oczekiwany komunikat |
|---|---|---|
| EX-B01 | `00459007731470000015` | Błędna cyfra kontrolna GS1: jest 5, powinna być 4 |
| EX-B02 | `00459007734470000023` | Błędna cyfra kontrolna GS1: jest 3, powinna być 2 |
| EX-B03 | `00659007731670000031` | Błędna cyfra kontrolna GS1: jest 1, powinna być 0 |
| EX-B04 | `00659007734670000049` | Błędna cyfra kontrolna GS1: jest 9, powinna być 8 |
| EX-B05 | `00859007731870000057` | Błędna cyfra kontrolna GS1: jest 7, powinna być 6 |
| EX-B06 | `00859007734870000065` | Błędna cyfra kontrolna GS1: jest 5, powinna być 4 |
| EX-B07 | `00959007731970000077` | Błędna cyfra kontrolna GS1: jest 7, powinna być 6 |
| EX-B08 | `00959007734970000085` | Błędna cyfra kontrolna GS1: jest 5, powinna być 4 |
| EX-B09 | `RR170000010PL` | Błędna cyfra kontrolna S10: jest 0, powinna być 9 |
| EX-B10 | `RA170000023PL` | Błędna cyfra kontrolna S10: jest 3, powinna być 2 |
| EX-B11 | `RB170000037PL` | Błędna cyfra kontrolna S10: jest 7, powinna być 6 |
| EX-B12 | `RR170000041DE` | Błędna cyfra kontrolna S10: jest 1, powinna być 0 |
| EX-B13 | `RR170000054FR` | Błędna cyfra kontrolna S10: jest 4, powinna być 3 |
| EX-B14 | `RZ170000068PL` | Błędna cyfra kontrolna S10: jest 8, powinna być 7 |

### 6.6 Krajowe z niedozwolonym S1

Cyfra kontrolna poprawna, ale rodzaj przesyłki inny niż polecona. Oczekiwany komunikat: „…ma S1 = N; przesyłka polecona ma 1 lub 4.”

| ID | Numer | S1 | Rodzaj wg GS1 Polska |
|---|---|---|---|
| EX-K01 | `00459007732490000008` | 2 | list z zadeklarowaną wartością |
| EX-K02 | `00459007733490000012` | 3 | paczka |
| EX-K03 | `00459007735490000023` | 5 | pobraniowa |
| EX-K04 | `00459007736490000037` | 6 | e-przesyłka |
| EX-K05 | `00459007737490000041` | 7 | nieużywany |
| EX-K06 | `00459007738490000055` | 8 | biznesowa |
| EX-K07 | `00459007739490000069` | 9 | nieużywany |

---

## 7. Przypadki biznesowe (use case) do zlecenia testerowi

Każdy przypadek ma: kontekst biznesowy (po co to jest), ryzyko (co się stanie, jeśli nie działa), zakres testu (co i jak sprawdzić), dane (odwołanie do sekcji tego pliku lub do `TESTS.md`) i priorytet. Priorytet P1 = blokuje wdrożenie, P2 = trzeba przed produkcją, P3 = może iść w kolejnej iteracji.

### UC-01 Zasilenie puli nową rolką z Elektronicznego Nadawcy

**Kontekst.** Kancelaria dostaje z Poczty rolkę 1000 nalepek. Operator ma je wprowadzić do systemu jednym ruchem: przepisuje pierwszy numer z rolki i podaje 1000. To najczęstsza operacja w module, wykonywana przy każdej dostawie.
**Ryzyko.** Jeśli generowanie się myli choć o jedną cyfrę kontrolną, wszystkie listy z tej rolki będą miały numery, których Poczta nie rozpozna.
**Zakres.** Zasilić rolkę krajową i zagraniczną w trybie „ilość”. Porównać ostatni numer z wyniku sprawdzenia z oczekiwanym z tabeli. Po zasileniu otworzyć „Lista”, przefiltrować po fragmencie numeru i sprawdzić, że jest dokładnie 1000 wierszy w stanie „Dostępny” z edytorem z nagłówka. Wyrywkowo (pierwszy, środkowy, ostatni) zweryfikować numer w „Sprawdź numer”.
**Dane.** 4.1 ROLL-D1, ROLL-I1; 6.1 i 6.2 dla pozostałych IAC i wskaźników.
**Priorytet.** P1.

### UC-02 Operator wpisuje numer tak, jak widzi go na nalepce

**Kontekst.** Na nalepce numer jest drukowany z nawiasem i spacjami: `(00) 7 5900773 1 51200062 1`. Operator nie powinien musieć go „czyścić” przed wklejeniem. Podobnie skaner kodu kreskowego może wstawić numer bez nawiasu, a ktoś może wpisać zagraniczny małymi literami.
**Ryzyko.** Odrzucanie poprawnych numerów przez format zapisu = frustracja i ręczne poprawianie, a w skrajnym przypadku wpisanie numeru z literówką po ręcznej „obróbce”.
**Zakres.** Każdy wariant z tabeli normalizacji ma dać ten sam wynik sprawdzenia co zapis kanoniczny.
**Dane.** 4.4.
**Priorytet.** P2.

### UC-03 Ponowne zasilenie tą samą rolką (pomyłka operatora lub dwie osoby)

**Kontekst.** Dwie osoby dostały tę samą rolkę do wprowadzenia, albo operator nie był pewien, czy zasilenie się udało, i kliknął drugi raz.
**Ryzyko.** Duplikaty w puli oznaczałyby wydanie tego samego numeru dwóm listom. Z drugiej strony twardy błąd przy ponownym imporcie blokowałby dosypanie brakującej końcówki rolki.
**Zakres.** Zasilić 5 numerów, potem ten sam przedział z ilością 7. Sprawdzenie ma pokazać 5 istniejących w tabeli z ich stanem i edytorem oraz 2 nowe; zasilenie ma dodać tylko 2. Następnie cały przedział ponownie: 0 nowych i nieaktywny „Zasil”. Sprawdzić też nakładkę na początku przedziału i przedział przylegający.
**Dane.** 4.3 DUP-01…DUP-07, DUP-I1, DUP-I2.
**Priorytet.** P1.

### UC-04 Rozdzielenie rolki na dwie transze

**Kontekst.** Z jednej rolki część nalepek idzie do jednego działu, część do drugiego. Operator zasila w dwóch krokach: pierwszy numer + 600, potem numer 601. z rolki + 400.
**Ryzyko.** Błąd o jeden na styku transz (pominięty lub zdublowany numer).
**Zakres.** Zasilić pierwszą transzę, policzyć snippetem z sekcji 5 numer o serialu `pierwszy + 600`, zasilić drugą transzę w trybie „ostatni numer” z oczekiwanym ostatnim numerem rolki. Sprawdzenie ma pokazać 0 istniejących, a łączna liczba w „Lista” po filtrze ma być 1000.
**Dane.** 6.1 EX-D03 (rolka 1000, IAC 6, S1 1, serial 61000000). Druga transza zaczyna się od `dom(6, 1, 61000600)`.
**Priorytet.** P2.

### UC-05 Zasilenie z podaniem pierwszego i ostatniego numeru

**Kontekst.** Część rolek ma na opakowaniu podany zakres „od-do” zamiast ilości. Operator przepisuje oba numery.
**Ryzyko.** Błąd w liczeniu ilości z zakresu, albo przyjęcie dwóch numerów z różnych rolek jako jednego przedziału.
**Zakres.** Pary z tabeli mają dawać dokładnie oczekiwaną ilość. Pary z różnymi prefiksami (typ, IAC, S1, wskaźnik, kraj) mają być odrzucone z komunikatem o różnych pulach. Ostatni mniejszy od pierwszego: odrzucony.
**Dane.** 6.3 EX-R01…R12; 4.8 RNG-01…RNG-08.
**Priorytet.** P1.

### UC-06 Literówka w przepisanym numerze

**Kontekst.** Operator przepisuje 20 cyfr ręcznie. Jedna pomyłka to normalna sytuacja.
**Ryzyko.** Bez wykrycia literówki system wygenerowałby 1000 numerów z niewłaściwej rolki, których nie ma fizycznie na nalepkach.
**Zakres.** Numery ze złą cyfrą kontrolną mają być odrzucone przed jakimkolwiek zapisem, z komunikatem „jest X, powinna być Y”. Sprawdzić, że po błędzie nie ma sekcji wyniku i „Zasil” jest nieaktywne. Sprawdzić za krótkie, za długie i obce numery.
**Dane.** 4.7 NEG-01…NEG-15; 6.5 EX-B01…B14.
**Priorytet.** P1.

### UC-07 Rolka z innym rodzajem przesyłki (paczka, pobranie, wartość zadeklarowana)

**Kontekst.** Poczta wydaje rolki dla różnych usług. Różnią się cyfrą S1. Moduł obsługuje tylko listy polecone.
**Ryzyko.** Wprowadzenie rolki paczkowej do puli listów poleconych = nadanie listu z numerem paczki, reklamacje, zwroty.
**Zakres.** Numery z S1 spoza {1, 4} mają być odrzucone z czytelnym komunikatem, mimo poprawnej cyfry kontrolnej. S1 = 4 ma być przyjęte tak samo jak S1 = 1.
**Dane.** 6.6 EX-K01…K07; 4.1 ROLL-D3, ROLL-D4.
**Priorytet.** P1.

### UC-08 Zabezpieczenie przed omyłkowym importem ogromnego zakresu

**Kontekst.** Operator zamiast 1000 wpisuje 1000000 (dodatkowe zero) albo podaje jako „ostatni numer” numer z zupełnie innej rolki o wyższym serialu.
**Ryzyko.** Miliony wierszy w bazie, numery, których fizycznie nie ma, spowolnienie listy i statystyk.
**Zakres.** Ilość 100 001 ma być odrzucona, 100 000 przyjęte. Zakres wychodzący poza 99999999 odrzucony. Ilość 0, ujemna, ułamkowa: „Sprawdź” nieaktywne lub 400 z API.
**Dane.** 4.6 BND-04…BND-09, BND-11.
**Priorytet.** P2.

### UC-09 Aplikacja nadawcza pobiera kolejny numer

**Kontekst.** System wysyłkowy przy nadaniu listu prosi Mass o kolejny wolny numer. Numer ma być wydany raz, w kolejności rosnącej, i od razu zarezerwowany.
**Ryzyko.** Dwa listy z tym samym numerem (najgroźniejszy błąd modułu) albo dziury w numeracji, które trudno rozliczyć z Pocztą.
**Zakres.** Na kafelku statystyk kliknąć „Pobierz kolejny numer”: ma zwrócić najniższy dostępny serial danego typu i przełączyć go w „Zarezerwowany”. Powtórzyć: kolejny numer. Na PostgreSQL uruchomić 20 równoległych wywołań `POST /acquire` i sprawdzić, że każde dostało inny numer.
**Dane.** pula po UC-01; `TESTS.md` TM-STA-10, TM-DB-05.
**Priorytet.** P1.

### UC-10 Cykl życia numeru: rezerwacja, użycie, zwolnienie

**Kontekst.** List może zostać nadany (numer użyty), a rezerwacja może zostać porzucona (użytkownik anulował wysyłkę), wtedy numer wraca do puli.
**Ryzyko.** Numer użyty wracający do puli = duplikat na dwóch listach. Numer zarezerwowany, który nigdy nie wraca = wyciek puli.
**Zakres.** Przejścia dozwolone: Dostępny → Zarezerwowany → Użyty, Zarezerwowany → Dostępny, Dostępny → Użyty. Przejścia zakazane: Użyty → cokolwiek. Sprawdzić przyciski w UI (mają się pojawiać tylko dozwolone) i odpowiedź 409 z API dla zakazanych.
**Dane.** numery z 6.4 po zasileniu z ilością 1; `TESTS.md` TM-STA-01…TM-STA-07.
**Priorytet.** P1.

### UC-11 Uszkodzona lub zgubiona nalepka

**Kontekst.** Nalepka się rozdarła, zabrudziła lub zgubiła. Numer fizycznie nie może zostać użyty i trzeba go wyłączyć z puli, ale zostawić ślad.
**Ryzyko.** Bez anulowania system wyda ten numer aplikacji, a operator nie będzie miał nalepki do naklejenia.
**Zakres.** „Anuluj” z potwierdzeniem: numer w stanie „Anulowany”, bez dalszych akcji, widoczny na liście po filtrze stanu, nieliczony w „Zostało N numerów”. Odrzucenie w oknie potwierdzenia nie zmienia stanu.
**Dane.** dowolny numer z 6.4; `TESTS.md` TM-STA-05, TM-STA-06.
**Priorytet.** P2.

### UC-12 Nalepka użyta ręcznie, poza systemem

**Kontekst.** W awarii ktoś nakleił nalepkę i nadał list bez rezerwacji w Mass. Trzeba to potem odnotować.
**Ryzyko.** Numer pozostaje „Dostępny” i zostanie wydany drugi raz.
**Zakres.** Na numerze w stanie „Dostępny” akcja „Oznacz jako użyty” bez wcześniejszej rezerwacji ma przejść od razu do „Użyty”.
**Dane.** `TESTS.md` TM-STA-03.
**Priorytet.** P2.

### UC-13 Dwie osoby zmieniają ten sam numer jednocześnie

**Kontekst.** Dwóch operatorów ma otwartą listę. Jeden oznacza numer jako użyty, drugi w tym czasie rezerwuje.
**Ryzyko.** Cicha utrata zmiany, stan niezgodny z rzeczywistością.
**Zakres.** Dwie karty przeglądarki. W A „Oznacz jako użyty”, w B „Zarezerwuj”. B ma dostać komunikat 409 z opisem konfliktu, a po odświeżeniu stan z A. Na PostgreSQL powtórzyć (token `xmin`).
**Dane.** `TESTS.md` TM-STA-08, TM-DB-06.
**Priorytet.** P2.

### UC-14 Weryfikacja numeru z dokumentu lub reklamacji

**Kontekst.** Klient reklamuje list i podaje numer. Pracownik chce sprawdzić, czy numer jest nasz, czy został użyty, przez kogo i kiedy.
**Ryzyko.** Brak możliwości szybkiej odpowiedzi „to nie nasz numer” lub „nadany przez X dnia Y”.
**Zakres.** Ekran „Sprawdź numer”: numer w puli (zielony, stan, edytor, data), numer poprawny spoza puli (żółty), numer błędny (czerwony z powodem), numer wpisany z literówką w zapisie (normalizacja).
**Dane.** 6.4 (po zasileniu i bez), 6.5, 4.4; `TESTS.md` TM-CHK-01…TM-CHK-06.
**Priorytet.** P2.

### UC-15 Kierownik sprawdza, ile numerów zostało

**Kontekst.** Przed końcem miesiąca trzeba wiedzieć, czy zamawiać nową rolkę.
**Ryzyko.** Złe liczby = brak nalepek w szczycie wysyłek albo zbędne zamówienia.
**Zakres.** Panel „Zostało N numerów do wykorzystania” i kafelki per typ (dostępne, zarezerwowane, użyte, anulowane, razem) mają zgadzać się z sumą wierszy po filtrach. Po każdej zmianie stanu liczby mają się aktualizować.
**Dane.** pula po UC-01, UC-10, UC-11; `TESTS.md` TM-LST-01, E2E-LST-04.
**Priorytet.** P2.

### UC-16 Pula wyczerpana

**Kontekst.** Wszystkie numery danego typu są użyte lub anulowane, a aplikacja prosi o kolejny.
**Ryzyko.** Błąd 500 zamiast czytelnego komunikatu, aplikacja nadawcza wpada w pętlę.
**Zakres.** Zasilić 3 numery zagraniczne (np. 4.3 DUP-I1), zużyć je przez „Pobierz kolejny numer” + „Oznacz jako użyty”. Kolejne pobranie: przycisk nieaktywny, API zwraca 409 „Pula numerów wyczerpana”.
**Dane.** 4.3 DUP-I1; `TESTS.md` TM-STA-11.
**Priorytet.** P2.

### UC-17 Rozliczenie z Pocztą: kto i kiedy użył numeru

**Kontekst.** Audyt lub rozliczenie faktury za listy polecone wymaga historii: jaki numer, jaki stan, kto zmienił, kiedy.
**Ryzyko.** Brak edytora lub daty = brak rozliczalności.
**Zakres.** Każda operacja (zasilenie, rezerwacja, użycie, anulowanie) ma zapisać edytora z pola „Edytor” i datę zmiany. Wywołanie API bez nagłówka `X-Editor` ma zapisać `api`. Na PostgreSQL sprawdzić kolumny `editor` i `change_date` (UTC).
**Dane.** `TESTS.md` TM-STA-01, TM-API-05, TM-DB-04.
**Priorytet.** P2.

### UC-18 Wyszukanie numerów po fragmencie, typie i stanie

**Kontekst.** Operator szuka konkretnej rolki (po IAC) albo listy numerów zarezerwowanych, których nikt nie zużył od tygodnia.
**Ryzyko.** Bez filtrów przy kilkudziesięciu tysiącach numerów lista jest bezużyteczna.
**Zakres.** Filtr typ, stan, fragment numeru (także ze spacjami i „(00)”), stronicowanie (25/strona), „Wyczyść”, brak dopasowań.
**Dane.** pula po UC-01 (ponad 25 numerów); `TESTS.md` TM-LST-02…TM-LST-07.
**Priorytet.** P3.

### UC-19 Rolki dla zagranicy z różnymi wskaźnikami i krajami

**Kontekst.** Poczta może wydać rolki ze wskaźnikiem innym niż RR (np. RA, RB) i dla przesyłek nadawanych z innych krajów grupy.
**Ryzyko.** System, który akceptuje tylko `RR…PL`, zablokuje nowe rolki bez zmiany kodu.
**Zakres.** Zasilić rolki z różnymi wskaźnikami i kodami kraju. Każda ma być osobną pulą: para RR/RB albo PL/DE w trybie „ostatni numer” odrzucona.
**Dane.** 6.2 EX-I03…I12; 4.8 RNG-04, RNG-05.
**Priorytet.** P3.

### UC-20 Wdrożenie na PostgreSQL

**Kontekst.** Testy dzienne idą na bazie InMemory. Produkcja to PostgreSQL ze skryptem SQL i ograniczeniami w bazie.
**Ryzyko.** Różnice zachowania (unikalność, blokady, typy) ujawnione dopiero na produkcji.
**Zakres.** Uruchomić skrypt, przejść UC-01, UC-03, UC-09, UC-13 na PostgreSQL. Próba ręcznego `INSERT` duplikatu `full_number` ma dać błąd unikalności, `type = 3` błąd CHECK.
**Dane.** `TESTS.md` sekcja 2.6.
**Priorytet.** P1 przed wdrożeniem.

### Kolejność dla testera

1. P1 na InMemory: UC-01, UC-03, UC-05, UC-06, UC-07, UC-09, UC-10.
2. P2 na InMemory: UC-02, UC-04, UC-08, UC-11…UC-17.
3. P3: UC-18, UC-19.
4. UC-20 na PostgreSQL po zaliczeniu 1-3.

Każdy wynik odnotować z ID przypadku, użytymi numerami (ID z tabel) i zrzutem ekranu przy błędzie. Numery raz zasilone zostają w bazie, więc przy powtórce testu bierz następny wiersz z tabeli albo policz nowy numer snippetem z sekcji 5.
