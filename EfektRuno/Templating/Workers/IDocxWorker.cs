namespace EfektRuno.Templating.Workers;

/// <summary>
/// Fills one located template section. Workers are composable: a worker asks the
/// <see cref="WorkerPipeline"/> to process the inside of every copy it produces, and the
/// pipeline dispatches nested sections to the right sub-worker (by name, then by shape).
/// </summary>
public interface IDocxWorker
{
    void Fill(Section section, DataContext context, WorkerPipeline pipeline);
}
