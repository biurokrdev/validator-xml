using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace EfektRuno.Templating;

public sealed class DocxTemplateOptions
{
    /// <summary>Template name -> JSON path, e.g. ["rodzaj_wiadomosci"] = "messageType". Lets old templates stay untouched.</summary>
    public IDictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>Dates carrying an offset ("Z", "+02:00") are converted to this zone before formatting.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public string TrueText { get; set; } = "Tak";

    public string FalseText { get; set; } = "Nie";

    public string MapPath(string path)
    {
        return Aliases.TryGetValue(path, out string? mapped) ? mapped : path;
    }
}

/// <summary>JSON value -> text in a run. Shared by every worker.</summary>
public sealed class ValueFormatter(DocxTemplateOptions options)
{
    public string Format(JsonNode? value, string? format)
    {
        if (value is null)
            return string.Empty;

        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
                return options.TrueText;
            case JsonValueKind.False:
                return options.FalseText;
            case JsonValueKind.Number:
                return ToDecimal(value).ToString(format, options.Culture);
            case JsonValueKind.String:
                string text = value.GetValue<string>();
                if (format != null && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTime date))
                {
                    if (date.Kind != DateTimeKind.Unspecified)
                        date = TimeZoneInfo.ConvertTime(date, options.TimeZone);
                    return date.ToString(format, options.Culture);
                }

                return text;
            default:
                return value.ToJsonString();
        }
    }

    public static bool IsTruthy(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return false;
            case JsonArray array:
                return array.Count > 0;
            case JsonObject:
                return true;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => ToDecimal(value) != 0,
            _ => true
        };
    }

    /// <summary>Parsed JSON is JsonElement-backed, JSON built in code holds CLR primitives (int, long, double...).</summary>
    private static decimal ToDecimal(JsonNode value)
    {
        return value.AsValue().TryGetValue(out decimal number)
            ? number
            : Convert.ToDecimal(value.GetValue<object>(), CultureInfo.InvariantCulture);
    }

    public static void SetRunText(Run run, string value)
    {
        run.RemoveAllChildren<Text>();

        string[] lines = Sanitize(value).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                run.AppendChild(new Break());
            run.AppendChild(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
    }

    /// <summary>Control characters are illegal in XML 1.0 and would make the save throw.</summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c >= ' ' || c is '\t' or '\n' or '\r')
                builder.Append(c);
        }

        return builder.ToString();
    }
}
