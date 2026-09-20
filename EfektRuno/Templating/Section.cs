using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using EfektRuno.Templating.Workers;

namespace EfektRuno.Templating;

/// <summary>
/// A template fragment enclosed by &lt;%#name%&gt; / &lt;%?name%&gt; / &lt;%^name%&gt; and &lt;%/name%&gt;.
/// The range is the lowest level on which both markers meet: runs of one paragraph,
/// paragraphs/tables of one body or cell, or whole table rows when the markers sit in different cells.
/// Markers are removed the moment the section is located.
/// </summary>
public sealed class Section
{
    private Section(TemplateToken token, List<OpenXmlElement> range)
    {
        Token = token;
        Range = range;
    }

    internal TemplateToken Token { get; }

    public string Name => Token.Path;

    public TokenKind Kind => Token.Kind;

    /// <summary>Sibling elements of the template fragment (markers already removed).</summary>
    public IReadOnlyList<OpenXmlElement> Range { get; }

    public bool IsRowRange => Range.All(e => e is TableRow);

    public bool IsInline => Range[0].Parent is Paragraph;

    /// <summary>Copies the fragment before the template, then lets the pipeline fill the copy for <paramref name="context"/>.</summary>
    public void Emit(DataContext context, WorkerPipeline pipeline, bool stripIds, Action<OpenXmlElement>? beforeFill = null)
    {
        OpenXmlElement anchor = Range[0];

        // The holder keeps recursion simple: a scope is always a single container element.
        OpenXmlElement holder = anchor.Parent!.CloneNode(false);
        foreach (OpenXmlElement element in Range)
            holder.AppendChild(element.CloneNode(true));

        if (stripIds)
            StripDuplicatedIds(holder);

        beforeFill?.Invoke(holder);
        pipeline.Run(holder, context);

        foreach (OpenXmlElement child in holder.ChildElements.ToList())
        {
            child.Remove();
            anchor.InsertBeforeSelf(child);
        }
    }

    public void RemoveTemplate()
    {
        foreach (OpenXmlElement element in Range)
            element.Remove();
    }

    /// <summary>Data contexts this section should be rendered for: one per array item, one, or none.</summary>
    public List<DataContext> Iterations(DataContext context, DocxTemplateOptions options)
    {
        context.TryResolve(options.MapPath(Token.Path), out JsonNode? value);

        if (Token.Literal != null)
        {
            bool equal = string.Equals(new ValueFormatter(options).Format(value, null), Token.Literal,
                StringComparison.OrdinalIgnoreCase);
            return equal != (Token.Kind == TokenKind.IfNot) ? [context] : [];
        }

        bool truthy = ValueFormatter.IsTruthy(value);
        switch (Token.Kind)
        {
            case TokenKind.IfNot:
                return truthy ? [] : [context];
            case TokenKind.If:
                return truthy ? [context] : [];
            default:
                if (value is JsonArray array)
                    return array.Select(item => new DataContext(item, context)).ToList();
                if (value is JsonObject)
                    return [new DataContext(value, context)];
                return truthy ? [context] : [];
        }
    }

    /// <summary>First open marker in document order (optionally only with the given name).</summary>
    public static Section? FindFirst(OpenXmlElement scope, string? name = null)
    {
        while (true)
        {
            Run? openRun = null;
            TemplateToken? open = null;
            foreach (Run run in scope.Descendants<Run>())
            {
                TemplateToken? token = TokenOf(run);
                if (token is null || token.Kind == TokenKind.Value)
                    continue;

                if (token.Kind == TokenKind.End && name is null)
                    throw new TemplateException($"Znacznik zamykający <%/{token.Path}%> nie ma otwarcia.");

                if (token.IsOpen && (name is null || SameName(token.Path, name)))
                {
                    openRun = run;
                    open = token;
                    break;
                }
            }

            if (openRun is null || open is null)
                return null;

            Run closeRun = FindClose(scope, openRun, open);
            List<OpenXmlElement> range = ExtractRange(openRun, closeRun);
            if (range.Count > 0)
                return new Section(open, range);
        }
    }

    internal static TemplateToken? TokenOf(Run run)
    {
        // Direct texts only - a run may host a drawing with a text box full of its own paragraphs.
        if (run.GetFirstChild<Text>() is null)
            return null;

        return TemplateToken.TryParse(string.Concat(run.Elements<Text>().Select(t => t.Text)));
    }

    private static bool SameName(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static Run FindClose(OpenXmlElement scope, Run openRun, TemplateToken open)
    {
        int depth = 0;
        bool afterOpen = false;
        foreach (Run run in scope.Descendants<Run>())
        {
            if (!afterOpen)
            {
                afterOpen = ReferenceEquals(run, openRun);
                continue;
            }

            TemplateToken? token = TokenOf(run);
            if (token is null || !SameName(token.Path, open.Path))
                continue;

            if (token.IsOpen)
                depth++;
            else if (token.Kind == TokenKind.End && depth-- == 0)
                return run;
        }

        throw new TemplateException($"Sekcja <%{open.Path}%> nie ma znacznika zamykającego <%/{open.Path}%>.");
    }

    private static List<OpenXmlElement> ExtractRange(Run openRun, Run closeRun)
    {
        (OpenXmlElement start, OpenXmlElement end) = LiftToCommonParent(openRun, closeRun);
        if (start is TableCell)
        {
            start = start.Parent!;
            end = end.Parent!;
        }

        var range = new List<OpenXmlElement> { start };
        if (!ReferenceEquals(start, end))
        {
            range.AddRange(start.ElementsAfter().TakeWhile(e => !ReferenceEquals(e, end)));
            range.Add(end);
        }

        RemoveMarker(openRun);
        RemoveMarker(closeRun);

        return range.Where(e => e.Parent != null).ToList();
    }

    private static (OpenXmlElement, OpenXmlElement) LiftToCommonParent(OpenXmlElement first, OpenXmlElement second)
    {
        for (OpenXmlElement? a = first; a?.Parent != null; a = a.Parent)
        {
            for (OpenXmlElement? b = second; b?.Parent != null; b = b.Parent)
            {
                if (ReferenceEquals(a.Parent, b.Parent))
                    return (a, b);
            }
        }

        throw new TemplateException("Znaczniki sekcji leżą w rozłącznych częściach dokumentu.");
    }

    private static void RemoveMarker(Run marker)
    {
        Paragraph? paragraph = marker.Ancestors<Paragraph>().FirstOrDefault();
        marker.Remove();

        if (paragraph is null || !IsEmpty(paragraph))
            return;

        // A marker-only paragraph is scaffolding, but never drop a section break with it.
        if (paragraph.ParagraphProperties?.SectionProperties != null)
            return;

        paragraph.Remove();
    }

    private static bool IsEmpty(Paragraph paragraph)
    {
        return !paragraph.Descendants<Run>().Any(r => r.ChildElements.Any(c => c is not RunProperties));
    }

    private static void StripDuplicatedIds(OpenXmlElement holder)
    {
        foreach (OpenXmlElement bookmark in holder.Descendants()
                     .Where(e => e is BookmarkStart or BookmarkEnd).ToList())
            bookmark.Remove();

        foreach (Paragraph paragraph in holder.Descendants<Paragraph>())
        {
            paragraph.ParagraphId = null;
            paragraph.TextId = null;
        }

        foreach (TableRow row in holder.Descendants<TableRow>())
        {
            row.ParagraphId = null;
            row.TextId = null;
        }
    }
}
