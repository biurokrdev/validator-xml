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

## Wariant „dane w pliku” – `file.html`

`file.html` to ta sama aplikacja (też jeden plik, bez bibliotek i bez sieci), ale **nie zapisuje niczego w przeglądarce**
(ani localStorage, ani IndexedDB). Cały stan – dni, zadania, typy, ustawienia i motyw – żyje w **pliku `.json` na dysku**,
który sam wskazujesz. Plik można kopiować, trzymać w folderze OneDrive i otwierać na innym komputerze.

- **Start:** okno „Plik danych” – „Otwórz…” (istniejący plik), „Utwórz…” (nowy) albo „Przenieś…” (nowy plik z danymi,
  które `index.html` zostawił w localStorage; to jedyne miejsce, gdzie localStorage jest **czytany**, nigdy zapisywany).
  Najszybciej: **przeciągnij plik `.json` z Eksploratora na okno „Plik danych”** – bez szukania folderu. Przeglądarka nie pozwala
  stronie wskazać folderu startowego ścieżką (nawet własnego), więc okno wyboru pliku otwiera się tam, gdzie sama je ostatnio
  zapamiętała; gdy jakiś plik danych jest już otwarty, kolejne okna („Otwórz…”, „Utwórz…”) startują w jego folderze.
  Bez wskazania pliku okna nie da się pominąć. Po odświeżeniu strony plik trzeba wskazać ponownie – przeglądarka nie
  pamięta go, bo aplikacja niczego w niej nie przechowuje.
- **Zapis:** automatyczny po każdej zmianie (File System Access API – Chrome / Edge), z krótką zwłoką sklejającą serie zmian
  (np. pisanie komentarza = jeden zapis). Zapis idzie przez plik tymczasowy podmieniany na końcu, więc przerwany zapis nie
  ucina danych. Stan widać na pasku u góry: zapisano (zielona kropka), niezapisane zmiany (żółta), błąd (czerwona).
- **Konflikt:** jeśli plik zmienił się na dysku poza tą kartą (druga karta, synchronizacja), zapis się wstrzymuje zamiast
  nadpisać cudze zmiany – w oknie pliku wybierasz „Zapisz” (nadpisz) albo „Wczytaj” (weź stan z dysku).
- **Tryb ręczny** (Firefox, Safari albo gdy przeglądarka zablokuje zapis): plik wczytujesz, a zmiany pobierasz przyciskiem
  „Pobierz plik”. Przy niezapisanych zmianach przeglądarka ostrzega przed zamknięciem karty.

`file.html` powstaje z `index.html` (ten sam kod + warstwa pliku danych), więc po zmianach w `index.html` trzeba ją wygenerować na nowo.

## Wygląd

Styl na wzór Azure DevOps (Fluent UI). Domyślnie motyw **ciemny** (paleta „Dark” z Azure DevOps),
przycisk słońce/księżyc w prawym górnym rogu przełącza na jasny; wybór jest zapamiętywany w przeglądarce.
Karty na osi czasu mają kolorowy lewy brzeg jak na Azure Boards: żółty = praca (Task),
fioletowy = scrum, morski = inne, szary = przerwa.

## Workflow

1. **Rozpocznij pracę** – licznik dnia startuje od `00:00:00`. Od tej chwili czas bez zadania
   liczy się jako **przerwa** (osobna karteczka na osi czasu z godziną początku i końca).
2. **Zadanie** – wpisz ID Azure lub tytuł (jedno z dwóch wymagane), typ i estymatę jako czas (`8:30` lub `8.5`), kliknij „▶ Rozpocznij”: wpis trafia na listę
   i od razu liczy czas (zamyka bieżącą przerwę). Jeśli dzień nie był rozpoczęty, startuje sam.
   - typ kategorii **praca** (np. „zadanie”, „utrzymanie środowisk”, „IT Risk”, „organizacyjne”) – schodzi z obliga pracy (domyślnie 6 h = 8 h − 2 h przerwy),
   - typ kategorii **scrum** (np. „scrum” – daily, planowanie, refinement; dawniej kategoria nazywała się „organizacyjne”) – schodzi z puli przerwy (2 h),
   - typ kategorii **inne** (np. „inne” – lekarz, sprawy prywatne, urzędowe) – też schodzi z puli przerwy, ale na wykresach i w podsumowaniu
     jest pokazywany osobno od przerw,
   - typ kategorii **przerwa** (własny typ, np. „lunch”) – nazwana przerwa: liczy się dokładnie jak zwykła przerwa, ale ma własną kartę i tytuł.
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

Każda karta (zadanie i przerwa) ma pole **komentarza**; komentarze trafiają do podsumowania. Pole jest wielolinijkowe i rośnie
razem z tekstem – długi komentarz zawija się, zamiast chować za krawędzią. Enter zatwierdza, Shift+Enter wstawia nową linię.

**Godzina rozpoczęcia / zatrzymania / wznowienia pracy** – ołówek przy zdarzeniu na osi czasu otwiera okno z godziną. Razem ze
zdarzeniem przesuwa się krawędź wpisu, który wtedy ruszył albo został zamknięty (np. „Rozpoczęto pracę” 10:00 → 7:00 wydłuża
pierwszy wpis do 7:00 i koryguje licznik dnia). Zdarzenia muszą zachować kolejność i nie mogą być w przyszłości.

**Przerwa → zadanie** – przycisk „Przypisz zadanie” na karcie przerwy (także trwającej). Okno otwiera się puste: podajesz zadanie – wpisując je albo
klikając **lupę przy polu Task (Azure)**, która otwiera wyszukiwarkę po liście zadań (bez zamkniętych, ostatnio używane na górze;
klik albo Enter wypełnia Task, tytuł, typ i estymatę) – i godzinę, od której trwa: zostawiona bez zmian zamienia całą przerwę, późniejsza dzieli wpis – do niej przerwa,
od niej zadanie (trwająca przerwa liczy dalej już jako zadanie). Jeśli takie zadanie było już dziś, dochodzi do niego kolejny wpis.

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

## Lista zadań (panel wysuwany)

Przycisk ☰ w lewym górnym rogu wysuwa z lewej **listę zadań** – rejestr wszystkich zadań, nad którymi pracujesz, żeby nie
wpisywać ich każdego dnia od nowa. Trafiają tam same: każde zadanie rozpoczęte w aplikacji (z Task (Azure) albo z samym
tytułem), także te z dni sprzed wprowadzenia listy. Zadania łączą się po ID Azure, a bez ID – po tytule.

- **Wyszukiwanie** po ID lub tytule, potem **▶ Wznów** – zadanie trafia na dzisiejszą listę i od razu liczy czas
  (z tym samym typem i estymatą). Zadanie, które trwa, jest na górze listy i ma „trwa…” zamiast przycisku.
- **Stan** każdej karteczki: *New* (jeszcze nierozpoczęte), *Active* (w pracy), *Closed* (zakończone). Stan zmienia się
  na karcie w panelu albo na karcie zadania na głównej liście dnia. **Closed chowa zadanie z panelu**; wpisy i czas zostają,
  a opcja „pokaż też zamknięte” pozwala je zobaczyć i przywrócić. Rozpoczęcie pracy nad zadaniem zawsze ustawia je na Active.
- **Edycja z listy:** ołówek na karteczce otwiera okno z polami Task (Azure), tytuł, typ, estymata i stan. Zmiana obejmuje
  wpisy tego zadania ze wszystkich dni (np. nadanie ID zadaniu, które miało sam tytuł). Zadanie bez żadnego czasu można usunąć (kosz).
- **Import z Azure (CSV):** przycisk w panelu wczytuje plik wyeksportowany z Azure Boards (*Queries → Export to CSV*). Do importu
  trafiają tylko elementy typu Task (inne typy są pomijane i policzone). Najpierw otwiera się **okno pośrednie** z listą zadań z pliku:
  przy każdym można poprawić Task (Azure), tytuł, estymatę i stan oraz wybrać typ – osobno albo „Typ dla wszystkich”. Na listę
  trafiają tylko zaznaczone wiersze. Zadania, które już są na liście, oraz zamknięte w Azure są domyślnie odznaczone
  (zaznaczenie istniejącego aktualizuje jego dane). Rozpoznawane kolumny: ID, Work Item Type, Title (także Title 1, Title 2…
  z zapytań drzewiastych), State, Original Estimate; separator przecinek, średnik albo tabulator.
- Karteczka pokazuje typ, czas łączny ze wszystkich dni (i estymatę) oraz datę ostatniej pracy. Esc albo × zamyka panel.

Rejestr jest częścią danych aplikacji (kopia JSON, plik danych w `file.html`).

## Typy zadań

Listę typów definiuje się w **Ustawieniach → Typy zadań** (prosta lista z ikonami edycji i usuwania, pod nią pole
„nazwa + kategoria + Dodaj”). Każdy typ wskazuje jedną z czterech **kategorii bazowych** (grup czasu) – *praca*, *scrum*, *inne*
albo *przerwa* – i to od niej zależy liczenie czasu (obligo, pula przerwy, nadgodziny, wykres, podsumowanie).
Domyślne typy, w tej kolejności: **zadanie** (praca), **scrum** (grupa scrum), **inne** (grupa inne), **utrzymanie środowisk**, **IT Risk** i **organizacyjne**
(wszystkie trzy liczone jako praca). Dane ze starszą listą typów są aktualizowane jednorazowo przy wczytaniu: „praca” → „zadanie”, „wsparcie produkcji” /
„utrzymanie środ.” → „utrzymanie środowisk”, „organizacyjne” zaczyna liczyć się jako praca, dochodzą IT Risk i scrum (tuż pod „zadanie”; „organizacyjne” ląduje na końcu listy), a typy
„lunch” i „daily” znikają z listy (ich dotychczasowe wpisy dalej liczą się tak jak wcześniej – jako przerwa / scrum). Grupa czasu „organizacyjne” została
przemianowana na **„scrum”**, a typ „scrum” przeniesiony do niej z grupy „przerwa” – obie grupy schodzą z puli przerwy, więc
sumy dni się nie zmieniają, zmienia się tylko podział (kolor na wykresie, sekcja „Scrum” w podsumowaniu, kolumna w CSV).
Typy dodane lub przemianowane ręcznie zostają bez zmian.

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

Kafelki **Nadgodziny** i **Czas do odpracowania** pokazują się tylko wtedy, gdy mają wartość większą od 0:00 (samotny zajmuje
całą szerokość); przy 0:00 są ukryte. Obligo i limity we wszystkich kafelkach pochodzą z Ustawień (dzień pracy − maks. przerwa).

- **Zadania (praca)** – suma zadań typu praca vs obligo (dzień − przerwa).
- **Inne + scrum** – wspólna pula z limitem (domyślnie 2:00): zadania grup *inne* i *scrum* oraz przerwy (automatyczne i nazwane). Rozbicie na scrum / inne / przerwę jest w „Podsumowaniu dnia”.
- **Łącznie** – cały naliczony czas dnia vs 8 h; „do końca” = ile zostało do 8 h.
- **Nadgodziny** – wyłącznie praca wykonana po zaliczeniu dnia. **Dzień jest zaliczony w chwili, gdy praca nad zadaniami
  osiągnie obligo 6 h** – niezależnie od tego, ile trwały przerwy (1:30 przerwy = wychodzisz o pół godziny wcześniej, 2:30 =
  dzień jest dłuższy; ani jedno, ani drugie nie jest „do odpracowania”). Przerwa po zaliczeniu dnia nie jest nadgodziną.
- **Czas do odpracowania** – tylko dla zamkniętego dnia: brakująca praca do obliga 6 h. W trakcie dnia kafelek pokazuje 0:00,
  a to, ile pracy jeszcze zostało, jest w linii nad licznikiem. Limit przerwy (2 h) służy tylko do kolorowania kafelka „Inne + scrum”.

Linia nad licznikiem: przed zaliczeniem dnia „praca 4:30 / 6:00 · do obliga 1:30”, po zaliczeniu „od 16:45 nadgodziny (1 h 20 min)”.
Licznik dnia nadal pokazuje całą obecność (praca + pula), a „Łącznie x / 8:00” w podsumowaniu jest tylko informacją.

Limity (dzień pracy `8:00` / przerwa `2:00`, wpisywane jako czas), typy zadań, kopia zapasowa (eksport/import JSON)
i czyszczenie dnia są w oknie **Ustawienia** (zębatka w górnym pasku).

## Podsumowanie miesiąca

Panel pod „Podsumowaniem dnia”: na górze statystyka miesiąca, pod nią – po kliknięciu **Rozwiń szczegóły** – lista
wszystkich dni (chowa ją **Ukryj szczegóły**, dostępne nad i pod listą; domyślnie lista jest zwinięta). (◀ ▶ zmieniają miesiąc, „Bieżący” wraca; panel sam
przechodzi na miesiąc dnia wybranego w podsumowaniu dnia). Soboty i niedziele są wyróżnione tłem i czerwonym skrótem dnia,
dzisiejszy dzień ma niebieski znacznik. Kliknięcie wiersza pokazuje ten dzień w „Podsumowaniu dnia” i na wykresach.

- **Dzień roboczy z pracą** – trzy wartości: praca nad zadaniami, pula **Inne + scrum** (scrum + inne + przerwa, jak na kafelku;
  żółta po przekroczeniu limitu, rozbicie w dymku po najechaniu) i czas do odpracowania (żółty). Gdy nie ma nic do odpracowania, a są nadgodziny, trzecia kolumna pokazuje je jako `+0:30`.
- **Dzień wolny** oznacza się ręcznie, wybierając **rodzaj: urlop, święto, szkolenie albo L4**. Menu z rodzajami otwiera
  przycisk **Dzień wolny ▾** w panelu „Dzień pracy” (dla dzisiaj, dopóki praca nie jest rozpoczęta) albo kółko **W** na końcu
  wiersza dowolnego dnia roboczego na liście miesiąca – także wstecz, gdy się zapomniało, i z wyprzedzeniem. Ponowne kliknięcie
  pozwala zmienić rodzaj albo wybrać „Cofnij dzień wolny”. Wiersz pokazuje rodzaj na niebiesko, a kółko jego skrót (U, Ś, Sz, L4);
  statystyka miesiąca podaje liczbę dni wolnych z podziałem na rodzaje, a kolumna „Dzień wolny” w CSV „Dni” – rodzaj.
- **Dzień roboczy bez wpisów i bez oznaczenia** – żółte „brak wpisów” (sygnał, że trzeba go oznaczyć jako wolny albo uzupełnić);
  dzisiejszy: „nie rozpoczęto”, przyszłe: „—”.
- **Sobota / niedziela albo dzień wolny z pracą** – zamiast trzech wartości: „nadgodziny H:MM” (cała praca tego dnia)
  i drobnym drukiem pula „inne + scrum”. W dzień oznaczony jako wolny nie ma obliga ani czasu do odpracowania – także na kafelkach
  i w podsumowaniu dnia cała praca liczy się jako nadgodziny.

Statystyka miesiąca (nad listą): dni robocze (pomniejszone o oznaczone dni wolne), dni wolne, dni przepracowane (osobno
te w weekend / dzień wolny), dni robocze bez wpisów, praca nad zadaniami łącznie, **praca w dni robocze względem obliga**
(bez pracy weekendowej – ta jest w nadgodzinach), pula **Inne + scrum** względem limitu (limit dzienny × dni pracy) z rozbiciem
na scrum / inne / przerwę, średnia pracy i puli na dzień, suma do odpracowania, nadgodziny (w tym weekendy i dni wolne)
oraz bilans = nadgodziny − do odpracowania. Święta nie są rozpoznawane automatycznie – oznacz je jako dzień wolny. Arkusz CSV „Dni” ma kolumnę „Dzień wolny”
i zawiera także dni wolne bez żadnych wpisów.

## Aktywność (wykres)

Panel **Aktywność** pod „Dniem pracy” to wykres kolumnowy **całej doby (0:00–24:00)**: każda kolumna to jedna godzina,
a jej wysokość to liczba minut (0–60) z podziałem na pracę, scrum, inne i przerwę (cztery kolory – „inne” jest oddzielone od przerw). Pokazuje dzień wybrany
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

Przycisk **Eksport** w nagłówku panelu „Podsumowanie dnia” otwiera okno eksportu: wybór zakresu i przyciski
**Pobierz** – arkusze CSV, plik do importu w Azure Boards oraz kopia zapasowa JSON. (W ustawieniach została tylko kopia JSON z importem.)

Bez bibliotek – plik jest składany w JS i pobierany przez przeglądarkę. Separator `;`, kodowanie UTF-8 z BOM
(otwiera się poprawnie w polskim Excelu). Czasy trwania są **co do sekundy** (`h:mm:ss`) – Excel traktuje je jak czas,
więc dają się sumować, a suma zadań zgadza się z sumą dnia (w interfejsie czasy są zaokrąglane do minut).
Estymata zostaje jako `h:mm`.

**Zakres** (lista na górze okna): wszystkie dni, miesiąc (miesiąc dnia wybranego w „Podsumowaniu dnia”) albo
**konkretny dzień** – po jego wybraniu pojawia się pole daty (domyślnie dzień z podsumowania, można wskazać dowolny).
Zakres trafia do nazwy pliku, np. `liczmiczas-wpisy-2026-09.csv` albo `liczmiczas-dni-2026-09-19.csv`.

- **Wpisy** – każdy wpis osobno: Data; Lp.; Typ (nazwa); Grupa czasu (praca / scrum / inne / przerwa); ID Azure; Tytuł;
  Start; Koniec (z sekundami); Czas; W toku; Komentarz. Najlepszy do pokazania przerwań i skakania między zadaniami.
- **Zadania** – jeden wiersz na zadanie **zsumowane za wybrany zakres** (dzień / miesiąc / wszystko; zadania łączone po ID Azure,
  bez ID – po tytule): Typ; Grupa czasu; ID Azure; Tytuł; Czas w zakresie (h:mm:ss i dziesiętnie do Azure); Czas łączny (ze
  wszystkich dni); Estymata; Ponad estymatę; Dni; Wpisów; Komentarze. Przerwy automatyczne to jeden wiersz za cały zakres.
- Trzy dawne arkusze są ukryte w oknie (kod został): **Zadania per dzień** – czysta tabela pod tabelę przestawną: Data; Typ; Grupa czasu; ID Azure; Tytuł; Czas dnia;
  Czas łączny (to samo ID we wszystkich dniach); Estymata; Ponad estymatę; Wpisów (na ile kawałków pocięto zadanie);
  Komentarze. Przerwy automatyczne to jeden wiersz na dzień; nazwane (np. lunch) mają własne wiersze.
- **Dni** – jeden wiersz na dzień, liczony tak jak w „Podsumowaniu miesiąca”: Data; Dzień tyg.; Rodzaj dnia (roboczy / weekend /
  urlop / święto / szkolenie / L4); Start; Koniec; Łącznie; Praca; Scrum; Inne; Przerwa; pula „Inne + scrum”; Limit puli;
  Pula ponad limit; Nadgodziny; Do odpracowania; Zadań; Wpisów; Zatrzymań pracy. Praca w weekend albo dzień wolny to w całości
  nadgodziny (bez limitu puli i bez „do odpracowania”); dni wolne bez wpisów też mają swój wiersz.
- **Miesiące** – jeden wiersz na miesiąc objęty zakresem, z liczbami z „Podsumowania miesiąca”: dni robocze / wolne / przepracowane /
  bez wpisów, praca łącznie i w dni robocze względem obliga, scrum, inne, przerwa, pula z limitem, do odpracowania, nadgodziny
  (w tym weekendy i dni wolne) oraz bilans (osobno znak „na plus / na minus” i wartość, bo Excel nie lubi ujemnego czasu).

Komórki są cytowane wg RFC 4180 (średniki, cudzysłowy i entery w tytułach / komentarzach nie psują kolumn), a tekst
zaczynający się od `=`, `+`, `-` lub `@` dostaje na początku apostrof, żeby Excel nie potraktował go jak formuły.

## Import do Azure Boards

W oknie „Eksport” jest przycisk **Azure Boards – import (CSV)**. Plik jest w formacie, którego oczekuje *Boards → Queries →
Import work items* (przecinek, UTF-8, kropka dziesiętna) i zawiera **tylko zadania z ID Azure**, jeden wiersz na ID, jako
`Work Item Type = Task` (User Story i wyżej uzupełniasz ręcznie – nie trafiają do pliku, bo w aplikacji zapisujesz tylko taski).
Kolumny to nazwy pól Azure: `ID`, `Work Item Type`, `Title`, `Completed Work` (godziny dziesiętnie, np. 5.25), a gdy zadanie
ma estymatę – także `Original Estimate` i `Remaining Work` (= estymata − cały czas). Nic poza tym, bo importer odrzuca nieznane
kolumny. Zakres eksportu decyduje, **które** zadania trafią do pliku (te, nad którymi pracowałeś w zakresie).

Dwie opcje w oknie:
- **Completed Work = czas ze wszystkich dni** (domyślnie) – Azure *zastępuje* wartość pola, nie dodaje, więc do pola trafia
  suma ze wszystkich dni, także spoza zakresu. Odznaczone: tylko czas z zakresu (gdy wolisz dopisywać przyrosty ręcznie).
- **Dołącz kolumnę Title** (domyślnie) – Azure nadpisze tytuł zadania tym z aplikacji; odznacz, jeśli wpisujesz skrócone tytuły.

Import w Azure najpierw pokazuje wiersze do przejrzenia i dopiero „Save items” je zapisuje – tam widać, co się zmieni.
Pola Completed / Remaining / Original Estimate istnieją w procesach Agile, Scrum i CMMI; w procesie Basic ich nie ma.

## Podsumowanie dnia

Zadania są łączone między dniami **wyłącznie po ID Azure** (czas „łącznie”, porównanie z estymatą).
Zadanie bez ID jest osobnym zadaniem każdego dnia, nawet gdy tytuł się powtarza.

Lista sum pokazuje osobno: Łącznie, Zadania, Scrum, Inne, Przerwa oraz pulę „Inne + scrum + przerwa” z limitem;
zadania grup scrum i inne mają własne sekcje pod listą zadań. Nawigacja po dniach, przycisk odświeżania, opcje: czas z dnia / łączny (to samo ID w innych dniach),
ID zadania, godziny wpisów. Podsumowanie ma widok sformatowany (lista sum, lista zadań, komentarze,
zdarzenia dnia) oraz rozwijaną **wersję tekstową** do skopiowania:

```
Dzień: 11.09.2026
Praca: 06:50 – 15:10 · łącznie 8:20 / 8:00 · nadgodziny 0:20
Zadania: 6:15 / 6:00 · inne + scrum: 2:05 / 2:00 (scrum 0:15, inne 0:00, przerwa 1:50)
Nadgodziny: 0:20 (praca od 14:50)
Zdarzenia: rozpoczęto 06:50, zakończono 12:00, wznowiono 12:30

Lista zadań:
1 - #12345 Text text text [2:30 / 10:00] → 2h 30m · Azure: 2.5
2 - Brak ID Import parametryzacji – walidator XML [3:45 / 4:00] – czekam na dostęp do bazy → 3h 45m · Azure: 3.75

Scrum:
1 - #777 Daily [0:15] → 15m · Azure: 0.25

Przerwy: 1:50 – kawa
```

Przy każdym zadaniu jest też wartość **„Azure”** – ten sam czas jako liczba godzin dziesiętnie (`0:30` → `0.5`, `1:45` → `1.75`,
`0:20` → `0.33`), gotowa do wpisania w polu Completed / Remaining Work w Azure Boards: w widoku sformatowanym pod czasem
zadania (niebieska), w wersji tekstowej po `· Azure:`. Nie zmienia to niczego w liczeniu – to tylko inny zapis tej samej sumy.

Na końcu każdego wpisu zadania (po `→`) jest dodatkowo czas **zaokrąglony w górę do pełnego kwadransa**
(`10:00` → `10h`, `10:05` → `10h 15m`, `10:16` → `10h 30m`, `10:49` → `11h`). Dotyczy tego samego czasu,
co w nawiasie (z dnia, a w trybie „czas łączny” – łącznego).

Skrót `Ctrl+Shift+S` zatrzymuje bieżące zadanie. Eksport / import danych do JSON w ustawieniach.
