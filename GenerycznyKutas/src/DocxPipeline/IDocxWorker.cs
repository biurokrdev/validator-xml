namespace DocxPipeline;

/// <summary>
/// Jedna czynność na dokumencie DOCX. Klasa workera jest jednocześnie jego konfiguracją: właściwości z <c>[JsonProperty("kebab-case")]</c>
/// są wypełniane z kroku potoku (JSON), a <see cref="Run"/> dostaje otwarty dokument, model danych i stan wspólny kroków.
/// Wymagany konstruktor bezparametrowy. Worker jest odkrywany refleksją – wystarczy, że jego assembly jest załadowane
/// albo leży w katalogu wtyczek.
/// </summary>
public interface IDocxWorker
{
    /// <summary>Wykonuje czynność. Wyjątek oznacza niepowodzenie kroku (polityka <c>on-error</c> decyduje, czy potok idzie dalej).</summary>
    void Run(DocxWorkerContext context);
}

/// <summary>
/// Nazwa, pod którą worker występuje w konfiguracji (<c>"worker": "field-replacer"</c>). Bez atrybutu nazwa to kebab-case nazwy klasy
/// bez przyrostka <c>Worker</c> (<c>FieldReplacerWorker</c> → <c>field-replacer</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DocxWorkerAttribute(string name) : Attribute
{
    /// <summary>Nazwa w konfiguracji.</summary>
    public string Name { get; } = name;

    /// <summary>Opis do listowania dostępnych workerów (np. w edytorze konfiguracji).</summary>
    public string? Description { get; init; }
}
