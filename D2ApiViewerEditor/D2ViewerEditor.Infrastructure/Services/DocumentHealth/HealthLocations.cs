using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public static class HealthLocations
{
    public static string Of(string partPath, XElement? element)
    {
        if (element is null)
        {
            return partPath;
        }

        var builder = new StringBuilder(partPath);

        if (element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
        {
            builder.Append(':').Append(lineInfo.LineNumber);
        }

        builder.Append(" — ").Append(PathOf(element));

        return builder.ToString();
    }

    public static string Of(string partPath, int? line, int? position)
    {
        if (line is null)
        {
            return partPath;
        }

        return position is null ? $"{partPath}:{line}" : $"{partPath}:{line}:{position}";
    }

    public static string PathOf(XElement element)
    {
        var segments = new Stack<string>();

        for (var current = element; current is not null; current = current.Parent)
        {
            var name = QualifiedName(current);
            var index = 1;

            for (var sibling = current.PreviousNode; sibling is not null; sibling = sibling.PreviousNode)
            {
                if (sibling is XElement previous && previous.Name == current.Name)
                {
                    index++;
                }
            }

            segments.Push($"{name}[{index}]");
        }

        return "/" + string.Join("/", segments);
    }

    public static string QualifiedName(XElement element)
    {
        var prefix = element.GetPrefixOfNamespace(element.Name.Namespace);

        return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : $"{prefix}:{element.Name.LocalName}";
    }

    public static string Preview(XElement element, int maxLength = 60)
    {
        var text = string.Concat(element.Descendants().Where(child => child.Name.LocalName == "t").Select(child => child.Value));

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Trim();

        return text.Length <= maxLength ? $" („{text}”)" : $" („{text[..maxLength]}…”)";
    }
}
