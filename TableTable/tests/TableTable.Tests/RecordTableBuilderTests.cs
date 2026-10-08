using System.Globalization;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json.Linq;
using static TableTable.Tests.TestDocs;
using DocProperties = DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties;

namespace TableTable.Tests;

public class RecordTableBuilderTests
{
    private static readonly RecordTableBuilder Builder = new();

    [Fact]
    public void Clones_template_rows_per_record_and_keeps_header_and_footer()
    {
        using var doc = Create(MessagesTable());

        var result = Builder.Apply(doc, Definition(), Model());

        Assert.Equal(3, result.RecordCount);
        Assert.Empty(result.UnresolvedPlaceholders);
        Assert.Empty(result.Warnings);

        var rows = Rows(doc);
        Assert.Equal(1 + 3 * 3 + 1, rows.Count);
        Assert.Equal("Korespondencja", rows[0].InnerText);
        Assert.Equal("Koniec", rows[^1].InnerText);
        Assert.DoesNotContain("<%", FirstTable(doc).InnerText);

        // rekord 1: 08:15 UTC → 10:15 czasu polskiego (CEST), format domyślny
        var first = CellTexts(rows[1]);
        Assert.Equal("Rodzaj wiadomościE-mail", first[1]);
        Assert.Equal("WysłanoOdebrano14.09.2026 10:15", first[2]);
        Assert.Equal("PrzezOdJan Kowalski", first[3]);

        // rekord 3: data bez godziny – format krótki, treść null → null-text
        Assert.Equal("WysłanoOdebrano16.09.2026", CellTexts(rows[7])[2]);
        Assert.Equal("(brak treści)", CellTexts(rows[8])[2]);
    }

    [Fact]
    public void List_control_repeats_bullet_paragraph_and_disappears_when_empty()
    {
        using var doc = Create(MessagesTable());

        Builder.Apply(doc, Definition(), Model());

        var rows = Rows(doc);
        var attachments1 = rows[3].Elements<TableCell>().Last().Descendants<Paragraph>().ToList();
        Assert.Equal(["umowa.pdf", "zal_1.xlsx"], attachments1.Select(p => p.InnerText));
        Assert.All(attachments1, p => Assert.True(IsBullet(p)));

        var cell2 = rows[6].Elements<TableCell>().Last();
        Assert.Empty(cell2.Descendants<SdtElement>());
        var single = Assert.Single(cell2.Elements<Paragraph>());
        Assert.Equal(string.Empty, single.InnerText);
        Assert.False(IsBullet(single), "komórka po usunięciu formantu dostaje pusty akapit bez punktora");

        Assert.Equal("decyzja.pdf", Assert.Single(rows[9].Elements<TableCell>().Last().Descendants<Paragraph>()).InnerText);
    }

    [Fact]
    public void Empty_list_with_empty_text_keeps_one_paragraph_with_that_text()
    {
        using var doc = Create(MessagesTable());
        var definition = Definition();
        definition.Collections[0].EmptyText = "brak załączników";

        Builder.Apply(doc, definition, Model());

        var single = Assert.Single(Rows(doc)[6].Elements<TableCell>().Last().Descendants<Paragraph>());
        Assert.Equal("brak załączników", single.InnerText);
        Assert.True(IsBullet(single));
    }

    [Fact]
    public void Tag_split_across_several_runs_is_replaced()
    {
        using var doc = Create(MessagesTable(splitTags: true));

        Builder.Apply(doc, Definition(), Model());

        Assert.Equal("Rodzaj wiadomościE-mail", Rows(doc)[1].Elements<TableCell>().ElementAt(1).InnerText);
        Assert.DoesNotContain("<%", FirstTable(doc).InnerText);
    }

    [Fact]
    public void New_lines_in_value_become_breaks()
    {
        using var doc = Create(MessagesTable());

        Builder.Apply(doc, Definition(), Model());

        var paragraph = Rows(doc)[2].Elements<TableCell>().Last().GetFirstChild<Paragraph>()!;
        Assert.Single(paragraph.Descendants<Break>());
        Assert.Equal(["Dzień dobry,", "przesyłam dokumenty."], paragraph.Descendants<Text>().Select(t => t.Text));
    }

    [Fact]
    public void Display_format_and_time_zone_are_applied()
    {
        using var doc = Create(MessagesTable());
        var definition = Definition();
        definition.Fields[1].DisplayFormat = "dd-MM-yyyy HH:mm";

        new RecordTableBuilder(new RecordTableOptions { TimeZoneId = "UTC" }).Apply(doc, definition, Model());

        Assert.Equal("WysłanoOdebrano14-09-2026 08:15", CellTexts(Rows(doc)[1])[2]);
    }

    [Fact]
    public void Numbers_are_formatted_with_culture_and_display_format()
    {
        using var doc = Create(new Table(new TableGrid(new GridColumn()), Row(Cell(P("<%kwota%>")))));
        var definition = new CustomTableDataBinding
        {
            ModelPropertyPath = "wiadomosci",
            Fields = { new FieldMapping { SearchFor = "<%kwota%>", ReplacementPropertyPath = "kwota", DisplayFormat = "N2", NullText = "-" } },
        };

        Builder.Apply(doc, definition, Model());

        var rows = Rows(doc);
        Assert.Equal(1234.5m.ToString("N2", CultureInfo.GetCultureInfo("pl-PL")), rows[0].InnerText);
        Assert.Equal("-", rows[1].InnerText);
    }

    [Fact]
    public void No_records_removes_template_rows_by_default()
    {
        using var doc = Create(MessagesTable());

        var result = Builder.Apply(doc, Definition(), JToken.Parse("""{ "wiadomosci": [] }"""));

        Assert.Equal(0, result.RecordCount);
        Assert.Equal(["Korespondencja", "Koniec"], Rows(doc).Select(r => r.InnerText));
    }

    [Fact]
    public void No_records_with_empty_text_inserts_full_width_row()
    {
        using var doc = Create(MessagesTable());
        var definition = Definition();
        definition.EmptyText = "Brak wiadomości";

        Builder.Apply(doc, definition, JToken.Parse("""{ "wiadomosci": null }"""));

        var rows = Rows(doc);
        Assert.Equal(3, rows.Count);
        var cell = Assert.Single(rows[1].Elements<TableCell>());
        Assert.Equal("Brak wiadomości", cell.InnerText);
        Assert.Equal(4, cell.TableCellProperties!.GridSpan!.Val!.Value);
    }

    [Fact]
    public void No_records_can_remove_whole_table_or_keep_template()
    {
        using var doc = Create(new Paragraph(new Run(new Text("przed"))), MessagesTable(), new Paragraph(new Run(new Text("po"))));
        var definition = Definition();
        definition.EmptyBehavior = EmptyTableBehavior.RemoveTable;
        var result = Builder.Apply(doc, definition, JToken.Parse("""{ }"""));
        Assert.True(result.TableRemoved);
        Assert.Empty(doc.MainDocumentPart!.Document!.Body!.Descendants<Table>());

        using var doc2 = Create(MessagesTable());
        definition.EmptyBehavior = EmptyTableBehavior.KeepTemplate;
        Builder.Apply(doc2, definition, JToken.Parse("""{ "wiadomosci": [] }"""));
        Assert.Equal(5, Rows(doc2).Count);
        Assert.Contains("<%rodzaj_wiadomosci%>", FirstTable(doc2).InnerText);
    }

    [Fact]
    public void Table_is_found_by_bookmark_before_it_or_inside_it()
    {
        using var doc = Create(
            new Table(new TableGrid(new GridColumn()), Row(Cell(P("inna tabela <%x%>")))),
            new Paragraph(new BookmarkStart { Id = "1", Name = "tabela_wiadomosci" }, new BookmarkEnd { Id = "1" }),
            MessagesTable());
        var definition = Definition();
        definition.BookmarkName = "tabela_wiadomosci";
        Assert.Equal(3, Builder.Apply(doc, definition, Model()).RecordCount);
        var tables = doc.MainDocumentPart!.Document!.Body!.Elements<Table>().ToList();
        Assert.Contains("<%x%>", tables[0].InnerText);
        Assert.DoesNotContain("<%", tables[1].InnerText);

        // zakładka w tabeli: przeżywa tylko w pierwszym klonie
        using var doc2 = Create(MessagesTable(bookmarkInside: true));
        definition.BookmarkName = "TABELA_WIADOMOSCI";
        Builder.Apply(doc2, definition, Model());
        Assert.Single(FirstTable(doc2).Descendants<BookmarkStart>().Where(b => b.Name == "tabela_wiadomosci"));
        Assert.Single(FirstTable(doc2).Descendants<BookmarkEnd>());
    }

    [Fact]
    public void Missing_table_throws_with_description()
    {
        using var doc = Create(new Paragraph(new Run(new Text("bez tabeli"))));
        var definition = Definition();
        definition.BookmarkName = "nie_ma";

        var ex = Assert.Throws<InvalidOperationException>(() => Builder.Apply(doc, definition, Model()));
        Assert.Contains("nie_ma", ex.Message);
    }

    [Fact]
    public void Auto_bind_resolves_record_properties_then_root_and_index()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn(), new GridColumn(), new GridColumn()),
            Row(Cell(P("<%$index%>")), Cell(P("<%nadawca%>")), Cell(P("<%numer_sprawy%> / <%$root.numer_sprawy%>")))));

        var result = Builder.Apply(doc, new CustomTableDataBinding { ModelPropertyPath = "wiadomosci" }, Model());

        Assert.Empty(result.UnresolvedPlaceholders);
        Assert.Equal(["1", "Jan Kowalski", "OH/2026/0142 / OH/2026/0142"], CellTexts(Rows(doc)[0]));
        Assert.Equal(["3", "Urząd Miasta", "OH/2026/0142 / OH/2026/0142"], CellTexts(Rows(doc)[2]));
    }

    [Fact]
    public void Convention_list_control_binds_item_then_record()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn()),
            Row(Cell(P("<%rodzaj%>"), BlockControl("zalaczniki", Bullet("<%nazwa%> (<%rozmiar%> B) – <%nadawca%>"))))));

        var result = Builder.Apply(doc, new CustomTableDataBinding { ModelPropertyPath = "wiadomosci" }, Model());

        Assert.Empty(result.UnresolvedPlaceholders);
        Assert.Equal(["E-mail", "umowa.pdf (2048 B) – Jan Kowalski", "zal_1.xlsx (512 B) – Jan Kowalski"], Rows(doc)[0].Descendants<Paragraph>().Select(p => p.InnerText));
        Assert.Equal(["SMS"], Rows(doc)[1].Descendants<Paragraph>().Select(p => p.InnerText));
    }

    [Fact]
    public void List_control_around_rows_repeats_whole_rows()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn(), new GridColumn()),
            Row(SpanCell(2, P("<%nadawca_wiadomosci%>"))),
            RowControl("zalaczniki", Row(Cell(P("<%nazwa_pliku%>")), Cell(P("<%rozmiar_pliku%>"))))));
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
                        new FieldMapping { SearchFor = "<%rozmiar_pliku%>", ReplacementPropertyPath = "rozmiar", DisplayFormat = "N0" },
                    },
                },
            },
        };

        var result = Builder.Apply(doc, definition, Model());

        Assert.Empty(result.Warnings);
        var rows = FirstTable(doc).Descendants<TableRow>().Select(r => string.Join("|", CellTexts(r))).ToList();
        var n2048 = 2048.ToString("N0", CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Equal(["Jan Kowalski", $"umowa.pdf|{n2048}", "zal_1.xlsx|512", "System", "Urząd Miasta", "decyzja.pdf|99"], rows);
    }

    [Fact]
    public void Expression_evaluator_is_used_and_missing_evaluator_warns()
    {
        using var doc = Create(new Table(new TableGrid(new GridColumn()), Row(Cell(P("[<%x%>]")))));
        var definition = new CustomTableDataBinding
        {
            ModelPropertyPath = "wiadomosci",
            Fields = { new FieldMapping { SearchFor = "<%x%>", ReplacementPropertyExpression = "formModel.Foo", NullText = "?" } },
        };
        var builder = new RecordTableBuilder(new RecordTableOptions
        {
            ExpressionEvaluator = new DelegateExpressionEvaluator((expr, scope) => $"{expr}:{scope.Index}:{scope.Current["rodzaj"]}:{scope.Root["numer_sprawy"]}"),
        });
        builder.Apply(doc, definition, Model());
        Assert.Equal("[formModel.Foo:2:SMS:OH/2026/0142]", Rows(doc)[1].InnerText);

        using var doc2 = Create(new Table(new TableGrid(new GridColumn()), Row(Cell(P("[<%x%>]")))));
        var result = Builder.Apply(doc2, definition, Model());
        Assert.Contains(result.Warnings, w => w.Contains("ExpressionEvaluator"));
        Assert.Equal("[?]", Rows(doc2)[0].InnerText);
    }

    [Fact]
    public void Unresolved_tags_are_blanked_and_reported_or_kept()
    {
        using var doc = Create(new Table(new TableGrid(new GridColumn()), Row(Cell(P("<%nadawca%> [<%nie_ma%>]")))));
        var result = Builder.Apply(doc, new CustomTableDataBinding { ModelPropertyPath = "wiadomosci" }, Model());
        Assert.Equal(["<%nie_ma%>"], result.UnresolvedPlaceholders);
        Assert.Equal("Jan Kowalski []", Rows(doc)[0].InnerText);

        using var doc2 = Create(new Table(new TableGrid(new GridColumn()), Row(Cell(P("<%nie_ma%>")))));
        var kept = Builder.Apply(doc2, new CustomTableDataBinding { ModelPropertyPath = "wiadomosci", KeepUnresolved = true }, Model());
        Assert.Equal(["<%nie_ma%>"], kept.UnresolvedPlaceholders);
        Assert.Equal("<%nie_ma%>", Rows(doc2)[0].InnerText);
    }

    [Fact]
    public void Match_case_false_matches_differently_cased_tag()
    {
        using var doc = Create(new Table(new TableGrid(new GridColumn()), Row(Cell(P("<%NADAWCA%>")))));
        var definition = new CustomTableDataBinding
        {
            ModelPropertyPath = "wiadomosci",
            AutoBind = false,
            Fields = { new FieldMapping { SearchFor = "<%nadawca%>", ReplacementPropertyPath = "nadawca", MatchCase = false } },
        };

        Builder.Apply(doc, definition, Model());

        Assert.Equal("Jan Kowalski", Rows(doc)[0].InnerText);
    }

    [Fact]
    public void Poco_model_is_accepted()
    {
        using var doc = Create(new Table(new TableGrid(new GridColumn()), Row(Cell(P("<%Nazwa%>: <%Kiedy%>")))));
        var model = new
        {
            Pozycje = new[]
            {
                new { Nazwa = "A", Kiedy = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified) },
                new { Nazwa = "B", Kiedy = new DateTime(2026, 1, 2, 23, 30, 0, DateTimeKind.Utc) },
            },
        };

        Builder.Apply(doc, new CustomTableDataBinding { ModelPropertyPath = "Pozycje" }, model);

        Assert.Equal(["A: 02.01.2026", "B: 03.01.2026 00:30"], Rows(doc).Select(r => r.InnerText));
    }

    [Fact]
    public void Definition_round_trips_through_kebab_case_json()
    {
        const string json = """
            {
              "tag": "tabela_wiadomosci",
              "model-property-path": "wiadomosci",
              "empty-behavior": "remove-table",
              "keep-unresolved": true,
              "fields": [ { "search-for": "<%data%>", "replacement-property-path": "data_czas", "display-format": "dd-MM-yyyy", "match-case": false } ],
              "collections": [ { "tag-name": "zalaczniki", "empty-text": "brak", "fields": [ { "search-for": "<%nazwa_pliku%>", "replacement-property-expression": "item.nazwa" } ] } ],
              "content-controls": [ { "tag-name": "ikona", "visible-property-path": "kierunek", "visible-when-equals": "wyslana", "negate": true } ]
            }
            """;

        var definition = CustomTableDataBinding.FromJson(json);

        Assert.Equal("tabela_wiadomosci", definition.Tag);
        Assert.Equal(EmptyTableBehavior.RemoveTable, definition.EmptyBehavior);
        Assert.True(definition.KeepUnresolved);
        Assert.False(definition.Fields[0].MatchCase);
        Assert.Equal("item.nazwa", definition.Collections[0].Fields[0].ReplacementPropertyExpression);
        Assert.True(definition.ContentControls[0].Negate);
        Assert.Equal(definition.ToJson(), CustomTableDataBinding.FromJson(definition.ToJson()).ToJson());
        Assert.Contains("\"empty-behavior\": \"remove-table\"", definition.ToJson());
    }

    [Fact]
    public void Graphic_in_last_template_row_is_cloned_with_unique_ids_and_page_anchor_warns()
    {
        using var doc = Create(new Table(
            new TableGrid(new GridColumn()),
            Row(Cell(P("<%rodzaj%>"))),
            Row(Cell(new Paragraph(GraphicRun("kreska", 7))))));
        // Wiersz z samą grafiką nie ma znacznika, więc liczbę wierszy rekordu podajemy jawnie.
        var result = Builder.Apply(doc, new CustomTableDataBinding { ModelPropertyPath = "wiadomosci", TemplateRowCount = 2 }, Model());
        Assert.Empty(result.Warnings);
        Assert.Equal(6, Rows(doc).Count);
        var ids = FirstTable(doc).Descendants<DocProperties>().Where(d => d.Name == "kreska").Select(d => d.Id!.Value).ToList();
        Assert.Equal(3, ids.Distinct().Count());

        using var doc2 = Create(new Table(new TableGrid(new GridColumn()), Row(Cell(P("<%rodzaj%>"), PageAnchoredGraphicP("plywajaca")))));
        var warned = Builder.Apply(doc2, new CustomTableDataBinding { ModelPropertyPath = "wiadomosci" }, Model());
        Assert.Single(warned.Warnings, w => w.Contains("plywajaca") && w.Contains("strony"));
    }

    [Fact]
    public void Output_is_valid_open_xml()
    {
        using var doc = Create(MessagesTable(splitTags: true, bookmarkInside: true));
        var definition = Definition();
        definition.Collections[0].EmptyText = "brak";

        Builder.Apply(doc, definition, Model());

        var errors = new OpenXmlValidator().Validate(doc).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
    }
}
