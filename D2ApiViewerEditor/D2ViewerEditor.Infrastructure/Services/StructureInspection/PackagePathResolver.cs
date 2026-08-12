namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public static class PackagePathResolver
{
    private const string RelationshipsExtension = ".rels";
    private const string RootRelationshipsPath = "_rels/.rels";
    private const string RelationshipsMarker = "/_rels/";
    private const string RootRelationshipsPrefix = "_rels/";

    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    public static string GetSourcePartPath(string relationshipsPath)
    {
        var normalized = Normalize(relationshipsPath);

        if (normalized.Equals(RootRelationshipsPath, StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(RelationshipsExtension, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (normalized.StartsWith(RootRelationshipsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return normalized[RootRelationshipsPrefix.Length..^RelationshipsExtension.Length];
        }

        var markerIndex = normalized.IndexOf(RelationshipsMarker, StringComparison.OrdinalIgnoreCase);

        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var directory = normalized[..markerIndex];
        var fileName = normalized[(markerIndex + RelationshipsMarker.Length)..^RelationshipsExtension.Length];

        return directory.Length == 0 ? fileName : $"{directory}/{fileName}";
    }

    public static string ResolveInternalTarget(string sourcePart, string target)
    {
        var targetPath = StripFragmentAndQuery(target);

        if (targetPath.StartsWith('/'))
        {
            return NormalizeSegments(targetPath);
        }

        var normalizedSource = Normalize(sourcePart);
        var separator = normalizedSource.LastIndexOf('/');
        var sourceDirectory = separator < 0 ? string.Empty : normalizedSource[..separator];

        return NormalizeSegments(sourceDirectory.Length == 0 ? targetPath : $"{sourceDirectory}/{targetPath}");
    }

    public static bool EscapesPackageRoot(string sourcePart, string target)
    {
        var targetPath = StripFragmentAndQuery(target);

        if (targetPath.StartsWith('/'))
        {
            return false;
        }

        var normalizedSource = Normalize(sourcePart);
        var separator = normalizedSource.LastIndexOf('/');
        var depth = separator < 0
            ? 0
            : normalizedSource[..separator].Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

        foreach (var segment in targetPath.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment != "..")
            {
                depth++;
                continue;
            }

            if (depth == 0)
            {
                return true;
            }

            depth--;
        }

        return false;
    }

    private static string StripFragmentAndQuery(string target)
    {
        var separator = target.IndexOfAny(['#', '?']);
        var path = separator < 0 ? target : target[..separator];

        try
        {
            return Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return path;
        }
    }

    private static string NormalizeSegments(string path)
    {
        var segments = new List<string>();

        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }
}
