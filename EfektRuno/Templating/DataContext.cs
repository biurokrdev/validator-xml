using System.Text.Json.Nodes;

namespace EfektRuno.Templating;

/// <summary>
/// Stack of JSON scopes. A name is looked up in the current item first, then in the parents,
/// so &lt;%application_number%&gt; still resolves inside a repeated message block.
/// </summary>
public sealed class DataContext(JsonNode? node, DataContext? parent)
{
    public JsonNode? Node { get; } = node;

    public DataContext? Parent { get; } = parent;

    public bool TryResolve(string path, out JsonNode? value)
    {
        if (path == ".")
        {
            value = Node;
            return true;
        }

        string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (DataContext? scope = this; scope != null; scope = scope.Parent)
        {
            if (scope.Node is not JsonObject root || !root.TryGetPropertyValue(segments[0], out JsonNode? current))
                continue;

            for (int i = 1; i < segments.Length; i++)
            {
                if (current is JsonObject nested && nested.TryGetPropertyValue(segments[i], out JsonNode? next))
                {
                    current = next;
                    continue;
                }

                value = null;
                return false;
            }

            value = current;
            return true;
        }

        value = null;
        return false;
    }
}
