// Przykład DocxPipeline: buduje szablon DOCX, wczytuje model i potok z data/, odkrywa workery refleksją
// (wbudowane + wtyczka DocxPipeline.TableTable.dll z katalogu programu) i wykonuje kroki po kolei na jednym dokumencie.
// Użycie: dotnet run --project samples/DocxPipeline.Sample -- [katalog_wyjściowy]

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Office2013.Word;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxPipeline;
using Newtonsoft.Json.Linq;

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "out");
Directory.CreateDirectory(outDir);

// 1. Rejestr: workery wbudowane + wszystko, co leży obok programu jako DocxPipeline.*.dll (wtyczki).
var registry = new WorkerRegistry().AddPluginsFolder(AppContext.BaseDirectory, "DocxPipeline.*.dll");
Console.WriteLine("Dostępne workery:");
foreach (var worker in registry.Workers)
    Console.WriteLine($"  {worker.Name,-26} {worker.Type.Assembly.GetName().Name,-26} {worker.Description}");

// 2. Szablon, model, potok.
var templatePath = Writable(Path.Combine(outDir, "szablon-pismo.docx"));
BuildTemplate(templatePath);
var model = JToken.Parse(File.ReadAllText(Path.Combine(dataDir, "model.json")));
var pipeline = PipelineDefinition.FromJson(File.ReadAllText(Path.Combine(dataDir, "pipeline.json")));

// 3. Wykonanie.
var outputPath = Writable(Path.Combine(outDir, "pismo.docx"));
var result = new PipelineRunner(registry).RunFile(templatePath, outputPath, model, pipeline);

Console.WriteLine();
Console.WriteLine($"Potok '{pipeline.Name}': {(result.Succeeded ? "OK" : "z błędami")}  →  {outputPath}");
foreach (var step in result.Steps)
{
    Console.WriteLine($"  {step.Index}. {step.Worker,-26} {step.Status,-8} {step.Duration.TotalMilliseconds,6:0} ms  {step.Message}");
    foreach (var warning in step.Warnings) Console.WriteLine($"       ! {warning}");
}

using var check = WordprocessingDocument.Open(outputPath, false);
var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(check).ToList();
Console.WriteLine($"Walidacja Open XML: {(errors.Count == 0 ? "OK" : errors.Count + " błędów")}");
foreach (var error in errors.Take(10)) Console.WriteLine($"  {error.Path?.XPath}: {error.Description}");
return;

static string Writable(string path)
{
    if (!File.Exists(path)) return path;
    try { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return path; }
    catch (IOException) { return Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}"); }
}

// ---------------------------------------------------------------------------------------------------------------
// Szablon: pola <%…%>, dwa formanty-klauzule, zakładka „uwagi”, tabela załączników (zakładka) i tabela wiadomości
// (sekcja powtarzana z ikonami w formantach i listą załączników w formancie).
// ---------------------------------------------------------------------------------------------------------------
static void BuildTemplate(string path)
{
    using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
    var main = doc.AddMainDocumentPart();
    main.Document = new Document(new Body());
    main.Document.AddNamespaceDeclaration("mc", "http://schemas.openxmlformats.org/markup-compatibility/2006");
    main.Document.AddNamespaceDeclaration("w15", "http://schemas.microsoft.com/office/word/2012/wordml");
    main.Document.MCAttributes = new MarkupCompatibilityAttributes { Ignorable = "w15" };
    var body = main.Document.Body!;
    AddBulletNumbering(main);

    body.Append(
        Heading("Szczegóły sprawy <%wybrana_sprawa_IWA%>"),
        Plain("Temat: <%temat_sprawy%>"),
        Plain("Data pisma: <%data_pisma%>"),
        Plain("<%opis_sprawy%>"),
        BlockControl("klauzula_firmowa", "Klauzula dla firm", Plain("Klauzula informacyjna dla podmiotów gospodarczych.")),
        BlockControl("klauzula_rodo", "Klauzula RODO", Plain("Klauzula informacyjna RODO dla osób fizycznych.")),
        new Paragraph(new Run(new Text("Uwagi: ") { Space = SpaceProcessingModeValues.Preserve }), new BookmarkStart { Id = "1", Name = "uwagi" }, new BookmarkEnd { Id = "1" }),
        Heading("Załączniki zgłaszającego"),
        new Paragraph(new BookmarkStart { Id = "2", Name = "tabela_zalaczniki" }, new BookmarkEnd { Id = "2" }),
        new Table(
            new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }, Borders()),
            new TableGrid(new GridColumn(), new GridColumn(), new GridColumn(), new GridColumn()),
            Row(Cell(Label("Nazwa załącznika")), Cell(Label("Data dołączenia")), Cell(Label("Użytkownik")), Cell(Label("Archiwizuj"))),
            Row(Cell(Plain("<%nazwa_zalacznika%>")), Cell(Plain("<%data_dolaczenia%>")), Cell(Plain("<%dodajacy%>")), Cell(Plain("<%archiwizuj%>")))),
        Heading("Wiadomości w sprawie"));

    var messages = new Table(
        new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }, Borders()),
        new TableGrid(new GridColumn { Width = "600" }, new GridColumn { Width = "2800" }, new GridColumn { Width = "2800" }, new GridColumn { Width = "2800" }));
    var record = new OpenXmlElement[]
    {
        Row(
            Cell(new Paragraph(RunControl("ikona_wyslana", "↑")), new Paragraph(RunControl("ikona_odebrana", "↓"))),
            Cell(Label("Rodzaj wiadomości"), Plain("<%rodzaj_wiadomosci%>")),
            Cell(Label("Wysłano / Odebrano"), Plain("<%data_czas_wiadomosci%>")),
            Cell(Label("Przez / Od"), Plain("<%nadawca_wiadomosci%>"))),
        Row(Cell(Plain("")), Cell(Label("Treść wiadomości")), SpanCell(2, Plain("<%tresc_wiadomosci%>"))),
        Row(Cell(Plain("")), Cell(Label("Załączniki")), SpanCell(2, BlockControl("nazwy_zalacznikow", "Załączniki wiadomości", Bullet("<%nazwa_pliku%>")))),
    };
    messages.Append(new SdtRow(
        new SdtProperties(new SdtAlias { Val = "Wiadomości" }, new Tag { Val = "tabela_wiadomosci" }, new SdtId { Val = 1 }, new SdtRepeatedSection()),
        new SdtContentRow(new SdtRow(new SdtProperties(new SdtId { Val = 2 }, new SdtRepeatedSectionItem()), new SdtContentRow(record)))));
    body.Append(messages, Plain("Koniec pisma."), new SectionProperties(new PageSize { Width = 11906, Height = 16838 }));
    main.Document.Save();
}

static void AddBulletNumbering(MainDocumentPart main)
{
    var part = main.AddNewPart<NumberingDefinitionsPart>();
    part.Numbering = new Numbering(
        new AbstractNum(new Level(new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "•" }, new LevelJustification { Val = LevelJustificationValues.Left },
            new ParagraphProperties(new Indentation { Left = "360", Hanging = "360" })) { LevelIndex = 0 }) { AbstractNumberId = 0 },
        new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
}

static SdtBlock BlockControl(string tag, string title, params OpenXmlElement[] content) =>
    new(new SdtProperties(new SdtAlias { Val = title }, new Tag { Val = tag }, new SdtId { Val = ++Ids.Next }), new SdtContentBlock(content));
static SdtRun RunControl(string tag, string text) =>
    new(new SdtProperties(new Tag { Val = tag }, new SdtId { Val = ++Ids.Next }), new SdtContentRun(new Run(new Text(text))));
static TableBorders Borders() => new(
    new TopBorder { Val = BorderValues.Single, Size = 4 }, new LeftBorder { Val = BorderValues.Single, Size = 4 },
    new BottomBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 });
static TableRow Row(params TableCell[] cells) => new(cells);
static TableCell Cell(params OpenXmlElement[] content) => new(content);
static TableCell SpanCell(int span, params OpenXmlElement[] content) { var c = new TableCell(new TableCellProperties(new GridSpan { Val = span })); foreach (var e in content) c.Append(e); return c; }
static Paragraph Heading(string text) => new(new ParagraphProperties(new SpacingBetweenLines { Before = "240", After = "120" }), new Run(new RunProperties(new Bold()), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
static Paragraph Plain(string text) => new(new ParagraphProperties(new SpacingBetweenLines { After = "0" }), new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
static Paragraph Label(string text) => new(new ParagraphProperties(new SpacingBetweenLines { After = "0" }), new Run(new RunProperties(new Bold()), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
static Paragraph Bullet(string text) => new(new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })), new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

file static class Ids
{
    public static int Next = 10;
}
