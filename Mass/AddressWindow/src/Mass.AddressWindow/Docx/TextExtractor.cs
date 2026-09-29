using System.Text;
using System.Xml.Linq;

namespace Mass.AddressWindow.Docx;

internal sealed class LineContent
{
    private readonly StringBuilder _text = new();
    private readonly List<double> _fontSizes = [];

    public string Text => _text.ToString();

    public IReadOnlyList<double> FontSizes => _fontSizes;

    public bool Italic { get; private set; }

    public bool Underline { get; private set; }

    public int VisualLines { get; set; } = 1;

    public bool HasVisibleText => _text.ToString().Any(c => !char.IsWhiteSpace(c));

    internal void Append(string text, RunProps props)
    {
        _text.Append(text);
        if (text.Any(c => !char.IsWhiteSpace(c)))
        {
            _fontSizes.Add(props.FontSizePt);
            Italic |= props.Italic;
            Underline |= props.Underline;
        }
    }
}

internal sealed class ParagraphContent(XElement source, ParagraphProps props)
{
    public XElement Source { get; } = source;

    public ParagraphProps Props { get; } = props;

    public List<LineContent> Lines { get; } = [new LineContent()];

    public bool HasVisibleText => Lines.Any(l => l.HasVisibleText);

    public double MaxFontSizePt => Lines.SelectMany(l => l.FontSizes).DefaultIfEmpty(Props.MarkFontSizePt).Max();
}

internal sealed class TextExtractor(StyleResolver styles)
{
    private static readonly HashSet<XName> Containers =
    [
        Ns.W + "hyperlink", Ns.W + "smartTag", Ns.W + "ins", Ns.W + "moveTo", Ns.W + "fldSimple",
        Ns.W + "customXml", Ns.W + "sdtContent", Ns.W + "bdo", Ns.W + "dir",
    ];

    public ParagraphContent Read(XElement paragraph)
    {
        var content = new ParagraphContent(paragraph, styles.ResolveParagraph(paragraph));
        Walk(paragraph, content);
        return content;
    }

    public IReadOnlyList<ParagraphContent> Read(IEnumerable<XElement> paragraphs) => paragraphs.Select(Read).ToList();

    private void Walk(XElement container, ParagraphContent content)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name == Ns.W + "r")
            {
                ReadRun(child, content);
            }
            else if (child.Name == Ns.W + "sdt")
            {
                if (child.Element(Ns.W + "sdtContent") is { } sdtContent)
                {
                    Walk(sdtContent, content);
                }
            }
            else if (Containers.Contains(child.Name))
            {
                Walk(child, content);
            }
        }
    }

    private void ReadRun(XElement run, ParagraphContent content)
    {
        var props = styles.ResolveRun(run, content.Props);
        if (props.Hidden)
        {
            return;
        }

        foreach (var child in run.Elements())
        {
            var name = child.Name.LocalName;
            if (child.Name.Namespace != Ns.W)
            {
                continue;
            }

            switch (name)
            {
                case "t":
                    content.Lines[^1].Append(props.Caps ? child.Value.ToUpperInvariant() : child.Value, props);
                    break;
                case "tab":
                case "ptab":
                    content.Lines[^1].Append(" ", props);
                    break;
                case "noBreakHyphen":
                    content.Lines[^1].Append("-", props);
                    break;
                case "br":
                case "cr":
                    content.Lines.Add(new LineContent());
                    break;
            }
        }
    }
}
