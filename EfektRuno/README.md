# EfektRuno — tabele „Wiadomości w sprawie" w Wordzie (OpenXML SDK + dynamiczny JSON)

PoC silnika szablonów DOCX. Wejście: szablon `.docx` ze znacznikami `<%...%>` + dowolny `JsonNode`.
Wyjście: wypełniony dokument + raport błędów szablonu. Jedyna zależność: `DocumentFormat.OpenXml` 3.3.0.

> **Założenie:** plik JSON z modelem danych nie dotarł razem ze zdjęciami. Model w `Demo/*.json` jest
> wyprowadzony z placeholderów widocznych w szablonie. Silnik nie zna żadnych nazw pól — po podmianie JSON-a
> wystarczy dopasować nazwy w szablonie albo podać mapę aliasów (pkt 5). Szablon demo jest generowany kodem
> (`Demo/DemoTemplateBuilder.cs`), bo oryginalnego `.docx` też nie było.

## 1. Dlaczego obecny kod (z notatek) tu nie wystarcza

| Klasa | Co robi | Czego brakuje dla tego szablonu |
|---|---|---|
| `DocXTableBuilder.BuildTable` | refleksją zamienia listę z `BaseFormModel` na `IEnumerable<IEnumerable<string>>` (wiersz = lista stringów, kolejność z `OrderColumnTableAttribute`) | tylko płaska siatka. Blok wiadomości ma 3 wiersze, scalone komórki, **listę w liście** (pliki), wariant ikony i etykiet (Wysłano/Odebrano, Przez/Od). Do tego wymaga klas i atrybutów, a dane mają być dynamicznym JSON-em |
| `TextReplacer` (kopia z OpenXml PowerTools) | rozbija runy na znaki, dopasowuje `search` ponad granicami runów, skleja z powrotem | poprawnie rozwiązuje problem „pociętych" placeholderów, ale zamienia **globalnie w całym dokumencie** — po sklonowaniu bloku N razy każdy klon dostałby tę samą wartość. Brak pojęcia zakresu, powtórzeń i warunków |

Tabela załączników (nagłówek + 1 wiersz) da się zrobić starym sposobem. Tabela wiadomości oraz ramki
„Zamknęliśmy…" / „Przesłaliśmy…" / „Brak dodatkowych wiadomości" — nie.

## 2. Propozycja: dwa generyczne workery na wspólnym rdzeniu

Wygląd zostaje w Wordzie (autor szablonu nadal go edytuje), kod tylko **klonuje, usuwa i podstawia**.
Oba workery mają interfejs jak `DocXTableBuilder`: **dokument + nazwa listy + wartości**.

```csharp
using var document = WordprocessingDocument.Open(copyOfTemplate, true);
var pipeline = new WorkerPipeline();

// Worker 1 - tabela prosta. Wiersz-szablon: <%#zalaczniki_sprawy%> w pierwszej komórce, <%/zalaczniki_sprawy%> w ostatniej.
// Znacznik w każdej komórce mówi, które pole JSON trafia do tej kolumny - kolejność pól w JSON nie ma znaczenia.
new SimpleTableWorker().Fill(document, "zalaczniki_sprawy", json["zalaczniki_sprawy"], pipeline);

// Worker 2 - "pieczątka": obszar między <%#wiadomosci%> a <%/wiadomosci%> (3-wierszowa tabela + akapit-separator)
// powielany per element. W środku pracują pod-workery: warunek (ikona, Wysłano/Odebrano), pieczątka listy plików, wartości.
new StampWorker().Fill(document, "wiadomosci", json["wiadomosci"], pipeline);

pipeline.Fill(document, json);   // reszta: nagłówek, ramki, zwykłe wartości; zwraca RenderReport
```

| Klasa (`Templating/Workers`) | Rola |
|---|---|
| `SimpleTableWorker` | wiersz-szablon × N elementów; mapowanie kolumn przez znaczniki w komórkach; opcjonalnie `Columns` (pozycyjnie, gdy komórki nie mają znaczników) i `RemoveTableWhenEmpty` |
| `StampWorker` | dowolny obszar × N elementów (tabela wielowierszowa, tabela + akapity, ramka, fragment akapitu); `SubWorkers[nazwa]` = własne pod-workery ważne tylko w tej pieczątce |
| `ConditionWorker` | `<%?…%>`/`<%^…%>`/`==` — zostaw albo usuń obszar, bez zmiany kontekstu |
| `ValueWorker` | `<%nazwa%>` w zadanym zakresie — następca `TextReplacer`, ale **z zasięgiem** |
| `WorkerPipeline` | dyspozytor: nazwa z rejestru → `?`/`^` → wiersze tabeli → pieczątka; `Run(scope, ctx)` = sekcje, potem wartości |
| `IDocxWorker` | `Fill(Section, DataContext, WorkerPipeline)` — punkt rozszerzeń (przykład `UpperCaseWorker` w `Demo/SelfTests.cs`) |

Wspólny rdzeń (`Templating/`): `PlaceholderNormalizer` (pocięte runy), `Section` (lokalizacja obszaru, klonowanie,
`Iterations` = ile razy i z jakim kontekstem), `DataContext` (stos zasięgów JSON), `ValueFormatter`.
`DocxTemplateEngine` to tylko fasada `byte[] + JSON → byte[]` nad pipeline'em.

Składnia rozszerza istniejącą konwencję `<%...%>`:

| Znacznik | Znaczenie |
|---|---|
| `<%nazwa%>` | wartość; szukana w bieżącym elemencie listy, potem w rodzicach aż do korzenia JSON-a |
| `<%nazwa\|dd.MM.yyyy HH:mm%>` | wartość z formatem (daty ISO, liczby); kultura `pl-PL`, daty z offsetem/`Z` → strefa `Europe/Warsaw` |
| `<%#lista%> … <%/lista%>` | tablica → powtórz zakres dla każdego elementu; obiekt → wejdź w kontekst raz; `null`/pusta → usuń |
| `<%?pole%> … <%/pole%>` | zostaw zakres, gdy pole „prawdziwe" (niepusta tablica/string, `true`, obiekt); kontekst bez zmian |
| `<%^pole%> … <%/pole%>` | odwrotność — np. „Brak dodatkowych wiadomości" |
| `<%?pole==WARTOŚĆ%> … <%/pole%>` | porównanie z literałem (bez rozróżniania wielkości liter); `^` neguje |
| `<%.%>` | bieżący element (dla tablic stringów) |

**Jedna reguła zakresu:** zakres to najniższy poziom, na którym spotykają się oba znaczniki.

- oba w **jednym akapicie** → zakresem są runy między nimi (wariant ikony, etykiety „Wysłano"/„Odebrano");
- w **różnych akapitach** tego samego body/komórki → akapity i tabele między nimi; akapit zawierający wyłącznie znacznik jest usuwany;
- w **różnych komórkach** → całe wiersze tabeli (powtarzany wiersz załączników, ukrywany wiersz „Załączniki").

### Jak oznaczyć szablon ze zdjęć

```
Tabela załączników, wiersz danych:
| <%#zalaczniki_sprawy%><%nazwa_zalacznika%> | <%data_dolaczenia|dd.MM.yyyy%> | <%dodajacy%> | <%archiwizuj%><%/zalaczniki_sprawy%> |

<%^wiadomosci%>
(i) Brak dodatkowych wiadomości w sprawie
<%/wiadomosci%>

<%#wiadomosci%>
| <%?kierunek==WYSLANA%>[ikona↗]<%/kierunek%><%?kierunek==ODEBRANA%>[ikona↘]<%/kierunek%> | Rodzaj wiadomości / <%rodzaj_wiadomosci%> | (Wysłano|Odebrano) / <%data_czas_wiadomosci|dd.MM.yyyy HH:mm%> | (Przez|Od) / <%nadawca_wiadomosci%> |
|   | Treść wiadomości            | <%tresc_wiadomosci%>                                          |
|   | <%?zalaczniki%>Załączniki   | <%#zalaczniki%> ¶ ▪ <%nazwa_pliku%> ¶ <%/zalaczniki%> ¶ <%/zalaczniki%> |
(pusty akapit — patrz pułapka nr 2)
<%/wiadomosci%>

<%#zamkniecie%>  [czerwona ramka: Powód: <%powod_zamkniecia%> …]   <%/zamkniecie%>
<%#przekazanie%> [niebieska ramka: Zespół: <%zespol%> …]           <%/przekazanie%>
```

## 3. Algorytm

1. **Normalizacja** (`PlaceholderNormalizer`) — Word tnie `<%rodzaj_wiadomosci%>` na runy `<%` + `rodzaj_wiadomosci` + `%>`
   (czerwone podkreślenia na zdjęciach = `w:proofErr`). Dla każdego akapitu: sklej teksty, znajdź tokeny regexem,
   przenieś każdy token do pierwszego `w:t`, a następnie wydziel go do **własnego runu** (z kopią `rPr`).
   Od tej chwili jednostką pracy jest run; formatowanie autora szablonu zostaje.
2. **Lokalizacja sekcji** (`Section.FindFirst`) — pierwszy znacznik otwierający → pasujący zamykający
   (licznik zagnieżdżeń po nazwie) → zakres wg reguły z pkt 2; znaczniki znikają od razu.
3. **Worker** (`WorkerPipeline.Resolve` → `IDocxWorker.Fill`) — `Section.Iterations` mówi ile kopii i z jakim kontekstem;
   `Section.Emit` klonuje zakres do tymczasowego kontenera i woła `pipeline.Run` na klonie z kontekstem elementu —
   tu wchodzą pod-workery (zagnieżdżone sekcje) i na końcu `ValueWorker`. To daje zasięg podstawień, którego brakuje w `TextReplacer`.
4. **Naprawa i lint** — `w:tc` musi kończyć się akapitem, tabele bez wierszy są usuwane, `wp:docPr/@id` przenumerowane,
   w klonach czyszczone zakładki i `w14:paraId`. Raport: `UnresolvedPlaceholders` (brak klucza w JSON)
   oraz `SuspiciousFragments` (zepsute ograniczniki).

Przetwarzane są: `document.xml`, wszystkie nagłówki i stopki (`<%application_number%>` siedzi w nagłówku).

## 4. Pułapki (wszystkie obsłużone i sprawdzone w PoC)

1. **Pocięte runy** — patrz normalizacja. Bez niej `Contains("<%x%>")` na pojedynczym `w:t` nie trafi nigdy.
2. **Sąsiednie tabele Word skleja w jedną.** Powtarzany blok-tabela musi zawierać w zakresie akapit-separator.
3. **Zaokrąglone ramki to kształty, nie tabele.** Word zapisuje je jako `mc:AlternateContent`: `wps:txbx` + zapasowy VML —
   **każdy placeholder istnieje w XML dwa razy**. `Descendants<Paragraph>()` obejmuje obie gałęzie, więc obie są
   wypełniane (demo: niebieska ramka; w wyniku wartość występuje 2×). Znaczniki sekcji stawiamy w akapitach *wokół* kształtu.
4. **Zduplikowane `wp:docPr/@id`** po klonowaniu ikon → Word potrafi zgłosić „nieczytelną zawartość". Przenumerowanie na końcu.
5. **Pusta komórka** po usunięciu sekcji → plik się nie otwiera. `RepairStructure` dokłada pusty akapit.
6. **Wartość z run-a kształtu** — token rozpoznajemy tylko po bezpośrednich `w:t` runu, bo run może nieść rysunek z własnymi akapitami.
7. **Oryginał (v1) jest nietykalny** — silnik pracuje na kopii bajtów (`Render(byte[], …)`), nigdy na pliku szablonu.

### Błędy widoczne w szablonie na zdjęciach (do poprawy w `.docx`)

- `%nadawca_wiadomosci%>` — brak otwierającego `<` (linter zgłosi jako `[błąd w szablonie]`);
- `<%wyjaśnienie_przeslania%>>` — polskie znaki w nazwie (niespójne z `wyjasnienie_zamkniecia`) i nadmiarowy `>`, który zostanie w dokumencie jako zwykły tekst — tego linter nie wykryje.

## 5. Dynamiczny JSON

Brak klas i refleksji: `JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true })`.
Jeśli nazwy w JSON-ie są inne niż w szablonie (np. angielski camelCase), dwie opcje:

```csharp
var options = new DocxTemplateOptions();
options.Aliases["rodzaj_wiadomosci"] = "messageType";     // szablon bez zmian
options.Aliases["wiadomosci"] = "case.messages";          // ścieżki z kropką
```

albo przemianowanie znaczników w szablonie. Warunek `==` pozwala obsłużyć enum kierunku bez dodatkowego
view-modelu; jeśli JSON niesie np. `"direction": "OUT"`, w szablonie piszemy `<%?direction==OUT%>`.

## 6. Integracja z D2Web / CommonDocX

- `SimpleTableWorker` i `StampWorker` stają obok `IDocXTableBuilder` (te same wejścia: dokument, nazwa, wartości);
  stare szablony (płaskie tabele + `TextReplacer`) działają bez zmian, bo `<%nazwa%>` bez sekcji zachowuje się jak dotąd.
- Kolejność w potoku: **najpierw** workery sekcji, **potem** ewentualny stary `TextReplacer` dla pól spoza JSON-a.
- Własne pod-workery (np. podpis, kod QR, pieczęć graficzna) = klasa `IDocxWorker` zarejestrowana pod nazwą sekcji
  w `pipeline.ByName` (globalnie) albo `stamp.SubWorkers` (tylko w danej pieczątce).
- `RenderReport` warto logować i wywalać jako błąd w testach szablonów (NUnit: szablon + przykładowy JSON → `Assert.That(report…, Is.Empty)`).
- Koszt: wyszukiwanie sekcji skanuje kontener od początku po każdej sekcji — O(n·sekcje). Dla dokumentów na kilka stron pomijalne;
  przy setkach wiadomości warto przejść na jednokrotne zebranie znaczników.

## 7. Uruchomienie

```powershell
cd EfektRuno
dotnet run -c Release                                  # szablon demo + wyniki w .\out + self-testy
dotnet run -c Release -- szablon.docx dane.json wynik.docx
```

`out/wynik.docx` (fasada) i `out/wynik-workers.docx` (workery wołane osobno) muszą mieć identyczny tekst — program to sprawdza.
Weryfikacja wykonana: `OpenXmlValidator` (0 błędów), eksport do PDF przez prawdziwego Worda, test lintera na celowo
zepsutym szablonie, self-testy w `Demo/SelfTests.cs` (kolumny pozycyjne, pusta lista, pod-worker po nazwie) — do przeniesienia na NUnit.
