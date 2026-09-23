using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.DocumentHealth;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

public sealed class DocumentComparer : IDocumentComparer
{
    private static readonly string[] DocumentPropertyParts = ["docProps/core.xml", "docProps/app.xml", "docProps/custom.xml"];

    private static readonly string[] RevisionAttributeNames =
    [
        "rsid", "rsidR", "rsidRPr", "rsidRDefault", "rsidP", "rsidDel", "rsidTr", "rsidSect"
    ];

    private readonly FileContainerCheck _containerCheck;
    private readonly SafeOoxmlXmlLoader _xmlLoader;
    private readonly XmlTreeDiffer _differ;
    private readonly DocumentCompareOptions _options;

    public DocumentComparer(
        FileContainerCheck containerCheck,
        SafeOoxmlXmlLoader xmlLoader,
        IOptions<DocumentCompareOptions> options)
    {
        _containerCheck = containerCheck;
        _xmlLoader = xmlLoader;
        _options = options.Value;
        _differ = new XmlTreeDiffer(_options);
    }

    public DocumentComparisonReport Compare(DocumentCompareRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var (leftPackage, leftFile) = Read(request.LeftBytes, request.LeftFileName);
        var (rightPackage, rightFile) = Read(request.RightBytes, request.RightFileName);

        var parts = new List<ComparedPart>();
        var differences = new List<DocumentDifference>();
        var total = 0;
        var truncated = false;
        var ignoredAttributes = 0;

        if (leftPackage is not null && rightPackage is not null)
        {
            var ignoredAttributeNames = request.IgnoreRevisionIds ? RevisionAttributes() : new HashSet<XName>();
            var ignoredElementNames = request.IgnoreRevisionIds
                ? new HashSet<XName> { XName.Get("rsids", OoxmlNamespaces.WordprocessingTransitional), XName.Get("rsids", OoxmlNamespaces.WordprocessingStrict) }
                : new HashSet<XName>();

            foreach (var path in UnionPaths(leftPackage, rightPackage, request.IgnoreDocumentProperties))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var left = leftPackage.Find(path);
                var right = rightPackage.Find(path);
                var partDifferences = new List<DocumentDifference>();
                var part = ComparePart(path, left, right, ignoredAttributeNames, ignoredElementNames, partDifferences,
                    ref total, ref ignoredAttributes, ref truncated, cancellationToken);

                parts.Add(part);

                foreach (var difference in partDifferences)
                {
                    if (differences.Count >= _options.MaxDifferences)
                    {
                        truncated = true;
                        break;
                    }

                    differences.Add(difference);
                }
            }
        }

        stopwatch.Stop();

        return new DocumentComparisonReport
        {
            Left = leftFile,
            Right = rightFile,
            IgnoreRevisionIds = request.IgnoreRevisionIds,
            IgnoreDocumentProperties = request.IgnoreDocumentProperties,
            Identical = leftPackage is not null && rightPackage is not null && total == 0 &&
                        parts.All(part => part.Status == ComparedPartStatus.Identical),
            TotalDifferences = total,
            Truncated = truncated,
            IgnoredAttributeCount = ignoredAttributes,
            CountsByKind = differences.GroupBy(difference => difference.Kind.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            CountsByCategory = differences.GroupBy(difference => difference.Category)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            Parts = parts,
            Differences = differences,
            ComparedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private (LenientPackage? Package, ComparedFile File) Read(byte[] bytes, string fileName)
    {
        var findings = new HealthFindingCollector(50, 5);
        var package = _containerCheck.Run(bytes, findings, out var format);
        var notes = findings.Findings
            .Where(finding => finding.Severity != StructureIssueSeverity.Info)
            .Select(finding => finding.Title)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();

        if (package is null)
        {
            notes.Insert(0, "Plik nie jest pakietem ZIP/OOXML — porównanie treści niemożliwe.");
        }

        return (package, new ComparedFile(fileName, bytes.LongLength, RefineFormat(package, format), package?.Entries.Count ?? 0, notes));
    }

    private static string RefineFormat(LenientPackage? package, string fallback)
    {
        if (package is null || package.DetectedFormat != "ooxml")
        {
            return package?.DetectedFormat ?? fallback;
        }

        var contentTypes = package.ReadText("[Content_Types].xml") ?? string.Empty;

        if (contentTypes.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase)) return "xlsx";
        if (contentTypes.Contains("presentationml", StringComparison.OrdinalIgnoreCase)) return "pptx";
        if (!contentTypes.Contains("wordprocessingml", StringComparison.OrdinalIgnoreCase)) return "ooxml";

        var template = contentTypes.Contains("wordprocessingml.template", StringComparison.OrdinalIgnoreCase);
        var macro = contentTypes.Contains("macroEnabled.main", StringComparison.OrdinalIgnoreCase);

        return (template, macro) switch
        {
            (true, true) => "dotm",
            (true, false) => "dotx",
            (false, true) => "docm",
            _ => "docx"
        };
    }

    private static IEnumerable<string> UnionPaths(LenientPackage left, LenientPackage right, bool ignoreDocumentProperties)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in left.Entries.Concat(right.Entries))
        {
            if (ignoreDocumentProperties && DocumentPropertyParts.Contains(entry.Path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            paths.Add(entry.Path);
        }

        return paths.OrderBy(Rank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

        static int Rank(string path) => path switch
        {
            "[Content_Types].xml" => 0,
            "_rels/.rels" => 1,
            _ when path.EndsWith("document.xml", StringComparison.OrdinalIgnoreCase) => 2,
            _ when path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) => 4,
            _ => 3
        };
    }

    private ComparedPart ComparePart(
        string path,
        LenientPackageEntry? left,
        LenientPackageEntry? right,
        IReadOnlySet<XName> ignoredAttributes,
        IReadOnlySet<XName> ignoredElements,
        List<DocumentDifference> differences,
        ref int total,
        ref int ignoredAttributeCount,
        ref bool truncated,
        CancellationToken cancellationToken)
    {
        var isXml = (left ?? right)!.IsXml;

        if (left is null || right is null)
        {
            var present = left ?? right!;
            var kind = left is null ? DifferenceKind.PartOnlyInRight : DifferenceKind.PartOnlyInLeft;
            total++;
            differences.Add(new DocumentDifference(kind, CategoryOfPart(path), path, null, null, null, null, path,
                left is null ? null : $"{left.Length} B", right is null ? null : $"{right.Length} B",
                left is null ? null : PartExcerpt(present), right is null ? null : PartExcerpt(present),
                present.IsXml && present.Bytes is { Length: > 0 } && present.Bytes.Length > _options.MaxExcerptChars,
                null, null));

            return new ComparedPart(path, left is null ? ComparedPartStatus.OnlyInRight : ComparedPartStatus.OnlyInLeft, isXml, 1,
                left?.Length, right?.Length, null, null, null);
        }

        if (left.Bytes is null || right.Bytes is null)
        {
            return new ComparedPart(path, ComparedPartStatus.Unreadable, isXml, 0, left.Length, right.Length, null, null,
                left.ReadError ?? right.ReadError ?? "Nie udało się odczytać wpisu.");
        }

        if (left.Bytes.AsSpan().SequenceEqual(right.Bytes))
        {
            return new ComparedPart(path, ComparedPartStatus.Identical, isXml, 0, left.Length, right.Length, null, null, null);
        }

        if (!isXml)
        {
            total++;
            differences.Add(new DocumentDifference(DifferenceKind.BinaryPartChanged, CategoryOfPart(path), path, null, null, null, null, path,
                $"{left.Length} B, SHA-256 {ShortHash(left.Bytes)}", $"{right.Length} B, SHA-256 {ShortHash(right.Bytes)}",
                null, null, false, null, null));

            return new ComparedPart(path, ComparedPartStatus.Changed, isXml, 1, left.Length, right.Length, null, null,
                "Część binarna różni się bajtami (obraz, font, obiekt OLE).");
        }

        XDocument leftDocument, rightDocument;

        try
        {
            leftDocument = Parse(left);
            rightDocument = Parse(right);
        }
        catch (XmlException exception)
        {
            return new ComparedPart(path, ComparedPartStatus.Unreadable, isXml, 0, left.Length, right.Length, null, null,
                $"Niepoprawny XML: {exception.Message}");
        }

        if (leftDocument.Root is null || rightDocument.Root is null)
        {
            return new ComparedPart(path, ComparedPartStatus.Unreadable, isXml, 0, left.Length, right.Length, null, null, "Część bez elementu korzenia.");
        }

        var result = _differ.Compare(leftDocument.Root, rightDocument.Root, ignoredAttributes, ignoredElements,
            _options.MaxDifferencesPerPart, cancellationToken);

        total += result.TotalCount;
        ignoredAttributeCount += result.IgnoredAttributeCount;
        truncated |= result.Truncated;

        foreach (var difference in result.Differences)
        {
            differences.Add(Map(path, difference));
        }

        var leftElements = leftDocument.Root.DescendantsAndSelf().Count();
        var rightElements = rightDocument.Root.DescendantsAndSelf().Count();

        return result.TotalCount == 0
            ? new ComparedPart(path, ComparedPartStatus.Identical, isXml, 0, left.Length, right.Length, leftElements, rightElements,
                "Bajty różnią się, ale po pominięciu ignorowanych atrybutów i białych znaków XML jest identyczny.")
            : new ComparedPart(path, ComparedPartStatus.Changed, isXml, result.TotalCount, left.Length, right.Length, leftElements, rightElements,
                result.Truncated ? $"Pokazano {result.Differences.Count} z {result.TotalCount} różnic." : null);
    }

    private XDocument Parse(LenientPackageEntry entry)
    {
        using var stream = new MemoryStream(entry.Bytes!, writable: false);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return _xmlLoader.Load(reader.ReadToEnd(), LoadOptions.SetLineInfo);
    }

    private DocumentDifference Map(string partPath, XmlDifference difference)
    {
        var anchor = difference.Left ?? difference.Right!;
        var leftTruncated = false;
        var rightTruncated = false;
        var excerptLeft = difference.Left is null ? null : Excerpt(difference.Left, out leftTruncated);
        var excerptRight = difference.Right is null ? null : Excerpt(difference.Right, out rightTruncated);

        return new DocumentDifference(
            difference.Kind,
            Categorize(partPath, anchor),
            partPath,
            difference.Left is null ? null : HealthLocations.PathOf(difference.Left),
            difference.Right is null ? null : HealthLocations.PathOf(difference.Right),
            Line(difference.Left),
            Line(difference.Right),
            difference.Name,
            difference.LeftValue,
            difference.RightValue,
            excerptLeft,
            excerptRight,
            leftTruncated || rightTruncated,
            Context(difference.Left),
            Context(difference.Right));
    }

    private string Excerpt(XElement element, out bool truncated)
    {
        var xml = element.ToString();
        truncated = xml.Length > _options.MaxExcerptChars;

        return truncated ? xml[.._options.MaxExcerptChars] + "\n… (ucięto)" : xml;
    }

    private string PartExcerpt(LenientPackageEntry entry)
    {
        if (!entry.IsXml || entry.Bytes is null)
        {
            return $"(część binarna, {entry.Length} B)";
        }

        var text = System.Text.Encoding.UTF8.GetString(entry.Bytes);

        return text.Length > _options.MaxExcerptChars ? text[.._options.MaxExcerptChars] + "\n… (ucięto)" : text;
    }

    private string? Context(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var paragraph = element.AncestorsAndSelf().FirstOrDefault(ancestor =>
            ancestor.Name.LocalName == "p" && OoxmlNamespaces.IsWordprocessing(ancestor.Name.NamespaceName));

        if (paragraph is null)
        {
            return null;
        }

        var text = string.Concat(paragraph.Descendants()
            .Where(node => node.Name.LocalName == "t" && OoxmlNamespaces.IsWordprocessing(node.Name.NamespaceName))
            .Select(node => node.Value)).Trim();

        if (text.Length == 0)
        {
            return null;
        }

        return text.Length > _options.MaxContextChars ? text[.._options.MaxContextChars] + "…" : text;
    }

    private static int? Line(XElement? element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;

    private static string ShortHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    private static HashSet<XName> RevisionAttributes()
    {
        var names = new HashSet<XName>();

        foreach (var ns in new[] { OoxmlNamespaces.WordprocessingTransitional, OoxmlNamespaces.WordprocessingStrict })
        {
            foreach (var name in RevisionAttributeNames)
            {
                names.Add(XName.Get(name, ns));
            }
        }

        return names;
    }

    private static string CategoryOfPart(string path)
    {
        var lower = path.ToLowerInvariant();

        if (lower == "[content_types].xml") return "Typy zawartości";
        if (lower.EndsWith(".rels")) return "Relationshipy";
        if (lower.StartsWith("docprops/")) return "Właściwości dokumentu";
        if (lower.StartsWith("customxml/")) return "Custom XML";
        if (lower.Contains("/media/")) return "Obrazy";
        if (lower.Contains("/fonts/")) return "Fonty osadzone";
        if (lower.Contains("/embeddings/")) return "Obiekty osadzone";
        if (lower.Contains("styles.xml")) return "Style";
        if (lower.Contains("numbering.xml")) return "Numeracja";
        if (lower.Contains("settings.xml")) return "Ustawienia";
        if (lower.Contains("fonttable.xml")) return "Tabela fontów";
        if (lower.Contains("/header")) return "Nagłówek";
        if (lower.Contains("/footer")) return "Stopka";
        if (lower.Contains("footnotes.xml") || lower.Contains("endnotes.xml")) return "Przypisy";
        if (lower.Contains("comments")) return "Komentarze";
        if (lower.Contains("theme")) return "Motyw";
        if (lower.Contains("glossary")) return "Glosariusz";
        if (lower.EndsWith("document.xml")) return "Treść";

        return "Inne";
    }

    private static string Categorize(string partPath, XElement element)
    {
        var partCategory = CategoryOfPart(partPath);

        if (partCategory is not ("Treść" or "Nagłówek" or "Stopka" or "Przypisy" or "Komentarze" or "Glosariusz"))
        {
            return partCategory;
        }

        foreach (var ancestor in element.AncestorsAndSelf())
        {
            if (!OoxmlNamespaces.IsWordprocessing(ancestor.Name.NamespaceName))
            {
                if (OoxmlNamespaces.IsWordprocessingDrawing(ancestor.Name.NamespaceName) ||
                    OoxmlNamespaces.IsDrawingMain(ancestor.Name.NamespaceName) ||
                    OoxmlNamespaces.IsVml(ancestor.Name.NamespaceName))
                {
                    return "Grafika";
                }

                continue;
            }

            switch (ancestor.Name.LocalName)
            {
                case "sectPr": return "Sekcja";
                case "numPr": return "Numeracja";
                case "rPr": return "Formatowanie znaku";
                case "pPr": return "Formatowanie akapitu";
                case "tblPr" or "tblGrid" or "trPr" or "tcPr" or "tblPrEx": return "Tabela: właściwości";
                case "drawing" or "pict" or "object": return "Grafika";
                case "fldChar" or "instrText" or "fldSimple": return "Pole";
                case "sdtPr": return "Formant";
                case "hyperlink": return "Hiperłącze";
                case "footnoteReference" or "endnoteReference" or "commentReference": return "Odwołanie";
                case "t" or "r": return "Tekst";
                case "tbl" or "tr" or "tc": return "Tabela";
                case "p": return "Akapit";
            }
        }

        return partCategory;
    }
}
