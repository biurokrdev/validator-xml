using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace CommonDocXDataBinder.DocX.Utilities
{
    public static class DocxTextReplacer
    {
        /// <summary>Podmienia wszystkie wystąpienia <paramref name="search"/> w dokumencie; zwraca liczbę podmian.</summary>
        public static int Replace(WordprocessingDocument document, string search, string replacement, bool matchCase = true)
        {
            if (string.IsNullOrEmpty(search)) return 0;
            var count = 0;
            foreach (var paragraph in AllParagraphs(document))
                count += Replace(paragraph, search, replacement ?? string.Empty, matchCase);
            return count;
        }

        /// <summary>Akapity ze wszystkich części z treścią – także zagnieżdżone w tabelach, formantach i polach tekstowych.</summary>
        public static IEnumerable<Paragraph> AllParagraphs(WordprocessingDocument document)
        {
            var main = document.MainDocumentPart ?? throw new InvalidOperationException("Dokument nie zawiera MainDocumentPart.");
            var roots = new List<OpenXmlPartRootElement>();
            if (main.Document != null) roots.Add(main.Document);
            roots.AddRange(main.HeaderParts.Select(p => p.Header).Where(h => h != null)!);
            roots.AddRange(main.FooterParts.Select(p => p.Footer).Where(f => f != null)!);
            if (main.FootnotesPart?.Footnotes != null) roots.Add(main.FootnotesPart.Footnotes);
            if (main.EndnotesPart?.Endnotes != null) roots.Add(main.EndnotesPart.Endnotes);
            // Descendants<Paragraph>() schodzi do w:txbxContent zarówno w mc:Choice (wps), jak i w mc:Fallback (VML).
            return roots.SelectMany(r => r.Descendants<Paragraph>()).ToList();
        }

        /// <summary>Podmiana w jednym akapicie: tekst składany z wszystkich w:t należących bezpośrednio do tego akapitu.</summary>
        public static int Replace(Paragraph paragraph, string search, string replacement, bool matchCase)
        {
            // Tylko w:t tego akapitu – akapity zagnieżdżone (pole tekstowe w akapicie) obsłuży ich własna iteracja.
            var texts = paragraph.Descendants<Text>()
                .Where(t => ReferenceEquals(t.Ancestors<Paragraph>().FirstOrDefault(), paragraph))
                .ToList();
            if (texts.Count == 0) return 0;

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

            // Od końca: pozycje wcześniejszych dopasowań nie zmieniają się po modyfikacji późniejszych fragmentów.
            for (var m = matches.Count - 1; m >= 0; m--)
            {
                var s = matches[m];
                var e = s + search.Length;
                var first = Segment(starts, segments, s, isEnd: false);
                var last = Segment(starts, segments, e, isEnd: true);
                var prefix = texts[first].Text.Substring(0, s - starts[first]);
                var suffix = texts[last].Text.Substring(e - starts[last]);
                if (first == last)
                {
                    SetText(texts[first], prefix + replacement + suffix);
                    continue;
                }

                SetText(texts[last], suffix);
                for (var k = first + 1; k < last; k++) SetText(texts[k], string.Empty);
                SetText(texts[first], prefix + replacement);
            }

            return matches.Count;
        }

        private static int Segment(int[] starts, string[] segments, int position, bool isEnd)
        {
            for (var i = 0; i < segments.Length; i++)
            {
                var len = segments[i].Length;
                if (len == 0) continue;
                var inside = isEnd ? position > starts[i] && position <= starts[i] + len
                                   : position >= starts[i] && position < starts[i] + len;
                if (inside) return i;
            }

            throw new InvalidOperationException("Pozycja poza zakresem tekstu akapitu.");
        }

        private static void SetText(Text text, string value)
        {
            text.Space = SpaceProcessingModeValues.Preserve;
            if (value.IndexOfAny(new[] { '\n', '\r', '\t' }) < 0)
            {
                text.Text = value;
                return;
            }

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
                if (ch == '\n' || ch == '\t')
                {
                    Flush();
                    OpenXmlElement brk = ch == '\n' ? new Break() : new TabChar();
                    last.InsertAfterSelf(brk);
                    last = brk;
                }
                else sb.Append(ch);
            }

            Flush();
        }
    }
}
