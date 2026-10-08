using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TableTable.Internal;

namespace TableTable;

/// <summary>
/// Inspektor szablonu: opisuje, jakich rekordów oczekuje każda tabela – pola (znaczniki), przełączniki (formanty bez znaczników)
/// i listy (formanty ze znacznikami) – i generuje szkielet JSON do wypełnienia. Struktura szablonu jest schematem danych.
/// </summary>
public static class TemplateSchema
{
    /// <summary>Opisuje wszystkie tabele dokumentu (treść, nagłówki, stopki).</summary>
    public static DocumentSchema Describe(WordprocessingDocument document, RecordTableOptions? options = null)
    {
        options ??= new RecordTableOptions();
        var scanner = new PlaceholderScanner(options.TagOpen, options.TagClose);
        var tables = new List<TableSchema>();
        foreach (var target in Locator.Discover(document, scanner))
        {
            var table = new TableSchema { Tag = target.Tag, Index = tables.Count, Kind = ContentControls.FindRepeatingSection(target.Element) != null ? "repeating-section" : "rows" };
            var template = Locator.FindTemplate(target.Element, scanner, null, out _);
            if (template != null) Describe(template.Elements, table, scanner);
            tables.Add(table);
        }

        return new DocumentSchema { Tables = tables };
    }

    /// <summary>Opisuje szablon z pliku.</summary>
    public static DocumentSchema Describe(string templatePath, RecordTableOptions? options = null)
    {
        using var document = WordprocessingDocument.Open(templatePath, isEditable: false);
        return Describe(document, options);
    }

    private static void Describe(IEnumerable<OpenXmlElement> elements, ScopeSchema schema, PlaceholderScanner scanner)
    {
        var units = elements.ToList();
        bool InUnits(OpenXmlElement e) => ContentControls.IsAttached(e, units);

        // Formanty z tagiem bezpośrednio w tym zasięgu (nie wewnątrz innego formantu z tagiem należącego do zasięgu).
        var controls = units.SelectMany(u => u.Descendants<SdtElement>())
            .Where(s => !string.IsNullOrWhiteSpace(ContentControls.TagOf(s)) && !ContentControls.IsSectionPart(s))
            .Where(s => !s.Ancestors<SdtElement>().Any(a => !string.IsNullOrWhiteSpace(ContentControls.TagOf(a)) && !ContentControls.IsSectionPart(a) && InUnits(a)))
            .ToList();

        foreach (var control in controls)
        {
            var tag = ContentControls.TagOf(control)!;
            var content = ContentControls.ContentOf(control);
            if (content != null && scanner.ContainsTag(content.InnerText))
            {
                var list = new CollectionSchema { Tag = tag };
                Describe(content.ChildElements, list, scanner);
                schema.Collections.Add(list);
            }
            else if (!schema.Flags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                schema.Flags.Add(tag);
            }
        }

        var insideControl = controls.Cast<OpenXmlElement>().ToList();
        foreach (var paragraph in OpenXmlHelpers.Paragraphs(units).Where(p => !ContentControls.IsAttached(p, insideControl)))
            foreach (var (_, name) in scanner.Find(ParagraphText.GetText(paragraph)))
                if (!name.StartsWith('$') && !schema.Fields.Contains(name, StringComparer.OrdinalIgnoreCase)) schema.Fields.Add(name);
    }
}

/// <summary>Opis danych oczekiwanych przez dokument.</summary>
public sealed class DocumentSchema
{
    /// <summary>Tabele w kolejności występowania.</summary>
    public IReadOnlyList<TableSchema> Tables { get; init; } = [];

    /// <summary>Szkielet modelu: klucz = tag tabeli (lub <c>table[i]</c>), wartość = lista z jednym przykładowym rekordem (pola puste, przełączniki <see langword="true"/>, listy z jednym elementem).</summary>
    public JObject ToSkeleton()
    {
        var root = new JObject();
        foreach (var table in Tables) root[table.Tag ?? $"table[{table.Index}]"] = new JArray(table.ToSkeletonRecord());
        return root;
    }

    /// <summary>Szkielet jako JSON z wcięciami.</summary>
    public string ToJson() => ToSkeleton().ToString(Formatting.Indented);
}

/// <summary>Elementy jednego zasięgu szablonu (rekordu albo elementu listy).</summary>
public class ScopeSchema
{
    /// <summary>Nazwy znaczników <c>&lt;%nazwa%&gt;</c>.</summary>
    public List<string> Fields { get; } = new();

    /// <summary>Tagi formantów bez znaczników – przełączniki pokaż/ukryj.</summary>
    public List<string> Flags { get; } = new();

    /// <summary>Formanty ze znacznikami – listy powtarzane per element.</summary>
    public List<CollectionSchema> Collections { get; } = new();

    /// <summary>Przykładowy rekord tego zasięgu.</summary>
    public JObject ToSkeletonRecord()
    {
        var record = new JObject();
        foreach (var f in Fields) record[f] = string.Empty;
        foreach (var f in Flags) record[f] = true;
        foreach (var c in Collections) record[c.Tag] = new JArray(c.ToSkeletonRecord());
        return record;
    }
}

/// <summary>Tabela dokumentu.</summary>
public sealed class TableSchema : ScopeSchema
{
    /// <summary>Tag formantu wskazującego tabelę; <see langword="null"/> dla tabeli bez tagu.</summary>
    public string? Tag { get; init; }

    /// <summary>Pozycja wśród wykrytych tabel (od 0).</summary>
    public int Index { get; init; }

    /// <summary><c>repeating-section</c> (sekcja powtarzana Worda) albo <c>rows</c> (wiersze wykrywane po znacznikach).</summary>
    public string Kind { get; init; } = "rows";
}

/// <summary>Lista (formant ze znacznikami).</summary>
public sealed class CollectionSchema : ScopeSchema
{
    /// <summary>Tag formantu = nazwa właściwości rekordu z tablicą elementów.</summary>
    public string Tag { get; init; } = string.Empty;
}
