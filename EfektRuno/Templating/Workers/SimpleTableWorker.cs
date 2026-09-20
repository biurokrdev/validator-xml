using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace EfektRuno.Templating.Workers;

/// <summary>
/// Worker 1: flat table. The template row carries &lt;%#lista%&gt; in its first cell and &lt;%/lista%&gt; in the last one;
/// each cell says by its own token which JSON field lands in that column, so column order in JSON is irrelevant
/// (unlike DocXTableBuilder + OrderColumnTableAttribute). The row is cloned once per array item.
/// </summary>
public sealed class SimpleTableWorker : IDocxWorker
{
    /// <summary>
    /// Optional positional fallback for templates without tokens in cells: JSON field per column index.
    /// Cells that do have tokens keep the token mapping.
    /// </summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>When the list is empty, drop the whole table instead of leaving the header row.</summary>
    public bool RemoveTableWhenEmpty { get; init; }

    /// <summary>Standalone use, mirroring DocXTableBuilder: document + list name + values.</summary>
    public void Fill(WordprocessingDocument document, string listName, JsonNode? rows, WorkerPipeline? pipeline = null)
    {
        (pipeline ?? new WorkerPipeline()).FillNamed(document, listName, rows, this);
    }

    public void Fill(Section section, DataContext context, WorkerPipeline pipeline)
    {
        if (!section.IsRowRange)
        {
            throw new TemplateException(
                $"Sekcja <%{section.Name}%> nie obejmuje wierszy tabeli - znaczniki muszą stać w pierwszej i ostatniej komórce wiersza-szablonu.");
        }

        List<DataContext> iterations = section.Iterations(context, pipeline.Options);
        for (int i = 0; i < iterations.Count; i++)
        {
            DataContext item = iterations[i];
            section.Emit(item, pipeline, stripIds: i > 0, beforeFill: holder => FillPositional(holder, item, pipeline));
        }

        OpenXmlElement? table = section.Range[0].Parent;
        section.RemoveTemplate();

        if (iterations.Count == 0 && RemoveTableWhenEmpty && table is Table)
            table.Remove();
    }

    private void FillPositional(OpenXmlElement holder, DataContext item, WorkerPipeline pipeline)
    {
        if (Columns is null)
            return;

        var formatter = new ValueFormatter(pipeline.Options);
        foreach (TableRow row in holder.Descendants<TableRow>())
        {
            List<TableCell> cells = row.Elements<TableCell>().ToList();
            for (int column = 0; column < Columns.Count && column < cells.Count; column++)
            {
                TableCell cell = cells[column];
                if (cell.Descendants<Run>().Any(r => Section.TokenOf(r) != null))
                    continue;

                if (!item.TryResolve(pipeline.Options.MapPath(Columns[column]), out JsonNode? value))
                    continue;

                Paragraph paragraph = cell.Elements<Paragraph>().FirstOrDefault() ?? cell.AppendChild(new Paragraph());
                RunProperties? style = paragraph.Descendants<RunProperties>().FirstOrDefault();
                paragraph.RemoveAllChildren<Run>();

                var run = new Run();
                if (style != null)
                    run.RunProperties = (RunProperties)style.CloneNode(true);
                ValueFormatter.SetRunText(run, formatter.Format(value, null));
                paragraph.AppendChild(run);
            }
        }
    }
}
