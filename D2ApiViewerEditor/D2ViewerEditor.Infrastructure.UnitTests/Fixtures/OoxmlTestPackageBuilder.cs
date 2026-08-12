using System.IO.Compression;
using System.Text;

namespace D2ViewerEditor.Infrastructure.UnitTests.Fixtures;

public sealed class OoxmlTestPackageBuilder
{
    public const string OfficeDocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    public const string MainDocumentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    private readonly Dictionary<string, string> _xmlParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _binaryParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<RelationshipEntry>> _relationships = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rels"] = "application/vnd.openxmlformats-package.relationships+xml",
        ["xml"] = "application/xml",
        ["png"] = "image/png"
    };

    private string? _contentTypesOverride;

    public string MainDocumentPath { get; private set; } = "word/document.xml";

    public static string RelationshipType(string suffix) =>
        $"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{suffix}";

    public OoxmlTestPackageBuilder WithMainDocument(string xml, string path = "word/document.xml")
    {
        MainDocumentPath = path;
        _xmlParts[path] = xml;
        _overrides[path] = MainDocumentContentType;

        return WithRelationship(string.Empty, "rId1", OfficeDocumentRelationship, path);
    }

    public OoxmlTestPackageBuilder WithPart(string path, string xml, string contentType)
    {
        _xmlParts[path] = xml;
        _overrides[path] = contentType;

        return this;
    }

    public OoxmlTestPackageBuilder WithBinaryPart(string path, byte[] content)
    {
        _binaryParts[path] = content;

        return this;
    }

    public OoxmlTestPackageBuilder WithRelationship(
        string sourcePart,
        string id,
        string type,
        string target,
        string? targetMode = null)
    {
        if (!_relationships.TryGetValue(sourcePart, out var entries))
        {
            entries = [];
            _relationships[sourcePart] = entries;
        }

        entries.Add(new RelationshipEntry(id, type, target, targetMode));

        return this;
    }

    public OoxmlTestPackageBuilder WithRawContentTypes(string xml)
    {
        _contentTypesOverride = xml;

        return this;
    }

    public OoxmlTestPackageBuilder WithRawRelationshipsPart(string relationshipsPath, string xml)
    {
        _xmlParts[relationshipsPath] = xml;

        return this;
    }

    public byte[] Build()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteText(archive, "[Content_Types].xml", _contentTypesOverride ?? BuildContentTypes());

            foreach (var relationshipPart in BuildRelationshipParts())
            {
                if (!_xmlParts.ContainsKey(relationshipPart.Key))
                {
                    WriteText(archive, relationshipPart.Key, relationshipPart.Value);
                }
            }

            foreach (var part in _xmlParts)
            {
                WriteText(archive, part.Key, part.Value);
            }

            foreach (var part in _binaryParts)
            {
                WriteBytes(archive, part.Key, part.Value);
            }
        }

        return buffer.ToArray();
    }

    private string BuildContentTypes()
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""");

        foreach (var pair in _defaults)
        {
            builder.AppendLine($"""  <Default Extension="{pair.Key}" ContentType="{pair.Value}"/>""");
        }

        foreach (var pair in _overrides)
        {
            builder.AppendLine($"""  <Override PartName="/{pair.Key}" ContentType="{pair.Value}"/>""");
        }

        builder.Append("</Types>");

        return builder.ToString();
    }

    private Dictionary<string, string> BuildRelationshipParts()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in _relationships)
        {
            var builder = new StringBuilder();
            builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
            builder.AppendLine("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");

            foreach (var entry in group.Value)
            {
                var targetMode = entry.TargetMode is null ? string.Empty : $""" TargetMode="{entry.TargetMode}" """.TrimEnd();
                builder.AppendLine(
                    $"""  <Relationship Id="{entry.Id}" Type="{entry.Type}" Target="{entry.Target}"{targetMode}/>""");
            }

            builder.Append("</Relationships>");
            result[GetRelationshipsPath(group.Key)] = builder.ToString();
        }

        return result;
    }

    private static string GetRelationshipsPath(string sourcePart)
    {
        if (sourcePart.Length == 0)
        {
            return "_rels/.rels";
        }

        var separator = sourcePart.LastIndexOf('/');
        var directory = separator < 0 ? string.Empty : sourcePart[..separator];
        var fileName = separator < 0 ? sourcePart : sourcePart[(separator + 1)..];

        return directory.Length == 0 ? $"_rels/{fileName}.rels" : $"{directory}/_rels/{fileName}.rels";
    }

    private static void WriteText(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteBytes(ZipArchive archive, string path, byte[] content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(content, 0, content.Length);
    }

    public static byte[] PngPixel() =>
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private sealed record RelationshipEntry(string Id, string Type, string Target, string? TargetMode);
}
