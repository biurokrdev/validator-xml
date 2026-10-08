using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TableTable.Internal;

/// <summary>Bieżący zasięg wiązania: rekord albo element listy i jego numer (od 1).</summary>
internal readonly record struct Scope(JToken Current, int Index);

/// <summary>Wartości z modelu (ścieżki JSONPath, wyrażenia, kolekcje, warunki) i ich zamiana na tekst (kultura, strefa, <c>display-format</c>).</summary>
internal sealed class ValueResolver(JToken root, RecordTableOptions options, Action<string> warn)
{
    private static readonly HashSet<string> FalseWords = new(StringComparer.OrdinalIgnoreCase) { "false", "0", "nie", "no", "n", "off" };
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.Compiled);

    private readonly TimeZoneInfo? _zone = FindZone(options.TimeZoneId);

    public JToken Root { get; } = root;

    // ----- odczyt z modelu -------------------------------------------------------------------------------------

    /// <summary>Ścieżka względem zasięgu; <c>$index</c>, <c>$index0</c>, prefiks <c>$root</c>. <paramref name="found"/> = czy coś jest pod ścieżką.</summary>
    public object? ResolvePath(string path, Scope scope, out bool found)
    {
        path = path.Trim();
        found = true;
        if (path.Equals("$index", StringComparison.OrdinalIgnoreCase)) return scope.Index;
        if (path.Equals("$index0", StringComparison.OrdinalIgnoreCase)) return scope.Index - 1;

        var token = path.StartsWith("$root", StringComparison.OrdinalIgnoreCase)
            ? path.Length <= 5 ? Root : Select(Root, path[5..].TrimStart('.'))
            : Select(scope.Current, path);
        found = token != null;
        return token;
    }

    private JToken? Select(JToken token, string path)
    {
        try { return token.SelectToken(path, errorWhenNoMatch: false); }
        catch (JsonException ex) { warn($"Nieprawidłowa ścieżka '{path}': {ex.Message}"); return null; }
    }

    /// <summary>Rekordy spod ścieżki: tablica → elementy, obiekt → jeden rekord, brak/null → pusto.</summary>
    public List<JToken> ResolveCollection(JToken current, string? path, string label)
    {
        var token = string.IsNullOrWhiteSpace(path) ? current : Select(current, path.Trim());
        switch (token)
        {
            case JArray array: return array.ToList();
            case JObject obj: return [obj];
            case null or JValue { Type: JTokenType.Null }: return [];
            default: warn($"{label}: pod ścieżką '{path}' jest wartość prosta, a oczekiwano tablicy."); return [];
        }
    }

    /// <summary>Źródło wartości: wyrażenie, ścieżka albo stały tekst.</summary>
    private object? ResolveSource(string? expression, string? path, string? text, Scope scope, string label)
    {
        if (!string.IsNullOrWhiteSpace(expression))
        {
            if (options.ExpressionEvaluator == null) { warn($"{label}: podano wyrażenie, ale RecordTableOptions.ExpressionEvaluator nie jest ustawiony."); return null; }
            try { return options.ExpressionEvaluator.Evaluate(expression, new ExpressionScope(Root, scope.Current, scope.Index)); }
            catch (Exception ex) { warn($"{label}: wyrażenie '{expression}' zgłosiło {ex.GetType().Name}: {ex.Message}"); return null; }
        }

        if (!string.IsNullOrWhiteSpace(path)) return ResolvePath(path, scope, out _);
        if (text != null) return text;
        warn($"{label}: brak ścieżki, wyrażenia ani stałego tekstu.");
        return null;
    }

    public string FieldText(FieldMapping field, Scope scope) =>
        Format(ResolveSource(field.ReplacementPropertyExpression, field.ReplacementPropertyPath, field.ReplacementText, scope, $"Pole '{field.SearchFor}'"), field.DisplayFormat)
        ?? field.NullText ?? string.Empty;

    /// <summary>Czy formant ma zostać; przy braku warunku zostaje (z ostrzeżeniem).</summary>
    public bool IsVisible(ContentControlMapping mapping, Scope scope)
    {
        var label = $"Formant '{mapping.TagName}'";
        if (string.IsNullOrWhiteSpace(mapping.VisiblePropertyExpression) && string.IsNullOrWhiteSpace(mapping.VisiblePropertyPath))
        {
            warn($"{label}: brak 'visible-property-path' ani 'visible-property-expression' – formant zostaje.");
            return true;
        }

        var value = ResolveSource(mapping.VisiblePropertyExpression, mapping.VisiblePropertyPath, null, scope, label);
        var visible = mapping.VisibleWhenEquals != null
            ? string.Equals(Format(value, null) ?? string.Empty, mapping.VisibleWhenEquals, StringComparison.OrdinalIgnoreCase)
            : IsTruthy(value);
        return mapping.Negate ? !visible : visible;
    }

    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        JValue jv => jv.Type != JTokenType.Null && IsTruthy(jv.Value),
        JArray ja => ja.Count > 0,
        JToken => true,
        bool b => b,
        string s => !string.IsNullOrWhiteSpace(s) && !FalseWords.Contains(s.Trim()),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0,
        _ => true,
    };

    // ----- formatowanie ----------------------------------------------------------------------------------------

    /// <summary><see langword="null"/> tylko dla wartości pustych, żeby wołający mógł użyć <c>null-text</c>.</summary>
    public string? Format(object? value, string? format) => value switch
    {
        null => null,
        JValue jv => jv.Type == JTokenType.Null ? null : Format(jv.Value, format),
        JArray ja => Join(ja, format),
        JToken jt => jt.ToString(Formatting.None),
        string s => FormatString(s, format),
        bool b => b ? options.TrueText : options.FalseText,
        DateTime dt => FormatDate(ToZone(dt), format),
        DateTimeOffset dto => FormatDate(_zone != null ? TimeZoneInfo.ConvertTime(dto, _zone).DateTime : dto.DateTime, format),
        IFormattable f => f.ToString(format, options.Culture),
        IEnumerable items => Join(items, format),
        _ => value.ToString(),
    };

    private string Join(IEnumerable items, string? format) =>
        string.Join(", ", items.Cast<object?>().Select(i => Format(i, format)).Where(s => !string.IsNullOrEmpty(s)));

    private string FormatString(string value, string? format)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        // Data ISO zapisana jako tekst (Newtonsoft zamienia na DateTime tylko pełne „yyyy-MM-ddTHH:mm:ss”) – prezentowana jak DateTime.
        if (IsoDate.IsMatch(value) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso))
            return FormatDate(ToZone(iso), format);
        // Jak formatter workerów: tekst wyglądający na liczbę formatowany jest po sparsowaniu, gdy podano format.
        if (format != null && decimal.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number))
            return number.ToString(format, options.Culture);
        return value;
    }

    private string FormatDate(DateTime value, string? format) =>
        value.ToString(format ?? (value.TimeOfDay == TimeSpan.Zero ? options.DateFormat : options.DateTimeFormat), options.Culture);

    /// <summary>Odpowiednik <c>InterpretDateTimeInPolishTimezone</c> z workera: UTC i Local do strefy docelowej, Unspecified bez zmian.</summary>
    private DateTime ToZone(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc when _zone != null => TimeZoneInfo.ConvertTimeFromUtc(value, _zone),
        DateTimeKind.Local when _zone != null => TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local, _zone),
        _ => value,
    };

    private static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var candidate in new[] { id, "Europe/Warsaw" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return null;
    }
}
