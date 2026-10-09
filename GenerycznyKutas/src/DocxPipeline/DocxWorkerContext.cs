using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace DocxPipeline;

/// <summary>Wspólny stan jednego przebiegu potoku: otwarty dokument, model danych, logger, ostrzeżenia kroku i słownik na dane między krokami.</summary>
public sealed class DocxWorkerContext
{
    private readonly List<string> _warnings = new();

    /// <summary>Tworzy kontekst dla dokumentu i modelu.</summary>
    public DocxWorkerContext(WordprocessingDocument document, JToken model, ILogger? logger = null)
    {
        Document = document;
        Model = model;
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Dokument otwarty do edycji; wszystkie kroki pracują na tym samym obiekcie.</summary>
    public WordprocessingDocument Document { get; }

    /// <summary>Model danych (np. JModel aplikacji); ścieżki w konfiguracjach workerów to JSONPath od jego korzenia.</summary>
    public JToken Model { get; }

    /// <summary>Logger.</summary>
    public ILogger Logger { get; }

    /// <summary>Dane przekazywane między krokami (np. worker A liczy coś, worker B z tego korzysta).</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ostrzeżenia bieżącego kroku (runner zbiera je do wyniku i czyści przed kolejnym krokiem).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Dodaje ostrzeżenie (nie przerywa kroku) i loguje je.</summary>
    public void Warn(string message)
    {
        _warnings.Add(message);
        Logger.LogWarning("{Message}", message);
    }

    internal void ClearWarnings() => _warnings.Clear();

    /// <summary>Korzenie części dokumentu z treścią: body, nagłówki, stopki.</summary>
    public IEnumerable<OpenXmlPartRootElement> Roots()
    {
        var main = Document.MainDocumentPart ?? throw new InvalidOperationException("Dokument nie zawiera MainDocumentPart.");
        if (main.Document != null) yield return main.Document;
        foreach (var header in main.HeaderParts) if (header.Header != null) yield return header.Header;
        foreach (var footer in main.FooterParts) if (footer.Footer != null) yield return footer.Footer;
    }

    /// <summary>Wszystkie akapity dokumentu (treść, nagłówki, stopki), w tym w tabelach i formantach.</summary>
    public IEnumerable<Paragraph> Paragraphs() => Roots().SelectMany(r => r.Descendants<Paragraph>());

    /// <summary>Wartość spod ścieżki JSONPath modelu albo <see langword="null"/>, gdy jej nie ma (zła ścieżka → ostrzeżenie).</summary>
    public JToken? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Model.SelectToken(path.Trim(), errorWhenNoMatch: false); }
        catch (Newtonsoft.Json.JsonException ex) { Warn($"Nieprawidłowa ścieżka '{path}': {ex.Message}"); return null; }
    }
}
