using DocxPipeline.TableTable;
using DocxPipeline.Workers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DocxPipeline.Tests;

/// <summary>Worker testowy bez atrybutu – nazwa z klasy; dopisuje się do listy w Items (kolejność wykonania).</summary>
public sealed class TraceStepWorker : IDocxWorker
{
    [JsonProperty("label")]
    public string Label { get; set; } = "?";

    [JsonProperty("fail")]
    public bool Fail { get; set; }

    public void Run(DocxWorkerContext context)
    {
        if (!context.Items.TryGetValue("trace", out var list) || list is not List<string> trace)
            context.Items["trace"] = trace = new List<string>();
        trace.Add(Label);
        if (Fail) throw new InvalidOperationException($"krok {Label} nie powiódł się");
    }
}

[DocxWorker("alias-z-atrybutu", Description = "opis")]
public sealed class AttributedWorker : IDocxWorker
{
    public void Run(DocxWorkerContext context) => context.Warn("uruchomiony");
}

public class RegistryTests
{
    [Fact]
    public void Built_in_workers_are_discovered_by_reflection()
    {
        var registry = new WorkerRegistry();

        Assert.Equal(["bookmark-text", "content-control-remover", "field-replacer"], registry.Workers.Select(w => w.Name));
        Assert.IsType<FieldReplacerWorker>(registry.Create("field-replacer"));
        Assert.All(registry.Workers, w => Assert.False(string.IsNullOrWhiteSpace(w.Description)));
    }

    [Fact]
    public void Name_comes_from_attribute_or_class_name_in_kebab_case()
    {
        var registry = new WorkerRegistry().AddAssembly(typeof(RegistryTests).Assembly);

        Assert.True(registry.Contains("trace-step"));
        Assert.True(registry.Contains("alias-z-atrybutu"));
        Assert.Equal("opis", registry.Workers.Single(w => w.Name == "alias-z-atrybutu").Description);
        Assert.Equal("field-replacer", WorkerRegistry.DefaultName(typeof(FieldReplacerWorker)));
        Assert.Equal("record-table", WorkerRegistry.DefaultName(typeof(RecordTableWorker)));
    }

    [Fact]
    public void Workers_from_other_assemblies_and_plugin_folder_are_found()
    {
        var byAssembly = new WorkerRegistry().AddAssembly(typeof(RecordTableWorker).Assembly);
        Assert.True(byAssembly.Contains("record-table"));

        // Katalog wtyczek: bin testów zawiera DocxPipeline.TableTable.dll – ładowanie refleksją po nazwie pliku.
        var byFolder = new WorkerRegistry().AddPluginsFolder(AppContext.BaseDirectory, "DocxPipeline.TableTable.dll");
        Assert.True(byFolder.Contains("record-table"));

        var loaded = WorkerRegistry.FromLoadedAssemblies();
        Assert.True(loaded.Contains("record-table"));
        Assert.True(loaded.Contains("trace-step"));
    }

    [Fact]
    public void Create_fills_configuration_from_json_and_ignores_unknown_keys()
    {
        var registry = new WorkerRegistry();
        var json = JObject.Parse("""{ "tag-name": "klauzula", "negate": true, "cos-obcego": 1 }""");

        var worker = Assert.IsType<ContentControlRemoverWorker>(registry.Create("content-control-remover", json));

        Assert.Equal("klauzula", worker.TagName);
        Assert.True(worker.Negate);
    }

    [Fact]
    public void Unknown_worker_lists_available_names()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => new WorkerRegistry().Create("nie-ma"));
        Assert.Contains("field-replacer", ex.Message);
    }

    [Fact]
    public void Same_name_for_different_types_is_an_error()
    {
        var registry = new WorkerRegistry();
        Assert.Throws<InvalidOperationException>(() => registry.Add(typeof(DuplicateFieldReplacerWorker)));
    }

    [DocxWorker("field-replacer")]
    private sealed class DuplicateFieldReplacerWorker : IDocxWorker
    {
        public void Run(DocxWorkerContext context) { }
    }
}
