using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace EfektRuno.Templating;

/// <summary>
/// Word splits "&lt;%rodzaj_wiadomosci%&gt;" into several runs (spell-check, rsid, formatting).
/// After normalization every token lives alone in its own run, so the engine can treat
/// a run as the unit of work. Formatting of the run holding the token start is kept.
/// </summary>
internal static class PlaceholderNormalizer
{
    public static void Normalize(OpenXmlElement root)
    {
        foreach (ProofError proof in root.Descendants<ProofError>().ToList())
            proof.Remove();

        foreach (Paragraph paragraph in root.Descendants<Paragraph>().ToList())
            NormalizeParagraph(paragraph);
    }

    /// <summary>Texts of this paragraph only - without paragraphs nested in text boxes.</summary>
    public static List<Text> OwnTexts(Paragraph paragraph)
    {
        return paragraph.Descendants<Text>()
            .Where(t => ReferenceEquals(t.Ancestors<Paragraph>().FirstOrDefault(), paragraph))
            .ToList();
    }

    private static void NormalizeParagraph(Paragraph paragraph)
    {
        List<Text> texts = OwnTexts(paragraph);
        if (texts.Count == 0)
            return;

        string full = string.Concat(texts.Select(t => t.Text));
        if (!full.Contains("<%", StringComparison.Ordinal))
            return;

        var starts = new int[texts.Count];
        var lengths = new int[texts.Count];
        int offset = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            starts[i] = offset;
            lengths[i] = texts[i].Text.Length;
            offset += lengths[i];
        }

        // Last to first: edits never touch the text that lies before the match being processed.
        MatchCollection matches = TemplateToken.Pattern.Matches(full);
        for (int i = matches.Count - 1; i >= 0; i--)
            MergeIntoSingleText(texts, starts, lengths, matches[i]);

        foreach (Text text in texts)
            IsolateTokens(text);

        foreach (Text text in texts.Where(t => t.Text.Length == 0))
            text.Remove();

        foreach (Run run in paragraph.Descendants<Run>()
                     .Where(r => !r.ChildElements.Any(c => c is not RunProperties)).ToList())
            run.Remove();
    }

    private static void MergeIntoSingleText(List<Text> texts, int[] starts, int[] lengths, Match match)
    {
        int matchEnd = match.Index + match.Length;
        int first = IndexOfTextAt(starts, lengths, match.Index);
        int last = IndexOfTextAt(starts, lengths, matchEnd - 1);
        if (first == last)
            return;

        string tail = texts[last].Text[(matchEnd - starts[last])..];

        SetText(texts[first], texts[first].Text[..(match.Index - starts[first])] + match.Value);
        for (int i = first + 1; i < last; i++)
            SetText(texts[i], string.Empty);
        SetText(texts[last], tail);
    }

    private static int IndexOfTextAt(int[] starts, int[] lengths, int position)
    {
        for (int i = starts.Length - 1; i >= 0; i--)
        {
            if (lengths[i] > 0 && starts[i] <= position && position < starts[i] + lengths[i])
                return i;
        }

        throw new InvalidOperationException("Text offset out of range.");
    }

    private static void IsolateTokens(Text text)
    {
        if (text.Parent is not Run run)
            return;

        MatchCollection matches = TemplateToken.Pattern.Matches(text.Text);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            Match match = matches[i];
            int end = match.Index + match.Length;

            if (end < text.Text.Length || text.NextSibling() != null)
                SplitRun(run, text, end);

            if (match.Index > 0 || text.PreviousSibling() is not (null or RunProperties))
                SplitRun(run, text, match.Index);
        }
    }

    /// <summary>Left part stays in <paramref name="run"/>, the right part goes to a new sibling run.</summary>
    private static void SplitRun(Run run, Text text, int offset)
    {
        var right = (Run)run.CloneNode(false);
        if (run.RunProperties is { } properties)
            right.RunProperties = (RunProperties)properties.CloneNode(true);

        string tail = text.Text[offset..];
        if (tail.Length > 0)
            right.AppendChild(new Text(tail) { Space = SpaceProcessingModeValues.Preserve });

        OpenXmlElement? next = text.NextSibling();
        while (next != null)
        {
            OpenXmlElement? following = next.NextSibling();
            next.Remove();
            right.AppendChild(next);
            next = following;
        }

        SetText(text, text.Text[..offset]);
        run.InsertAfterSelf(right);
    }

    private static void SetText(Text text, string value)
    {
        text.Text = value;
        text.Space = SpaceProcessingModeValues.Preserve;
    }
}
