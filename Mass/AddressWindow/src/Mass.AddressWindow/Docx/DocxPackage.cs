using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Mass.AddressWindow.Docx;

internal sealed class DocxFormatException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class DocxPackage
{
    private const long MaxPartBytes = 64L * 1024 * 1024;

    private readonly Dictionary<string, XDocument> _headersByRelId;

    private DocxPackage(XDocument document, XDocument? styles, Dictionary<string, XDocument> headersByRelId)
    {
        Document = document;
        Styles = styles;
        _headersByRelId = headersByRelId;
    }

    public XDocument Document { get; }

    public XDocument? Styles { get; }

    public XElement Body => Document.Root?.Element(Ns.W + "body")
        ?? throw new DocxFormatException("Dokument nie zawiera treści (brak elementu w:body).");

    public XDocument? GetHeader(string relationshipId) =>
        _headersByRelId.TryGetValue(relationshipId, out var header) ? header : null;

    public static DocxPackage Open(Stream stream)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new DocxFormatException(
                "Plik nie jest dokumentem DOCX (nie jest archiwum ZIP). Pliki .doc i dokumenty zaszyfrowane hasłem nie są obsługiwane.",
                ex);
        }

        using (archive)
        {
            try
            {
                return Read(archive);
            }
            catch (XmlException ex)
            {
                throw new DocxFormatException($"Uszkodzony XML w dokumencie: {ex.Message}", ex);
            }
            catch (InvalidDataException ex)
            {
                throw new DocxFormatException($"Uszkodzone archiwum DOCX: {ex.Message}", ex);
            }
        }
    }

    private static DocxPackage Read(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            entries[entry.FullName.Replace('\\', '/').TrimStart('/')] = entry;
        }

        var rootRels = ReadRelationships(entries, "_rels/.rels");
        var mainPath = rootRels.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target
            ?? (entries.ContainsKey("word/document.xml") ? "word/document.xml" : null)
            ?? throw new DocxFormatException("Brak głównej części dokumentu (word/document.xml).");

        var document = LoadXml(entries, mainPath)
            ?? throw new DocxFormatException($"Brak części {mainPath}.");
        if (document.Root?.Name != Ns.W + "document")
        {
            throw new DocxFormatException("Główna część pakietu nie jest dokumentem Word.");
        }

        var docRels = ReadRelationships(entries, RelsPathFor(mainPath))
            .Select(r => r with { Target = ResolvePath(mainPath, r.Target) })
            .ToList();

        XDocument? styles = null;
        var stylesRel = docRels.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal));
        if (stylesRel.Target is not null)
        {
            styles = LoadXml(entries, stylesRel.Target);
        }

        var headers = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var rel in docRels.Where(r => r.Type.EndsWith("/header", StringComparison.Ordinal)))
        {
            var header = LoadXml(entries, rel.Target);
            if (header is not null)
            {
                headers[rel.Id] = header;
            }
        }

        return new DocxPackage(document, styles, headers);
    }

    private readonly record struct Relationship(string Id, string Type, string Target);

    private static List<Relationship> ReadRelationships(Dictionary<string, ZipArchiveEntry> entries, string relsPath)
    {
        var xml = LoadXml(entries, relsPath, normalize: false);
        if (xml?.Root is null)
        {
            return [];
        }

        return xml.Root.Elements(Ns.PackageRels + "Relationship")
            .Where(e => !string.Equals((string?)e.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            .Select(e => new Relationship(
                (string?)e.Attribute("Id") ?? "",
                (string?)e.Attribute("Type") ?? "",
                Uri.UnescapeDataString((string?)e.Attribute("Target") ?? "")))
            .Where(r => r.Target.Length > 0)
            .Select(r => relsPath == "_rels/.rels" ? r with { Target = r.Target.TrimStart('/') } : r)
            .ToList();
    }

    private static string RelsPathFor(string partPath)
    {
        var slash = partPath.LastIndexOf('/');
        return slash < 0
            ? $"_rels/{partPath}.rels"
            : $"{partPath[..slash]}/_rels/{partPath[(slash + 1)..]}.rels";
    }

    internal static string ResolvePath(string sourcePart, string target)
    {
        if (target.StartsWith('/'))
        {
            return target.TrimStart('/');
        }

        var segments = new List<string>();
        var slash = sourcePart.LastIndexOf('/');
        if (slash > 0)
        {
            segments.AddRange(sourcePart[..slash].Split('/'));
        }

        foreach (var segment in target.Split('/'))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment is not ("." or ""))
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    private static XDocument? LoadXml(Dictionary<string, ZipArchiveEntry> entries, string path, bool normalize = true)
    {
        if (!entries.TryGetValue(path, out var entry))
        {
            return null;
        }

        if (entry.Length > MaxPartBytes)
        {
            throw new DocxFormatException($"Część {path} jest za duża ({entry.Length / 1024 / 1024} MB).");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxPartBytes,
            IgnoreComments = true,
        };

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader);
        if (normalize)
        {
            NormalizeStrictNamespaces(document);
        }

        return document;
    }

    private static void NormalizeStrictNamespaces(XDocument document)
    {
        if (document.Root is null)
        {
            return;
        }

        foreach (var element in document.Root.DescendantsAndSelf())
        {
            if (Ns.StrictToTransitional.TryGetValue(element.Name.NamespaceName, out var ns))
            {
                element.Name = ns + element.Name.LocalName;
            }

            if (!element.Attributes().Any(a => Ns.StrictToTransitional.ContainsKey(a.Name.NamespaceName)))
            {
                continue;
            }

            var attributes = element.Attributes()
                .Select(a => Ns.StrictToTransitional.TryGetValue(a.Name.NamespaceName, out var ans)
                    ? new XAttribute(ans + a.Name.LocalName, a.Value)
                    : a)
                .ToList();
            element.ReplaceAttributes(attributes);
        }
    }
}
