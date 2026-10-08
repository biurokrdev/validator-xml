using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json.Linq;
using static TableTable.Tests.TestDocs;

namespace TableTable.Tests;

/// <summary>
/// Dokument z dwiema tabelami oznaczonymi tagami: każda wiązana osobnym wywołaniem (jak N workerów),
/// konwencją (tylko tag + ścieżka) albo z definicją nadpisującą.
/// </summary>
public class ConventionBindingTests
{
    private static readonly RecordTableBuilder Builder = new();

    /// <summary>
    /// Tabela A (sekcja powtarzana „tabela_a”): pole, formant-przełącznik, grafika-przełącznik, lista w formancie „pozycje”.
    /// Tabela B (formant blokowy „tabela_b” owijający tabelę): nagłówek + wiersz z dwoma polami.
    /// </summary>
    private static OpenXmlElement[] TwoTables() =>
    [
        new Paragraph(new Run(new Text("Tabela A"))),
        new Table(
            new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }),
            new TableGrid(new GridColumn(), new GridColumn()),
            RepeatingSection("tabela_a",
                Row(
                    Cell(P("<%nazwa%>"), new Paragraph(RunControl("cc_flag", new Run(new Text("[flaga]"))))),
                    Cell(new Paragraph(GraphicRun("ikona", 1)), BlockControl("pozycje", Bullet("<%opis%> / <%nazwa%>")))))),
        new Paragraph(new Run(new Text("Tabela B"))),
        BlockControl("tabela_b", new Table(
            new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }),
            new TableGrid(new GridColumn(), new GridColumn()),
            Row(Cell(P("Kolumna 1")), Cell(P("Kolumna 2"))),
            Row(Cell(P("<%kol1%>")), Cell(P("<%kol2%>"))))),
    ];

    private static JToken TwoTablesModel() => JToken.Parse("""
        {
          "tabela_a": [
            { "nazwa": "A1", "cc_flag": true,  "ikona": false, "pozycje": [ { "opis": "x" }, { "opis": "y" } ] },
            { "nazwa": "A2", "cc_flag": false, "ikona": true,  "pozycje": [] }
          ],
          "inne": { "wiersze": [ { "kol1": "2026-09-14", "kol2": 1234.5 } ] }
        }
        """);

    private static List<SdtRow> ItemsOfTableA(DocumentFormat.OpenXml.Packaging.WordprocessingDocument doc) =>
        doc.MainDocumentPart!.Document!.Body!.Elements<Table>().First().Elements<SdtRow>().Single().SdtContentRow!.Elements<SdtRow>().ToList();

    [Fact]
    public void Each_table_bound_by_tag_and_path_only_everything_else_from_template()
    {
        using var doc = Create(TwoTables());

        var a = Builder.Apply(doc, "tabela_a", "tabela_a", TwoTablesModel());
        var b = Builder.Apply(doc, "tabela_b", "inne.wiersze", TwoTablesModel());

        Assert.Empty(a.Warnings);
        Assert.Empty(b.Warnings);
        Assert.Equal(2, a.RecordCount);
        Assert.Equal(1, b.RecordCount);

        var items = ItemsOfTableA(doc);
        // rekord 1: flaga zostaje, lista ma dwa punkty (znacznik rekordu wewnątrz elementu listy też związany); grafika poza formantem jest tylko klonowana
        Assert.Equal(["cc_flag", "pozycje"], ControlTags(items[0]));
        Assert.Equal(["ikona"], GraphicNames(items[0]));
        Assert.Equal(["x / A1", "y / A1"], items[0].Descendants<SdtBlock>().Single().Descendants<Paragraph>().Select(p => p.InnerText));

        // rekord 2: flaga znika, grafika zostaje, pusta lista = formant usunięty
        Assert.Empty(ControlTags(items[1]));
        Assert.Equal(["ikona"], GraphicNames(items[1]));
        Assert.DoesNotContain("[flaga]", items[1].InnerText);

        var tableB = doc.MainDocumentPart!.Document!.Body!.Descendants<Table>().Last();
        Assert.Equal(["Kolumna 1Kolumna 2", "14.09.20261234,5"], tableB.Elements<TableRow>().Select(r => r.InnerText));
        Assert.DoesNotContain("<%", doc.MainDocumentPart.Document.Body.InnerText);

        var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(doc).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
    }

    [Fact]
    public void Definition_overrides_convention_only_where_given()
    {
        using var doc = Create(TwoTables());
        var binding = CustomTableDataBinding.FromJson("""
            {
              "tag": "tabela_b",
              "model-property-path": "inne.wiersze",
              "fields": [
                { "search-for": "<%kol1%>", "replacement-property-path": "kol1", "display-format": "dd-MM-yyyy" },
                { "search-for": "<%kol2%>", "replacement-property-path": "kol2", "display-format": "N2" }
              ]
            }
            """);

        Builder.Apply(doc, binding, TwoTablesModel());

        var tableB = doc.MainDocumentPart!.Document!.Body!.Descendants<Table>().Last();
        Assert.Equal("14-09-2026" + 1234.5m.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("pl-PL")), tableB.Elements<TableRow>().Last().InnerText);
    }

    [Fact]
    public void Unknown_tag_throws_with_description()
    {
        using var doc = Create(TwoTables());

        var ex = Assert.Throws<InvalidOperationException>(() => Builder.Apply(doc, "nie_ma_takiej", "x", TwoTablesModel()));
        Assert.Contains("nie_ma_takiej", ex.Message);
    }

    [Fact]
    public void Missing_table_data_removes_template_rows_and_reports_zero_records()
    {
        using var doc = Create(TwoTables());

        var result = Builder.Apply(doc, "tabela_a", "tabela_a", JToken.Parse("""{ }"""));

        Assert.Equal(0, result.RecordCount);
        Assert.Empty(ItemsOfTableA(doc));
    }

    [Fact]
    public void Explicit_collection_by_tag_name_with_empty_text_and_unwrap()
    {
        using var doc = Create(TwoTables());
        var binding = new CustomTableDataBinding
        {
            Tag = "tabela_a",
            ModelPropertyPath = "tabela_a",
            Collections = { new CollectionMapping { TagName = "pozycje", EmptyText = "brak pozycji", Unwrap = true } },
        };

        var result = Builder.Apply(doc, binding, TwoTablesModel());

        Assert.Empty(result.Warnings);
        var items = ItemsOfTableA(doc);
        Assert.DoesNotContain("pozycje", ControlTags(items[0]));
        // po rozpakowaniu punkty listy są zwykłymi akapitami komórki
        Assert.Equal(["x / A1", "y / A1"], items[0].Descendants<TableCell>().Last().Elements<Paragraph>().Where(IsBullet).Select(p => p.InnerText));
        Assert.Equal("brak pozycji", items[1].Descendants<TableCell>().Last().Elements<Paragraph>().Last().InnerText);
    }

    [Fact]
    public void Template_schema_describes_tables_and_builds_data_skeleton()
    {
        using var doc = Create(TwoTables());

        var schema = TemplateSchema.Describe(doc);

        Assert.Equal(["tabela_a", "tabela_b"], schema.Tables.Select(t => t.Tag));
        Assert.Equal(["repeating-section", "rows"], schema.Tables.Select(t => t.Kind));
        var a = schema.Tables[0];
        Assert.Equal(["nazwa"], a.Fields);
        Assert.Equal(["cc_flag"], a.Flags);
        var pozycje = Assert.Single(a.Collections);
        Assert.Equal("pozycje", pozycje.Tag);
        Assert.Equal(["opis", "nazwa"], pozycje.Fields);

        var expected = JToken.Parse("""
            {
              "tabela_a": [ { "nazwa": "", "cc_flag": true, "pozycje": [ { "opis": "", "nazwa": "" } ] } ],
              "tabela_b": [ { "kol1": "", "kol2": "" } ]
            }
            """);
        Assert.True(JToken.DeepEquals(expected, schema.ToSkeleton()), schema.ToJson());
    }

    [Fact]
    public void Skeleton_filled_with_values_binds_every_table_without_warnings()
    {
        using var doc = Create(TwoTables());
        var schema = TemplateSchema.Describe(doc);
        var skeleton = schema.ToSkeleton();
        skeleton["tabela_a"]![0]!["nazwa"] = "ze szkieletu";
        skeleton["tabela_b"]![0]!["kol1"] = "k1";

        foreach (var table in schema.Tables)
        {
            var result = Builder.Apply(doc, table.Tag!, table.Tag!, skeleton);
            Assert.Empty(result.Warnings);
            Assert.Empty(result.UnresolvedPlaceholders);
        }

        Assert.Contains("ze szkieletu", doc.MainDocumentPart!.Document!.Body!.InnerText);
    }
}
