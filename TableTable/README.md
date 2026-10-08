# TableTable

Biblioteka .NET 8 (Open XML SDK) wypełniająca tabele w dokumentach DOCX danymi „wiele rekordów”. Szablon Word jest jedynym opisem struktury: tabela ma **tag formantu**, w komórkach są **znaczniki** `<%nazwa%>`, a warunkowe fragmenty (np. ikony) i listy są **formantami zawartości** z tagiem. Dane to jeden JSON na dokument, kluczowany tagami tabel. Dziś jeden szablon, jutro inny – biblioteka nie wie, czy to wiadomości, załączniki czy faktury.

| Składnik | Licencja | Rola |
|---|---|---|
| [DocumentFormat.OpenXml](https://github.com/dotnet/Open-XML-SDK) 3.5 | MIT | odczyt i modyfikacja DOCX bez Worda |
| Newtonsoft.Json 13 | MIT | model jako `JToken`, ścieżki JSONPath (`SelectToken`) jak w `context.JModel` workerów |
| Microsoft.Extensions.Logging.Abstractions 8 | MIT | `ILogger` (opcjonalny) |

## Jak to działa

Jedno wywołanie = jedna tabela. Dokument z N tabelami to N wywołań (np. N instancji workera), każde z innym tagiem i inną ścieżką do rekordów w modelu. Biblioteka nie zna domeny: pola, przełączniki i listy odczytuje z szablonu.

```csharp
using TableTable;

var builder = new RecordTableBuilder();                   // bezstanowy, może być singletonem
using var doc = WordprocessingDocument.Open(path, true);

// minimalnie: tag tabeli w szablonie + JSONPath do rekordów w modelu (ctx.JModel)
RecordTableResult r1 = builder.Apply(doc, "tabela_wiadomosci", "sprawa.korespondencja", model);
RecordTableResult r2 = builder.Apply(doc, "tabela_zalaczniki", "sprawa.zalaczniki", model);

// z nadpisaniami (formaty, teksty zastępcze, wyrażenia) – definicja JSON jak w konfiguracji workera
RecordTableResult r3 = builder.Apply(doc, CustomTableDataBinding.FromJson(json), model);

r1.RecordCount; r1.UnresolvedPlaceholders; r1.Warnings;
```

Minimalna definicja tabeli w JSON:

```json
{ "tag": "tabela_wiadomosci", "model-property-path": "sprawa.korespondencja" }
```

W trakcie projektowania szablonu inspektor podaje, jakich rekordów oczekuje każda tabela:

```csharp
DocumentSchema schema = TemplateSchema.Describe("szablon.docx");
File.WriteAllText("schemat.json", schema.ToJson());
//   { "tabela_wiadomosci": [ { "rodzaj_wiadomosci": "", "ikona_wyslana": true, "zalaczniki": [ { "nazwa_pliku": "" } ] } ],
//     "tabela_zalaczniki": [ { "nazwa_zalacznika": "", "data_dolaczenia": "", "dodajacy": "", "archiwizuj": "" } ] }
```

Rdzeń biblioteki nie zawiera orkiestracji po dokumencie – to zadanie aplikacji (workerów). Przykładowy pomocnik `DocxTableBinder` (w projekcie `samples/TableTable.Sample`, zbudowany wyłącznie na publicznym API: `TemplateSchema` + `RecordTableBuilder`) pokazuje, jak związać wszystkie otagowane tabele jednym wywołaniem, gdy ktoś takiej orkiestracji nie ma.

### Szablon (co oznacza się w Wordzie)

| Element szablonu | Jak oznaczyć | Co dostaje z danych |
|---|---|---|
| **Tabela** | formant z tagiem `T`: *Deweloper → Sekcja powtarzana* na wierszach rekordu (zalecane) albo dowolny formant owijający tabelę | `model.T` = tablica rekordów |
| **Pole** | tekst `<%nazwa%>` w komórce (może być rozbity przez Worda na kilka runów) | `rekord.nazwa` |
| **Przełącznik** | formant z tagiem `t` owijający ikonę, akapit, wiersz, tabelę zagnieżdżoną (bez znaczników w środku) | `rekord.t` – prawda = zostaje, fałsz = znika |
| **Lista** | formant z tagiem `l` owijający fragment ze znacznikami (np. akapit z punktorem, wiersze) | `rekord.l` = tablica elementów; zawartość formantu powtarzana per element |

Reguły: wiersz nagłówka i stopki leżą poza sekcją powtarzaną i zostają; w zwykłej tabeli (bez sekcji) szablonem rekordu jest ciągły blok wierszy od pierwszego do ostatniego ze znacznikiem (wiersz z samą grafiką na końcu rekordu wymaga `template-row-count`). Grafiki w wierszach rekordu są klonowane razem z nim z unikalnymi identyfikatorami. Listy i przełączniki mogą się zagnieżdżać (lista w liście, przełącznik w elemencie listy). Znacznik rekordu wewnątrz elementu listy też jest wiązany (`<%nadawca%>` w punkcie listy → rekord). `<%$index%>` = numer rekordu od 1, `<%$root.x%>` = właściwość korzenia modelu. Pusta lista = formant znika; pusta tabela = wiersze szablonu znikają.

### Dane

```json
{
  "tabela_wiadomosci": [ { "rodzaj_wiadomosci": "E-mail", "ikona_wyslana": true, "ikona_odebrana": false,
                           "zalaczniki": [ { "nazwa_pliku": "umowa.pdf", "rozmiar_pliku": 248193 } ] } ],
  "tabela_zalaczniki": [ { "nazwa_zalacznika": "umowa.pdf", "data_dolaczenia": "2026-09-14T08:15:00Z", "archiwizuj": true } ]
}
```

Wartości: tekst wstawiany jak jest (`\n` → podział wiersza), liczby i daty w kulturze `pl-PL` (data z godziną `dd.MM.yyyy HH:mm`, UTC przeliczane na czas polski), `true`/`false` → `Tak`/`Nie`, `null` → pusto. Model może być `JToken` albo dowolny obiekt .NET (`JToken.FromObject`).

### Definicja (opcjonalna)

Nadpisuje konwencję tam, gdzie trzeba. Nazwy JSON jak w konfiguracji workerów (`search-for`, `replacement-property-path`, `replacement-property-expression`, `display-format`, `match-case`). Dopuszczalne komentarze `//`.

```json
{
  "tables": [
    {
      "tag": "tabela_wiadomosci",
      "empty-text": "Brak wiadomości w sprawie.",
      "fields": [
        { "search-for": "<%data_czas_wiadomosci%>", "replacement-property-path": "data_czas_wiadomosci", "display-format": "dd-MM-yyyy HH:mm" },
        { "search-for": "<%tresc_wiadomosci%>", "replacement-property-path": "tresc_wiadomosci", "null-text": "(brak treści)" }
      ],
      "collections":      [ { "tag-name": "zalaczniki", "empty-text": "brak załączników", "unwrap": true,
                              "fields": [ { "search-for": "<%rozmiar_pliku%>", "replacement-property-path": "rozmiar_pliku", "display-format": "N0" } ] } ],
      "content-controls": [ { "tag-name": "ikona_wyslana", "visible-property-path": "ikona_wyslana", "unwrap": true } ]
    }
  ]
}
```

Tabela (`tables[]`, klasa `CustomTableDataBinding`):

| Klucz | Domyślnie | Uwagi |
|---|---|---|
| `tag` | – | tag formantu tabeli (`*` jako symbol wieloznaczny); zamiast tagu `bookmark-name`; bez obu – pierwsza tabela ze znacznikami |
| `model-property-path` | – | JSONPath do rekordów od korzenia modelu, np. `sprawa.korespondencja[?(@.typ=='email')]`; puste = model jest tablicą |
| `fields` | `[]` | `search-for`, `replacement-property-path` / `replacement-property-expression` / `replacement-text`, `display-format` (`dd-MM-yyyy`, `N0`, `N2`, `C`), `match-case`, `null-text` |
| `collections` | `[]` | `tag-name` (formant-lista), `model-property-path` (= `tag-name`), `empty-text`, `unwrap`, zagnieżdżone `fields`/`collections`/`content-controls` |
| `content-controls` | `[]` | `tag-name`, `visible-property-path` / `visible-property-expression`, `visible-when-equals`, `negate`, `unwrap` |
| `auto-bind` | `true` | konwencja dla elementów bez mapowania |
| `empty-behavior` | `remove-template-rows` | `remove-table`, `keep-template` |
| `empty-text` | – | wiersz scalony na całą szerokość przy braku rekordów |
| `keep-unresolved` | `false` | zostaw znaczniki bez wartości (zawsze zgłaszane w wyniku) |
| `template-row-count` | auto | liczba wierszy szablonu w zwykłej tabeli, gdy ostatni wiersz rekordu nie ma znacznika |

Warunek widoczności: `visible-when-equals` porównuje wartość jako tekst; bez niego wartość jest logiczna (`true`, liczba ≠ 0, niepusty tekst inny niż `false/0/nie/no`, niepusta tablica). `negate` odwraca.

Wyrażenia `replacement-property-expression` / `visible-property-expression` liczy `RecordTableOptions.ExpressionEvaluator` (`IExpressionEvaluator`: wyrażenie, korzeń modelu, bieżący rekord, numer) – tu podpina się silnik wyrażeń workerów.

## Pozostałe API

- `RecordTableBuilder.Apply(doc, binding, model)` – jedna tabela wskazana w `binding` (tag / zakładka); rekordy z `model-property-path` albo cały model jako tablica.
- `ContentControlVisibilityWorker` – przełączniki na całym dokumencie (treść, nagłówki, stopki), ścieżki od korzenia modelu. Odpowiednik `DocXContentControlRemover` z warunkiem.
- `TemplateSchema.Describe(doc)` → `DocumentSchema.Tables[]` (`Tag`, `Kind`, `Fields`, `Flags`, `Collections`) i `ToSkeleton()`.
- `RecordTableOptions`: ograniczniki znacznika, kultura, strefa, formaty domyślne, teksty Tak/Nie, `ExpressionEvaluator`, `Logger`.

## Integracja z workerami

Jeden worker = jedna tabela, a jego konfiguracja w edytorze to wprost definicja tabeli (te same klucze co `CustomTableDataBinding`). Dla N tabel w szablonie konfiguruje się N workerów, każdy z innym `tag` i `model-property-path`; kolejność nie ma znaczenia.

```json
{ "tag": "tabela_wiadomosci", "model-property-path": "sprawa.korespondencja" }
```

```json
{
  "tag": "tabela_wiadomosci",
  "model-property-path": "sprawa.korespondencja",
  "empty-text": "Brak wiadomości w sprawie.",
  "fields":           [ { "search-for": "<%data_czas_wiadomosci%>", "replacement-property-path": "data_czas", "display-format": "dd-MM-yyyy HH:mm" } ],
  "collections":      [ { "tag-name": "zalaczniki", "empty-text": "brak załączników", "unwrap": true } ],
  "content-controls": [ { "tag-name": "ikona_wyslana", "visible-property-path": "kierunek", "visible-when-equals": "wyslana" } ]
}
```

```csharp
[JsonObject]
public class DocXTableDataWorker : DocXConditionalWorker
{
    public DocXTableDataWorker(ILogger logger) : base(logger) { }

    [JsonProperty("tag")]
    public string Tag { get; set; }

    [JsonProperty("model-property-path")]
    public string ModelPropertyPath { get; set; }

    // Pozostałe klucze definicji (fields, collections, content-controls, empty-text, ...) bez deklarowania każdego z osobna.
    [JsonExtensionData]
    public IDictionary<string, JToken> Overrides { get; set; } = new Dictionary<string, JToken>();

    public override void Run(DocXWorkerContext ctx)
    {
        logger.LogInformation("DocXWorker: {WorkerName}", nameof(DocXTableDataWorker));
        if (!base.IsValid(ctx)) return;
        try
        {
            var json = new JObject(Overrides.Select(kv => new JProperty(kv.Key, kv.Value)))
            {
                ["tag"] = Tag,
                ["model-property-path"] = ModelPropertyPath,
            };
            var binding = CustomTableDataBinding.FromJson(json.ToString());

            var builder = new RecordTableBuilder(new RecordTableOptions
            {
                Logger = logger,
                ExpressionEvaluator = new DelegateExpressionEvaluator((expr, scope) => ctx.GetExpressionValue<object>(expr)),
            });
            var result = builder.Apply(ctx.WordDoc, binding, ctx.JModel);
            foreach (var tag in result.UnresolvedPlaceholders) logger.LogWarning("Znacznik {Tag} bez wartości", tag);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker {Name} exception message: {Message}", nameof(DocXTableDataWorker), ex.Message);
        }
    }
}
```

`replacement-property-expression` trafia do `ctx.GetExpressionValue` (silnik na `formModel`); wartości per rekord bierz przez `replacement-property-path`, dopóki silnik nie dostanie `scope.Current` jako zmiennej bieżącego rekordu.

Kształt rekordów pod `model-property-path` podaje `TemplateSchema` dla danego szablonu.

## Ograniczenia

- Zakładki w klonach rekordów poza pierwszym są usuwane (Word nie toleruje zdublowanych identyfikatorów).
- Grafika pływająca zakotwiczona względem strony/marginesu powieli się w jednym miejscu – builder ostrzega; używaj grafik w tekście albo zakotwiczonych względem akapitu.
- Znaczniki w polach tekstowych/kształtach osadzonych w komórce nie są podmieniane (tylko akapity).
- Wartości nie są interpretowane jako RTF/HTML.

## Budowanie, testy, przykład

```
dotnet build TableTable.sln
dotnet test  TableTable.sln
dotnet run --project samples/TableTable.Sample -- samples/TableTable.Sample/out
```

Przykład buduje jeden szablon z dwiema tabelami (korespondencja jako sekcja powtarzana z ikonami w formantach, listą załączników i przerywaną linią; tabela załączników sprawy w formancie blokowym), zapisuje szkielet danych `schemat.json` z inspektora i wypełnia szablon trzy razy, wołając `RecordTableBuilder.Apply` osobno dla każdej tabeli z listy w definicji – tak jak zrobi to N workerów w aplikacji:

| Wynik w `out/` | Definicja | Dane |
|---|---|---|
| `dokument-minimalna.docx` | `data/dokument.definicja-minimalna.json` – per tabela tylko `tag` + `model-property-path` | `data/dokument.dane.json` |
| `dokument-nadpisania.docx` | `data/dokument.definicja.json` – dodatkowo formaty, teksty zastępcze, zdjęcie ramek | `data/dokument.dane.json` |
| `dokument-puste.docx` | jw. | `data/dokument-puste.dane.json` (puste listy) |

Każdy wynik jest sprawdzany walidatorem Open XML. Gdy plik w `out/` jest otwarty w Wordzie, generator zapisuje pod nazwą ze znacznikiem czasu.
