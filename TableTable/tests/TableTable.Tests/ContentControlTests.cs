using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json.Linq;
using static TableTable.Tests.TestDocs;
using DocProperties = DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties;

namespace TableTable.Tests;

public class ContentControlTests
{
    private static readonly RecordTableBuilder Builder = new();

    /// <summary>Pierwsza komórka: dwie ikony, każda owinięta formantem w tekście w osobnym akapicie.</summary>
    private static Table TableWithWrappedIcons() => new(
        new TableGrid(new GridColumn(), new GridColumn()),
        Row(
            Cell(
                new Paragraph(RunControl("cc_wyslana", GraphicRun("strzalka_gora", 1))),
                new Paragraph(RunControl("cc_odebrana", GraphicRun("strzalka_dol", 2)))),
            Cell(P("<%rodzaj%>"))));

    private static CustomTableDataBinding IconDefinition(bool unwrap = false) => new()
    {
        ModelPropertyPath = "wiadomosci",
        ContentControls =
        {
            new ContentControlMapping { TagName = "cc_wyslana", VisiblePropertyPath = "kierunek", VisibleWhenEquals = "wyslana", Unwrap = unwrap },
            new ContentControlMapping { TagName = "CC_ODEBRANA", VisiblePropertyPath = "kierunek", VisibleWhenEquals = "odebrana", Unwrap = unwrap },
        },
    };

    [Fact]
    public void Run_level_control_is_removed_or_kept_per_record()
    {
        using var doc = Create(TableWithWrappedIcons());

        var result = Builder.Apply(doc, IconDefinition(), Model());

        Assert.Empty(result.Warnings);
        var rows = Rows(doc);
        Assert.Equal(["cc_wyslana"], ControlTags(rows[0]));
        Assert.Equal(["cc_odebrana"], ControlTags(rows[1]));
        Assert.Equal(["strzalka_dol"], GraphicNames(rows[1]));
        Assert.Single(rows[0].GetFirstChild<TableCell>()!.Elements<Paragraph>());
    }

    [Fact]
    public void Unwrap_keeps_content_and_drops_control_frame()
    {
        using var doc = Create(TableWithWrappedIcons());

        Builder.Apply(doc, IconDefinition(unwrap: true), Model());

        var rows = Rows(doc);
        Assert.Empty(ControlTags(rows[0]));
        Assert.Equal(["strzalka_gora"], GraphicNames(rows[0]));
        Assert.Equal(["strzalka_dol"], GraphicNames(rows[1]));
    }

    [Fact]
    public void Convention_toggles_control_by_property_named_like_tag()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn(), new GridColumn()),
            Row(Cell(new Paragraph(RunControl("wyslana", new Run(new Text("↑"))))), Cell(P("<%rodzaj%>")))));

        Builder.Apply(doc, new CustomTableDataBinding { ModelPropertyPath = "wiadomosci" }, Model());

        var rows = Rows(doc);
        Assert.Equal("↑", rows[0].GetFirstChild<TableCell>()!.InnerText);
        Assert.Equal(string.Empty, rows[1].GetFirstChild<TableCell>()!.InnerText);
        Assert.Equal("↑", rows[2].GetFirstChild<TableCell>()!.InnerText);
    }

    [Fact]
    public void Block_control_in_cell_is_removed_and_cell_stays_valid()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn(), new GridColumn()),
            Row(Cell(BlockControl("blok", P("tylko gdy wyslana"))), Cell(P("<%rodzaj%>")))));
        var definition = new CustomTableDataBinding
        {
            ModelPropertyPath = "wiadomosci",
            ContentControls = { new ContentControlMapping { TagName = "blok", VisiblePropertyPath = "wyslana" } },
        };

        Builder.Apply(doc, definition, Model());

        var rows = Rows(doc);
        Assert.Equal("tylko gdy wyslana", rows[0].GetFirstChild<TableCell>()!.InnerText);
        var emptied = rows[1].GetFirstChild<TableCell>()!;
        Assert.Equal(string.Empty, emptied.InnerText);
        Assert.IsType<Paragraph>(emptied.LastChild);
    }

    [Fact]
    public void Cell_control_is_emptied_not_removed()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn(), new GridColumn()),
            Row(CellControl("komorka", Cell(P("poufne"))), Cell(P("<%rodzaj%>")))));
        var definition = new CustomTableDataBinding
        {
            ModelPropertyPath = "wiadomosci",
            ContentControls = { new ContentControlMapping { TagName = "komorka", VisiblePropertyPath = "wyslana" } },
        };

        Builder.Apply(doc, definition, Model());

        var rows = Rows(doc);
        Assert.Equal(["poufne", "E-mail"], CellTexts(rows[0]));
        Assert.Equal(["", "SMS"], CellTexts(rows[1]));
        Assert.Equal(2, rows[1].Elements<TableCell>().Count());
        Assert.Empty(rows[1].Descendants<SdtCell>());
    }

    [Fact]
    public void Repeating_section_defines_record_template_including_graphic_only_row()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn(), new GridColumn()),
            Row(SpanCell(2, P("Nagłówek"))),
            RepeatingSection("sekcja_wiadomosci",
                Row(Cell(P("<%rodzaj_wiadomosci%>")), Cell(P("<%nadawca_wiadomosci%>"))),
                Row(SpanCell(2, BlockControl("zalaczniki", Bullet("<%nazwa_pliku%>")))),
                Row(SpanCell(2, new Paragraph(GraphicRun("kreska", 9))))),
            Row(SpanCell(2, P("Stopka")))));
        var definition = Definition();
        definition.Tag = "sekcja_wiadomosci";

        var result = Builder.Apply(doc, definition, Model());

        Assert.Equal(3, result.RecordCount);
        Assert.Empty(result.Warnings);
        var table = FirstTable(doc);
        Assert.Equal("Nagłówek", table.GetFirstChild<TableRow>()!.InnerText);
        Assert.Equal("Stopka", table.Elements<TableRow>().Last().InnerText);

        var section = Assert.Single(table.Elements<SdtRow>());
        var items = section.SdtContentRow!.Elements<SdtRow>().ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.Equal(3, item.SdtContentRow!.Elements<TableRow>().Count()));

        Assert.Equal("E-mail", items[0].Descendants<TableRow>().First().GetFirstChild<TableCell>()!.InnerText);
        Assert.Equal(["umowa.pdf", "zal_1.xlsx"], items[0].Descendants<TableRow>().ElementAt(1).Descendants<Paragraph>().Select(p => p.InnerText));
        Assert.Single(items[1].Descendants<TableRow>().ElementAt(1).Descendants<Paragraph>());

        var ids = table.Descendants<DocProperties>().Where(d => d.Name == "kreska").Select(d => d.Id!.Value).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.DoesNotContain("<%", table.InnerText);
    }

    [Fact]
    public void Row_list_control_inside_repeating_item_clones_rows_not_whole_item()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn(), new GridColumn()),
            RepeatingSection("sekcja",
                Row(SpanCell(2, P("<%nadawca_wiadomosci%>"))),
                RowControl("zalaczniki", Row(Cell(P("<%nazwa_pliku%>")), Cell(P("<%rozmiar_pliku%>")))))));
        var definition = new CustomTableDataBinding
        {
            ModelPropertyPath = "wiadomosci",
            Fields = { new FieldMapping { SearchFor = "<%nadawca_wiadomosci%>", ReplacementPropertyPath = "nadawca" } },
            Collections =
            {
                new CollectionMapping
                {
                    TagName = "zalaczniki",
                    Fields =
                    {
                        new FieldMapping { SearchFor = "<%nazwa_pliku%>", ReplacementPropertyPath = "nazwa" },
                        new FieldMapping { SearchFor = "<%rozmiar_pliku%>", ReplacementPropertyPath = "rozmiar" },
                    },
                },
            },
        };

        var result = Builder.Apply(doc, definition, Model());

        Assert.Empty(result.Warnings);
        var items = FirstTable(doc).Elements<SdtRow>().Single().SdtContentRow!.Elements<SdtRow>().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(["Jan Kowalski", "umowa.pdf|2048", "zal_1.xlsx|512"], items[0].Descendants<TableRow>().Select(r => string.Join("|", CellTexts(r))));
        Assert.Equal(["System"], items[1].Descendants<TableRow>().Select(r => string.Join("|", CellTexts(r))));
        Assert.Equal(["Urząd Miasta", "decyzja.pdf|99"], items[2].Descendants<TableRow>().Select(r => string.Join("|", CellTexts(r))));
    }

    [Fact]
    public void Repeating_section_with_no_records_gets_empty_text_row_or_is_removed()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn(), new GridColumn()),
            Row(SpanCell(2, P("Nagłówek"))),
            RepeatingSection("sekcja", Row(Cell(P("<%rodzaj%>")), Cell(P("<%nadawca%>"))))));
        var definition = new CustomTableDataBinding { ModelPropertyPath = "wiadomosci", Tag = "sekcja", EmptyText = "Brak wiadomości" };

        Builder.Apply(doc, definition, JToken.Parse("""{ "wiadomosci": [] }"""));

        var section = FirstTable(doc).Elements<SdtRow>().Single();
        var row = Assert.Single(section.SdtContentRow!.Elements<TableRow>());
        Assert.Equal("Brak wiadomości", row.InnerText);
        Assert.Equal(2, row.GetFirstChild<TableCell>()!.TableCellProperties!.GridSpan!.Val!.Value);

        using var doc2 = Create(new Table(new TableGrid(new GridColumn()), RepeatingSection("sekcja", Row(Cell(P("<%rodzaj%>"))))));
        definition.EmptyBehavior = EmptyTableBehavior.RemoveTable;
        var result = Builder.Apply(doc2, definition, JToken.Parse("""{ }"""));
        Assert.True(result.TableRemoved);
        Assert.Empty(doc2.MainDocumentPart!.Document!.Body!.Descendants<Table>());
    }

    [Fact]
    public void Block_level_repeating_section_repeats_paragraphs_with_convention_list()
    {
        var item = new SdtBlock(
            new SdtProperties(new SdtId { Val = 77 }, new DocumentFormat.OpenXml.Office2013.Word.SdtRepeatedSectionItem()),
            new SdtContentBlock(P("<%rodzaj%>: <%nadawca%>"), BlockControl("zalaczniki", Bullet("<%nazwa%>"))));
        var section = new SdtBlock(
            new SdtProperties(new Tag { Val = "lista" }, new SdtId { Val = 76 }, new DocumentFormat.OpenXml.Office2013.Word.SdtRepeatedSection()),
            new SdtContentBlock(item));
        using var doc = Create(P("przed"), section, P("po"));

        var result = Builder.Apply(doc, "lista", "wiadomosci", Model());

        Assert.Equal(3, result.RecordCount);
        var texts = doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Select(p => p.InnerText).ToList();
        Assert.Equal(["przed", "E-mail: Jan Kowalski", "umowa.pdf", "zal_1.xlsx", "SMS: System", "Pismo: Urząd Miasta", "decyzja.pdf", "po"], texts);
    }

    [Fact]
    public void Table_is_located_by_content_control_wrapping_it()
    {
        using var doc = Create(
            new Table(new TableGrid(new GridColumn()), Row(Cell(P("inna <%x%>")))),
            BlockControl("tabela_wiadomosci", MessagesTable()));
        var definition = Definition();
        definition.Tag = "tabela_wiadomosci";

        var result = Builder.Apply(doc, definition, Model());

        Assert.Equal(3, result.RecordCount);
        var tables = doc.MainDocumentPart!.Document!.Body!.Descendants<Table>().ToList();
        Assert.Contains("<%x%>", tables[0].InnerText);
        Assert.DoesNotContain("<%", tables[1].InnerText);
    }

    [Fact]
    public void Document_worker_removes_unwraps_and_keeps()
    {
        using var doc = Create(
            BlockControl("logo", new Paragraph(GraphicRun("logo", 1))),
            new Paragraph(RunControl("podpis", new Run(new Text("Jan")))),
            BlockControl("klauzula", P("RODO")),
            BlockControl("brak_warunku", P("x")));
        var model = JToken.Parse("""{ "pokaz_logo": false, "status": "aktywna" }""");
        var mappings = new[]
        {
            new ContentControlMapping { TagName = "logo", VisiblePropertyPath = "pokaz_logo" },
            new ContentControlMapping { TagName = "podpis", VisiblePropertyPath = "pokaz_logo", Negate = true, Unwrap = true },
            new ContentControlMapping { TagName = "klauzula", VisiblePropertyPath = "status", VisibleWhenEquals = "aktywna" },
            new ContentControlMapping { TagName = "brak_warunku" },
        };

        var (removed, warnings) = new ContentControlVisibilityWorker().Apply(doc, mappings, model);

        Assert.Equal(1, removed);
        Assert.Single(warnings, w => w.Contains("brak_warunku"));
        var body = doc.MainDocumentPart!.Document!.Body!;
        Assert.Equal(["klauzula", "brak_warunku"], ControlTags(body));
        Assert.Equal(["Jan", "RODO", "x"], body.Descendants<Paragraph>().Select(p => p.InnerText));
        Assert.Empty(body.Descendants<Drawing>());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("\"nie\"", false)]
    [InlineData("\"tak\"", true)]
    [InlineData("\"\"", false)]
    [InlineData("null", false)]
    [InlineData("[]", false)]
    [InlineData("[1]", true)]
    [InlineData("{}", true)]
    public void Truthiness_follows_documented_rules(string json, bool expected)
    {
        using var doc = Create(BlockControl("g", P("x")));
        var model = JToken.Parse("{ \"v\": " + json + " }");

        var (removed, _) = new ContentControlVisibilityWorker().Apply(doc, [new ContentControlMapping { TagName = "g", VisiblePropertyPath = "v" }], model);

        Assert.Equal(expected ? 0 : 1, removed);
    }

    [Fact]
    public void Output_with_repeating_section_and_controls_is_valid_open_xml()
    {
        using var doc = Create(new Table(
            new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }),
            new TableGrid(new GridColumn(), new GridColumn()),
            RepeatingSection("sekcja",
                Row(Cell(new Paragraph(RunControl("cc_wyslana", new Run(new Text("↑")))), new Paragraph(RunControl("cc_odebrana", new Run(new Text("↓"))))), Cell(P("<%rodzaj%>"))),
                Row(SpanCell(2, BlockControl("zalaczniki", Bullet("<%nazwa%>")))))));
        var definition = new CustomTableDataBinding
        {
            Tag = "sekcja",
            ModelPropertyPath = "wiadomosci",
            ContentControls =
            {
                new ContentControlMapping { TagName = "cc_wyslana", VisiblePropertyPath = "wyslana" },
                new ContentControlMapping { TagName = "cc_odebrana", VisiblePropertyPath = "wyslana", Negate = true, Unwrap = true },
            },
            Collections = { new CollectionMapping { TagName = "zalaczniki", EmptyText = "brak" } },
        };

        var result = Builder.Apply(doc, definition, Model());

        Assert.Empty(result.Warnings);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(doc).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
    }
}
