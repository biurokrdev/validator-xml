# Podsumowanie implementacji D2 ViewerEditor (materiał do oceny rocznej)

Stan repo: 2026-09-17. Dane z `.ai/`, historii git i kodu.

---

# Odpowiedź 1

## Skala i liczby (stan repo na 2026-09-17)

| Metryka | Wartość |
|---|---|
| Okres prac w repo | 2026-01-28 → 2026-09-17 (ok. 8 miesięcy) |
| Commity | 240 |
| Backend C# (produkcyjny / testy) | ~39 tys. / ~34 tys. linii |
| Konwertery DOCX↔HTML (własne) | ~16 tys. linii |
| Frontend TS (produkcyjny / testy / HTML+SCSS) | ~25 tys. / ~16 tys. / ~16 tys. linii |
| Sam edytor WYSIWYG | ~18,5 tys. linii TS |
| Testy backend (NUnit, Domain/Application/Infrastructure/Api) | 81 / 336 / 821 / 134 ≈ 1370, zielone |
| Testy frontend (Vitest) | 914 w 96 plikach, zielone |
| Decyzje architektoniczne (ADR) | 112 |
| Wpisy changelog (od 2026-05-23) | 277 |
| Zamknięte/udokumentowane ryzyka | ok. 50 pozycji |

## Co zostało zbudowane

**Produkt.** D2 ViewerEditor: webowy viewer/edytor DOCX i PDF z wersjonowanym storage i integracją z aplikacjami zewnętrznymi. Trzy aplikacje: internal API (.NET 8, Clean Architecture, MediatR, FluentValidation), external API dla integratorów, SPA Angular. Pełny cykl: ingest od aplikacji źródłowej, podgląd, edycja z auto-save, „Zakończ i wyślij" z asynchroniczną dostawą na returnUrl.

**Platforma i backend.**
- **Model wersji**: oryginał v1 nietykalny, kopia edytowalna v2 nadpisywana w miejscu. PostgreSQL (schemat w surowym SQL), Google Cloud Storage, Docker.
- **Kolejka dostaw bez brokera**: zadania w PostgreSQL, worker z `FOR UPDATE SKIP LOCKED`, lease, backoff z jitterem do 24 h, dead-letter, idempotencja, synchroniczna pierwsza próba. Panel administracyjny wysyłek z retry/anuluj/zmiana adresu.
- **Bezpieczeństwo**: Entra ID (MSAL + Microsoft.Identity.Web), role Operator/Administrator, autoryzacja resource-based, mapowanie grup na role, Microsoft Graph, sekrety z GCP Secret Manager. Centralne polityki uploadu (magic bytes, hardening ZIP, VBA) i walidator returnUrl (SSRF). Kontrola dostępu po kluczach korporacyjnych. Re-autoryzacja po wygaśnięciu sesji z bezpiecznikiem antypętlowym. Remediacja findingów Checkmarx (ADR-0104).
- **Observability**: strukturalne logi JSON dla GCP/ELK, correlationId, propozycja dashboardów Kibana.
- **Funkcje domenowe**: podpisy cyfrowe (RSA-SHA256 w Custom XML Part), kody kreskowe/QR, szablony, DOCX z hasłem, generowanie PDF za interfejsem.

**Własny silnik dokumentów (główny wysiłek roku).** Dwukierunkowa konwersja DOCX↔HTML na OpenXml SDK, bez zewnętrznego renderera (LibreOffice/Aspose odrzucone świadomie, ADR-0074). Zakres: style z dziedziczeniem, motywy, docDefaults, wiele sekcji z geometrią i nagłówkami/stopkami per sekcja (first/even), tabele (style warunkowe, obramowania pozycyjne, scalenia, wysokości, cellSpacing, tabele pływające, zagnieżdżone), listy wg specyfikacji numeracji, tabulatory pozycyjne, pola (PAGE, DATE, TOC), przypisy dolne i końcowe, obrazy (inline/anchor, crop, ramka), kształty DrawingML/VML/OLE z pass-through i edycją drag/resize, akceptacja śledzonych zmian, ochrona dokumentu, siatka docGrid.

**Konwertery legacy napisane od zera**: parser binarnego .doc (FIB, piece table, CHPX/SttbfFfn) oraz pure-managed tłumacz EMF/WMF/VML na SVG, oba bez zależności natywnych.

**Edytor WYSIWYG w Angularze.** Własna paginacja zgodna z Wordem: podział akapitów po liniach, dzielenie wierszy tabel między strony, keepNext/keepLines, cantSplit, powtarzane nagłówki tabel, regiony przypisów per strona, kolumny sekcji. Edycja nagłówka/stopki z wariantami, linijki, wyszukiwanie, panel tabel, własny pipeline schowka, undo/redo, malarz formatów, sticky formatting, znaki formatowania ¶, tryb read-only, obsługa klawiatury jak w Wordzie. Viewer PDF na pdf.js z watchdogiem etapów. Migracja Angular 20→21→22 z zachowaniem zone-based CD.

## Jak pracowano i co to mówi o poziomie

- **Zgodność z Wordem mierzona, nie zgadywana**: mapy stron per akapit z Word COM zestawiane z sondą CDP edytora, pomiar glifów w PDF. Wyniki na fixture: 12/12 stron zgodnych, mediana odchylenia pionowego 0,8 pt, 84/88 tabel zgodnych strukturalnie. Pomiary obaliły własne wcześniejsze założenia i doprowadziły do korekt (ADR-0107: odstępy to MAX, nie suma).
- **Audyty i raporty luk**: audyt ~60 problemów zgodności z Wordem, osobne raporty GAP dla układu tabel, paginacji tabel i numeracji list, macierz zgodności.
- **Inżynieria jakości**: harness regresji na golden DOCX ze snapshotami HTML, centralne jednostki OOXML, benchmarki BenchmarkDotNet, CLI diagnostyczne do inspekcji i porównania DOCX, weryfikacja runtime headless Chrome przez CDP bez Playwrighta.
- **Praca z wydajnością**: profilowanie gorącej ścieżki edytora, OnPush na komponentach per strona, świadome wycofanie zmiany po regresji wydajności (ADR-0075).
- **Bugi produkcyjne diagnozowane do root cause**, m.in. re-auth po wygaśnięciu sesji, undo z resetem karetki, paginacja tabel, dziedziczenie stylu nowych komórek, zawieszenie pdf.js przez konflikt `Promise.try` z zone.js.
- **Metodyka AI-assisted**: katalog `.ai/` (40 plików) jako pamięć projektu dla agentów, protokół startowy, ADR-y z korektami i wycofaniami, changelog. To pozwala kontynuować pracę bez odkrywania repo od zera.

Dwie uwagi do raportu. Git przypisuje commity czterem tożsamościom (krdev 220, bot Copilot 37, biurokrdev 15, rybkakrzy 8), więc warto to spiąć w jedno nazwisko. Nazw klienta nie ma w repo, w raporcie trzymaj się pseudonimu Doc2.

---

# Odpowiedź 2

## Kontekst i skala

Przez ostatnie osiem miesięcy powstał od podstaw D2 ViewerEditor, czyli webowy edytor i przeglądarka dokumentów DOCX oraz PDF, przeznaczony do pracy wewnątrz procesów biznesowych klienta (w repo pod pseudonimem Doc2). Aplikacja zewnętrzna przekazuje dokument, użytkownik ogląda go lub edytuje w przeglądarce, a po zakończeniu pracy system odsyła finalny plik z powrotem do aplikacji źródłowej. Oryginał dokumentu nigdy nie jest modyfikowany, a wszystkie zmiany trafiają na osobną, edytowalną kopię.

To nie był projekt integracyjny w stylu „spiąć gotowe komponenty". Najbardziej wymagająca część, czyli wierne odwzorowanie dokumentu Word w przeglądarce i zapisanie go z powrotem bez utraty treści, została zbudowana własnymi siłami, bez zewnętrznego renderera. Poniżej skala tego, co powstało.

| Metryka | Wartość |
|---|---|
| Okres prac | 2026-01-28 → 2026-09-17 |
| Commity | 240 |
| Backend C# (kod produkcyjny / testy) | ~39 tys. / ~34 tys. linii |
| Własne konwertery DOCX↔HTML | ~16 tys. linii |
| Frontend TypeScript (kod / testy / szablony i style) | ~25 tys. / ~16 tys. / ~16 tys. linii |
| Sam komponent edytora WYSIWYG | ~18,5 tys. linii |
| Testy backend (NUnit) | ok. 1370, wszystkie zielone |
| Testy frontend (Vitest) | 914 w 96 plikach, wszystkie zielone |
| Udokumentowane decyzje architektoniczne (ADR) | 112 |
| Wpisy w changelogu projektu | 277 |

## Co powstało w ciągu roku

**Platforma i architektura.** System składa się z trzech aplikacji: wewnętrznego API dla interfejsu użytkownika (.NET 8, Clean Architecture, CQRS na MediatR, walidacja FluentValidation), zewnętrznego API dla aplikacji integrujących się oraz SPA w Angularze. Dane opisowe leżą w PostgreSQL, pliki w Google Cloud Storage, całość jest skonteneryzowana pod Docker i przygotowana pod GCP. Od początku pilnowano czytelnych granic warstw i stabilnych kontraktów REST, żeby kolejne osoby mogły wchodzić w projekt bez bólu.

**Cykl życia dokumentu.** Zaimplementowano pełną ścieżkę: przyjęcie dokumentu z metadanymi od aplikacji zewnętrznej, tryb podglądu, tryb edycji z auto-zapisem nadpisującym kopię edytowalną w miejscu, historię wersji z przywracaniem oraz krok „Zakończ i wyślij". Ten ostatni zasługuje na osobne zdanie: dostawa pliku na adres zwrotny została zaprojektowana jako kolejka w PostgreSQL z workerem w tle, bez dodatkowego brokera. Zadania są pobierane bezpiecznie przy wielu instancjach, ponawiane z rosnącym odstępem przez dobę, a po wyczerpaniu prób trafiają do osobnego stanu. Użytkownik dostaje natychmiastową informację zwrotną dzięki synchronicznej pierwszej próbie, a administrator ma panel z podglądem, ponawianiem, anulowaniem i zmianą adresu dostawy.

**Bezpieczeństwo i tożsamość.** Uwierzytelnianie oparto na Microsoft Entra ID (MSAL po stronie przeglądarki, Microsoft.Identity.Web po stronie API) z dwiema rozłącznymi rolami, autoryzacją opartą o zasoby, mapowaniem grup na role, wyszukiwaniem użytkowników przez Microsoft Graph i sekretami pobieranymi z GCP Secret Manager. Dostęp do konkretnego dokumentu jest dodatkowo ograniczany listą kluczy korporacyjnych. Upload chroni centralna polityka (sprawdzanie sygnatur plików, hardening archiwów ZIP, blokada makr), a adresy zwrotne przechodzą przez walidator przeciw SSRF. Osobno rozwiązano nieprzyjemny w praktyce problem wygasającej sesji, po którym użytkownicy lądowali na stronie „brak uprawnień"; teraz następuje ciche odświeżenie tokenu z zabezpieczeniem przed pętlą logowania. Przeprowadzono też remediację zgłoszeń ze skanera Checkmarx w kodzie budującym pakiety DOCX.

**Funkcje domenowe.** Podpisy cyfrowe w dokumencie (RSA-SHA256, własny format w Custom XML Part), generowanie kodów kreskowych i QR, biblioteka szablonów, otwieranie dokumentów zabezpieczonych hasłem, generowanie podglądu PDF z bieżącego stanu edytora oraz strukturalne logowanie JSON pod Google Cloud Logging i ELK z identyfikatorem korelacji.

**Własny silnik dokumentów, czyli serce projektu.** Największa część roku poszła na dwukierunkową konwersję DOCX↔HTML zbudowaną na OpenXml SDK. Świadomie odrzucono LibreOffice i komercyjne biblioteki, zarówno z powodów licencyjnych i wdrożeniowych, jak i dlatego, że żadne z nich nie dawało kontroli nad wiernością i nad tym, co dzieje się z dokumentem przy zapisie. Silnik obsługuje dziś: style z dziedziczeniem i motywy, wiele sekcji z własną geometrią oraz nagłówkami i stopkami (w tym warianty pierwszej i parzystej strony), tabele w niemal pełnym zakresie (style warunkowe, obramowania, scalenia, wysokości wierszy, tabele pływające i zagnieżdżone), listy zgodne ze specyfikacją numeracji, tabulatory pozycyjne, pola dokumentu (numery stron, daty, spis treści), przypisy dolne i końcowe, obrazy z kadrowaniem i ramkami, kształty DrawingML, VML i obiekty OLE (przenoszone bez strat i edytowalne w zakresie przesuwania i skalowania), akceptację śledzonych zmian, ochronę dokumentu oraz siatkę dokumentu. Do tego dwa konwertery napisane od zera bez natywnych zależności: parser binarnego formatu .doc (struktury FIB, piece table, formatowanie znaków) oraz tłumacz grafik EMF/WMF/VML na SVG.

**Edytor WYSIWYG.** Po stronie przeglądarki powstała własna paginacja naśladująca Worda: podział akapitów między strony po liniach, dzielenie wierszy tabel, respektowanie „razem z następnym" i „nie dziel", powtarzane nagłówki tabel, regiony przypisów rezerwujące miejsce na stronie, kolumny sekcji. Użytkownik dostał edycję nagłówka i stopki z wariantami, linijki, wyszukiwanie i zamianę, panel właściwości tabel z obramowaniami, własny pipeline schowka (kopiowanie fragmentów list i tabel bez psucia struktury), undo/redo, malarza formatów, „lepkie" formatowanie przy pisaniu, widok znaków formatowania, tryb tylko do odczytu oraz obsługę klawiatury zbliżoną do Worda. Przeglądarka PDF oparta na pdf.js dostała mechanizm nadzoru etapów renderowania, dzięki któremu błąd jednej strony nie blokuje pozostałych. W ostatnich tygodniach GUI przeszło migrację Angular 20 → 21 → 22 z zachowaniem dotychczasowego modelu detekcji zmian, żeby nie ryzykować regresji w rozbudowanym edytorze.

## Sposób pracy, jakość i wnioski

**Zgodność z Wordem była mierzona, nie zakładana.** Zamiast polegać na dokumentacji formatu, budowano mapy stron per akapit z Worda (przez COM) i zestawiano je z sondą uruchamianą w przeglądarce, a w trudniejszych przypadkach mierzono pozycje glifów w PDF wygenerowanym przez Worda. Na przygotowanych dokumentach testowych edytor osiąga zgodność liczby i początków stron z Wordem (12/12 na fixture odstępów), medianę odchylenia pionowego rzędu 0,8 pt i zgodność strukturalną 84 z 88 tabel na fixture paginacji tabel. Kilka razy pomiary obaliły wcześniejsze własne założenia, na przykład model odstępów między akapitami, i zostało to uczciwie odnotowane jako korekta wcześniejszej decyzji.

**Praca systematyczna, z audytami.** Powstał pełny audyt zgodności z Wordem (około 60 problemów sklasyfikowanych według ryzyka i warstwy), osobne raporty luk dla układu tabel, paginacji tabel i numeracji list oraz macierz zgodności funkcji. Dzięki temu priorytety napraw wynikały z faktów, a nie z kolejności zgłoszeń.

**Inżynieria jakości.** Harness regresji na „złotych" dokumentach DOCX ze snapshotami HTML, wspólny moduł jednostek OOXML zamiast magicznych stałych, benchmarki wydajności i pamięci na BenchmarkDotNet, narzędzia diagnostyczne do inspekcji i porównywania plików DOCX, a także własna metoda weryfikacji w działającej przeglądarce (headless Chrome sterowany przez protokół CDP), bo część zachowań edytora nie da się sprawdzić w środowisku testów jednostkowych. Wydajność edytora była profilowana, a jedna zmiana została świadomie wycofana po wykryciu regresji, z udokumentowanym powodem.

**Zgłoszenia produkcyjne rozwiązywane do przyczyny.** Wśród nich: wygasająca sesja i strona „brak uprawnień", cofanie zmian przestawiające kursor, tabele źle łamane między stronami, nowe komórki tabeli bez formatowania wzorca, a ostatnio przeglądarka PDF zawieszająca się na stronach z obrazami, gdzie przyczyną okazał się konflikt między biblioteką pdf.js a zone.js, a nie przeglądarka klienta ani plik.

**Praca z AI jako narzędziem, nie skrótem.** Repozytorium ma katalog `.ai/` z około czterdziestoma plikami pełniącymi rolę pamięci projektu: kontekst, aktualny stan, przekazanie zadań, decyzje, ryzyka, standardy kodowania. Każda istotna zmiana zostawia ślad w changelogu i w razie potrzeby w ADR. To pozwala nowej osobie (albo agentowi) kontynuować pracę bez odkrywania projektu od zera i jest praktyką, którą warto przenosić na inne zespoły.

**Co pozostaje otwarte.** Uczciwie warto wspomnieć, że część obszarów jest wciąż w toku: pełne modelowanie stylów jako jednego obiektu obliczonego, edycja nagłówka strony parzystej z poziomu interfejsu, kilka luk w listach i tabelach opisanych w raportach GAP oraz docelowy konwerter DOCX → PDF, który dziś działa za interfejsem jako atrapa.

Dwie uwagi praktyczne do raportu: git przypisuje commity czterem tożsamościom (krdev 220, bot Copilot 37, biurokrdev 15, rybkakrzy 8), więc warto to zsumować pod jednym nazwiskiem, a w tekście trzymać się pseudonimu Doc2 zamiast realnej nazwy klienta.
