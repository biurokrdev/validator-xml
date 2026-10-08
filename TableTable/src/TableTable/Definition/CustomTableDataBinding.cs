using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace TableTable;

/// <summary>
/// Wiązanie jednej tabeli dokumentu z danymi. Minimum to <see cref="Tag"/> (formant w szablonie) i <see cref="ModelPropertyPath"/>
/// (rekordy w modelu) – resztę wyznacza szablon: znacznik <c>&lt;%x%&gt;</c> → <c>rekord.x</c>, formant z tagiem <c>t</c> bez znaczników
/// → przełącznik <c>rekord.t</c>, formant z tagiem <c>l</c> ze znacznikami → lista <c>rekord.l</c>. Pozostałe pola nadpisują tę konwencję.
/// Nazwy JSON jak w konfiguracji workerów (kebab-case).
/// </summary>
[JsonObject]
public sealed class CustomTableDataBinding
{
    /// <summary>Tag formantu wskazującego tabelę: sekcja powtarzana (Deweloper → Sekcja powtarzana) na wierszach rekordu albo formant owijający tabelę.</summary>
    [JsonProperty("tag")]
    public string? Tag { get; set; }

    /// <summary>Zamiast tagu: zakładka Word wewnątrz tabeli albo bezpośrednio przed nią. Bez tagu i zakładki brana jest pierwsza tabela ze znacznikami.</summary>
    [JsonProperty("bookmark-name")]
    public string? BookmarkName { get; set; }

    /// <summary>JSONPath (Newtonsoft <c>SelectToken</c>) do tablicy rekordów od korzenia modelu, np. <c>sprawa.korespondencja</c>. Puste = model jest tablicą.</summary>
    [JsonProperty("model-property-path")]
    public string? ModelPropertyPath { get; set; }

    /// <summary>Nadpisania znaczników rekordu (ścieżki względem rekordu).</summary>
    [JsonProperty("fields")]
    public List<FieldMapping> Fields { get; set; } = new();

    /// <summary>Nadpisania list (formantów ze znacznikami): format elementów, tekst dla pustej listy.</summary>
    [JsonProperty("collections")]
    public List<CollectionMapping> Collections { get; set; } = new();

    /// <summary>Nadpisania przełączników (formantów): inny warunek niż „właściwość o nazwie tagu”.</summary>
    [JsonProperty("content-controls")]
    public List<ContentControlMapping> ContentControls { get; set; } = new();

    /// <summary>Konwencja dla elementów bez nadpisania (domyślnie włączona). Działają też <c>&lt;%$index%&gt;</c> (numer od 1) i prefiks <c>$root.</c>.</summary>
    [JsonProperty("auto-bind")]
    public bool AutoBind { get; set; } = true;

    /// <summary>W zwykłej tabeli (bez sekcji powtarzanej) liczba wierszy rekordu od pierwszego wiersza ze znacznikiem; domyślnie do ostatniego wiersza ze znacznikiem.</summary>
    [JsonProperty("template-row-count")]
    public int? TemplateRowCount { get; set; }

    /// <summary>Co zrobić, gdy nie ma rekordów.</summary>
    [JsonProperty("empty-behavior")]
    [JsonConverter(typeof(StringEnumConverter))]
    public EmptyTableBehavior EmptyBehavior { get; set; } = EmptyTableBehavior.RemoveTemplateRows;

    /// <summary>Przy braku rekordów: jeden wiersz scalony na całą szerokość z tym tekstem (tylko z <see cref="EmptyTableBehavior.RemoveTemplateRows"/>).</summary>
    [JsonProperty("empty-text")]
    public string? EmptyText { get; set; }

    /// <summary>Zostaw w dokumencie znaczniki bez wartości (domyślnie są usuwane); zawsze trafiają do <see cref="RecordTableResult.UnresolvedPlaceholders"/>.</summary>
    [JsonProperty("keep-unresolved")]
    public bool KeepUnresolved { get; set; }

    /// <summary>Wczytuje wiązanie z JSON (dopuszczalne komentarze <c>//</c>).</summary>
    public static CustomTableDataBinding FromJson(string json) =>
        JsonConvert.DeserializeObject<CustomTableDataBinding>(json) ?? throw new JsonException("Pusta definicja tabeli.");

    /// <summary>JSON z pominięciem wartości pustych.</summary>
    public string ToJson() =>
        JsonConvert.SerializeObject(this, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
}

/// <summary>Znacznik → wartość (odpowiednik workera „Field replacer” w zasięgu rekordu).</summary>
[JsonObject]
public sealed class FieldMapping
{
    /// <summary>Szukany tekst, zwykle cały znacznik <c>&lt;%x%&gt;</c>; dopasowanie literalne, także gdy Word rozbił go na kilka runów.</summary>
    [JsonProperty("search-for")]
    public string SearchFor { get; set; } = string.Empty;

    /// <summary>JSONPath względem rekordu; specjalne: <c>$index</c>, <c>$index0</c>, prefiks <c>$root.</c>.</summary>
    [JsonProperty("replacement-property-path")]
    public string? ReplacementPropertyPath { get; set; }

    /// <summary>Wyrażenie dla <see cref="RecordTableOptions.ExpressionEvaluator"/> (silnik workerów); ma pierwszeństwo przed ścieżką.</summary>
    [JsonProperty("replacement-property-expression")]
    public string? ReplacementPropertyExpression { get; set; }

    /// <summary>Stały tekst, gdy nie ma ścieżki ani wyrażenia.</summary>
    [JsonProperty("replacement-text")]
    public string? ReplacementText { get; set; }

    /// <summary>Format .NET: daty np. <c>dd-MM-yyyy HH:mm</c>, liczby <c>N0</c>, <c>N2</c>, <c>C</c>.</summary>
    [JsonProperty("display-format")]
    public string? DisplayFormat { get; set; }

    /// <summary>Czy wielkość liter w <see cref="SearchFor"/> ma znaczenie (domyślnie tak).</summary>
    [JsonProperty("match-case")]
    public bool MatchCase { get; set; } = true;

    /// <summary>Tekst dla <see langword="null"/> lub brakującej właściwości; domyślnie pusty.</summary>
    [JsonProperty("null-text")]
    public string? NullText { get; set; }
}

/// <summary>Lista: zawartość formantu o tagu <see cref="TagName"/> jest powtarzana per element tablicy; przy pustej liście formant znika.</summary>
[JsonObject]
public sealed class CollectionMapping
{
    /// <summary>Tag formantu owijającego szablon elementu (akapit z punktorem, wiersze, dowolny fragment).</summary>
    [JsonProperty("tag-name")]
    public string TagName { get; set; } = string.Empty;

    /// <summary>JSONPath do tablicy względem rekordu; puste = równa tagowi.</summary>
    [JsonProperty("model-property-path")]
    public string? ModelPropertyPath { get; set; }

    /// <summary>Nadpisania znaczników elementu (ścieżki względem elementu).</summary>
    [JsonProperty("fields")]
    public List<FieldMapping> Fields { get; set; } = new();

    /// <summary>Listy zagnieżdżone głębiej.</summary>
    [JsonProperty("collections")]
    public List<CollectionMapping> Collections { get; set; } = new();

    /// <summary>Przełączniki wewnątrz elementu.</summary>
    [JsonProperty("content-controls")]
    public List<ContentControlMapping> ContentControls { get; set; } = new();

    /// <summary>Konwencja dla elementów bez nadpisania.</summary>
    [JsonProperty("auto-bind")]
    public bool AutoBind { get; set; } = true;

    /// <summary>Tekst zamiast szablonu przy pustej liście (np. „brak załączników”); puste = formant znika.</summary>
    [JsonProperty("empty-text")]
    public string? EmptyText { get; set; }

    /// <summary>Po wypełnieniu zdejmij ramkę formantu.</summary>
    [JsonProperty("unwrap")]
    public bool Unwrap { get; set; }
}

/// <summary>Przełącznik: formant o tagu <see cref="TagName"/> zostaje albo znika z zawartością (formant komórki: komórka jest opróżniana).</summary>
[JsonObject]
public sealed class ContentControlMapping
{
    /// <summary>Tag formantu (lub jego tytuł); bez rozróżniania wielkości liter, <c>*</c> jako symbol wieloznaczny.</summary>
    [JsonProperty("tag-name")]
    public string TagName { get; set; } = string.Empty;

    /// <summary>JSONPath do wartości warunku (względem rekordu; w workerze dokumentowym od korzenia).</summary>
    [JsonProperty("visible-property-path")]
    public string? VisiblePropertyPath { get; set; }

    /// <summary>Wyrażenie dla <see cref="RecordTableOptions.ExpressionEvaluator"/>; ma pierwszeństwo przed ścieżką.</summary>
    [JsonProperty("visible-property-expression")]
    public string? VisiblePropertyExpression { get; set; }

    /// <summary>Widoczny, gdy wartość jako tekst równa się temu ciągowi; bez tego wartość jest logiczna (<see langword="true"/>, liczba ≠ 0, niepusty tekst inny niż false/0/nie/no, niepusta tablica).</summary>
    [JsonProperty("visible-when-equals")]
    public string? VisibleWhenEquals { get; set; }

    /// <summary>Widoczny, gdy wartość jako tekst pasuje do wyrażenia regularnego (bez rozróżniania wielkości liter), np. <c>^Wysłana</c>.</summary>
    [JsonProperty("visible-when-matches")]
    public string? VisibleWhenMatches { get; set; }

    /// <summary>Odwraca warunek.</summary>
    [JsonProperty("negate")]
    public bool Negate { get; set; }

    /// <summary>Gdy formant zostaje, zdejmij jego ramkę.</summary>
    [JsonProperty("unwrap")]
    public bool Unwrap { get; set; }
}

/// <summary>Zachowanie przy braku rekordów.</summary>
public enum EmptyTableBehavior
{
    /// <summary>Usuń wiersze szablonu (z <c>empty-text</c>: wstaw w ich miejsce jeden wiersz z tekstem).</summary>
    [EnumMember(Value = "remove-template-rows")]
    RemoveTemplateRows,

    /// <summary>Usuń całą tabelę.</summary>
    [EnumMember(Value = "remove-table")]
    RemoveTable,

    /// <summary>Zostaw tabelę i znaczniki bez zmian.</summary>
    [EnumMember(Value = "keep-template")]
    KeepTemplate,
}
