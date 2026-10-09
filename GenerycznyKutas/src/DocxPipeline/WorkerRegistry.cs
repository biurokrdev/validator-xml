using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DocxPipeline;

/// <summary>Opis workera dostępnego w rejestrze.</summary>
public sealed record WorkerInfo(string Name, Type Type, string? Description);

/// <summary>
/// Rejestr workerów odkrywanych refleksją: wszystkie niestatyczne, nieabstrakcyjne klasy implementujące <see cref="IDocxWorker"/>
/// z podanych assembly (w tym własnego), z assembly już załadowanych do procesu oraz z plików DLL w katalogu wtyczek.
/// Liczba i rodzaj workerów nie są znane z góry – konfiguracja odwołuje się do nich po nazwie.
/// </summary>
public sealed class WorkerRegistry
{
    private readonly Dictionary<string, WorkerInfo> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializer _serializer = JsonSerializer.CreateDefault();

    /// <summary>Rejestr z workerami wbudowanymi w tę bibliotekę.</summary>
    public WorkerRegistry() => AddAssembly(typeof(WorkerRegistry).Assembly);

    /// <summary>Zarejestrowane workery (nazwa, typ, opis), posortowane po nazwie.</summary>
    public IReadOnlyCollection<WorkerInfo> Workers => _workers.Values.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Rejestr z workerami wbudowanymi i ze wszystkich assembly załadowanych do procesu (np. aplikacji hosta).</summary>
    public static WorkerRegistry FromLoadedAssemblies()
    {
        var registry = new WorkerRegistry();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            registry.AddAssembly(assembly);
        return registry;
    }

    /// <summary>Dodaje workery z assembly. Ten sam typ można dodać wielokrotnie; inny typ pod tą samą nazwą to błąd konfiguracji.</summary>
    public WorkerRegistry AddAssembly(Assembly assembly)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        // Tylko typy widoczne publicznie – prywatne/zagnieżdżone klasy pomocnicze nie są wtyczkami.
        foreach (var type in types.Where(t => typeof(IDocxWorker).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false, IsVisible: true }))
            Add(type);
        return this;
    }

    /// <summary>Dodaje pojedynczy typ workera.</summary>
    public WorkerRegistry Add(Type type)
    {
        if (!typeof(IDocxWorker).IsAssignableFrom(type) || type.IsAbstract)
            throw new ArgumentException($"Typ {type.FullName} nie jest workerem (IDocxWorker).", nameof(type));

        var attribute = type.GetCustomAttribute<DocxWorkerAttribute>();
        var name = attribute?.Name ?? DefaultName(type);
        if (_workers.TryGetValue(name, out var existing) && existing.Type != type)
            throw new InvalidOperationException($"Worker '{name}' jest zarejestrowany dwa razy: {existing.Type.FullName} i {type.FullName}.");

        _workers[name] = new WorkerInfo(name, type, attribute?.Description);
        return this;
    }

    /// <summary>Ładuje wszystkie DLL z katalogu (wtyczki) i rejestruje ich workery. Brak katalogu = nic.</summary>
    public WorkerRegistry AddPluginsFolder(string directory, string searchPattern = "*.dll")
    {
        if (!Directory.Exists(directory)) return this;
        foreach (var file in Directory.EnumerateFiles(directory, searchPattern))
        {
            try { AddAssembly(Assembly.LoadFrom(file)); }
            catch (BadImageFormatException) { /* nie jest assembly .NET (np. natywna DLL) */ }
        }

        return this;
    }

    /// <summary>Czy worker o tej nazwie jest dostępny.</summary>
    public bool Contains(string name) => _workers.ContainsKey(name);

    /// <summary>Tworzy worker po nazwie i wypełnia jego konfigurację z JSON kroku (klucze nieznane workerowi są ignorowane).</summary>
    public IDocxWorker Create(string name, JObject? configuration = null)
    {
        if (!_workers.TryGetValue(name, out var info))
            throw new KeyNotFoundException($"Nieznany worker '{name}'. Dostępne: {string.Join(", ", _workers.Keys.OrderBy(k => k))}.");

        var worker = (configuration ?? new JObject()).ToObject(info.Type, _serializer)
                     ?? throw new InvalidOperationException($"Nie udało się utworzyć workera '{name}' ({info.Type.FullName}).");
        return (IDocxWorker)worker;
    }

    /// <summary><c>FieldReplacerWorker</c> → <c>field-replacer</c>.</summary>
    internal static string DefaultName(Type type)
    {
        var name = type.Name.EndsWith("Worker", StringComparison.Ordinal) ? type.Name[..^6] : type.Name;
        return Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant();
    }
}
