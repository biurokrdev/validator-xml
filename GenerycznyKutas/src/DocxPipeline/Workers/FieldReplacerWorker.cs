using DocxPipeline.Internal;
using Newtonsoft.Json;

namespace DocxPipeline.Workers;

/// <summary>
/// Podmienia znaczniki <c>&lt;%nazwa%&gt;</c> w całym dokumencie (treść, nagłówki, stopki, tabele, formanty).
/// Jawne mapowania w <see cref="Fields"/>; przy <see cref="AutoBind"/> każdy pozostały znacznik jest wiązany po nazwie
/// z właściwością modelu (<c>&lt;%temat_sprawy%&gt;</c> → <c>model.temat_sprawy</c>). Znaczniki rozbite przez Worda na kilka runów są obsługiwane.
/// </summary>
[DocxWorker("field-replacer", Description = "Podmiana znaczników <%nazwa%> wartościami z modelu (jawne mapowania i/lub po nazwie).")]
public sealed class FieldReplacerWorker : IDocxWorker
{
    /// <summary>Jawne mapowania (jak w workerze „Field replacer”).</summary>
    [JsonProperty("fields")]
    public List<FieldMapping> Fields { get; set; } = new();

    /// <summary>Wiąż pozostałe znaczniki po nazwie właściwości modelu (domyślnie tak).</summary>
    [JsonProperty("auto-bind")]
    public bool AutoBind { get; set; } = true;

    /// <summary>Tekst wstawiany za znaczniki bez wartości; <see langword="null"/> = zostaw znacznik (i zgłoś ostrzeżenie).</summary>
    [JsonProperty("unresolved-text")]
    public string? UnresolvedText { get; set; }

    /// <inheritdoc />
    public void Run(DocxWorkerContext context)
    {
        var paragraphs = context.Paragraphs().ToList();

        foreach (var field in Fields)
        {
            if (string.IsNullOrEmpty(field.SearchFor)) { context.Warn("Pominięto mapowanie bez 'search-for'."); continue; }
            var text = ModelValues.Format(context.Resolve(field.ModelPropertyPath), field.DisplayFormat) ?? field.Text ?? field.NullText ?? string.Empty;
            foreach (var paragraph in paragraphs) ParagraphText.Replace(paragraph, field.SearchFor, text, field.MatchCase);
        }

        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paragraph in paragraphs)
        {
            foreach (var match in ParagraphText.Placeholder.Matches(ParagraphText.GetText(paragraph)).DistinctBy(m => m.Value))
            {
                var value = AutoBind ? context.Resolve(match.Groups[1].Value) : null;
                if (value != null) ParagraphText.Replace(paragraph, match.Value, ModelValues.Format(value) ?? string.Empty);
                else if (UnresolvedText != null) ParagraphText.Replace(paragraph, match.Value, UnresolvedText);
                else unresolved.Add(match.Value);
            }
        }

        foreach (var tag in unresolved) context.Warn($"Znacznik {tag} nie ma wartości w modelu.");
    }
}

/// <summary>Mapowanie znacznika na wartość.</summary>
[JsonObject]
public sealed class FieldMapping
{
    /// <summary>Szukany tekst, zwykle cały znacznik <c>&lt;%x%&gt;</c>.</summary>
    [JsonProperty("search-for")]
    public string SearchFor { get; set; } = string.Empty;

    /// <summary>JSONPath od korzenia modelu.</summary>
    [JsonProperty("model-property-path")]
    public string? ModelPropertyPath { get; set; }

    /// <summary>Stały tekst, gdy nie ma ścieżki.</summary>
    [JsonProperty("text")]
    public string? Text { get; set; }

    /// <summary>Format .NET dla dat i liczb, np. <c>dd-MM-yyyy</c>, <c>N2</c>.</summary>
    [JsonProperty("display-format")]
    public string? DisplayFormat { get; set; }

    /// <summary>Czy wielkość liter ma znaczenie (domyślnie tak).</summary>
    [JsonProperty("match-case")]
    public bool MatchCase { get; set; } = true;

    /// <summary>Tekst dla <see langword="null"/> / brakującej właściwości.</summary>
    [JsonProperty("null-text")]
    public string? NullText { get; set; }
}
