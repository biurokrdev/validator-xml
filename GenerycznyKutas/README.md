# DocxPipeline

Biblioteka .NET 8 (Open XML SDK) wykonująca na dokumencie DOCX **sekwencję workerów opisaną konfiguracją JSON**. Zestaw czynności nie jest znany z góry: workery są odkrywane **refleksją** – z własnego assembly, z assembly załadowanych do procesu i z plików DLL w katalogu wtyczek – a konfiguracja odwołuje się do nich po nazwie. Ile ich będzie i ile zostanie użytych w potoku, decyduje JSON.

| Składnik | Licencja | Rola |
|---|---|---|
| [DocumentFormat.OpenXml](https://github.com/dotnet/Open-XML-SDK) 3.5 | MIT | odczyt i modyfikacja DOCX bez Worda |
| Newtonsoft.Json 13 | MIT | konfiguracja potoku i workerów, model jako `JToken` (JSONPath) |
| Microsoft.Extensions.Logging.Abstractions 8 | MIT | `ILogger` (opcjonalny) |

## Jak to działa

```
pipeline.json  ─┐
model (JToken) ─┼─▶ PipelineRunner ──▶ krok 1 → krok 2 → … (jeden otwarty dokument, wspólny kontekst) ──▶ wynik.docx
szablon.docx   ─┘        ▲
                   WorkerRegistry: nazwa → typ (refleksja: [DocxWorker("…")] albo nazwa klasy)
```

```csharp
using DocxPipeline;

var registry = WorkerRegistry.FromLoadedAssemblies()          // wbudowane + wszystko, co host ma załadowane
    .AddPluginsFolder(@"C:\app\plugins");                      // + DLL z katalogu wtyczek
foreach (var w in registry.Workers) Console.WriteLine($"{w.Name}: {w.Description}");

var pipeline = PipelineDefinition.FromJson(File.ReadAllText("pipeline.json"));
var runner = new PipelineRunner(registry, logger);

using var doc = WordprocessingDocument.Open(path, true);     // albo runner.RunFile(szablon, wynik, model, pipeline)
PipelineResult result = runner.Run(doc, model, pipeline);
foreach (var step in result.Steps)
    Console.WriteLine($"{step.Index}. {step.Worker}: {step.Status} {step.Message}");
```

### Konfiguracja potoku

```json
{
  "name": "Pismo w sprawie",
  "steps": [
    { "worker": "record-table", "tag": "tabela_zalaczniki", "model-property-path": "zalaczniki_zglaszajacego" },
    { "worker": "content-control-remover", "tag-name": "klauzula_firmowa", "remove-when": "osoba_fizyczna" },
    { "worker": "bookmark-text", "bookmark-name": "uwagi", "model-property-path": "uwagi", "null-text": "brak uwag" },
    { "worker": "field-replacer" },
    { "worker": "podpis-kwalifikowany", "on-error": "continue", "certyfikat": "…" }
  ]
}
```

Krok to obiekt: klucz `worker` (nazwa z rejestru) plus **płaska konfiguracja workera** – dokładnie tak, jak konfiguruje się workery w edytorze. Klucze zarezerwowane dla potoku:

| Klucz | Domyślnie | Znaczenie |
|---|---|---|
| `worker` | – | nazwa workera |
| `enabled` | `true` | wyłączony krok jest pomijany |
| `when` | – | JSONPath w modelu; krok wykonuje się tylko, gdy wartość jest „prawdziwa” (true, liczba ≠ 0, niepusty tekst, niepusta tablica) |
| `unless` | – | JSONPath; krok pomijany, gdy wartość „prawdziwa” |
| `on-error` | `stop` | `continue` – wyjątek workera (albo nieznany worker) nie przerywa potoku |

Wszystkie inne klucze trafiają do workera (`[JsonExtensionData]`). Kolejność kroków ma znaczenie: np. tabele „wiele rekordów” przed `field-replacer`, żeby znaczniki rekordów nie zostały wcześniej dotknięte wiązaniem po nazwie.

### Własny worker

```csharp
[DocxWorker("stempel-daty", Description = "Wstawia datę do stopki")]   // bez atrybutu: StempelDatyWorker → stempel-daty
public sealed class StempelDatyWorker : IDocxWorker
{
    [JsonProperty("format")] public string Format { get; set; } = "dd.MM.yyyy";

    public void Run(DocxWorkerContext ctx)
    {
        // ctx.Document (WordprocessingDocument), ctx.Model (JToken), ctx.Logger, ctx.Items (stan między krokami)
        // ctx.Roots() / ctx.Paragraphs() – treść, nagłówki, stopki; ctx.Resolve("sciezka") – wartość z modelu; ctx.Warn("…")
    }
}
```

Wymagania: publiczna klasa, konstruktor bezparametrowy, właściwości konfiguracji z `[JsonProperty("kebab-case")]`. Worker staje się dostępny, gdy jego assembly jest załadowane (`AddAssembly`, `FromLoadedAssemblies`) albo leży w katalogu wtyczek (`AddPluginsFolder`). Wyjątek z `Run` = krok `Failed`; `ctx.Warn` = ostrzeżenie bez przerywania.

## Workery wbudowane

| Nazwa | Co robi | Konfiguracja |
|---|---|---|
| `field-replacer` | podmienia `<%nazwa%>` w całym dokumencie (także rozbite na runy); po nazwie z modelu i/lub z jawnych mapowań | `fields[] { search-for, model-property-path / text, display-format, match-case, null-text }`, `auto-bind` (true), `unresolved-text` |
| `content-control-remover` | usuwa formanty o tagu (jak `DocXContentControlRemover`), opcjonalnie warunkowo; formant, który zostaje, może stracić ramkę | `tag-name` (`*` wieloznaczne), `remove-when`, `negate`, `unwrap` (bez warunku: tylko zdjęcie ramki) |
| `bookmark-text` | wstawia tekst w punkcie zakładki albo zastępuje nim akapit; pusta wartość → tekst zastępczy lub usunięcie akapitu | `bookmark-name`, `model-property-path` / `text`, `display-format`, `null-text`, `replace-paragraph`, `remove-paragraph-when-empty` |

**Wtyczka `DocxPipeline.TableTable`** (osobne assembly): worker `record-table` opakowujący bibliotekę `TableTable` – konfiguracja kroku to wprost definicja tabeli (`tag`, `model-property-path`, `fields`, `collections`, `content-controls`, `empty-text` …). Pokazuje, że do potoku wchodzi dowolny worker spoza rdzenia.

Wartości z modelu: `true/false` → Tak/Nie, daty → `dd.MM.yyyy HH:mm` (lub `display-format`), liczby w `pl-PL`, `\n` w tekście → podział wiersza.

## Budowanie, testy, przykład

```
dotnet build DocxPipeline.sln
dotnet test  DocxPipeline.sln
dotnet run --project samples/DocxPipeline.Sample -- samples/DocxPipeline.Sample/out
```

Przykład buduje szablon (pola, dwie klauzule w formantach, zakładka, tabela załączników, tabela wiadomości jako sekcja powtarzana z ikonami i listą w formantach), wypisuje workery znalezione refleksją (wbudowane + wtyczka z katalogu programu), wykonuje `data/pipeline.json` na `data/model.json` (kształt JModel aplikacji) i raportuje każdy krok: status, czas, ostrzeżenia. Potok celowo zawiera krok pominięty warunkiem i krok z nieistniejącym workerem (`on-error: continue`). Wynik `out/pismo.docx` jest sprawdzany walidatorem Open XML.

## Integracja z workerami aplikacji

Nadrzędny worker w aplikacji trzyma `pipeline.json` w swojej konfiguracji i w `Run` woła `PipelineRunner.Run(ctx.WordDoc, ctx.JModel, pipeline)`. Istniejące workery aplikacji (`DocXReplaceWorker`, `DocXContentControlRemover`, `DocXTableArchitect` …) można podpiąć bez przepisywania: cienki adapter `IDocxWorker` tworzący worker aplikacji z konfiguracji kroku i wywołujący jego `Run(DocXWorkerContext)`; rejestr znajdzie adaptery refleksją tak samo jak workery wbudowane.
