# LiczMiCzas

Timer dnia pracy i zadań z Azure Boards – czysty HTML + CSS + JS w **jednym pliku** `index.html`, bez zależności i bez backendu.
Otwórz `index.html` w przeglądarce. Dane są zapisywane w `localStorage`.

## Prywatność – praca wyłącznie lokalna

Aplikacja niczego nie pobiera z internetu i niczego nie wysyła: brak zewnętrznych skryptów, stylów, fontów,
obrazów, analityki i wywołań sieciowych. Jest to wymuszone nagłówkiem **Content-Security-Policy** w `index.html`
(`default-src 'none'`, `connect-src 'none'`, `form-action 'none'`) – przeglądarka zablokuje każde połączenie,
nawet gdyby ktoś je dopisał w kodzie. Dane opuszczają przeglądarkę tylko na Twoje żądanie: eksport JSON/CSV
(plik zapisywany lokalnie) oraz kliknięcie linku `#ID`, który otwiera Azure DevOps w nowej karcie
(tylko gdy adres projektu jest ustawiony w ustawieniach).

## Wygląd

Styl na wzór Azure DevOps (Fluent UI). Domyślnie motyw **ciemny** (paleta „Dark” z Azure DevOps),
przycisk słońce/księżyc w prawym górnym rogu przełącza na jasny; wybór jest zapamiętywany w przeglądarce.
Karty na osi czasu mają kolorowy lewy brzeg jak na Azure Boards: żółty = praca (Task),
fioletowy = organizacyjne, szary = przerwa.

## Workflow

1. **Rozpocznij pracę** – licznik dnia startuje od `00:00:00`. Od tej chwili czas bez zadania
   liczy się jako **przerwa** (osobna karteczka na osi czasu z godziną początku i końca).
2. **Zadanie** – wpisz ID Azure lub tytuł (jedno z dwóch wymagane), typ i estymatę jako czas (`8:30` lub `8.5`), kliknij „▶ Rozpocznij”: wpis trafia na listę
   i od razu liczy czas (zamyka bieżącą przerwę). Jeśli dzień nie był rozpoczęty, startuje sam.
   - typ kategorii **praca** (np. „praca”, „wsparcie produkcji”) – schodzi z obliga pracy (domyślnie 6 h = 8 h − 2 h przerwy),
   - typ kategorii **organizacyjne** (np. „daily”) – schodzi z puli przerwy (2 h),
   - typ kategorii **przerwa** (np. „lunch”) – nazwana przerwa: liczy się dokładnie jak zwykła przerwa, ale ma własną kartę i tytuł.
3. **Zatrzymaj** na karcie zadania – zamyka wpis, czas leci dalej jako przerwa.
4. **Wznów** – dodaje **nową kartę** tego samego zadania (nie dubluje danych zadania), dzięki czemu
   widać, że w ciągu dnia zadania były robione naprzemiennie; podsumowanie sumuje czas per zadanie.
   Na dole panelu „Dzień pracy” (tylko gdy dzień pracy trwa) jest jeden przycisk, który zmienia się zależnie od stanu:
   w trakcie zadania pomarańczowy **⏸ Przerwa** – zamyka bieżące zadanie i startuje kartę przerwy; w trakcie przerwy
   zielony **▶ Wznów ostatnie zadanie** – zamyka przerwę i dodaje nową kartę **ostatniego zadania** (niebędącego przerwą),
   do którego dalej dolicza się czas (dymek podpowiada, które to zadanie). „Wznów” na karcie wznawia konkretnie to zadanie,
   a „Wznów pracę” – zatrzymany dzień.
5. **Zakończ pracę** – zatrzymuje wszystko (czas do „Wznów pracę” nie jest liczony wcale).
6. **Wznów pracę** – kontynuuje liczenie dnia (jako przerwa, dopóki nie wystartujesz zadania).

Każde rozpoczęcie, zakończenie i wznowienie pracy jest zapisywane jako znacznik z godziną na osi czasu
i w podsumowaniu (linia „Zdarzenia”). Zadanie bez ID ma etykietę **Brak ID Azure**.

**Estymata z godzin „od – do”:** ikona obok pola „Estymata (czas)” (nowe zadanie i edycja wpisu) otwiera okienko, w którym
podajesz godzinę od i do (wpisując albo z siatki zegara), a aplikacja sama wylicza czas – `8:00`–`16:30` to `8:30`,
`22:00`–`02:00` to `4:00` (przez północ). Są tam też gotowe wartości (0:30 … 8:00). Estymatę nadal można wpisać ręcznie.

Każda karta (zadanie i przerwa) ma pole **komentarza**; komentarze trafiają do podsumowania.
Ikona ✎ pozwala poprawić godziny wpisu, ID/tytuł/typ/estymatę zadania i komentarz. Godzinę wpisuje się z klawiatury
(`930`, `9:30`, `9.30`, samo `9` = 09:00) albo wybiera z **siatki** pod ikoną zegara: 24 godziny i 60 minut widoczne naraz,
bez przewijania (klik w godzinę, potem w minutę). Strzałki ↑ ↓ zmieniają o minutę, z Shift o 5. Godziny edytuje się z dokładnością
do minuty; godzina, której nie zmienisz, zachowuje oryginalne sekundy (edycja samego komentarza nie przesuwa czasu).
O północy otwarty wpis zamyka się o 23:59:59 – nowy dzień trzeba rozpocząć ponownie.

**Wpisy nie mogą nakładać się w czasie** – inaczej te same minuty liczą się podwójnie (dzień i kafelki są zawyżone,
a godzina na wykresie miałaby ponad 60 min). Gdy przy edycji nowe godziny nachodzą na sąsiedni wpis, aplikacja pyta,
czy go przyciąć (np. cofasz start zadania o 10 min → poprzednia przerwa kończy się 10 min wcześniej). Jeśli inny wpis
znalazłby się w całości w środku edytowanego, zmiana jest blokowana. Nakładki, które już są w danych dzisiejszego dnia,
pokazuje żółty pasek nad listą z przyciskiem **Napraw** (wcześniejszy wpis kończy się tam, gdzie zaczyna się następny);
na wykresie aktywności taka godzina jest przeskalowana do 60 min i opisana pod wykresem.

## Typy zadań

Listę typów definiuje się w **Ustawieniach → Typy zadań** (prosta lista z ikonami edycji i usuwania, pod nią pole
„nazwa + kategoria + Dodaj”). Każdy typ wskazuje jedną z trzech **kategorii bazowych** – *praca*, *organizacyjne*
albo *przerwa* – i to od niej zależy liczenie czasu (obligo, pula przerwy, nadgodziny, wykres, podsumowanie).
Domyślne typy: praca, organizacyjne, wsparcie produkcji (praca), daily (organizacyjne), lunch (przerwa).

- Zmiana nazwy lub kategorii typu działa na wszystkie jego zadania, także z poprzednich dni.
- Usunięcie typu nie rusza czasu: zadania zostają w jego kategorii bazowej (etykieta wraca do nazwy kategorii).
  Musi zostać co najmniej jeden typ.
- Zadanie typu „przerwa” zachowuje się jak przerwa także dla przycisku **Przerwa / Wznów ostatnie zadanie** – wznowienie wraca do
  ostatniego zadania, które przerwą nie jest.
- W podsumowaniu nazwane przerwy mają sekcję „Przerwy nazwane”; wiersz „Przerwy” to suma wszystkich przerw.
- Dane sprzed wprowadzenia typów (zadania „praca” / „organizacyjne”) działają bez migracji.

## Format czasu

Wszędzie – w interfejsie i w wersji tekstowej podsumowania – czas jest w formacie **godziny:minuty**, np. `1:32`.
Dotyczy to także limitów w ustawieniach; eksport CSV podaje czasy dokładniej, jako `h:mm:ss` (żeby sumy w Excelu się zgadzały). Pola czasu (estymata, limity dnia) przyjmują `8:30`;
dla wygody wpisane `8.5` lub `8,5` jest od razu zamieniane na `8:30`.

## Kafelki

- **Zadania (praca)** – suma zadań typu praca vs obligo (dzień − przerwa).
- **Przerwa + organizacyjne** – przerwy automatyczne + zadania organizacyjne vs limit przerwy.
- **Łącznie** – cały naliczony czas dnia vs 8 h; „do końca” = ile zostało do 8 h.
- **Nadgodziny** – wyłącznie praca wykonana po zaliczeniu dnia. Dzień jest zaliczony, gdy obecność
  osiągnie 8 h **i** praca nad zadaniami osiągnie obligo 6 h. Przerwa po zaliczeniu dnia nie jest nadgodziną.
- **Czas do odpracowania** – przed 8 h obecności: przerwa ponad limit 2 h (o tyle wydłuży się dzień);
  po 8 h obecności: brakująca praca do obliga 6 h. Przerwa nie zmniejsza tego czasu – tylko faktyczna praca.

Linia nad licznikiem: przed limitem „z 8:00 · do końca 3:30”, po 8 h obecności z brakującą pracą
„Odpracowujesz 45 min przerwy · pozostało 30 min pracy”, po zaliczeniu dnia „od 16:45 nadgodziny (1 h 20 min)”.

Limity (dzień pracy `8:00` / przerwa `2:00`, wpisywane jako czas), adres projektu Azure DevOps (do linków `#ID`), typy zadań, kopia zapasowa (eksport/import JSON)
i czyszczenie dnia są w oknie **Ustawienia** (zębatka w górnym pasku).

## Podsumowanie miesiąca

Panel pod „Podsumowaniem dnia”: na górze statystyka miesiąca, pod nią – po kliknięciu **Rozwiń szczegóły** – lista
wszystkich dni (chowa ją **Ukryj szczegóły**, dostępne nad i pod listą; domyślnie lista jest zwinięta). (◀ ▶ zmieniają miesiąc, „Bieżący” wraca; panel sam
przechodzi na miesiąc dnia wybranego w podsumowaniu dnia). Soboty i niedziele są wyróżnione tłem i czerwonym skrótem dnia,
dzisiejszy dzień ma niebieski znacznik. Kliknięcie wiersza pokazuje ten dzień w „Podsumowaniu dnia” i na wykresach.

- **Dzień roboczy z pracą** – trzy wartości: praca nad zadaniami, przerwa łącznie (przerwy + organizacyjne, jak na kafelku)
  i czas do odpracowania (żółty). Gdy nie ma nic do odpracowania, a są nadgodziny, trzecia kolumna pokazuje je jako `+0:30`.
- **Dzień wolny** oznacza się ręcznie, wybierając **rodzaj: urlop, święto, szkolenie albo L4**. Menu z rodzajami otwiera
  przycisk **Dzień wolny ▾** w panelu „Dzień pracy” (dla dzisiaj, dopóki praca nie jest rozpoczęta) albo kółko **W** na końcu
  wiersza dowolnego dnia roboczego na liście miesiąca – także wstecz, gdy się zapomniało, i z wyprzedzeniem. Ponowne kliknięcie
  pozwala zmienić rodzaj albo wybrać „Cofnij dzień wolny”. Wiersz pokazuje rodzaj na niebiesko, a kółko jego skrót (U, Ś, Sz, L4);
  statystyka miesiąca podaje liczbę dni wolnych z podziałem na rodzaje, a kolumna „Dzień wolny” w CSV „Dni” – rodzaj.
- **Dzień roboczy bez wpisów i bez oznaczenia** – żółte „brak wpisów” (sygnał, że trzeba go oznaczyć jako wolny albo uzupełnić);
  dzisiejszy: „nie rozpoczęto”, przyszłe: „—”.
- **Sobota / niedziela albo dzień wolny z pracą** – zamiast trzech wartości: „nadgodziny H:MM” (cała praca tego dnia)
  i drobnym drukiem przerwy. W dzień oznaczony jako wolny nie ma obliga ani czasu do odpracowania – także na kafelkach
  i w podsumowaniu dnia cała praca liczy się jako nadgodziny.

Statystyka miesiąca (nad listą): dni robocze (pomniejszone o oznaczone dni wolne), dni wolne, dni przepracowane (osobno
te w weekend / dzień wolny), dni robocze bez wpisów, suma pracy względem obliga
przepracowanych dni, suma przerw, średnia pracy i przerwy na dzień, suma do odpracowania, nadgodziny (w tym weekendowe)
oraz bilans = nadgodziny − do odpracowania. Święta nie są rozpoznawane automatycznie – oznacz je jako dzień wolny. Arkusz CSV „Dni” ma kolumnę „Dzień wolny”
i zawiera także dni wolne bez żadnych wpisów.

## Aktywność (wykres)

Panel **Aktywność** pod „Dniem pracy” to wykres kolumnowy **całej doby (0:00–24:00)**: każda kolumna to jedna godzina,
a jej wysokość to liczba minut (0–60) z podziałem na pracę, organizacyjne i przerwę. Pokazuje dzień wybrany
w „Podsumowaniu dnia” (domyślnie dziś) i odświeża się na bieżąco. Najechanie lub fokus klawiaturą (Tab) na kolumnie
pokazuje dokładne czasy; te same liczby są w rozwijanej **Tabeli**. Wykres to czysty SVG generowany w JS – bez bibliotek.
Zakres godzin ustawiają stałe `ACT_FROM` / `ACT_TO` w `index.html`.

Przycisk **Wykres** w nagłówku panelu otwiera okno **Wykres dnia** – szczegółowy obraz tego samego dnia, pomyślany
do pokazania, jak bardzo dzień był poszatkowany. Oś X to godziny doby (00:00–23:00), oś Y to minuta danej godziny
(0:00 na dole, 60:00 u góry). **Każdy wpis jest osobnym fragmentem** w miejscu, w którym faktycznie trwał, więc
przerwania i skakanie między zadaniami widać jako pocięte kolumny. Najechanie na fragment pokazuje dymek: zadanie
(ID + tytuł) albo „Przerwa”, typ, godziny od–do z czasem trwania i komentarz. Kropki z kreską oznaczają zdarzenia dnia:
zielona – rozpoczęcie pracy, czerwona – zatrzymanie, niebieska – wznowienie (dymek podaje godzinę). W nagłówku okna jest
krótka statystyka: liczba wpisów, średni czas na wpis i liczba zatrzymań pracy. Lista „Dzisiaj” zostaje bez zmian.

## Eksport CSV

Przycisk **Eksport** w nagłówku panelu „Podsumowanie dnia” otwiera okno eksportu: wybór zakresu i cztery przyciski
**Pobierz** – trzy arkusze CSV oraz kopia zapasowa JSON. (W ustawieniach została tylko kopia JSON z importem.)

Bez bibliotek – plik jest składany w JS i pobierany przez przeglądarkę. Separator `;`, kodowanie UTF-8 z BOM
(otwiera się poprawnie w polskim Excelu). Czasy trwania są **co do sekundy** (`h:mm:ss`) – Excel traktuje je jak czas,
więc dają się sumować, a suma zadań zgadza się z sumą dnia (w interfejsie czasy są zaokrąglane do minut).
Estymata zostaje jako `h:mm`.

**Zakres** (lista na górze okna): wszystkie dni, miesiąc (miesiąc dnia wybranego w „Podsumowaniu dnia”) albo
**konkretny dzień** – po jego wybraniu pojawia się pole daty (domyślnie dzień z podsumowania, można wskazać dowolny).
Zakres trafia do nazwy pliku, np. `liczmiczas-wpisy-2026-09.csv` albo `liczmiczas-dni-2026-09-19.csv`.

- **Wpisy** – każdy wpis osobno: Data; Lp.; Typ (nazwa); Kategoria (praca / organizacyjne / przerwa); ID Azure; Tytuł;
  Start; Koniec (z sekundami); Czas; W toku; Komentarz. Najlepszy do pokazania przerwań i skakania między zadaniami.
- **Zadania per dzień** – czysta tabela pod tabelę przestawną: Data; Typ; Kategoria; ID Azure; Tytuł; Czas dnia;
  Czas łączny (to samo ID we wszystkich dniach); Estymata; Ponad estymatę; Wpisów (na ile kawałków pocięto zadanie);
  Komentarze. Przerwy automatyczne to jeden wiersz na dzień; nazwane (np. lunch) mają własne wiersze.
- **Dni** – jeden wiersz na dzień: Data; Dzień tyg.; Start; Koniec; Łącznie; Praca; Organizacyjne; Przerwy; Nadgodziny;
  Do odpracowania; Zadań; Wpisów; Zatrzymań pracy.

Komórki są cytowane wg RFC 4180 (średniki, cudzysłowy i entery w tytułach / komentarzach nie psują kolumn), a tekst
zaczynający się od `=`, `+`, `-` lub `@` dostaje na początku apostrof, żeby Excel nie potraktował go jak formuły.

## Podsumowanie dnia

Zadania są łączone między dniami **wyłącznie po ID Azure** (czas „łącznie”, porównanie z estymatą).
Zadanie bez ID jest osobnym zadaniem każdego dnia, nawet gdy tytuł się powtarza.

Nawigacja po dniach, przycisk odświeżania, opcje: czas z dnia / łączny (to samo ID w innych dniach),
ID zadania, godziny wpisów. Podsumowanie ma widok sformatowany (lista sum, lista zadań, komentarze,
zdarzenia dnia) oraz rozwijaną **wersję tekstową** do skopiowania:

```
Dzień: 11.09.2026
Praca: 06:50 – 15:10 · łącznie 8:20 / 8:00 · nadgodziny 0:20
Zadania: 6:15 / 6:00 · przerwa: 2:05 / 2:00 (w tym organizacyjne 0:15)
Nadgodziny: 0:20 (praca od 14:50)
Zdarzenia: rozpoczęto 06:50, zakończono 12:00, wznowiono 12:30

Lista zadań:
1 - #12345 Text text text [2:30 / 10:00] → 2h 30m
2 - Brak ID Import parametryzacji – walidator XML [3:45 / 4:00] – czekam na dostęp do bazy → 3h 45m

Organizacyjne:
1 - #777 Daily [0:15] → 15m

Przerwy: 1:50 – kawa
```

Na końcu każdego wpisu zadania (po `→`) jest dodatkowo czas **zaokrąglony w górę do pełnego kwadransa**
(`10:00` → `10h`, `10:05` → `10h 15m`, `10:16` → `10h 30m`, `10:49` → `11h`). Dotyczy tego samego czasu,
co w nawiasie (z dnia, a w trybie „czas łączny” – łącznego).

Skrót `Ctrl+Shift+S` zatrzymuje bieżące zadanie. Eksport / import danych do JSON w ustawieniach.
