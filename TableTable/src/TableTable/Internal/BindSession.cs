using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace TableTable.Internal;

/// <summary>Jedno wypełnienie jednej tabeli: klonowanie szablonu per rekord, wiązanie pól, list i przełączników.</summary>
internal sealed class BindSession(OpenXmlElement target, CustomTableDataBinding binding, JToken root, RecordTableOptions options)
{
    private readonly PlaceholderScanner _scanner = new(options.TagOpen, options.TagClose);
    private ValueResolver? _resolver;
    private readonly OpenXmlHelpers.DrawingIdFixer _drawingIds = new(target);
    private readonly List<string> _warnings = new();
    private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);

    // Leniwie, bo inicjalizator pola nie może użyć metody instancji Warn.
    private ValueResolver Resolver => _resolver ??= new ValueResolver(root, options, Warn);

    public RecordTableResult Run()
    {
        var template = Locator.FindTemplate(target, _scanner, binding.TemplateRowCount, out var reason);
        if (template == null)
        {
            Warn(reason);
            return Result(0, false);
        }

        var records = Resolver.ResolveCollection(Resolver.Root, binding.ModelPropertyPath, "model-property-path");
        options.Logger.LogInformation("TableTable: tabela '{Tag}' ← {Path}: {Count} rekordów.", binding.Tag ?? binding.BookmarkName, binding.ModelPropertyPath, records.Count);
        if (records.Count == 0)
            return HandleEmpty(template);

        foreach (var name in template.Elements.SelectMany(OpenXmlHelpers.PageAnchoredGraphicNames).Distinct())
            Warn($"Grafika '{name}' jest zakotwiczona względem strony/marginesu – każdy klon rekordu trafi w to samo miejsce. Zakotwicz ją względem akapitu albo wstaw w tekście.");

        // Klony każdego rekordu wstawiane są przed szablonem; szablon (i nadmiarowe elementy sekcji) znika na końcu.
        var index = 0;
        foreach (var record in records)
            Bind(Clone(template.Elements, ++index), new Scope(record, index), binding.Fields, binding.Collections, binding.ContentControls, binding.AutoBind, isTop: true);

        foreach (var element in template.Elements.Concat(template.Extra)) element.Remove();
        return Result(records.Count, false);
    }

    private RecordTableResult HandleEmpty(RecordTemplate template)
    {
        switch (binding.EmptyBehavior)
        {
            case EmptyTableBehavior.KeepTemplate:
                return Result(0, false);
            case EmptyTableBehavior.RemoveTable:
                var removable = target as Table ?? target.Ancestors<Table>().FirstOrDefault() ?? target;
                var parent = removable.Parent;
                removable.Remove();
                OpenXmlHelpers.EnsureCellEndsWithParagraph(parent);
                return Result(0, true);
            default:
                if (binding.EmptyText != null)
                    template.Elements[0].InsertBeforeSelf(EmptyElement(template.Elements[0], binding.EmptyText, keepNumbering: false));
                foreach (var element in template.Elements.Concat(template.Extra)) element.Remove();
                return Result(0, false);
        }
    }

    /// <summary>Wiersz scalony na całą szerokość (gdy szablon to wiersze) albo akapit z tekstem – w miejsce pustej tabeli/listy.</summary>
    private static OpenXmlElement EmptyElement(OpenXmlElement sample, string text, bool keepNumbering) =>
        (sample as TableRow ?? sample.Descendants<TableRow>().FirstOrDefault()) is { } row
            ? OpenXmlHelpers.CreateFullWidthTextRow(row, text)
            : OpenXmlHelpers.CreateTextParagraph(sample as Paragraph ?? sample.Descendants<Paragraph>().FirstOrDefault(), text, keepNumbering);

    /// <summary>Klony szablonu wstawione tuż przed nim (kolejne rekordy trafiają za poprzednie), bez zduplikowanych zakładek i id grafik.</summary>
    private List<OpenXmlElement> Clone(List<OpenXmlElement> template, int index)
    {
        var clones = template.Select(t => t.CloneNode(true)).ToList();
        foreach (var clone in clones)
        {
            template[0].InsertBeforeSelf(clone);
            if (index > 1) OpenXmlHelpers.StripBookmarks(clone);
            _drawingIds.Fix(clone);
        }

        return clones;
    }

    /// <summary>Kolejność: listy (ich szablony muszą jeszcze mieć znaczniki) → pola → przełączniki → konwencja → nierozwiązane.</summary>
    private void Bind(List<OpenXmlElement> units, Scope scope, List<FieldMapping> fields, List<CollectionMapping> collections, List<ContentControlMapping> controls, bool autoBind, bool isTop)
    {
        var explicitTags = collections.Select(c => c.TagName).Concat(controls.Select(c => c.TagName)).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        foreach (var collection in collections)
            ExpandCollection(units, collection, scope);
        if (autoBind)
            foreach (var (_, tag) in TaggedControls(units, explicitTags))
                if (Property(scope, tag) is JArray)
                    ExpandCollection(units, new CollectionMapping { TagName = tag }, scope);

        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field.SearchFor)) { Warn("Pominięto mapowanie bez 'search-for'."); continue; }
            foreach (var paragraph in OpenXmlHelpers.Paragraphs(units).ToList())
                ParagraphText.Replace(paragraph, field.SearchFor, Resolver.FieldText(field, scope), field.MatchCase);
        }

        foreach (var control in controls)
        {
            var found = ContentControls.Find(units, control.TagName);
            if (found.Count == 0) { Warn($"Formant '{control.TagName}': nie znaleziono w szablonie rekordu."); continue; }
            var visible = Resolver.IsVisible(control, scope);
            foreach (var sdt in found.Where(s => ContentControls.IsAttached(s, units)))
            {
                if (!visible) ContentControls.Remove(sdt);
                else if (control.Unwrap) ContentControls.Unwrap(sdt);
            }
        }

        if (autoBind)
        {
            foreach (var (sdt, tag) in TaggedControls(units, explicitTags))
                if (Property(scope, tag) is { } value && value is not JArray && !ValueResolver.IsTruthy(value))
                    ContentControls.Remove(sdt);

            foreach (var paragraph in OpenXmlHelpers.Paragraphs(units).ToList())
                foreach (var (tag, name) in _scanner.Find(ParagraphText.GetText(paragraph)).ToList())
                {
                    var value = Resolver.ResolvePath(name, scope, out var found);
                    if (!found && isTop) value = Resolver.ResolvePath(name, new Scope(Resolver.Root, scope.Index), out found);
                    if (found) ParagraphText.Replace(paragraph, tag, Resolver.Format(value, null) ?? string.Empty, matchCase: true);
                }
        }

        if (!isTop) return;
        foreach (var paragraph in OpenXmlHelpers.Paragraphs(units).ToList())
            foreach (var (tag, _) in _scanner.Find(ParagraphText.GetText(paragraph)).ToList())
            {
                if (_unresolved.Add(tag)) options.Logger.LogWarning("TableTable: znacznik {Tag} nie ma wartości w modelu.", tag);
                if (!binding.KeepUnresolved) ParagraphText.Replace(paragraph, tag, string.Empty, matchCase: true);
            }
    }

    /// <summary>Formanty z tagiem w zasięgu (poza sekcją powtarzaną i poza tagami obsłużonymi jawnie), nadal osadzone w dokumencie.</summary>
    private static IEnumerable<(SdtElement Sdt, string Tag)> TaggedControls(List<OpenXmlElement> units, List<string> explicitTags) =>
        units.SelectMany(u => u.Descendants<SdtElement>()).ToList()
            .Select(sdt => (sdt, tag: ContentControls.TagOf(sdt)))
            .Where(x => !string.IsNullOrWhiteSpace(x.tag) && !ContentControls.IsSectionPart(x.sdt) && ContentControls.IsAttached(x.sdt, units)
                        && !explicitTags.Any(p => ContentControls.Matches(x.tag, p)))
            .Select(x => (x.sdt, x.tag!));

    private static JToken? Property(Scope scope, string name) =>
        scope.Current is JObject o ? o.GetValue(name, StringComparison.OrdinalIgnoreCase) : null;

    /// <summary>Lista: zawartość formantu o tagu klonowana per element; pusta lista → tekst zastępczy albo usunięcie formantu.</summary>
    private void ExpandCollection(List<OpenXmlElement> units, CollectionMapping collection, Scope parentScope)
    {
        if (string.IsNullOrWhiteSpace(collection.TagName)) { Warn("Lista bez 'tag-name' – pominięto."); return; }

        var wrapper = ContentControls.Find(units, collection.TagName).FirstOrDefault(s => ContentControls.IsAttached(s, units));
        var template = wrapper == null ? null : ContentControls.ContentOf(wrapper)?.ChildElements.ToList();
        if (wrapper == null || template == null || template.Count == 0) { Warn($"Lista '{collection.TagName}': brak formantu o tym tagu w szablonie rekordu albo formant jest pusty."); return; }

        var items = Resolver.ResolveCollection(parentScope.Current, string.IsNullOrWhiteSpace(collection.ModelPropertyPath) ? collection.TagName : collection.ModelPropertyPath, $"Lista '{collection.TagName}'");
        var produced = new List<OpenXmlElement>();
        var index = 0;
        foreach (var item in items)
        {
            var clones = Clone(template, ++index);
            Bind(clones, new Scope(item, index), collection.Fields, collection.Collections, collection.ContentControls, collection.AutoBind, isTop: false);
            produced.AddRange(clones);
        }

        if (items.Count == 0 && collection.EmptyText != null)
        {
            produced.Add(EmptyElement(template[0], collection.EmptyText, keepNumbering: true));
            template[0].InsertBeforeSelf(produced[0]);
        }

        foreach (var element in template) element.Remove();

        var unitIndex = units.FindIndex(u => ReferenceEquals(u, wrapper));
        if (produced.Count == 0) ContentControls.Remove(wrapper);
        else if (collection.Unwrap) ContentControls.Unwrap(wrapper);
        else return;

        // Formant zniknął (usunięty albo rozpakowany): jeśli był jednostką zasięgu, zastępują go jego klony.
        if (unitIndex >= 0) { units.RemoveAt(unitIndex); units.InsertRange(unitIndex, produced); }
    }

    private void Warn(string message)
    {
        if (_warnings.Contains(message)) return;
        _warnings.Add(message);
        options.Logger.LogWarning("TableTable: {Message}", message);
    }

    private RecordTableResult Result(int records, bool tableRemoved) => new()
    {
        RecordCount = records,
        TableRemoved = tableRemoved,
        UnresolvedPlaceholders = _unresolved.ToList(),
        Warnings = _warnings.ToList(),
    };
}
