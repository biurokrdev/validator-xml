using DocumentFormat.OpenXml.Wordprocessing;
using DocxPipeline.TableTable;
using static DocxPipeline.Tests.TestDocs;

namespace DocxPipeline.Tests;

public class WorkerTests
{
    private static PipelineRunner Runner() => new(new WorkerRegistry().AddAssembly(typeof(RecordTableWorker).Assembly));

    private static PipelineResult Run(DocumentFormat.OpenXml.Packaging.WordprocessingDocument doc, string stepsJson) =>
        Runner().Run(doc, Model(), PipelineDefinition.FromJson("{ \"steps\": " + stepsJson + " }"));

    [Fact]
    public void Field_replacer_binds_by_name_and_by_mapping_and_handles_split_runs()
    {
        using var doc = Create(
            P("Temat: <%temat_sprawy%>"),
            P("Kwota: <%", "kwo", "ta%> zł"),
            P("Data: <%data%>, osoba: <%osoba_fizyczna%>, brak: <%nie_ma%>"));

        var result = Run(doc, """
            [ { "worker": "field-replacer", "fields": [ { "search-for": "<%data%>", "model-property-path": "data_pisma", "display-format": "dd.MM.yyyy" } ] } ]
            """);

        Assert.True(result.Succeeded);
        Assert.Equal(["Temat: Odnowienie CDD", "Kwota: 1234,5 zł", "Data: 08.10.2026, osoba: Tak, brak: <%nie_ma%>"], ParagraphTexts(doc));
        Assert.Single(result.Steps[0].Warnings, w => w.Contains("<%nie_ma%>"));

        using var doc2 = Create(P("<%nie_ma%>"));
        Run(doc2, """[ { "worker": "field-replacer", "unresolved-text": "" } ]""");
        Assert.Equal([""], ParagraphTexts(doc2));
    }

    [Fact]
    public void Content_control_remover_unconditional_conditional_and_unwrap()
    {
        using var doc = Create(
            BlockControl("zawsze", P("A")),
            BlockControl("gdy_firma", P("B")),
            BlockControl("gdy_osoba", P("C")),
            BlockControl("ramka", P("D")));

        var result = Run(doc, """
            [ { "worker": "content-control-remover", "tag-name": "zawsze" },
              { "worker": "content-control-remover", "tag-name": "gdy_firma", "remove-when": "osoba_fizyczna" },
              { "worker": "content-control-remover", "tag-name": "gdy_osoba", "remove-when": "osoba_fizyczna", "negate": true },
              { "worker": "content-control-remover", "tag-name": "ramka", "unwrap": true },
              { "worker": "content-control-remover", "tag-name": "nie_ma" } ]
            """);

        Assert.True(result.Succeeded);
        Assert.Equal(["C", "D"], ParagraphTexts(doc));
        Assert.Equal(["gdy_osoba"], ControlTags(BodyOf(doc)));
        Assert.Single(result.Steps[4].Warnings, w => w.Contains("nie_ma"));
    }

    [Fact]
    public void Bookmark_text_inserts_replaces_or_removes_paragraph()
    {
        using var doc = Create(
            Bookmarked("temat", "Temat: ", "."),
            Bookmarked("caly", "stary tekst"),
            Bookmarked("uwagi", "Uwagi: "),
            Bookmarked("brak_zakladki_w_modelu", "x"));

        var result = Run(doc, """
            [ { "worker": "bookmark-text", "bookmark-name": "temat", "model-property-path": "temat_sprawy" },
              { "worker": "bookmark-text", "bookmark-name": "caly", "text": "nowy\ntekst", "replace-paragraph": true },
              { "worker": "bookmark-text", "bookmark-name": "uwagi", "model-property-path": "uwagi", "remove-paragraph-when-empty": true },
              { "worker": "bookmark-text", "bookmark-name": "nie_ma", "text": "x" } ]
            """);

        Assert.True(result.Succeeded);
        Assert.Equal(["Temat: Odnowienie CDD.", "nowytekst", "x"], ParagraphTexts(doc));
        Assert.Single(BodyOf(doc).Descendants<Paragraph>().ElementAt(1).Descendants<Break>());
        Assert.Single(result.Steps[3].Warnings, w => w.Contains("nie_ma"));
    }

    [Fact]
    public void Record_table_plugin_fills_table_from_pipeline_step()
    {
        using var doc = Create(P("Załączniki"), AttachmentsTable());

        var result = Run(doc, """
            [ { "worker": "record-table", "model-property-path": "zalaczniki", "empty-text": "brak" } ]
            """);

        Assert.True(result.Succeeded);
        var rows = BodyOf(doc).Descendants<TableRow>().Select(r => r.InnerText).ToList();
        Assert.Equal(["NazwaDodał", "a.pdfJan", "b.jpgAnna"], rows);
    }

    [Fact]
    public void Whole_pipeline_combines_workers_on_one_document()
    {
        using var doc = Create(
            P("Pismo: <%temat_sprawy%>"),
            BlockControl("klauzula_firmowa", P("Klauzula dla firm")),
            Bookmarked("uwagi", "Uwagi: "),
            AttachmentsTable());
        var pipeline = PipelineDefinition.FromJson("""
            {
              "name": "pismo",
              "steps": [
                { "worker": "field-replacer" },
                { "worker": "content-control-remover", "tag-name": "klauzula_firmowa", "remove-when": "osoba_fizyczna" },
                { "worker": "bookmark-text", "bookmark-name": "uwagi", "model-property-path": "uwagi", "null-text": "brak uwag" },
                { "worker": "record-table", "model-property-path": "zalaczniki" }
              ]
            }
            """);

        var result = Runner().Run(doc, Model(), pipeline);

        Assert.True(result.Succeeded, string.Join("; ", result.Steps.Select(s => s.Message)));
        Assert.Equal(["Pismo: Odnowienie CDD", "Uwagi: brak uwag", "Nazwa", "Dodał", "a.pdf", "Jan", "b.jpg", "Anna"], ParagraphTexts(doc));
    }
}
