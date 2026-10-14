using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json.Linq;
using static TableTable.Tests.TestDocs;

namespace TableTable.Tests;

/// <summary>Prosta tabela załączników: nagłówek i jeden wiersz-szablon z czterema znacznikami.</summary>
public class SimpleAttachmentTableTests
{
    private static Table AttachmentsTable() => new(
        new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }),
        new TableGrid(new GridColumn(), new GridColumn(), new GridColumn(), new GridColumn()),
        Row(Cell(P("Nazwa załącznika")), Cell(P("Data dołączenia")), Cell(P("Użytkownik")), Cell(P("Archiwizuj"))),
        Row(Cell(P("<%nazwa_zalacznika%>")), Cell(P("<%data_dolaczenia%>")), Cell(P("<%dodajacy%>")), Cell(P("<%archiwizuj%>"))));

    private static JToken AttachmentsModel() => JToken.Parse("""
        {
          "zalaczniki": [
            { "nazwa": "umowa.pdf",   "data": "2026-09-14T08:15:00Z", "uzytkownik": "Jan Kowalski", "archiwizuj": true },
            { "nazwa": "skan.jpg",    "data": "2026-09-15",           "uzytkownik": "Anna Nowak",   "archiwizuj": false },
            { "nazwa": "notatka.txt", "data": null,                   "uzytkownik": null,           "archiwizuj": true }
          ]
        }
        """);

    [Fact]
    public void Each_placeholder_mapped_to_its_model_property()
    {
        using var doc = Create(AttachmentsTable());
        var binding = CustomTableDataBinding.FromJson("""
            {
              "model-property-path": "zalaczniki",
              "empty-text": "Brak załączników",
              "fields": [
                { "search-for": "<%nazwa_zalacznika%>", "replacement-property-path": "nazwa" },
                { "search-for": "<%data_dolaczenia%>",  "replacement-property-path": "data", "display-format": "dd-MM-yyyy", "null-text": "–" },
                { "search-for": "<%dodajacy%>",         "replacement-property-path": "uzytkownik", "null-text": "system" },
                { "search-for": "<%archiwizuj%>",       "replacement-property-path": "archiwizuj" }
              ]
            }
            """);

        var result = new RecordTableBuilder().Apply(doc, binding, AttachmentsModel());

        Assert.Equal(3, result.RecordCount);
        Assert.Empty(result.Warnings);
        var rows = Rows(doc).Select(CellTexts).ToList();
        Assert.Equal(["Nazwa załącznika", "Data dołączenia", "Użytkownik", "Archiwizuj"], rows[0]);
        Assert.Equal(["umowa.pdf", "14-09-2026", "Jan Kowalski", "Tak"], rows[1]);
        Assert.Equal(["skan.jpg", "15-09-2026", "Anna Nowak", "Nie"], rows[2]);
        Assert.Equal(["notatka.txt", "–", "system", "Tak"], rows[3]);
        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void Without_field_mappings_placeholders_bind_by_property_name()
    {
        using var doc = Create(AttachmentsTable());
        var model = JToken.Parse("""
            { "zalaczniki": [ { "nazwa_zalacznika": "umowa.pdf", "data_dolaczenia": "2026-09-14", "dodajacy": "Jan", "archiwizuj": true } ] }
            """);

        var result = new RecordTableBuilder().Apply(doc, new CustomTableDataBinding { ModelPropertyPath = "zalaczniki" }, model);

        Assert.Empty(result.UnresolvedPlaceholders);
        Assert.Equal(["umowa.pdf", "14.09.2026", "Jan", "Tak"], CellTexts(Rows(doc)[1]));
    }

    [Fact]
    public void Empty_list_replaces_template_row_with_full_width_text()
    {
        using var doc = Create(AttachmentsTable());
        var binding = new CustomTableDataBinding { ModelPropertyPath = "zalaczniki", EmptyText = "Brak załączników" };

        new RecordTableBuilder().Apply(doc, binding, JToken.Parse("""{ "zalaczniki": [] }"""));

        var rows = Rows(doc);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Brak załączników", rows[1].InnerText);
        Assert.Equal(4, rows[1].GetFirstChild<TableCell>()!.TableCellProperties!.GridSpan!.Val!.Value);
    }
}
