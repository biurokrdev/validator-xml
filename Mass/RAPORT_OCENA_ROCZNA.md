# Mass - podsumowanie implementacji do raportu rocznego

Dwie wersje tego samego podsumowania. Odpowiedź 1 to wersja faktograficzna (punkty, liczby, decyzje). Odpowiedź 2 to wersja narracyjna do wklejenia w raport.

---

# Odpowiedź 1 - wersja faktograficzna

## Co zostało zbudowane

**Problem biznesowy.** Aplikacje nadające listy polecone potrzebują numerów nadawczych R z rolek Elektronicznego Nadawcy Poczty Polskiej. Moduł Mass to centralna pula tych numerów: zasilanie przedziałami, pobieranie kolejnego wolnego numeru, śledzenie stanu (dostępny, zarezerwowany, użyty, anulowany) i walidacja.

**Zakres dostarczony (pełny pion, end-to-end):**
- **Schemat bazy** PostgreSQL 13+: schemat `mass`, jedna tabela z ograniczeniami CHECK, unikalnością pełnego numeru i indeksem pod pobieranie. Skrypt idempotentny, z osobnym rollbackiem.
- **API .NET 8** (ok. 1,4 tys. linii C#): 8 endpointów REST, Swagger, błędy w RFC 7807 ProblemDetails, mapowanie wyjątków domenowych na 400/404/409, CORS z konfiguracji, endpoint health, autor zmian z nagłówka X-Editor.
- **Domena** obsługująca dwa standardy numerów: krajowy GS1 SSCC (20 cyfr, cyfra kontrolna mod 10) i zagraniczny UPU S10 (13 znaków, ważony mod 11). Parser normalizuje zapis z nalepki (spacje, nawias, małe litery) i zwraca opisowy powód odrzucenia. Klasa zakresu generuje R-ki z pierwszego numeru i ilości lub z pierwszego i ostatniego. Maszyna stanów z jawnie zdefiniowanymi przejściami. Bezpieczniki: limit 100 000 numerów na import, tylko przesyłki polecone (S1 = 1 lub 4).
- **Front Angular 22** (ok. 1,5 tys. linii TS/HTML): lista ze statystykami puli, filtrami i stronicowaniem, formularz zasilenia z obowiązkową weryfikacją duplikatów przed importem, sprawdzanie pojedynczego numeru, akcje zmiany stanu, pobieranie kolejnego numeru. Signals, bez routera, selektory `data-testid` pod automaty.
- **Dokumentacja**: README z opisem API, tabeli, klas i przykładów użycia repozytorium; specyfikacja testów TESTS.md; zestaw danych testowych z policzonymi cyframi kontrolnymi.

## Decyzje techniczne warte podkreślenia

- **Bezpieczna współbieżność** pobierania numerów: na PostgreSQL `SELECT ... FOR UPDATE SKIP LOCKED`, więc wiele instancji aplikacji dostaje różne numery bez czekania. Dodatkowo blokada optymistyczna na kolumnie systemowej `xmin`, konflikt kończy się czytelnym 409.
- **Idempotentny import**: ponowne zasilenie tym samym przedziałem pomija istniejące numery zamiast rzucać błąd. UI wymusza „Sprawdź” przed „Zasil”.
- **Baza celowo prosta**, logika (cyfry kontrolne, składanie numeru, przejścia stanów) w aplikacji, co ułatwia testowanie i przenośność.
- **Provider bazy przełączany konfiguracją**: InMemory do developmentu i e2e bez PostgreSQL, Npgsql produkcyjnie. Ten sam kod, zero infrastruktury dla testów.
- **Punkt rozszerzenia** dla innych rodzajów przesyłek (S1 = 2, 3, 5...) wskazany w kodzie i README.

## Jakość i testy

| Element | Liczba |
|---|---|
| Testy e2e Playwright | 21 (3 pliki) |
| Scenariusze manualne w TESTS.md | ok. 50, z macierzą pokrycia e2e → manual |
| Testy jednostkowe Vitest | 3 (komponent główny) |
| Endpointy REST | 8 |

Testy e2e są niezależne od stanu bazy: każdy generuje losowy przedział, dane przygotowuje przez API, a stan backendu po akcji w UI potwierdza przez API. Brak API kończy się czytelnym komunikatem, nie timeoutem.

## Co nie zostało zrobione (uczciwie do raportu)

- **Brak uwierzytelniania.** Tożsamość edytora to nagłówek X-Editor, docelowo do podpięcia pod użytkownika.
- **Brak testów integracyjnych .NET** (NUnit z WebApplicationFactory i Testcontainers). Program.cs jest już na to przygotowany (`public partial class Program`).
- **Scenariusze wymagające PostgreSQL** (SKIP LOCKED, unikalność, xmin) opisane, ale tylko manualne.
- **Historia git** dla Mass to 2 commity z nieopisowymi komunikatami. Jeśli chapter lead patrzy w repo, warto to uprzedzić lub poprawić przy następnej pracy.

Kompetencje, które ta praca pokazuje: samodzielne dostarczenie całego pionu (SQL, backend, frontend, testy, dokumentacja), implementacja standardów branżowych GS1 i UPU z własnymi algorytmami kontrolnymi, świadome projektowanie pod współbieżność i idempotencję, projektowanie testów z myślą o utrzymaniu.

---

# Odpowiedź 2 - wersja narracyjna

W ramach projektu zaprojektowałem i zaimplementowałem od zera moduł Mass, czyli centralną pulę numerów nadawczych listów poleconych (tzw. R-ek) Poczty Polskiej. Do tej pory numery z rolek Elektronicznego Nadawcy były przypisywane ręcznie i nikt nie miał pewności, który numer został już użyty, a który wciąż czeka. Mass rozwiązuje ten problem systemowo: zasila pulę całą rolką na raz, wydaje aplikacjom kolejne wolne numery i pilnuje ich cyklu życia od momentu dostępności, przez rezerwację, aż po użycie lub anulowanie.

Sercem rozwiązania jest algorytm generowania numerów, który obsługuje dwa niezależne standardy pocztowe. Numery krajowe bazują na standardzie GS1 SSCC, czyli 20 cyfrach z prefiksem Poczty Polskiej i cyfrą kontrolną liczoną metodą mod 10 z wagami 3 i 1. Numery zagraniczne bazują na standardzie UPU S10, czyli 13 znakach z wagami 8, 6, 4, 2, 3, 5, 9, 7 i ważoną sumą kontrolną mod 11. Dzięki temu wystarczy, że użytkownik przepisze pierwszy numer z rolki i poda liczbę nalepek, a system sam wyliczy i zapisze wszystkie pozostałe numery z poprawnymi cyframi kontrolnymi. Parser jest przy tym odporny na to, jak numer wygląda na nalepce: spacje, nawias „(00)”, myślniki i małe litery są normalizowane automatycznie, a każdy odrzucony numer dostaje czytelne uzasadnienie, na przykład jaka cyfra kontrolna jest, a jaka powinna być.

Dużo uwagi poświęciłem bezpieczeństwu danych przy pracy równoległej, bo z puli będzie korzystać jednocześnie wiele instancji aplikacji nadawczych. Pobieranie kolejnego numeru na PostgreSQL wykorzystuje mechanizm `FOR UPDATE SKIP LOCKED`, więc równoległe żądania dostają różne numery zamiast czekać na siebie lub kolidować. Na wypadek konfliktu przy zmianie stanu dołożyłem blokadę optymistyczną opartą o systemową kolumnę `xmin`, co oznacza, że dwie osoby nie nadpiszą sobie nawzajem zmiany, a przegrany dostanie jasny komunikat zamiast cichej utraty danych. Import przedziału jest idempotentny: ponowne zasilenie tą samą rolką po prostu pomija numery, które już są w bazie, a interfejs wymusza sprawdzenie duplikatów przed faktycznym zasileniem.

Rozwiązanie dostarczyłem jako kompletny pion technologiczny. Po stronie bazy powstał idempotentny skrypt SQL ze schematem, ograniczeniami i indeksem pod pobieranie, plus skrypt wycofujący. Po stronie backendu jest API w .NET 8 z ośmioma endpointami, Swaggerem, błędami w standardzie RFC 7807 i konfigurowalnym CORS-em. Po stronie frontendu jest aplikacja Angular 22 z listą numerów, statystykami puli, filtrami, stronicowaniem, formularzem zasilania i sprawdzaniem pojedynczego numeru. Bazę danych da się przełączyć konfiguracją na tryb InMemory, więc całość uruchamia się i testuje bez PostgreSQL.

Testowanie potraktowałem jako część produktu, nie dodatek. Napisałem specyfikację około pięćdziesięciu scenariuszy manualnych z macierzą pokrycia oraz 21 testów end-to-end w Playwright, które są niezależne od stanu bazy, bo każdy generuje własny losowy przedział numerów i potwierdza wynik przez API. Do tego przygotowałem gotowy zestaw danych testowych z policzonymi cyframi kontrolnymi, obejmujący przypadki brzegowe, duplikaty i błędne dane, tak żeby tester mógł od razu siadać do formularza. Całość jest udokumentowana w README z opisem API, modelu danych i przykładami użycia repozytorium, z myślą o osobie, która przejmie moduł po mnie.

Świadomie zostawiłem otwarte dwa tematy, które są opisane w dokumentacji jako następne kroki: uwierzytelnianie (dziś tożsamość edytora idzie nagłówkiem, docelowo ma być brana z sesji użytkownika) oraz testy integracyjne .NET na prawdziwym PostgreSQL, pod które kod jest już przygotowany.

Z tego projektu wynoszę przede wszystkim doświadczenie w samodzielnym doprowadzeniu funkcjonalności od analizy standardów branżowych, przez projekt bazy i API, po interfejs, testy i dokumentację, oraz w projektowaniu pod współbieżność i odporność na błędy użytkownika.
