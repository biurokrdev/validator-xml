// Generator przykładów TableTable.
// Jeden szablon DOCX z dwiema tabelami oznaczonymi tagami formantów (budowany w kodzie, żeby przykład nie zależał od
// plików binarnych). Dane i definicje wczytywane są z katalogu data/. Każda tabela jest wiązana osobnym wywołaniem
// RecordTableBuilder.Apply – tak jak zrobi to worker w aplikacji dla N tabel. Przebiegi:
//   A. definicja minimalna – per tabela tylko tag i ścieżka do rekordów; pola, przełączniki i listy wynikają z szablonu,
//   B. definicja z nadpisaniami – te same dane, dodatkowo formaty dat/liczb i teksty zastępcze,
//   C. puste listy – obie tabele bez rekordów.
// Dodatkowo inspektor szablonu zapisuje szkielet danych (schemat.json), jakiego oczekuje ten szablon.
// Użycie: dotnet run --project samples/TableTable.Sample -- [katalog_wyjściowy]   (domyślnie ./out)

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Office2013.Word;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json.Linq;
using TableTable;
using TableTable.Sample;

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "out");
Directory.CreateDirectory(outDir);

// 1. Szablon
var templatePath = Writable(Path.Combine(outDir, "szablon-dokument.docx"));
BuildDocumentTemplate(templatePath);
Console.WriteLine($"Szablon: {templatePath}");

// 2. Inspektor szablonu: jakich danych oczekują tabele tego dokumentu
var schema = TemplateSchema.Describe(templatePath);
File.WriteAllText(Path.Combine(outDir, "schemat.json"), schema.ToJson());
Console.WriteLine();
Console.WriteLine("Schemat danych odczytany z szablonu (out/schemat.json):");
foreach (var table in schema.Tables)
{
    Console.WriteLine($"  tabela '{table.Tag}' ({table.Kind}): pola [{string.Join(", ", table.Fields)}], przełączniki [{string.Join(", ", table.Flags)}]");
    foreach (var list in table.Collections)
        Console.WriteLine($"    lista '{list.Tag}': pola [{string.Join(", ", list.Fields)}]");
}

// 3. Wiązanie – po jednej tabeli, tak jak zrobi to worker w aplikacji: N tabel = N wywołań z innym tagiem i ścieżką.
var builder = new RecordTableBuilder();
var model = JToken.Parse(File.ReadAllText(Path.Combine(dataDir, "dokument.dane.json")));
var emptyModel = JToken.Parse(File.ReadAllText(Path.Combine(dataDir, "dokument-puste.dane.json")));
var minimal = DocumentTableBindings.FromJson(File.ReadAllText(Path.Combine(dataDir, "dokument.definicja-minimalna.json")));
var overrides = DocumentTableBindings.FromJson(File.ReadAllText(Path.Combine(dataDir, "dokument.definicja.json")));

Run("A. Definicja minimalna – tylko tag + ścieżka, reszta z szablonu (data/dokument.definicja-minimalna.json)", "dokument-minimalna", minimal.Tables, model);
RunWithBinder("A2. Bez żadnej definicji – pomocnik DocxTableBinder z tego przykładu (rekordy spod właściwości o nazwie tagu)", "dokument-bez-definicji", model);
Run("B. Definicja z nadpisaniami – formaty, teksty zastępcze, zdjęcie ramek (data/dokument.definicja.json)", "dokument-nadpisania", overrides.Tables, model);
Run("C. Puste listy (data/dokument-puste.dane.json)", "dokument-puste", overrides.Tables, emptyModel);
return;

void Run(string title, string outputName, IEnumerable<CustomTableDataBinding> tables, JToken data)
{
    var output = Writable(Path.Combine(outDir, $"{outputName}.docx"));
    File.Copy(templatePath, output, overwrite: true);

    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine($"  Wynik: {output}");
    using (var doc = WordprocessingDocument.Open(output, isEditable: true))
    {
        foreach (var table in tables)
        {
            // Jedno wywołanie = jedna tabela; definicja może być pełna albo tylko { tag, model-property-path }.
            var result = builder.Apply(doc, table, data);
            var unresolved = result.UnresolvedPlaceholders.Count == 0 ? "brak" : string.Join(", ", result.UnresolvedPlaceholders);
            Console.WriteLine($"  tabela '{table.Tag}' ← {table.ModelPropertyPath}: rekordów {result.RecordCount}, nierozwiązane znaczniki: {unresolved}");
            foreach (var warning in result.Warnings)
                Console.WriteLine($"    Ostrzeżenie: {warning}");
        }

        doc.Save();
    }

    using var check = WordprocessingDocument.Open(output, false);
    var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(check).ToList();
    Console.WriteLine($"  Walidacja Open XML: {(errors.Count == 0 ? "OK" : errors.Count + " błędów")}");
    foreach (var error in errors.Take(10))
        Console.WriteLine($"    {error.Path?.XPath}: {error.Description}");
}

void RunWithBinder(string title, string outputName, JToken data)
{
    var output = Writable(Path.Combine(outDir, $"{outputName}.docx"));
    File.Copy(templatePath, output, overwrite: true);

    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine($"  Wynik: {output}");
    using (var doc = WordprocessingDocument.Open(output, isEditable: true))
    {
        var result = new DocxTableBinder().Apply(doc, null, data);
        foreach (var table in result.Tables)
            Console.WriteLine($"  tabela '{table.Tag}': rekordów {table.Result.RecordCount}");
        foreach (var warning in result.AllWarnings)
            Console.WriteLine($"    Ostrzeżenie: {warning}");
        doc.Save();
    }

    using var check = WordprocessingDocument.Open(output, false);
    var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(check).ToList();
    Console.WriteLine($"  Walidacja Open XML: {(errors.Count == 0 ? "OK" : errors.Count + " błędów")}");
}

/// <summary>Gdy plik jest otwarty (np. w Wordzie), zwraca nazwę ze znacznikiem czasu, żeby nie przerywać przykładu.</summary>
static string Writable(string path)
{
    if (!File.Exists(path)) return path;
    try
    {
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return path;
    }
    catch (IOException)
    {
        return Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Szablon: dwie niezależne tabele w jednym dokumencie. Każda ma tag formantu – to jedyne, co łączy ją z danymi.
// ---------------------------------------------------------------------------------------------------------------
static void BuildDocumentTemplate(string path)
{
    using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
    var main = doc.AddMainDocumentPart();
    main.Document = new Document(new Body());
    // Sekcje powtarzane to rozszerzenie Word 2013 (w15) – deklaracja przestrzeni i mc:Ignorable dla starszych czytników.
    main.Document.AddNamespaceDeclaration("mc", "http://schemas.openxmlformats.org/markup-compatibility/2006");
    main.Document.AddNamespaceDeclaration("w15", "http://schemas.microsoft.com/office/word/2012/wordml");
    main.Document.MCAttributes = new MarkupCompatibilityAttributes { Ignorable = "w15" };
    var body = main.Document.Body!;
    AddBulletNumbering(main);

    // --- Tabela 1: korespondencja. Rekord wielowierszowy = element sekcji powtarzanej z tagiem "tabela_wiadomosci".
    body.Append(Heading("Korespondencja w sprawie"));

    var messages = new Table(
        new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }, Borders(BorderValues.Dashed)),
        new TableGrid(new GridColumn { Width = "600" }, new GridColumn { Width = "2800" }, new GridColumn { Width = "2800" }, new GridColumn { Width = "2800" }));

    var recordRows = new OpenXmlElement[]
    {
        // Wiersz 1: ikony w formantach-przełącznikach | Rodzaj | Wysłano/Odebrano | Przez/Od
        Row(
            Cell(
                ShapeParagraph(RunControl("ikona_wyslana", "Ikona: wiadomość wysłana", ArrowShape("strzalka_gora", up: true))),
                ShapeParagraph(RunControl("ikona_odebrana", "Ikona: wiadomość odebrana", ArrowShape("strzalka_dol", up: false)))),
            Cell(Label("Rodzaj wiadomości"), Plain("<%rodzaj_wiadomosci%>")),
            Cell(Label("Wysłano"), Label("Odebrano"), Plain("<%data_czas_wiadomosci%>")),
            Cell(Label("Przez"), Label("Od"), Plain("<%nadawca_wiadomosci%>"))),
        // Wiersz 2: treść
        Row(Cell(Plain(string.Empty)), Cell(Label("Treść wiadomości")), SpanCell(2, Plain("<%tresc_wiadomosci%>"))),
        // Wiersz 3: załączniki – lista = formant blokowy z tagiem owijający akapit z punktorem
        Row(Cell(Plain(string.Empty)), Cell(Label("Załączniki")), SpanCell(2, BlockControl("zalaczniki", "Lista załączników", Bullet("<%nazwa_pliku%> (<%rozmiar_pliku%> B)")))),
        // Wiersz 4: przerywana linia zamykająca rekord – należy do rekordu, bo jest w elemencie sekcji
        Row(SpanCell(4, ShapeParagraph(new Run(DashedLineShape("kreska_rekordu"))))),
    };

    messages.Append(new SdtRow(
        new SdtProperties(
            new SdtAlias { Val = "Wiadomości" },
            new Tag { Val = "tabela_wiadomosci" },
            new SdtId { Val = ++Ids.Next },
            new SdtRepeatedSection(new SectionTitle { Val = "Wiadomość" })),
        new SdtContentRow(new SdtRow(
            new SdtProperties(new SdtId { Val = ++Ids.Next }, new SdtRepeatedSectionItem()),
            new SdtContentRow(recordRows)))));
    body.Append(messages);

    // --- Tabela 2: załączniki sprawy. Zwykła tabela (nagłówek + wiersz ze znacznikami) owinięta formantem z tagiem.
    body.Append(Heading("Załączniki sprawy"));

    var attachments = new Table(
        new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }, Borders(BorderValues.Single)),
        new TableGrid(new GridColumn { Width = "2800" }, new GridColumn { Width = "2400" }, new GridColumn { Width = "2400" }, new GridColumn { Width = "1400" }),
        Row(HeaderCell("Nazwa załącznika"), HeaderCell("Data dołączenia"), HeaderCell("Użytkownik"), HeaderCell("Archiwizuj")),
        Row(Cell(Plain("<%nazwa_zalacznika%>")), Cell(Plain("<%data_dolaczenia%>")), Cell(Plain("<%dodajacy%>")), Cell(Plain("<%archiwizuj%>"))));
    body.Append(BlockControl("tabela_zalaczniki", "Załączniki sprawy", attachments));

    body.Append(new Paragraph(new Run(new Text("Koniec zestawienia."))));
    body.Append(PageSetup());
    main.Document.Save();
}

// ---------------------------------------------------------------------------------------------------------------
// Elementy wspólne.
// ---------------------------------------------------------------------------------------------------------------
static void AddBulletNumbering(MainDocumentPart main)
{
    var numbering = new Numbering(
        new AbstractNum(
            new Level(
                new NumberingFormat { Val = NumberFormatValues.Bullet },
                new LevelText { Val = "•" },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new ParagraphProperties(new Indentation { Left = "360", Hanging = "360" }))
            { LevelIndex = 0 })
        { AbstractNumberId = 0 },
        new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
    var part = main.AddNewPart<NumberingDefinitionsPart>();
    part.Numbering = numbering;
}

static SectionProperties PageSetup() =>
    new(new PageSize { Width = 11906, Height = 16838 }, new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134, Header = 708, Footer = 708, Gutter = 0 });

static TableBorders Borders(BorderValues style) => new(
    new TopBorder { Val = style, Size = 4 },
    new LeftBorder { Val = style, Size = 4 },
    new BottomBorder { Val = style, Size = 4 },
    new RightBorder { Val = style, Size = 4 },
    new InsideHorizontalBorder { Val = style, Size = 4 },
    new InsideVerticalBorder { Val = style, Size = 4 });

static SdtProperties ControlProperties(string tag, string title) =>
    new(new SdtAlias { Val = title }, new Tag { Val = tag }, new SdtId { Val = ++Ids.Next });

/// <summary>Formant tekstu sformatowanego w tekście z tagiem i tytułem (Deweloper → „Formant zawartości tekstu sformatowanego”).</summary>
static SdtRun RunControl(string tag, string title, Drawing drawing) =>
    new(ControlProperties(tag, title), new SdtContentRun(new Run(drawing)));

/// <summary>Formant blokowy (owija akapity lub tabelę) z tagiem i tytułem.</summary>
static SdtBlock BlockControl(string tag, string title, params OpenXmlElement[] content) =>
    new(ControlProperties(tag, title), new SdtContentBlock(content));

static Paragraph Heading(string text) =>
    new(new ParagraphProperties(new SpacingBetweenLines { Before = "240", After = "120" }), new Run(new RunProperties(new Bold()), new Text(text)));

static TableRow Row(params TableCell[] cells) => new(cells);

static TableCell Cell(params OpenXmlElement[] content) => new(content);

static TableCell HeaderCell(string text) =>
    new(new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "E7E6E6" }), Label(text));

static TableCell SpanCell(int span, params OpenXmlElement[] content)
{
    var cell = new TableCell(new TableCellProperties(new GridSpan { Val = span }));
    foreach (var c in content) cell.Append(c);
    return cell;
}

static Paragraph Plain(string text) =>
    new(new ParagraphProperties(new SpacingBetweenLines { After = "0" }), new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

static Paragraph Label(string text) =>
    new(new ParagraphProperties(new SpacingBetweenLines { After = "0" }), new Run(new RunProperties(new Bold()), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

static Paragraph Bullet(string text) =>
    new(
        new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 }), new SpacingBetweenLines { After = "0" }),
        new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

static Paragraph ShapeParagraph(OpenXmlElement runOrControl) =>
    new(new ParagraphProperties(new SpacingBetweenLines { After = "0" }), runOrControl);

// Kształty DrawingML (wps) nie wymagają części z obrazem; nazwa w wp:docPr jest tym, co widać w okienku zaznaczenia Worda.
static Drawing ArrowShape(string name, bool up) => Shape(name, up ? "upArrow" : "downArrow", 200000, 200000,
    """<a:solidFill><a:srgbClr val="E8601C"/></a:solidFill><a:ln><a:noFill/></a:ln>""");

static Drawing DashedLineShape(string name) => Shape(name, "line", 5400000, 12700,
    """<a:noFill/><a:ln w="9525"><a:solidFill><a:srgbClr val="7F7F7F"/></a:solidFill><a:prstDash val="dash"/></a:ln>""");

static Drawing Shape(string name, string preset, long cx, long cy, string fillAndLine) => new($"""
    <w:drawing xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
               xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
               xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
               xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
      <wp:inline distT="0" distB="0" distL="0" distR="0">
        <wp:extent cx="{cx}" cy="{cy}"/>
        <wp:docPr id="1" name="{name}"/>
        <a:graphic>
          <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
            <wps:wsp>
              <wps:cNvSpPr/>
              <wps:spPr>
                <a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm>
                <a:prstGeom prst="{preset}"><a:avLst/></a:prstGeom>
                {fillAndLine}
              </wps:spPr>
              <wps:bodyPr/>
            </wps:wsp>
          </a:graphicData>
        </a:graphic>
      </wp:inline>
    </w:drawing>
    """);

file static class Ids
{
    public static int Next = 100;
}
