using DocumentFormat.OpenXml.Packaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TableTable.Internal;

namespace TableTable;

/// <summary>
/// Wypełnia jedną tabelę dokumentu rekordami z modelu: szablon rekordu (element sekcji powtarzanej albo wiersze ze znacznikami)
/// jest klonowany per rekord, znaczniki podmieniane, formanty-listy powtarzane, formanty-przełączniki usuwane lub zostawiane.
/// Dla N tabel w dokumencie wołaj N razy. Bezstanowy.
/// </summary>
public sealed class RecordTableBuilder(RecordTableOptions? options = null)
{
    private readonly RecordTableOptions _options = options ?? new RecordTableOptions();

    /// <summary>Minimalne wywołanie: tabela o tagu <paramref name="tag"/>, rekordy spod <paramref name="modelPropertyPath"/>, reszta z szablonu.</summary>
    public RecordTableResult Apply(WordprocessingDocument document, string tag, string modelPropertyPath, JToken model) =>
        Apply(document, new CustomTableDataBinding { Tag = tag, ModelPropertyPath = modelPropertyPath }, model);

    /// <summary>Wypełnia tabelę wskazaną w <paramref name="binding"/>.</summary>
    /// <exception cref="InvalidOperationException">Gdy dokument nie zawiera tabeli pasującej do wiązania.</exception>
    public RecordTableResult Apply(WordprocessingDocument document, CustomTableDataBinding binding, JToken model)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(model);

        var scanner = new PlaceholderScanner(_options.TagOpen, _options.TagClose);
        var target = Locator.Find(document, binding, scanner) ?? throw new InvalidOperationException(Locator.Describe(binding));
        return new BindSession(target, binding, model, _options).Run();
    }

    /// <summary>Jak wyżej, ale model to dowolny obiekt .NET (POCO, słownik, typ anonimowy).</summary>
    public RecordTableResult Apply(WordprocessingDocument document, CustomTableDataBinding binding, object model) =>
        Apply(document, binding, ToToken(model));

    /// <summary>Konwersja modelu .NET na <see cref="JToken"/> serializerem Newtonsoft (atrybuty <c>JsonProperty</c> są honorowane).</summary>
    public static JToken ToToken(object model) =>
        model as JToken ?? JToken.FromObject(model, JsonSerializer.CreateDefault());
}
