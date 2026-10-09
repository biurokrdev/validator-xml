using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TableTable;

namespace DocxPipeline.TableTable;

/// <summary>
/// Worker potoku opakowujący <see cref="RecordTableBuilder"/>: wypełnia jedną tabelę (tag / zakładka) rekordami spod ścieżki w modelu.
/// Konfiguracja kroku to wprost definicja tabeli z TableTable (<c>tag</c>, <c>model-property-path</c>, opcjonalne <c>fields</c>,
/// <c>collections</c>, <c>content-controls</c>, <c>empty-text</c> …) – klucze trafiają tu przez <c>[JsonExtensionData]</c>.
/// </summary>
[DocxWorker("record-table", Description = "Tabela „wiele rekordów” (TableTable): klonowanie szablonu rekordu, listy w formantach, przełączniki.")]
public sealed class RecordTableWorker : IDocxWorker
{
    /// <summary>Cała konfiguracja kroku = definicja tabeli (<see cref="CustomTableDataBinding"/>).</summary>
    [JsonExtensionData]
    public IDictionary<string, JToken> Binding { get; set; } = new Dictionary<string, JToken>();

    /// <inheritdoc />
    public void Run(DocxWorkerContext context)
    {
        var binding = new JObject(Binding.Select(kv => new JProperty(kv.Key, kv.Value))).ToObject<CustomTableDataBinding>()
                      ?? throw new InvalidOperationException("Pusta definicja tabeli.");
        var result = new RecordTableBuilder(new RecordTableOptions { Logger = context.Logger }).Apply(context.Document, binding, context.Model);

        foreach (var warning in result.Warnings) context.Warn(warning);
        foreach (var tag in result.UnresolvedPlaceholders) context.Warn($"Znacznik {tag} bez wartości w rekordach tabeli '{binding.Tag ?? binding.BookmarkName}'.");
        context.Items[$"record-table:{binding.Tag ?? binding.BookmarkName}"] = result.RecordCount;
    }
}
