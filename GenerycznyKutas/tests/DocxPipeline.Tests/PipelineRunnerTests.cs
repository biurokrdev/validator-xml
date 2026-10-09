using static DocxPipeline.Tests.TestDocs;

namespace DocxPipeline.Tests;

public class PipelineRunnerTests
{
    private static PipelineRunner Runner() => new(new WorkerRegistry().AddAssembly(typeof(PipelineRunnerTests).Assembly));

    [Fact]
    public void Steps_run_sequentially_and_share_items()
    {
        using var doc = Create(P("x"));
        var pipeline = PipelineDefinition.FromJson("""
            { "name": "test", "steps": [
                { "worker": "trace-step", "label": "A" },
                { "worker": "trace-step", "label": "B" },
                { "worker": "trace-step", "label": "C", "enabled": false },
                { "worker": "trace-step", "label": "D" } ] }
            """);

        var result = Runner().Run(doc, Model(), pipeline);

        Assert.True(result.Succeeded);
        Assert.Equal([StepStatus.Done, StepStatus.Done, StepStatus.Skipped, StepStatus.Done], result.Steps.Select(s => s.Status));
        Assert.Equal(4, result.Steps.Count);
    }

    [Fact]
    public void When_and_unless_skip_steps_based_on_model()
    {
        using var doc = Create(P("x"));
        var pipeline = PipelineDefinition.FromJson("""
            { "steps": [
                { "worker": "trace-step", "label": "A", "when": "osoba_fizyczna" },
                { "worker": "trace-step", "label": "B", "when": "uwagi" },
                { "worker": "trace-step", "label": "C", "unless": "osoba_fizyczna" },
                { "worker": "trace-step", "label": "D", "unless": "zalaczniki[5]" } ] }
            """);

        var result = Runner().Run(doc, Model(), pipeline);

        Assert.Equal([StepStatus.Done, StepStatus.Skipped, StepStatus.Skipped, StepStatus.Done], result.Steps.Select(s => s.Status));
        Assert.Contains("when", result.Steps[1].Message);
    }

    [Fact]
    public void Failed_step_stops_pipeline_unless_on_error_continue()
    {
        using var doc = Create(P("x"));
        var stop = PipelineDefinition.FromJson("""
            { "steps": [ { "worker": "trace-step", "label": "A", "fail": true }, { "worker": "trace-step", "label": "B" } ] }
            """);
        var stopped = Runner().Run(doc, Model(), stop);
        Assert.False(stopped.Succeeded);
        Assert.Single(stopped.Steps);
        Assert.Equal(StepStatus.Failed, stopped.Steps[0].Status);
        Assert.Contains("krok A", stopped.Steps[0].Message);

        var go = PipelineDefinition.FromJson("""
            { "steps": [ { "worker": "nie-istnieje", "on-error": "continue" }, { "worker": "trace-step", "label": "A", "fail": true, "on-error": "continue" }, { "worker": "trace-step", "label": "B" } ] }
            """);
        var continued = Runner().Run(doc, Model(), go);
        Assert.False(continued.Succeeded);
        Assert.Equal([StepStatus.Failed, StepStatus.Failed, StepStatus.Done], continued.Steps.Select(s => s.Status));
        Assert.Contains("Nieznany worker", continued.Steps[0].Message);
    }

    [Fact]
    public void Warnings_are_collected_per_step()
    {
        using var doc = Create(P("x"));
        var pipeline = PipelineDefinition.FromJson("""{ "steps": [ { "worker": "alias-z-atrybutu" }, { "worker": "trace-step", "label": "A" } ] }""");

        var result = Runner().Run(doc, Model(), pipeline);

        Assert.Equal(["uruchomiony"], result.Steps[0].Warnings);
        Assert.Empty(result.Steps[1].Warnings);
        Assert.Equal(["[1. alias-z-atrybutu] uruchomiony"], result.AllWarnings);
    }

    [Fact]
    public void Step_keeps_pipeline_keys_apart_from_worker_configuration()
    {
        var pipeline = PipelineDefinition.FromJson("""{ "steps": [ { "worker": "field-replacer", "on-error": "continue", "when": "x", "auto-bind": false, "fields": [] } ] }""");

        var step = pipeline.Steps[0];

        Assert.Equal(StepErrorPolicy.Continue, step.OnError);
        Assert.Equal("x", step.When);
        Assert.Equal(["auto-bind", "fields"], step.ConfigurationObject().Properties().Select(p => p.Name));
    }
}
