using System.Xml;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Interfaces;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class OoxmlFragmentReader
{
    public OoxmlFragment? Read(XDocument document, IReadOnlyList<int> nodePath)
    {
        var node = Resolve(document, nodePath);

        return node is null ? null : new OoxmlFragment(SerializeStandalone(node), GetLineNumber(node));
    }

    public int? FindLine(XDocument document, IReadOnlyList<int> nodePath) =>
        GetLineNumber(Resolve(document, nodePath));

    private static XElement? Resolve(XDocument document, IReadOnlyList<int> nodePath)
    {
        var current = document.Root;

        foreach (var childIndex in nodePath)
        {
            if (current is null || childIndex < 0)
            {
                return null;
            }

            current = current.Elements().ElementAtOrDefault(childIndex);
        }

        return current;
    }

    private static int? GetLineNumber(XElement? node) =>
        node is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : null;

    private static string SerializeStandalone(XElement element)
    {
        var clone = new XElement(element);
        var content = clone.Nodes().ToArray();
        clone.RemoveNodes();

        foreach (var declaration in element.AncestorsAndSelf().Reverse()
                     .SelectMany(ancestor => ancestor.Attributes().Where(attribute => attribute.IsNamespaceDeclaration)))
        {
            clone.Attribute(declaration.Name)?.Remove();
            clone.Add(new XAttribute(declaration));
        }

        clone.Add(content);

        return clone.ToString(SaveOptions.None);
    }
}
