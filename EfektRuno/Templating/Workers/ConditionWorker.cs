namespace EfektRuno.Templating.Workers;

/// <summary>&lt;%?flag%&gt;, &lt;%^flag%&gt;, &lt;%?field==X%&gt;: keeps or drops the fragment, context unchanged.</summary>
public sealed class ConditionWorker : IDocxWorker
{
    public void Fill(Section section, DataContext context, WorkerPipeline pipeline)
    {
        if (section.Iterations(context, pipeline.Options).Count > 0)
            section.Emit(context, pipeline, stripIds: false);

        section.RemoveTemplate();
    }
}
