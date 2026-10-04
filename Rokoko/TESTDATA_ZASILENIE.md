# Mass - dane testowe do formularza „Zasilenie” (import przedziału numerów R)

Wszystkie numery poniżej mają poprawną cyfrę kontrolną (GS1 mod 10 dla krajowych, S10 mod 11 dla zagranicznych), chyba że sekcja mówi inaczej. Policzone tym samym algorytmem co `RegisteredNumberFormatter` i `e2e/helpers/numbers.ts`.

## Jak działa formularz

1. W nagłówku aplikacji kliknij przycisk **Zasilenie**. Wpisz też coś w pole **Edytor** (np. `tester`), bo ta wartość trafia do kolumny „edytor” każdego dodanego numeru.
2. W pole **Pierwszy numer** wklej pełny numer z cyfrą kontrolną (20 cyfr krajowy lub 13 znaków zagraniczny). Spacje, myślniki i „(00)” są dozwolone.
3. W polu **Tryb** wybierz `ilość` albo `ostatni numer`. Zależnie od wyboru pojawia się pole **Ilość** (liczba, domyślnie 1000) albo **Ostatni numer** (pełny numer).
4. Kliknij **Sprawdź**. Pod formularzem pojawi się wynik: typ, przedział, ile numerów w przedziale, ile nowych, ile już w puli, oraz tabela istniejących.
5. Kliknij **Zasil** (aktywne tylko po aktualnym sprawdzeniu i gdy jest co dodać). Komunikat podaje: dodano / pominięto.
6. **Wyczyść** przywraca pusty formularz. Jeśli po „Sprawdź” zmienisz jakiekolwiek pole, „Zasil” blokuje się do ponownego sprawdzenia.

Pula pamięta numery między testami (także w InMemory do restartu API). Każda sekcja używa innych prefiksów (IAC / S1 / wskaźnik usługi / serial), żeby scenariusze nie wchodziły sobie w drogę. Po wyczerpaniu zestawu zmień IAC (1-9) albo serial.

## 1. Pełne rolki (happy path, tryb „ilość”)

Odpowiada rolce 1000 nalepek z Elektronicznego Nadawcy: pierwszy numer z rolki + ilość.

| ID | Typ | IAC | S1 | Pierwszy numer | Ilość | Oczekiwany ostatni numer | Jak wprowadzić |
|---|---|---|---|---|---|---|---|
| ROLL-D1 | Krajowy | 1 | 1 | `00159007731100010009` | 1000 | `00159007731100019996` | W „Pierwszy numer” wklej numer, tryb `ilość`, w „Ilość” zostaw domyślne 1000, kliknij „Sprawdź”, potem „Zasil”. |
| ROLL-D2 | Krajowy | 2 | 1 | `00259007731200020004` | 1000 | `00259007731200029991` | Jak ROLL-D1, tylko z tym numerem; służy do sprawdzenia, że drugi IAC to osobna pula. |
| ROLL-D3 | Krajowy | 3 | 4 | `00359007734300030000` | 1000 | `00359007734300039997` | Jak ROLL-D1; numer ma S1 = 4, formularz musi go przyjąć tak samo jak S1 = 1. |
| ROLL-D4 | Krajowy | 7 | 4 | `00759007734512000622` | 1000 | `00759007734512010614` | Jak ROLL-D1; ten sam IAC 7 i serial co numer D1 z TESTS.md, ale S1 = 4, więc nie może pokazać duplikatów z D1. |

| ID | Typ | Wskaźnik | Kraj | Pierwszy numer | Ilość | Oczekiwany ostatni numer | Jak wprowadzić |
|---|---|---|---|---|---|---|---|
| ROLL-I1 | Zagraniczny | RR | PL | `RR473124829PL` | 1000 | `RR473134812PL` | W „Pierwszy numer” wklej numer, tryb `ilość`, „Ilość” 1000, „Sprawdź”, „Zasil”; w wyniku typ musi być „Zagraniczny”. |
| ROLL-I2 | Zagraniczny | RR | PL | `RR600000007PL` | 500 | `RR600004998PL` | Jak ROLL-I1, ale w „Ilość” wpisz 500 zamiast domyślnego 1000. |
| ROLL-I3 | Zagraniczny | RB | PL | `RB700000000PL` | 100 | `RB700000999PL` | Jak ROLL-I1 z „Ilość” 100; sprawdza wskaźnik usługi inny niż RR. |
| ROLL-I4 | Zagraniczny | RR | DE | `RR800000002DE` | 50 | `RR800000492DE` | Jak ROLL-I1 z „Ilość” 50; sprawdza kod kraju inny niż PL. |

Oczekiwane: „Sprawdź” → N w przedziale, N nowych, 0 w puli, zielony komunikat. „Zasil” → dodano N, pominięto 0.

Wariant w trybie „ostatni numer”: dla dowolnego wiersza wybierz tryb `ostatni numer`, w „Pierwszy numer” wklej pierwszy numer, w „Ostatni numer” wklej oczekiwany ostatni numer z tabeli i kliknij „Sprawdź”; liczba w przedziale musi być równa kolumnie „Ilość”.

## 2. Krótkie przedziały do scenariuszy duplikatów (krajowy IAC 5, S1 1)

Kolejne numery seryjne 55000000…55000011. Używaj ich w podanej kolejności kroków, na tej samej bazie, bez restartu API.

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
| DUP-01 pierwsze zasilenie | `00559007731550000009` | ilość | 5 | 5 nowych, 0 w puli; po „Zasil”: dodano 5, pominięto 0 | Wklej numer, tryb `ilość`, w „Ilość” wpisz 5, „Sprawdź”, „Zasil”. |
| DUP-02 nakładka częściowa (koniec) | `00559007731550000009` | ilość | 7 | 2 nowe, 5 w puli, żółte ostrzeżenie, tabela 5 istniejących; „Zasil”: dodano 2, pominięto 5 | Zostaw ten sam pierwszy numer, zmień „Ilość” na 7, „Sprawdź” (przycisk „Zasil” odblokuje się dopiero po sprawdzeniu), „Zasil”. |
| DUP-03 cały przedział w puli | `00559007731550000016` | ilość | 3 | „Cały przedział jest już w puli”, 0 nowych, „Zasil” nieaktywne | Wklej numer serialu 55000001, „Ilość” 3, „Sprawdź”; nie klikaj „Zasil” (ma być nieaktywne). |
| DUP-04 nakładka częściowa (początek) | `00559007731550000054` | ilość | 4 | 2 nowe (55000007, 55000008), 2 w puli (55000005, 55000006) | Wklej numer serialu 55000005, „Ilość” 4, „Sprawdź”, „Zasil”. |
| DUP-05 przedział przylegający | `00559007731550000092` | ilość | 3 | 3 nowe, 0 w puli (55000009-55000011) | Wklej numer serialu 55000009, „Ilość” 3, „Sprawdź”, „Zasil”. |
| DUP-06 cały ciąg | `00559007731550000009` | ostatni numer | `00559007731550000115` | 12 w przedziale, 0 nowych, 12 w puli (po DUP-01…05 wszystko już jest) | Wklej pierwszy numer, przełącz tryb na `ostatni numer`, w „Ostatni numer” wklej numer serialu 55000011, „Sprawdź”; „Zasil” ma być nieaktywne. |
| DUP-07 jeden numer | `00559007731550000108` | ilość | 1 | 1 w przedziale, 0 nowych, 1 w puli | Wklej numer serialu 55000010, tryb `ilość`, „Ilość” 1, „Sprawdź”; w tabeli istniejących jeden wiersz. |

Analogiczny ciąg zagraniczny (RR, PL, serial 48000000…48000005):

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
| DUP-I1 | `RR480000008PL` | ostatni numer | `RR480000025PL` | Typ Zagraniczny, 3 w przedziale, dodano 3 | Wklej pierwszy numer, tryb `ostatni numer`, w „Ostatni numer” wklej numer serialu 48000002, „Sprawdź”, „Zasil”. |
| DUP-I2 | `RR480000011PL` | ilość | 5 | 3 nowe, 2 w puli | Wklej numer serialu 48000001, tryb `ilość`, „Ilość” 5, „Sprawdź”; w tabeli istniejących 2 wiersze; „Zasil” dodaje 3. |

## 3. Warianty zapisu tego samego numeru (normalizacja)

Każdy wpis wklej dokładnie w takiej postaci w pole „Pierwszy numer”, ustaw tryb `ilość` i „Ilość” 1, kliknij „Sprawdź”. W wyniku pole „przedział” ma pokazać numer znormalizowany z prawej kolumny.

| Wpis w polu | Znormalizowany | Jak wprowadzić |
|---|---|---|
| `(00) 7 5900773 1 51200062 1` | `00759007731512000621` | Wklej z nawiasem i spacjami tak jak na nalepce; nie usuwaj niczego. |
| `00 7 5900773 1 51200062 1` | `00759007731512000621` | Wklej ze spacjami, bez nawiasu. |
| `00-7-5900773-1-51200062-1` | `00759007731512000621` | Wklej z myślnikami między grupami. |
| `  00759007731512000621  ` (spacje na końcach) | `00759007731512000621` | Wpisz dwie spacje, numer, dwie spacje. |
| `rr 473124829 pl` | `RR473124829PL` | Wpisz małymi literami ze spacjami. |
| `RR-47312482-9-PL` | `RR473124829PL` | Wpisz z myślnikami, cyfra kontrolna oddzielona. |
| `rr473124829pl` | `RR473124829PL` | Wpisz małymi literami bez spacji. |

## 4. Wartości graniczne

| ID | Pierwszy numer | Tryb | Wartość | Oczekiwany wynik | Jak wprowadzić |
|---|---|---|---|---|---|
| BND-01 serial minimalny | `00959007731000000007` (serial 00000000) | ilość | 1 | OK, 1 w przedziale | Wklej numer, tryb `ilość`, „Ilość” 1, „Sprawdź”. |
| BND-02 serial maksymalny | `00959007731999999993` (serial 99999999) | ilość | 1 | OK, 1 w przedziale | Wklej numer, tryb `ilość`, „Ilość” 1, „Sprawdź”. |
| BND-03 do końca zakresu | `00959007731999999955` (serial 99999995) | ilość | 5 | OK, ostatni = `00959007731999999993` | Wklej numer, „Ilość” 5, „Sprawdź”; ostatni numer w wyniku ma się równać BND-02. |
| BND-04 poza zakres seriali | `00959007731999999900` (serial 99999990) | ilość | 20 | Błąd: „Zakres wychodzi poza maksymalny numer seryjny 99999999” | Wklej numer, „Ilość” 20, „Sprawdź”; oczekuj czerwonego komunikatu, bez wyniku sprawdzenia. |
| BND-05 limit ilości | `00859007731010000007` | ilość | 100000 | OK; ostatni = `00859007731010999998` (uwaga: import 100 000 wierszy, tylko na bazie testowej) | Wklej numer, w „Ilość” wpisz 100000, „Sprawdź”; „Zasil” kliknij tylko na bazie testowej. |
| BND-06 ponad limit | `00859007731010000007` | ilość | 100001 | Błąd limitu (100 000) | Wklej numer, w „Ilość” wpisz 100001, „Sprawdź”; oczekuj błędu. |
| BND-07 ilość 0 | dowolny poprawny | ilość | 0 | „Sprawdź” nieaktywne (UI); Swagger: 400 z walidacji `Range(1, 100000)` | Wklej np. BND-01, w „Ilość” wpisz 0; sprawdź, że „Sprawdź” jest nieaktywne. |
| BND-08 ilość ujemna | dowolny poprawny | ilość | -5 | jak BND-07 | W „Ilość” wpisz -5; „Sprawdź” ma być nieaktywne. |
| BND-09 ilość ułamkowa | dowolny poprawny | ilość | 2.5 | pole `number` nie przyjmie / Swagger 400 | W „Ilość” spróbuj wpisać 2.5 (lub 2,5); pole ma odrzucić wartość lub „Sprawdź” ma być nieaktywne. |
| BND-10 zagraniczny min | `RR000000005PL` | ilość | 1 | OK | Wklej numer, „Ilość” 1, „Sprawdź”. |
| BND-11 zagraniczny max | `RR999999995PL` | ilość | 2 | Błąd: poza maksymalny numer seryjny | Wklej numer, „Ilość” 2, „Sprawdź”; oczekuj błędu (z „Ilość” 1 przechodzi). |
| BND-12 ostatni = pierwszy | `00559007731550000009` | ostatni numer | `00559007731550000009` | 1 w przedziale | Wklej ten sam numer w „Pierwszy numer” i w „Ostatni numer” (tryb `ostatni numer`), „Sprawdź”. |

## 5. Dane negatywne - pierwszy numer

Dla każdego wiersza: wklej wartość w „Pierwszy numer”, zostaw tryb `ilość` i „Ilość” 1, kliknij „Sprawdź”. Oczekuj czerwonego komunikatu, braku sekcji wyniku i nieaktywnego „Zasil”.

| ID | Pierwszy numer | Oczekiwany błąd | Jak wprowadzić |
|---|---|---|---|
| NEG-01 zła cyfra kontrolna GS1 | `00759007731512000622` | „Błędna cyfra kontrolna GS1: jest 2, powinna być 1.” | Wklej numer D1 z ostatnią cyfrą zmienioną z 1 na 2. |
| NEG-02 zła cyfra kontrolna S10 | `RR473124820PL` | „Błędna cyfra kontrolna S10: jest 0, powinna być 9.” | Wklej numer I1 z cyfrą kontrolną (11. znak) zmienioną z 9 na 0. |
| NEG-03 S1 = 3 (paczka) | `00759007733512000625` | „…ma S1 = 3; przesyłka polecona ma 1 lub 4.” | Wklej numer w całości; cyfra kontrolna jest poprawna, błąd dotyczy tylko S1. |
| NEG-04 S1 = 2 (wartość zadeklarowana) | `00759007732512000628` | „…ma S1 = 2; przesyłka polecona ma 1 lub 4.” | Wklej numer w całości. |
| NEG-05 S1 = 0 | `00759007730512000624` | „…ma S1 = 0; przesyłka polecona ma 1 lub 4.” | Wklej numer w całości. |
| NEG-06 obcy prefiks GS1 | `00112345678901234560` | „20 cyfr, ale prefiks nie jest numerem SSCC Poczty Polskiej…” | Wklej 20 cyfr w całości. |
| NEG-07 IAC = 0 | `00059007731512000622` | jak NEG-06 (regex wymaga IAC 1-9) | Wklej numer w całości. |
| NEG-08 19 cyfr | `0075900773151200062` | „Nieznany format…” | Wklej D1 bez ostatniej cyfry. |
| NEG-09 21 cyfr | `007590077315120006210` | „Nieznany format…” | Wklej D1 z dopisanym zerem na końcu. |
| NEG-10 litery | `abc` | „Nieznany format…” | Wpisz `abc`. |
| NEG-11 wskaźnik spoza R? | `QR473124829PL` | „Nieznany format…” | Wklej numer w całości (pierwsza litera Q zamiast R). |
| NEG-12 kod kraju 1 litera | `RR473124829P` | „Nieznany format…” | Wklej I1 bez ostatniej litery. |
| NEG-13 7 cyfr serialu | `RR47312489PL` | „Nieznany format…” | Wklej numer w całości (12 znaków). |
| NEG-14 pusty | (puste pole) | „Sprawdź” nieaktywne; Swagger: 400 (`Required`) | Zostaw „Pierwszy numer” puste (lub kliknij „Wyczyść”); sprawdź, że „Sprawdź” jest nieaktywne. |
| NEG-15 tylko spacje | `   ` | „Numer jest pusty.” / 400 | Wpisz trzy spacje; jeśli UI przycina spacje, „Sprawdź” pozostaje nieaktywne, a przez Swagger przychodzi komunikat. |

## 6. Dane negatywne - tryb „ostatni numer”

Dla każdego wiersza: wklej pierwszy numer w „Pierwszy numer”, przełącz „Tryb” na `ostatni numer`, wklej drugi numer w „Ostatni numer”, kliknij „Sprawdź”. Oczekuj czerwonego komunikatu i nieaktywnego „Zasil”.

| ID | Pierwszy numer | Ostatni numer | Oczekiwany błąd | Jak wprowadzić |
|---|---|---|---|---|
| RNG-01 krajowy vs zagraniczny | `00759007731512000621` | `RR473124829PL` | „…należą do różnych pul (inny typ lub prefiks).” | Krajowy w „Pierwszy numer”, zagraniczny w „Ostatni numer”. |
| RNG-02 inny IAC | `00159007731100010009` | `00259007731100010105` | jak RNG-01 | Oba krajowe, różnią się trzecią cyfrą (IAC 1 i 2). |
| RNG-03 inne S1 (1 vs 4) | `00159007731100010009` | `00159007734100010109` | jak RNG-01 | Oba krajowe IAC 1, różnią się 11. cyfrą (S1 1 i 4). |
| RNG-04 inny wskaźnik (RR vs RB) | `RR600000007PL` | `RB600000109PL` | jak RNG-01 | Oba zagraniczne, różne dwie pierwsze litery. |
| RNG-05 inny kraj (PL vs DE) | `RR600000007PL` | `RR600000109DE` | jak RNG-01 | Oba zagraniczne RR, różne dwie ostatnie litery. |
| RNG-06 ostatni < pierwszy | `00559007731550000054` | `00559007731550000009` | „Zakres musi mieć co najmniej jeden numer.” | Wklej numery w odwrotnej kolejności niż rosnąca (serial 55000005 jako pierwszy, 55000000 jako ostatni). |
| RNG-07 ostatni ze złą cyfrą kontrolną | `00759007731512000621` | `00759007731512000639` | „Błędna cyfra kontrolna GS1: jest 9, powinna być 8.” | Pierwszy poprawny D1; w „Ostatni numer” wklej D1+1 z ostatnią cyfrą zmienioną z 8 na 9. |
| RNG-08 ostatni pusty | `00759007731512000621` | (puste pole) | „Sprawdź” nieaktywne | Wklej pierwszy numer, tryb `ostatni numer`, „Ostatni numer” zostaw puste; sprawdź, że „Sprawdź” jest nieaktywne. |

## 7. Ciała żądań do Swaggera / curl (`POST /api/registered-numbers/ranges/check` i `/ranges/import`)

W Swaggerze (`http://localhost:5080/swagger`) rozwiń endpoint, kliknij „Try it out”, wklej ciało do pola „Request body”, opcjonalnie dodaj nagłówek `X-Editor`, kliknij „Execute”. Najpierw `/ranges/check`, potem to samo ciało do `/ranges/import`.

```json
{ "firstNumber": "00159007731100010009", "count": 1000 }
```
```json
{ "firstNumber": "RR480000008PL", "lastNumber": "RR480000025PL" }
```
```json
{ "firstNumber": "00559007731550000009", "count": 5, "lastNumber": "00559007731550000047" }
```
Ostatnie → 400 „Podaj dokładnie jedno z: count albo lastNumber.” (tego przypadku nie da się wywołać z UI, bo formularz wysyła tylko jedno z pól).

```bash
curl -s -X POST http://localhost:5080/api/registered-numbers/ranges/check \
  -H "Content-Type: application/json" -H "X-Editor: tester" \
  -d '{"firstNumber":"00159007731100010009","count":1000}'
```

## 8. Szybkie policzenie własnego numeru

Wklej poniższy kod do konsoli przeglądarki (F12 → Console) albo uruchom `node`, potem wywołaj `dom(...)` lub `intl(...)` z własnymi parametrami; wynik wklej w „Pierwszy numer”.

```js
const gs1 = d => { let s=0; for (let i=0;i<d.length;i++) s += +d[d.length-1-i] * (i%2 ? 1 : 3); return (10 - s%10) % 10; };
const s10 = s => { const w=[8,6,4,2,3,5,9,7]; let t=0; for (let i=0;i<8;i++) t += +s[i]*w[i]; const c = 11 - t%11; return c===10?0:c===11?5:c; };
const dom  = (iac, s1, serial) => { const b = `${iac}5900773${s1}${String(serial).padStart(8,'0')}`; return `00${b}${gs1(b)}`; };
const intl = (serial, svc='RR', cc='PL') => { const s = String(serial).padStart(8,'0'); return `${svc}${s}${s10(s)}${cc}`; };
dom(7, 1, 51200062);   // 00759007731512000621
intl(47312482);        // RR473124829PL
```
