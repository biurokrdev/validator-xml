using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace DocxPipeline;

/// <summary>Potok: uporządkowana lista kroków, każdy wskazuje worker po nazwie i niesie jego konfigurację (płasko, jak w edytorze workerów).</summary>
[JsonObject]
public sealed class PipelineDefinition
{
    /// <summary>Nazwa potoku (do logów).</summary>
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>Kroki w kolejności wykonania.</summary>
    [JsonProperty("steps")]
    public List<PipelineStep> Steps { get; set; } = new();

    /// <summary>Wczytuje potok z JSON (dopuszczalne komentarze <c>//</c>).</summary>
    public static PipelineDefinition FromJson(string json) =>
        JsonConvert.DeserializeObject<PipelineDefinition>(json) ?? throw new JsonException("Pusta definicja potoku.");
}

/// <summary>
/// Krok potoku. Klucze <c>worker</c>, <c>enabled</c>, <c>when</c>, <c>unless</c>, <c>on-error</c> należą do potoku;
/// wszystkie pozostałe klucze obiektu trafiają do konfiguracji workera.
/// </summary>
[JsonObject]
public sealed class PipelineStep
{
    /// <summary>Nazwa workera z rejestru, np. <c>field-replacer</c>.</summary>
    [JsonProperty("worker")]
    public string Worker { get; set; } = string.Empty;

    /// <summary>Wyłączony krok jest pomijany bez błędu.</summary>
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>JSONPath w modelu; krok wykonuje się tylko, gdy wartość jest „prawdziwa” (true, liczba ≠ 0, niepusty tekst, niepusta tablica).</summary>
    [JsonProperty("when")]
    public string? When { get; set; }

    /// <summary>JSONPath w modelu; krok jest pomijany, gdy wartość jest „prawdziwa”.</summary>
    [JsonProperty("unless")]
    public string? Unless { get; set; }

    /// <summary>Co zrobić po wyjątku workera: <c>stop</c> (domyślnie) albo <c>continue</c>.</summary>
    [JsonProperty("on-error")]
    [JsonConverter(typeof(StringEnumConverter))]
    public StepErrorPolicy OnError { get; set; } = StepErrorPolicy.Stop;

    /// <summary>Konfiguracja workera: wszystkie klucze kroku poza powyższymi.</summary>
    [JsonExtensionData]
    public IDictionary<string, JToken> Configuration { get; set; } = new Dictionary<string, JToken>();

    /// <summary>Konfiguracja workera jako obiekt JSON.</summary>
    public JObject ConfigurationObject() => new(Configuration.Select(kv => new JProperty(kv.Key, kv.Value)));
}

/// <summary>Polityka błędów kroku.</summary>
public enum StepErrorPolicy
{
    /// <summary>Przerwij potok; kolejne kroki nie są wykonywane.</summary>
    [EnumMember(Value = "stop")]
    Stop,

    /// <summary>Zaloguj i wykonaj kolejne kroki.</summary>
    [EnumMember(Value = "continue")]
    Continue,
}
