using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json.Linq;

namespace TableTable.Tests;

/// <summary>Buduje w pamięci dokumenty z tabelą jak na wzorze (rodzaj / wysłano / przez, treść, załączniki w formancie-liście).</summary>
internal static class TestDocs
{
    public const string ModelJson = """
        {
          "numer_sprawy": "OH/2026/0142",
          "wiadomosci": [
            {
              "rodzaj": "E-mail",
              "kierunek": "wyslana",
              "wyslana": true,
              "data_czas": "2026-09-14T08:15:00Z",
              "nadawca": "Jan Kowalski",
              "tresc": "Dzień dobry,\nprzesyłam dokumenty.",
              "kwota": 1234.5,
              "zalaczniki": [ { "nazwa": "umowa.pdf", "rozmiar": 2048 }, { "nazwa": "zal_1.xlsx", "rozmiar": 512 } ]
            },
            {
              "rodzaj": "SMS",
              "kierunek": "odebrana",
              "wyslana": false,
              "data_czas": "2026-09-15T06:00:00Z",
              "nadawca": "System",
              "tresc": "Przypomnienie",
              "kwota": null,
              "zalaczniki": []
            },
            {
              "rodzaj": "Pismo",
              "kierunek": "wyslana",
              "wyslana": true,
              "data_czas": "2026-09-16",
              "nadawca": "Urząd Miasta",
              "tresc": null,
              "zalaczniki": [ { "nazwa": "decyzja.pdf", "rozmiar": 99 } ]
            }
          ]
        }
        """;

    public static JToken Model() => JToken.Parse(ModelJson);

    public static CustomTableDataBinding Definition() => new()
    {
        ModelPropertyPath = "wiadomosci",
        Fields =
        {
            new FieldMapping { SearchFor = "<%rodzaj_wiadomosci%>", ReplacementPropertyPath = "rodzaj" },
            new FieldMapping { SearchFor = "<%data_czas_wiadomosci%>", ReplacementPropertyPath = "data_czas" },
            new FieldMapping { SearchFor = "<%nadawca_wiadomosci%>", ReplacementPropertyPath = "nadawca" },
            new FieldMapping { SearchFor = "<%tresc_wiadomosci%>", ReplacementPropertyPath = "tresc", NullText = "(brak treści)" },
        },
        Collections =
        {
            new CollectionMapping
            {
                TagName = "zalaczniki",
                Fields = { new FieldMapping { SearchFor = "<%nazwa_pliku%>", ReplacementPropertyPath = "nazwa" } },
            },
        },
    };

    /// <summary>Nowy dokument na MemoryStream z podanymi elementami w treści. Dokument pozostaje otwarty do inspekcji.</summary>
    public static WordprocessingDocument Create(params OpenXmlElement[] body)
    {
        var stream = new MemoryStream();
        var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(body));
        return doc;
    }

    /// <summary>
    /// Tabela: nagłówek „Korespondencja”, trzy wiersze szablonu rekordu (rodzaj/wysłano/przez; treść; załączniki = formant „zalaczniki”
    /// owijający akapit z punktorem) i stopka „Koniec”.
    /// </summary>
    public static Table MessagesTable(bool splitTags = false, bool bookmarkInside = false)
    {
        var rodzaj = splitTags ? P("<%", "rodzaj_", "wiadomosci%>") : P("<%rodzaj_wiadomosci%>");
        var firstCell = Cell(P(string.Empty));
        if (bookmarkInside)
            firstCell.GetFirstChild<Paragraph>()!.Append(new BookmarkStart { Id = "7", Name = "tabela_wiadomosci" }, new BookmarkEnd { Id = "7" });

        return new Table(
            new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }),
            new TableGrid(new GridColumn(), new GridColumn(), new GridColumn(), new GridColumn()),
            Row(SpanCell(4, P("Korespondencja"))),
            Row(
                firstCell,
                Cell(P("Rodzaj wiadomości"), rodzaj),
                Cell(P("Wysłano"), P("Odebrano"), P("<%data_czas_wiadomosci%>")),
                Cell(P("Przez"), P("Od"), P("<%nadawca_wiadomosci%>"))),
            Row(Cell(P(string.Empty)), Cell(P("Treść wiadomości")), SpanCell(2, P("<%tresc_wiadomosci%>"))),
            Row(Cell(P(string.Empty)), Cell(P("Załączniki")), SpanCell(2, BlockControl("zalaczniki", Bullet("<%nazwa_pliku%>")))),
            Row(SpanCell(4, P("Koniec"))));
    }

    /// <summary>Akapit z grafiką pływającą zakotwiczoną pionowo względem strony.</summary>
    public static Paragraph PageAnchoredGraphicP(string name, uint id = 1) =>
        new(new Run(new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalPosition { RelativeFrom = DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalRelativePositionValues.Page },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = id, Name = name }))));

    private static int _sdtId = 1000;

    private static SdtProperties SdtProps(string? tag, OpenXmlElement? extra = null)
    {
        var props = new SdtProperties();
        if (tag != null) props.Append(new SdtAlias { Val = tag }, new Tag { Val = tag });
        props.Append(new SdtId { Val = ++_sdtId });
        if (extra != null) props.Append(extra);
        return props;
    }

    /// <summary>Formant w tekście (run-level) z podaną zawartością, np. runem z grafiką.</summary>
    public static SdtRun RunControl(string tag, params OpenXmlElement[] content) => new(SdtProps(tag), new SdtContentRun(content));

    /// <summary>Formant blokowy (akapity, tabele).</summary>
    public static SdtBlock BlockControl(string tag, params OpenXmlElement[] content) => new(SdtProps(tag), new SdtContentBlock(content));

    /// <summary>Formant wierszy tabeli.</summary>
    public static SdtRow RowControl(string tag, params TableRow[] rows) => new(SdtProps(tag), new SdtContentRow(rows));

    /// <summary>Formant komórki tabeli.</summary>
    public static SdtCell CellControl(string tag, TableCell cell) => new(SdtProps(tag), new SdtContentCell(cell));

    /// <summary>Sekcja powtarzana Worda w tabeli: jeden element sekcji z podanymi wierszami.</summary>
    public static SdtRow RepeatingSection(string tag, params OpenXmlElement[] rows) =>
        new(SdtProps(tag, new DocumentFormat.OpenXml.Office2013.Word.SdtRepeatedSection()),
            new SdtContentRow(new SdtRow(SdtProps(null, new DocumentFormat.OpenXml.Office2013.Word.SdtRepeatedSectionItem()), new SdtContentRow(rows))));

    /// <summary>Run z poprawną schematowo grafiką w tekście (extent, docPr, graphic) o podanej nazwie.</summary>
    public static Run GraphicRun(string name, uint id = 1) =>
        new(new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 100000, Cy = 100000 },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = id, Name = name },
            new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData { Uri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape" }))));

    public static IEnumerable<string> ControlTags(OpenXmlElement scope) =>
        scope.Descendants<SdtElement>().Select(s => s.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value).OfType<string>();

    public static IEnumerable<string> GraphicNames(OpenXmlElement scope) =>
        scope.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().Select(d => d.Name!.Value!);

    public static Paragraph P(params string[] runs) =>
        new(runs.Select(r => (OpenXmlElement)new Run(new Text(r) { Space = SpaceProcessingModeValues.Preserve })));

    public static Paragraph Bullet(string text) =>
        new(new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    public static TableCell Cell(params OpenXmlElement[] content) => new(content);

    public static TableCell SpanCell(int span, params OpenXmlElement[] content)
    {
        var cell = new TableCell(new TableCellProperties(new GridSpan { Val = span }));
        foreach (var c in content) cell.Append(c);
        return cell;
    }

    public static TableRow Row(params OpenXmlElement[] cells) => new(cells);

    public static Table FirstTable(WordprocessingDocument doc) => doc.MainDocumentPart!.Document!.Body!.Descendants<Table>().First();

    public static List<TableRow> Rows(WordprocessingDocument doc) => FirstTable(doc).Elements<TableRow>().ToList();

    /// <summary>Teksty komórek wiersza, także komórek owiniętych formantem (<c>w:sdt/w:sdtContent/w:tc</c>).</summary>
    public static string[] CellTexts(TableRow row) =>
        row.ChildElements
            .SelectMany(c => c is TableCell tc ? [tc] : c is SdtCell ? c.Descendants<TableCell>().Take(1) : Enumerable.Empty<TableCell>())
            .Select(c => c.InnerText)
            .ToArray();

    public static bool IsBullet(Paragraph p) => p.ParagraphProperties?.NumberingProperties != null;
}
