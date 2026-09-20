using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using EfektRuno.Templating.Workers;

namespace EfektRuno.Demo;

/// <summary>Minimal in-memory checks for paths the demo template does not exercise. To be moved to NUnit.</summary>
internal static class SelfTests
{
    public static int Run()
    {
        int failures = 0;
        failures += Check("SimpleTableWorker: kolumny pozycyjne bez tokenów w komórkach", PositionalColumns);
        failures += Check("SimpleTableWorker: pusta lista usuwa tabelę (RemoveTableWhenEmpty)", EmptyListRemovesTable);
        failures += Check("StampWorker: pod-worker zarejestrowany po nazwie", NamedSubWorker);
        return failures;
    }

    private static void PositionalColumns()
    {
        using MemoryStream stream = TableDocument(cells: ["<%#lista%>", "", "<%/lista%>"]);
        using (WordprocessingDocument document = WordprocessingDocument.Open(stream, true))
        {
            var rows = new JsonArray(
                new JsonObject { ["a"] = "A1", ["b"] = "B1", ["c"] = 1 },
                new JsonObject { ["a"] = "A2", ["b"] = "B2", ["c"] = 2 });

            new SimpleTableWorker { Columns = ["a", "b", "c"] }.Fill(document, "lista", rows);
        }

        Expect(CellTexts(stream), "A1|B1|1;A2|B2|2");
    }

    private static void EmptyListRemovesTable()
    {
        using MemoryStream stream = TableDocument(cells: ["<%#lista%><%a%>", "<%b%>", "<%c%><%/lista%>"]);
        using (WordprocessingDocument document = WordprocessingDocument.Open(stream, true))
            new SimpleTableWorker { RemoveTableWhenEmpty = true }.Fill(document, "lista", new JsonArray());

        using WordprocessingDocument check = WordprocessingDocument.Open(stream, false);
        Expect(check.MainDocumentPart!.Document.Descendants<Table>().Count().ToString(), "0");
    }

    private static void NamedSubWorker()
    {
        using MemoryStream stream = ParagraphDocument("<%#pieczatki%>", "<%#podpis%><%.%><%/podpis%>", "<%/pieczatki%>");
        using (WordprocessingDocument document = WordprocessingDocument.Open(stream, true))
        {
            var stamp = new StampWorker();
            stamp.SubWorkers["podpis"] = new UpperCaseWorker();

            var data = new JsonArray(
                new JsonObject { ["podpis"] = "anna" },
                new JsonObject { ["podpis"] = "jan" });
            stamp.Fill(document, "pieczatki", data);
        }

        using WordprocessingDocument check = WordprocessingDocument.Open(stream, false);
        Expect(string.Join("|", check.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Select(p => p.InnerText)), "ANNA|JAN");
    }

    /// <summary>Example of a custom sub-worker: renders the section once with the value upper-cased.</summary>
    private sealed class UpperCaseWorker : IDocxWorker
    {
        public void Fill(Templating.Section section, Templating.DataContext context, WorkerPipeline pipeline)
        {
            context.TryResolve(section.Name, out JsonNode? value);
            var upper = new Templating.DataContext(JsonValue.Create(value?.ToString().ToUpperInvariant()), context);
            section.Emit(upper, pipeline, stripIds: false);
            section.RemoveTemplate();
        }
    }

    private static MemoryStream TableDocument(string[] cells)
    {
        var stream = new MemoryStream();
        using (WordprocessingDocument document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            MainDocumentPart main = document.AddMainDocumentPart();
            var row = new TableRow(cells.Select(c => new TableCell(new Paragraph(new Run(new Text(c))))));
            main.Document = new Document(new Body(new Table(row), new Paragraph()));
        }

        return stream;
    }

    private static MemoryStream ParagraphDocument(params string[] paragraphs)
    {
        var stream = new MemoryStream();
        using (WordprocessingDocument document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            MainDocumentPart main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(paragraphs.Select(p => new Paragraph(new Run(new Text(p))))));
        }

        return stream;
    }

    private static string CellTexts(MemoryStream stream)
    {
        using WordprocessingDocument document = WordprocessingDocument.Open(stream, false);
        return string.Join(";", document.MainDocumentPart!.Document.Descendants<TableRow>()
            .Select(r => string.Join("|", r.Elements<TableCell>().Select(c => c.InnerText))));
    }

    private static void Expect(string actual, string expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"oczekiwano '{expected}', jest '{actual}'");
    }

    private static int Check(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"  [test] OK   {name}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  [test] FAIL {name}: {exception.Message}");
            return 1;
        }
    }
}
