using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json.Linq;

namespace DocxPipeline.Tests;

internal static class TestDocs
{
    public static JToken Model() => JToken.Parse("""
        {
          "temat_sprawy": "Odnowienie CDD",
          "osoba_fizyczna": true,
          "uwagi": null,
          "data_pisma": "2026-10-08T07:30:00Z",
          "kwota": 1234.5,
          "zalaczniki": [ { "nazwa": "a.pdf", "dodajacy": "Jan" }, { "nazwa": "b.jpg", "dodajacy": "Anna" } ]
        }
        """);

    public static WordprocessingDocument Create(params OpenXmlElement[] body)
    {
        var doc = WordprocessingDocument.Create(new MemoryStream(), WordprocessingDocumentType.Document, autoSave: true);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(body));
        return doc;
    }

    public static Body BodyOf(WordprocessingDocument doc) => doc.MainDocumentPart!.Document!.Body!;

    public static List<string> ParagraphTexts(WordprocessingDocument doc) => BodyOf(doc).Descendants<Paragraph>().Select(p => p.InnerText).ToList();

    public static Paragraph P(params string[] runs) =>
        new(runs.Select(r => (OpenXmlElement)new Run(new Text(r) { Space = SpaceProcessingModeValues.Preserve })));

    public static Paragraph Bookmarked(string bookmark, string before, string after = "") =>
        new(new Run(new Text(before) { Space = SpaceProcessingModeValues.Preserve }),
            new BookmarkStart { Id = "1", Name = bookmark }, new BookmarkEnd { Id = "1" },
            new Run(new Text(after) { Space = SpaceProcessingModeValues.Preserve }));

    private static int _id = 100;

    public static SdtBlock BlockControl(string tag, params OpenXmlElement[] content) =>
        new(new SdtProperties(new Tag { Val = tag }, new SdtId { Val = ++_id }), new SdtContentBlock(content));

    public static IEnumerable<string> ControlTags(OpenXmlElement scope) =>
        scope.Descendants<SdtElement>().Select(s => s.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value).OfType<string>();

    public static Table AttachmentsTable() => new(
        new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }),
        new TableGrid(new GridColumn(), new GridColumn()),
        new TableRow(new TableCell(P("Nazwa")), new TableCell(P("Dodał"))),
        new TableRow(new TableCell(P("<%nazwa%>")), new TableCell(P("<%dodajacy%>"))));
}
