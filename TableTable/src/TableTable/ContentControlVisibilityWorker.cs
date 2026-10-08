using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using TableTable.Internal;

namespace TableTable;

/// <summary>
/// Przełączniki na całym dokumencie (treść, nagłówki, stopki): formant o tagu z mapowania zostaje albo znika zależnie od warunku
/// liczonego od korzenia modelu. Odpowiednik workera <c>DocXContentControlRemover</c> z warunkiem.
/// </summary>
public sealed class ContentControlVisibilityWorker(RecordTableOptions? options = null)
{
    private readonly RecordTableOptions _options = options ?? new RecordTableOptions();

    /// <summary>Stosuje mapowania; zwraca liczbę usuniętych formantów i ostrzeżenia (brak formantu, brak warunku, błędne ścieżki).</summary>
    public (int Removed, IReadOnlyList<string> Warnings) Apply(WordprocessingDocument document, IEnumerable<ContentControlMapping> mappings, JToken model)
    {
        var warnings = new List<string>();
        var resolver = new ValueResolver(model, _options, warnings.Add);
        var scopes = Locator.Roots(document).Cast<DocumentFormat.OpenXml.OpenXmlElement>().ToList();
        var scope = new Scope(model, 1);
        var removed = 0;

        foreach (var mapping in mappings)
        {
            var found = ContentControls.Find(scopes, mapping.TagName);
            if (found.Count == 0) { warnings.Add($"Formant '{mapping.TagName}': nie znaleziono w dokumencie."); continue; }
            var visible = resolver.IsVisible(mapping, scope);
            foreach (var sdt in found.Where(s => ContentControls.IsAttached(s, scopes)))
            {
                if (!visible) { ContentControls.Remove(sdt); removed++; }
                else if (mapping.Unwrap) ContentControls.Unwrap(sdt);
            }
        }

        foreach (var warning in warnings) _options.Logger.LogWarning("TableTable: {Message}", warning);
        return (removed, warnings);
    }
}
