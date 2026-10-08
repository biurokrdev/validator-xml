using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using Anchor = DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor;
using DocProperties = DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties;
using VerticalRelativePositionValues = DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalRelativePositionValues;

namespace TableTable.Internal;

/// <summary>Operacje strukturalne na Open XML potrzebne przy klonowaniu fragmentów tabeli.</summary>
internal static class OpenXmlHelpers
{
    /// <summary>Akapity w podanych elementach (sam element, gdy jest akapitem, plus zagnieżdżone), w kolejności dokumentu.</summary>
    public static IEnumerable<Paragraph> Paragraphs(IEnumerable<OpenXmlElement> units) =>
        units.SelectMany(u => u is Paragraph p ? [p] : u.Descendants<Paragraph>());

    /// <summary>Zakładki w sklonowanym fragmencie są usuwane, żeby nie powielać identyfikatorów.</summary>
    public static void StripBookmarks(OpenXmlElement element)
    {
        foreach (var b in element.Descendants<BookmarkStart>().Cast<OpenXmlElement>().Concat(element.Descendants<BookmarkEnd>()).ToList()) b.Remove();
    }

    /// <summary>Komórka musi kończyć się akapitem – po usunięciu zawartości dokładamy pusty.</summary>
    public static void EnsureCellEndsWithParagraph(OpenXmlElement? container)
    {
        if (container is TableCell cell && cell.LastChild is not Paragraph) cell.Append(new Paragraph());
    }

    /// <summary>Jeden wiersz z komórką scaloną na całą szerokość i podanym tekstem; formatowanie z wiersza wzorcowego.</summary>
    public static TableRow CreateFullWidthTextRow(TableRow sample, string text)
    {
        var columns = sample.Ancestors<Table>().FirstOrDefault()?.GetFirstChild<TableGrid>()?.Elements<GridColumn>().Count() ?? 0;
        if (columns == 0) columns = Math.Max(1, sample.Elements<TableCell>().Sum(c => c.TableCellProperties?.GridSpan?.Val?.Value ?? 1));

        var sampleCell = sample.GetFirstChild<TableCell>();
        var properties = sampleCell?.TableCellProperties?.CloneNode(true) as TableCellProperties ?? new TableCellProperties();
        properties.TableCellWidth?.Remove();
        properties.VerticalMerge?.Remove();
        properties.GridSpan = new GridSpan { Val = columns };

        var row = new TableRow();
        if (sample.TableRowProperties != null) row.Append(sample.TableRowProperties.CloneNode(true));
        row.Append(new TableCell(properties, CreateTextParagraph(sampleCell?.GetFirstChild<Paragraph>(), text, keepNumbering: false)));
        return row;
    }

    /// <summary>Nowy akapit z tekstem; właściwości akapitu i pierwszego runu z wzorca.</summary>
    public static Paragraph CreateTextParagraph(Paragraph? sample, string text, bool keepNumbering)
    {
        var paragraph = new Paragraph();
        if (sample?.ParagraphProperties != null)
        {
            var properties = (ParagraphProperties)sample.ParagraphProperties.CloneNode(true);
            if (!keepNumbering) properties.NumberingProperties?.Remove();
            paragraph.Append(properties);
        }

        var run = new Run();
        var sampleRun = sample?.Descendants<Run>().FirstOrDefault();
        if (sampleRun?.RunProperties != null) run.Append(sampleRun.RunProperties.CloneNode(true));
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.Append(run);
        return paragraph;
    }

    /// <summary>Grafiki pływające zakotwiczone względem strony/marginesu nie powielą się poprawnie – każdy klon trafi w to samo miejsce.</summary>
    public static IEnumerable<string> PageAnchoredGraphicNames(OpenXmlElement element) =>
        element.Descendants<Anchor>()
            .Where(a => a.VerticalPosition?.RelativeFrom?.Value is { } r
                        && (r == VerticalRelativePositionValues.Page || r == VerticalRelativePositionValues.Margin
                            || r == VerticalRelativePositionValues.TopMargin || r == VerticalRelativePositionValues.BottomMargin))
            .Select(a => a.GetFirstChild<DocProperties>()?.Name?.Value ?? "(bez nazwy)");

    /// <summary>Unikalne identyfikatory obiektów graficznych (<c>wp:docPr/@id</c>) w sklonowanych fragmentach.</summary>
    public sealed class DrawingIdFixer(OpenXmlElement scope)
    {
        private uint _next = (scope.Ancestors<OpenXmlPartRootElement>().FirstOrDefault() ?? scope)
            .Descendants<DocProperties>().Select(d => d.Id?.Value ?? 0u).DefaultIfEmpty(0u).Max() + 1;

        public void Fix(OpenXmlElement clone)
        {
            foreach (var dp in clone.Descendants<DocProperties>()) dp.Id = _next++;
        }
    }
}
