using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;

namespace EfektRuno.Templating.Workers;

/// <summary>
/// Worker 2: "pieczątka" - any area between &lt;%#lista%&gt; and &lt;%/lista%&gt; (a multi-row table, a table plus
/// paragraphs, a framed box, a run inside a paragraph) is stamped once per array item. Everything inside a stamp
/// is delegated to sub-workers through the pipeline: nested stamps, simple tables, conditions and values.
/// </summary>
public sealed class StampWorker : IDocxWorker
{
    /// <summary>Sub-workers by section name, valid only inside this stamp (take precedence over the pipeline registry).</summary>
    public Dictionary<string, IDocxWorker> SubWorkers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Standalone use: document + list name + values.</summary>
    public void Fill(WordprocessingDocument document, string listName, JsonNode? items, WorkerPipeline? pipeline = null)
    {
        (pipeline ?? new WorkerPipeline()).FillNamed(document, listName, items, this);
    }

    public void Fill(Section section, DataContext context, WorkerPipeline pipeline)
    {
        WorkerPipeline inner = SubWorkers.Count > 0 ? pipeline.With(SubWorkers) : pipeline;

        List<DataContext> iterations = section.Iterations(context, pipeline.Options);
        for (int i = 0; i < iterations.Count; i++)
            section.Emit(iterations[i], inner, stripIds: i > 0);

        section.RemoveTemplate();
    }
}
