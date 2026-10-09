using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxPipeline.Internal;
using Newtonsoft.Json;

namespace DocxPipeline.Workers;

/// <summary>
/// Wstawia tekst w miejscu zakładki Word: w punkcie zakładki (domyślnie, formatowanie z poprzedzającego runu) albo zastępując
/// cały tekst akapitu z zakładką (<see cref="ReplaceParagraph"/>). Pusta wartość i brak <see cref="NullText"/> = akapit jest usuwany,
/// gdy <see cref="RemoveParagraphWhenEmpty"/>, inaczej nic nie jest wstawiane.
/// </summary>
[DocxWorker("bookmark-text", Description = "Wstawia tekst z modelu w miejscu zakładki albo zastępuje nim akapit z zakładką.")]
public sealed class BookmarkTextWorker : IDocxWorker
{
    /// <summary>Nazwa zakładki.</summary>
    [JsonProperty("bookmark-name")]
    public string BookmarkName { get; set; } = string.Empty;

    /// <summary>JSONPath od korzenia modelu.</summary>
    [JsonProperty("model-property-path")]
    public string? ModelPropertyPath { get; set; }

    /// <summary>Stały tekst, gdy nie ma ścieżki.</summary>
    [JsonProperty("text")]
    public string? Text { get; set; }

    /// <summary>Format .NET dla dat i liczb.</summary>
    [JsonProperty("display-format")]
    public string? DisplayFormat { get; set; }

    /// <summary>Tekst dla pustej wartości.</summary>
    [JsonProperty("null-text")]
    public string? NullText { get; set; }

    /// <summary>Zastąp cały tekst akapitu z zakładką zamiast wstawiać w jej punkcie.</summary>
    [JsonProperty("replace-paragraph")]
    public bool ReplaceParagraph { get; set; }

    /// <summary>Gdy wartość jest pusta (i nie ma <c>null-text</c>), usuń akapit z zakładką.</summary>
    [JsonProperty("remove-paragraph-when-empty")]
    public bool RemoveParagraphWhenEmpty { get; set; }

    /// <inheritdoc />
    public void Run(DocxWorkerContext context)
    {
        if (string.IsNullOrWhiteSpace(BookmarkName)) throw new InvalidOperationException("Brak 'bookmark-name'.");

        var start = context.Roots().SelectMany(r => r.Descendants<BookmarkStart>())
            .FirstOrDefault(b => string.Equals(b.Name?.Value, BookmarkName, StringComparison.OrdinalIgnoreCase));
        if (start == null) { context.Warn($"Zakładka '{BookmarkName}': nie znaleziono w dokumencie."); return; }

        var paragraph = start.Ancestors<Paragraph>().FirstOrDefault();
        var text = (ModelPropertyPath != null ? ModelValues.Format(context.Resolve(ModelPropertyPath), DisplayFormat) : Text) ?? NullText;

        if (text == null)
        {
            if (RemoveParagraphWhenEmpty && paragraph != null && paragraph.Parent is not TableCell { ChildElements.Count: <= 2 })
                paragraph.Remove();
            return;
        }

        if (ReplaceParagraph && paragraph != null)
        {
            foreach (var run in paragraph.Elements<Run>().ToList()) run.Remove();
            paragraph.Append(NewRun(start, text));
            return;
        }

        start.InsertAfterSelf(NewRun(start, text));
    }

    /// <summary>Run z tekstem i formatowaniem skopiowanym z najbliższego runu przed zakładką.</summary>
    private static Run NewRun(BookmarkStart start, string text)
    {
        var run = new Run();
        var sample = start.ElementsBefore().OfType<Run>().LastOrDefault() ?? start.Ancestors<Paragraph>().FirstOrDefault()?.Descendants<Run>().FirstOrDefault();
        if (sample?.RunProperties != null) run.Append(sample.RunProperties.CloneNode(true));
        var t = new Text();
        run.Append(t);
        ParagraphText.SetText(t, text);
        return run;
    }
}
