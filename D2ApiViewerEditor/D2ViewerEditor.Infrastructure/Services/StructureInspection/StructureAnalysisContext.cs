using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class StructureAnalysisContext
{
    private readonly Dictionary<XElement, InspectedElement> _elementsByNode;
    private readonly Dictionary<string, List<IndexedNode>> _wordprocessingByLocalName;
    private readonly Dictionary<string, InspectedElement> _rootsByPart;

    public StructureAnalysisContext(
        IReadOnlyList<IndexedNode> nodes,
        RawPackageContents package,
        OpcPackageAnalysis opc,
        SafeOoxmlXmlLoader xmlLoader,
        CancellationToken cancellationToken)
    {
        Nodes = nodes;
        Package = package;
        Opc = opc;
        XmlLoader = xmlLoader;
        CancellationToken = cancellationToken;

        _elementsByNode = new Dictionary<XElement, InspectedElement>(nodes.Count);
        _wordprocessingByLocalName = new Dictionary<string, List<IndexedNode>>(StringComparer.Ordinal);
        _rootsByPart = new Dictionary<string, InspectedElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            _elementsByNode[node.Node] = node.Element;

            if (node.Element.Depth == 0)
            {
                _rootsByPart.TryAdd(node.Element.PartPath, node.Element);
            }

            if (!OoxmlNamespaces.IsWordprocessing(node.Element.NamespaceUri))
            {
                continue;
            }

            if (!_wordprocessingByLocalName.TryGetValue(node.Element.LocalName, out var bucket))
            {
                bucket = [];
                _wordprocessingByLocalName[node.Element.LocalName] = bucket;
            }

            bucket.Add(node);
        }
    }

    public IReadOnlyList<IndexedNode> Nodes { get; }
    public RawPackageContents Package { get; }
    public OpcPackageAnalysis Opc { get; }
    public SafeOoxmlXmlLoader XmlLoader { get; }
    public CancellationToken CancellationToken { get; }

    public string MainDocumentPartPath => Opc.MainDocumentPartPath;

    public InspectedElement? FindElement(XElement? node) =>
        node is null ? null : _elementsByNode.GetValueOrDefault(node);

    public IEnumerable<IndexedNode> WordprocessingNodes(params string[] localNames)
    {
        if (localNames.Length == 1)
        {
            return _wordprocessingByLocalName.GetValueOrDefault(localNames[0]) ?? Enumerable.Empty<IndexedNode>();
        }

        return localNames
            .SelectMany(localName => _wordprocessingByLocalName.GetValueOrDefault(localName) ?? Enumerable.Empty<IndexedNode>())
            .OrderBy(node => node.Element.Order);
    }

    public InspectedElement? FindPartRoot(string partPath) => _rootsByPart.GetValueOrDefault(partPath);

    public RawPackagePart? FindRelatedPart(string relationshipTypeSuffix, string contentTypeFragment)
    {
        var byRelationship = Opc.Relationships.FindTargetByType(MainDocumentPartPath, relationshipTypeSuffix);

        if (byRelationship is not null && Package.XmlParts.TryGetValue(byRelationship, out var related))
        {
            return related;
        }

        return Package.XmlParts.Values.FirstOrDefault(part =>
            part.ContentType?.Contains(contentTypeFragment, StringComparison.OrdinalIgnoreCase) == true);
    }
}

public interface IStructureAnalyzer
{
    void Analyze(StructureAnalysisContext context);
}
