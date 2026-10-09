using System.Globalization;
using Newtonsoft.Json.Linq;

namespace DocxPipeline.Internal;

/// <summary>Wartości z modelu jako tekst i jako warunek.</summary>
internal static class ModelValues
{
    private static readonly HashSet<string> FalseWords = new(StringComparer.OrdinalIgnoreCase) { "false", "0", "nie", "no", "n", "off" };
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary><see langword="null"/> dla wartości pustych (żeby wołający mógł użyć tekstu zastępczego).</summary>
    public static string? Format(JToken? value, string? format = null) => value switch
    {
        null => null,
        JValue { Type: JTokenType.Null } => null,
        JValue { Type: JTokenType.Boolean } b => b.Value<bool>() ? "Tak" : "Nie",
        JValue { Type: JTokenType.Date } d => d.Value<DateTime>().ToString(format ?? "dd.MM.yyyy HH:mm", Culture),
        JValue { Value: IFormattable f } when value.Type != JTokenType.String => f.ToString(format, Culture),
        JValue v => format != null && decimal.TryParse(v.ToString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var n) ? n.ToString(format, Culture) : v.ToString(),
        JArray a => string.Join(", ", a.Select(i => Format(i, format)).Where(s => !string.IsNullOrEmpty(s))),
        _ => value.ToString(Newtonsoft.Json.Formatting.None),
    };

    public static bool IsTruthy(JToken? value) => value switch
    {
        null => false,
        JValue { Type: JTokenType.Null } => false,
        JValue { Type: JTokenType.Boolean } b => b.Value<bool>(),
        JValue { Type: JTokenType.Integer or JTokenType.Float } n => n.Value<double>() != 0,
        JValue v => !string.IsNullOrWhiteSpace(v.ToString()) && !FalseWords.Contains(v.ToString().Trim()),
        JArray a => a.Count > 0,
        _ => true,
    };
}
