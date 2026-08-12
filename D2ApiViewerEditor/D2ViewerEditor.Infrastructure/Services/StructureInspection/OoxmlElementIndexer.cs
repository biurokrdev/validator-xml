using System.Xml;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Models;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed record IndexedNode(InspectedElement Element, XElement Node);

public sealed record OoxmlElementIndex(
    IReadOnlyList<IndexedNode> Nodes,
    IReadOnlyList<InspectedPackagePart> Parts,
    bool Truncated);

public sealed class OoxmlElementIndexer
{
    private readonly StructureInspectionOptions _options;
    private readonly SafeOoxmlXmlLoader _xmlLoader;
    private readonly OoxmlElementClassifier _classifier;

    public OoxmlElementIndexer(
        IOptions<StructureInspectionOptions> options,
        SafeOoxmlXmlLoader xmlLoader,
        OoxmlElementClassifier classifier)
    {
        _options = options.Value;
        _xmlLoader = xmlLoader;
        _classifier = classifier;
    }

    public OoxmlElementIndex Build(
        RawPackageContents package,
        OpcPackageAnalysis opc,
        CancellationToken cancellationToken)
    {
        var nodes = new List<IndexedNode>();
        var indexedParts = new List<InspectedPackagePart>(package.XmlParts.Count);
        var truncated = false;

        var orderedParts = package.XmlParts.Values
            .OrderBy(part => GetPartOrder(part, opc.MainDocumentPartPath))
            .ThenBy(part => part.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var partIndex = 0; partIndex < orderedParts.Length; partIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var part = orderedParts[partIndex];
            var countBefore = nodes.Count;
            var indexed = false;

            if (!truncated)
            {
                var root = LoadPart(part).Root;

                if (root is not null)
                {
                    var context = new PartIndexingContext(part, partIndex, opc, nodes);
                    truncated = !Visit(root, context, parentId: null, depth: 0, nodePath: [],
                        parentDisplayPath: string.Empty, sameNameIndex: 1, cancellationToken);
                    indexed = true;
                }
            }

            indexedParts.Add(new InspectedPackagePart(
                part.Path,
                part.ContentType,
                part.UncompressedSize,
                part.CompressedSize,
                nodes.Count - countBefore,
                indexed));
        }

        return new OoxmlElementIndex(nodes, indexedParts, truncated);
    }

    private XDocument LoadPart(RawPackagePart part)
    {
        try
        {
            return _xmlLoader.Load(part.Content);
        }
        catch (XmlException exception)
        {
            throw new InvalidOoxmlPackageException($"Część '{part.Path}' zawiera niepoprawny XML: {exception.Message}");
        }
    }

    private bool Visit(
        XElement node,
        PartIndexingContext context,
        string? parentId,
        int depth,
        IReadOnlyList<int> nodePath,
        string parentDisplayPath,
        int sameNameIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (depth > _options.MaxXmlDepth)
        {
            throw new InvalidOoxmlPackageException(
                $"Zagnieżdżenie XML przekracza limit {_options.MaxXmlDepth} poziomów.");
        }

        if (context.Nodes.Count >= _options.MaxElements)
        {
            return false;
        }

        var category = _classifier.GetCategory(node);
        var qualifiedName = OoxmlElementClassifier.GetQualifiedName(node);
        var displayPath = $"{parentDisplayPath}/{qualifiedName}[{sameNameIndex}]";
        var id = CreateElementId(context.PartIndex, nodePath);

        var element = new InspectedElement
        {
            Id = id,
            ParentId = parentId,
            PartPath = context.Part.Path,
            Depth = depth,
            Order = context.Nodes.Count,
            NodePath = nodePath,
            DisplayPath = displayPath,
            XmlName = qualifiedName,
            LocalName = node.Name.LocalName,
            NamespaceUri = node.Name.NamespaceName,
            Category = category,
            DisplayName = _classifier.GetDisplayName(node, category),
            Preview = _classifier.GetPreview(node, category),
            HasChildren = node.Elements().Any()
        };

        AddAttributes(element, node);
        AddRelationships(element, node, context.Opc);

        context.Nodes.Add(new IndexedNode(element, node));

        var childIndex = 0;
        var nameCounters = new Dictionary<XName, int>();

        foreach (var child in node.Elements())
        {
            nameCounters.TryGetValue(child.Name, out var previousCount);
            nameCounters[child.Name] = previousCount + 1;

            var childPath = new List<int>(nodePath.Count + 1);
            childPath.AddRange(nodePath);
            childPath.Add(childIndex);

            if (!Visit(child, context, id, depth + 1, childPath, displayPath, previousCount + 1, cancellationToken))
            {
                return false;
            }

            childIndex++;
        }

        return true;
    }

    private static void AddAttributes(InspectedElement element, XElement node)
    {
        foreach (var attribute in node.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            var prefix = node.GetPrefixOfNamespace(attribute.Name.Namespace);
            var qualifiedName = string.IsNullOrEmpty(prefix)
                ? attribute.Name.LocalName
                : $"{prefix}:{attribute.Name.LocalName}";

            element.Attributes.Add(new StructureAttribute(
                qualifiedName,
                attribute.Name.LocalName,
                attribute.Name.NamespaceName,
                attribute.Value,
                InterpretAttribute(attribute.Name.LocalName, attribute.Value)));
        }
    }

    private static void AddRelationships(InspectedElement element, XElement node, OpcPackageAnalysis opc)
    {
        foreach (var relationshipId in ReadRelationshipIds(element.Category, node))
        {
            var relationship = opc.Relationships.Find(element.PartPath, relationshipId);

            if (relationship is null)
            {
                element.Relationships.Add(new StructureRelationship(
                    element.PartPath,
                    string.Empty,
                    relationshipId,
                    string.Empty,
                    string.Empty,
                    "Internal",
                    null,
                    StructureRelationshipStatus.NotDeclared));

                element.Issues.Add(new StructureIssue(
                    StructureIssueCodes.ElementRelationshipNotDeclared,
                    StructureIssueSeverity.Error,
                    "Brak deklaracji relationshipu",
                    $"Element odwołuje się do '{relationshipId}', ale część '{element.PartPath}' nie deklaruje takiego relationshipu."));
                continue;
            }

            element.Relationships.Add(relationship);

            switch (relationship.Status)
            {
                case StructureRelationshipStatus.TargetMissing:
                    element.Issues.Add(new StructureIssue(
                        StructureIssueCodes.ElementRelationshipTargetMissing,
                        StructureIssueSeverity.Error,
                        "Cel relationshipu nie istnieje",
                        $"Relationship '{relationshipId}' wskazuje '{relationship.ResolvedTarget}', ale pakiet nie zawiera takiego wpisu."));
                    break;

                case StructureRelationshipStatus.External:
                    element.Issues.Add(new StructureIssue(
                        StructureIssueCodes.ElementRelationshipExternal,
                        StructureIssueSeverity.Warning,
                        "Zasób zewnętrzny",
                        $"Relationship '{relationshipId}' wskazuje zasób poza pakietem: {relationship.Target}."));
                    break;
            }
        }
    }

    private static IEnumerable<string> ReadRelationshipIds(string category, XElement node)
    {
        var attributes = category is ElementCategories.Drawing or ElementCategories.AnchoredDrawing
            or ElementCategories.InlineDrawing or ElementCategories.LegacyDrawingContainer
            or ElementCategories.EmbeddedObject or ElementCategories.DrawingGraphic
            ? node.DescendantsAndSelf().Attributes()
            : node.Attributes();

        return attributes
            .Where(attribute => OoxmlNamespaces.IsOfficeRelationship(attribute.Name.NamespaceName))
            .Where(attribute => attribute.Name.LocalName is "id" or "embed" or "link" or "dm" or "lo" or "qs" or "cs")
            .Select(attribute => attribute.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal);
    }

    private static string? InterpretAttribute(string localName, string value)
    {
        if (localName is not ("behindDoc" or "locked" or "layoutInCell" or "allowOverlap"
            or "simplePos" or "hidden" or "dirty" or "fldLock" or "noProof" or "rtl" or "vanish"))
        {
            return null;
        }

        return value switch
        {
            "1" or "true" or "on" => "true",
            "0" or "false" or "off" => "false",
            _ => null
        };
    }

    private static string CreateElementId(int partIndex, IReadOnlyList<int> nodePath) =>
        nodePath.Count == 0 ? $"p{partIndex}" : $"p{partIndex}-{string.Join('.', nodePath)}";

    private static int GetPartOrder(RawPackagePart part, string mainDocumentPartPath)
    {
        if (part.Path.Equals(mainDocumentPartPath, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var contentType = part.ContentType ?? string.Empty;

        if (contentType.Contains("styles", StringComparison.OrdinalIgnoreCase)) return 1;
        if (contentType.Contains("numbering", StringComparison.OrdinalIgnoreCase)) return 2;
        if (contentType.Contains("settings", StringComparison.OrdinalIgnoreCase)) return 3;
        if (contentType.Contains("header", StringComparison.OrdinalIgnoreCase)) return 4;
        if (contentType.Contains("footer", StringComparison.OrdinalIgnoreCase)) return 5;
        if (contentType.Contains("footnotes", StringComparison.OrdinalIgnoreCase)) return 6;
        if (contentType.Contains("endnotes", StringComparison.OrdinalIgnoreCase)) return 7;
        if (contentType.Contains("comments", StringComparison.OrdinalIgnoreCase)) return 8;
        if (part.Path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) return 20;

        return 10;
    }

    private sealed record PartIndexingContext(
        RawPackagePart Part,
        int PartIndex,
        OpcPackageAnalysis Opc,
        List<IndexedNode> Nodes);
}
