using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using EfektRuno.Demo;
using EfektRuno.Templating;
using EfektRuno.Templating.Workers;

// Usage:
//   EfektRuno                                   -> builds the demo template, renders both sample JSON files into .\out
//   EfektRuno <template.docx> <data.json> <output.docx>
if (args.Length == 3)
{
    return Render(args[0], args[1], args[2]);
}

string outDir = Path.Combine(Directory.GetCurrentDirectory(), "out");
Directory.CreateDirectory(outDir);

string template = Path.Combine(outDir, "szablon-demo.docx");
DemoTemplateBuilder.Build(template);
Console.WriteLine($"Szablon: {template}");

string demoDir = Path.Combine(AppContext.BaseDirectory, "Demo");
string fullJson = Path.Combine(demoDir, "dane-sprawy.json");

int exitCode = Render(template, fullJson, Path.Combine(outDir, "wynik.docx"));
exitCode |= Render(template, Path.Combine(demoDir, "dane-sprawy-pusta.json"), Path.Combine(outDir, "wynik-pusta.docx"));
exitCode |= RenderWithWorkers(template, fullJson, Path.Combine(outDir, "wynik-workers.docx"), Path.Combine(outDir, "wynik.docx"));
exitCode |= SelfTests.Run();
return exitCode;

/// Path A: façade - whole document against one JSON root.
static int Render(string templatePath, string jsonPath, string outputPath)
{
    JsonNode? data = LoadJson(jsonPath);

    // v1 of the template is never touched - the engine works on a copy.
    byte[] result = new DocxTemplateEngine().Render(File.ReadAllBytes(templatePath), data, out RenderReport report);
    File.WriteAllBytes(outputPath, result);

    Console.WriteLine($"Wynik:   {outputPath}");
    foreach (string placeholder in report.UnresolvedPlaceholders)
        Console.WriteLine($"  [brak danych]      {placeholder}");
    foreach (string fragment in report.SuspiciousFragments)
        Console.WriteLine($"  [błąd w szablonie] {fragment}");

    return Validate(outputPath);
}

/// Path B: the two workers called one by one, the way DocXTableBuilder is called today.
static int RenderWithWorkers(string templatePath, string jsonPath, string outputPath, string referencePath)
{
    JsonNode? data = LoadJson(jsonPath);
    File.Copy(templatePath, outputPath, overwrite: true);

    using (WordprocessingDocument document = WordprocessingDocument.Open(outputPath, true))
    {
        var pipeline = new WorkerPipeline();

        // Worker 1: flat table, columns bound by the tokens in the template row.
        new SimpleTableWorker().Fill(document, "zalaczniki_sprawy", data?["zalaczniki_sprawy"], pipeline);

        // Worker 2: "pieczątka" per message; nested list of files, icon variant and labels are handled by sub-workers.
        new StampWorker().Fill(document, "wiadomosci", data?["wiadomosci"], pipeline);

        // Whatever is left (header, framed boxes, plain values).
        pipeline.Fill(document, data);
    }

    Console.WriteLine($"Wynik:   {outputPath}");
    bool same = DocumentText(outputPath) == DocumentText(referencePath);
    Console.WriteLine($"  Tekst identyczny z wynikiem fasady: {(same ? "tak" : "NIE")}");
    return Validate(outputPath) | (same ? 0 : 1);
}

static JsonNode? LoadJson(string path)
{
    return JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true });
}

static string DocumentText(string path)
{
    using WordprocessingDocument document = WordprocessingDocument.Open(path, false);
    return string.Concat(document.MainDocumentPart!.Document.Descendants<Text>().Select(t => t.Text));
}

static int Validate(string path)
{
    using WordprocessingDocument document = WordprocessingDocument.Open(path, false);
    List<ValidationErrorInfo> errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).ToList();
    foreach (ValidationErrorInfo error in errors.Take(10))
        Console.WriteLine($"  [walidacja] {error.Path?.XPath}: {error.Description}");

    Console.WriteLine($"  Walidacja OpenXML: {errors.Count} błędów");
    return errors.Count == 0 ? 0 : 1;
}
