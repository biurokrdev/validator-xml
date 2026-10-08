using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace TableTable.Internal;

/// <summary>Tekst akapitu jako całość i podmiana fragmentów rozbitych przez Worda na wiele runów (<c>&lt;%</c> | <c>nazwa</c> | <c>%&gt;</c>).</summary>
internal static class ParagraphText
{
    /// <summary>Elementy <c>w:t</c> należące bezpośrednio do akapitu (bez akapitów zagnieżdżonych, np. w polach tekstowych).</summary>
    private static List<Text> DirectTexts(Paragraph paragraph) =>
        paragraph.Descendants<Text>().Where(t => ReferenceEquals(t.Ancestors<Paragraph>().FirstOrDefault(), paragraph)).ToList();

    public static string GetText(Paragraph paragraph) => string.Concat(DirectTexts(paragraph).Select(t => t.Text));

    /// <summary>Podmienia wszystkie wystąpienia <paramref name="search"/>; znaki nowej linii i tabulacji w zamienniku stają się <c>w:br</c> / <c>w:tab</c>.</summary>
    public static void Replace(Paragraph paragraph, string search, string replacement, bool matchCase)
    {
        if (string.IsNullOrEmpty(search)) return;
        var texts = DirectTexts(paragraph);
        if (texts.Count == 0) return;

        var segments = texts.Select(t => t.Text).ToArray();
        var full = string.Concat(segments);
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var starts = new int[segments.Length];
        for (var i = 1; i < segments.Length; i++) starts[i] = starts[i - 1] + segments[i - 1].Length;

        var matches = new List<int>();
        for (var from = 0; from <= full.Length;)
        {
            var idx = full.IndexOf(search, from, comparison);
            if (idx < 0) break;
            matches.Add(idx);
            from = idx + search.Length;
        }

        // Od końca: wcześniejsze dopasowania leżą przed modyfikowanym fragmentem, więc ich pozycje pozostają prawidłowe.
        for (var m = matches.Count - 1; m >= 0; m--)
        {
            var s = matches[m];
            var e = s + search.Length;
            var first = Segment(starts, segments, s, isEnd: false);
            var last = Segment(starts, segments, e, isEnd: true);
            var prefix = texts[first].Text[..(s - starts[first])];
            var suffix = texts[last].Text[(e - starts[last])..];

            if (first == last)
            {
                SetText(texts[first], prefix + replacement + suffix);
                continue;
            }

            SetText(texts[last], suffix);
            for (var k = first + 1; k < last; k++) SetText(texts[k], string.Empty);
            SetText(texts[first], prefix + replacement);
        }
    }

    private static int Segment(int[] starts, string[] segments, int position, bool isEnd)
    {
        for (var i = 0; i < segments.Length; i++)
        {
            var len = segments[i].Length;
            if (len == 0) continue;
            if (isEnd ? position > starts[i] && position <= starts[i] + len : position >= starts[i] && position < starts[i] + len) return i;
        }

        throw new InvalidOperationException("Pozycja poza zakresem tekstu akapitu.");
    }

    private static void SetText(Text text, string value)
    {
        text.Space = SpaceProcessingModeValues.Preserve;
        if (value.IndexOfAny(['\n', '\r', '\t']) < 0)
        {
            text.Text = value;
            return;
        }

        // Pierwszy fragment zostaje w tym samym w:t, kolejne fragmenty i podziały dokładane są za nim w tym samym runie.
        OpenXmlElement last = text;
        var first = true;
        var sb = new StringBuilder();
        void Flush()
        {
            if (first) { text.Text = sb.ToString(); first = false; }
            else { var next = new Text(sb.ToString()) { Space = SpaceProcessingModeValues.Preserve }; last.InsertAfterSelf(next); last = next; }
            sb.Clear();
        }

        foreach (var ch in value.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (ch is '\n' or '\t')
            {
                Flush();
                OpenXmlElement brk = ch == '\n' ? new Break() : new TabChar();
                last.InsertAfterSelf(brk);
                last = brk;
            }
            else
            {
                sb.Append(ch);
            }
        }

        Flush();
    }
}
