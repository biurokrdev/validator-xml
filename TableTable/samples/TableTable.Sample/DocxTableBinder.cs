// Pomocnik przykładu, NIE część biblioteki: wiąże wszystkie otagowane tabele dokumentu jednym wywołaniem,
// korzystając wyłącznie z publicznego API rdzenia (TemplateSchema + RecordTableBuilder). W aplikacji tę rolę pełni
// własna orkiestracja workerów – po jednym wywołaniu RecordTableBuilder.Apply na tabelę.

using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TableTable;

namespace TableTable.Sample;

/// <summary>Definicja dla całego dokumentu: lista tabel rozpoznawanych po tagu; tabele bez wpisu są wiązane konwencją.</summary>
[JsonObject]
public sealed class DocumentTableBindings
{
    [JsonProperty("tables")]
    public List<CustomTableDataBinding> Tables { get; set; } = new();

    public static DocumentTableBindings FromJson(string json) =>
        JsonConvert.DeserializeObject<DocumentTableBindings>(json) ?? throw new JsonException("Pusta definicja dokumentu.");
}

/// <summary>Wynik wiązania całego dokumentu.</summary>
public sealed record DocumentBindingResult(IReadOnlyList<TableBindingResult> Tables, IReadOnlyList<string> Warnings)
{
    public IEnumerable<string> AllWarnings => Warnings.Concat(Tables.SelectMany(t => t.Result.Warnings.Select(w => $"[{t.Tag}] {w}")));
}

public sealed record TableBindingResult(string? Tag, RecordTableResult Result);

/// <summary>
/// Każda tabela z tagiem dostaje rekordy z właściwości modelu o nazwie równej tagowi (<c>{ "tabela_a": [...], "tabela_b": [...] }</c>),
/// chyba że wpis definicji podaje inną ścieżkę. Wpis definicji o tym samym tagu nadpisuje konwencję.
/// </summary>
public sealed class DocxTableBinder
{
    private readonly RecordTableBuilder _builder;
    private readonly RecordTableOptions _options;

    public DocxTableBinder(RecordTableOptions? options = null)
    {
        _options = options ?? new RecordTableOptions();
        _builder = new RecordTableBuilder(_options);
    }

    public DocumentBindingResult Apply(WordprocessingDocument document, DocumentTableBindings? definition, JToken model)
    {
        var warnings = new List<string>();
        var results = new List<TableBindingResult>();
        var bindings = definition?.Tables ?? new List<CustomTableDataBinding>();
        var used = new HashSet<CustomTableDataBinding>(ReferenceEqualityComparer.Instance);

        foreach (var table in TemplateSchema.Describe(document, _options).Tables.Where(t => t.Tag != null))
        {
            var binding = bindings.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Tag) && TagRegex(b.Tag).IsMatch(table.Tag!));
            if (binding != null) used.Add(binding);

            // Kopia, żeby nie modyfikować obiektu definicji: tag z dokumentu, ścieżka domyślnie = tag.
            var effective = binding == null ? new CustomTableDataBinding() : CustomTableDataBinding.FromJson(binding.ToJson());
            effective.Tag = table.Tag;
            if (string.IsNullOrWhiteSpace(effective.ModelPropertyPath)) effective.ModelPropertyPath = table.Tag;

            results.Add(new TableBindingResult(table.Tag, _builder.Apply(document, effective, model)));
        }

        foreach (var binding in bindings.Where(b => !used.Contains(b)))
        {
            if (!string.IsNullOrWhiteSpace(binding.Tag))
            {
                warnings.Add($"Definicja tabeli '{binding.Tag}': w dokumencie nie ma formantu o takim tagu.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(binding.ModelPropertyPath))
            {
                warnings.Add("Definicja tabeli bez tagu wymaga 'model-property-path'.");
                continue;
            }

            try
            {
                var tag = binding.BookmarkName ?? "(bez tagu)";
                results.Add(new TableBindingResult(tag, _builder.Apply(document, binding, model)));
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add(ex.Message);
            }
        }

        return new DocumentBindingResult(results, warnings);
    }

    private static Regex TagRegex(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
