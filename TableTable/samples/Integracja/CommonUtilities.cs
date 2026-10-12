using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace CommonDocXDataBinder.DocX.Utilities
{
    public static partial class CommonUtilities
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        // ============================================================================================================
        // Podmiana frazy w dokumencie
        // ============================================================================================================

        public static void SearchAndReplace(WordprocessingDocument document, DocumentPart searchIn, string searchFor, string text)
        {
            if (string.IsNullOrEmpty(searchFor)) return;
            text ??= string.Empty;
            var roots = RootsOf(document, searchIn);

            foreach (var paragraph in roots.SelectMany(r => r.Descendants<Paragraph>()).ToList())
            {
                if (!paragraph.InnerText.Contains(searchFor)) continue;                 // tani filtr, większość akapitów odpada tutaj

                if (!paragraph.Descendants<Text>().Any(t => t.Text.Contains(searchFor)))
                    MergeAdjacentRuns(paragraph);                                       // fraza rozcięta na kilka runów → sklej

                foreach (var t in paragraph.Descendants<Text>().Where(t => t.Text.Contains(searchFor)).ToList())
                    SetTextWithBreaks(t, t.Text.Replace(searchFor, text));              // dotychczasowa logika + \n → w:br
            }

            ReplaceInUnknownElements(roots, searchFor, text);
        }

        /// <summary>Korzenie części wskazanych przez <paramref name="searchIn"/>: treść, nagłówki, stopki.</summary>
        private static List<OpenXmlPartRootElement> RootsOf(WordprocessingDocument document, DocumentPart searchIn)
        {
            var roots = new List<OpenXmlPartRootElement>();
            var main = document.MainDocumentPart;
            if (main == null) return roots;

            if (searchIn.HasFlag(DocumentPart.Body) && main.Document != null) roots.Add(main.Document);
            if (searchIn.HasFlag(DocumentPart.Header)) roots.AddRange(main.HeaderParts.Select(p => p.Header).Where(h => h != null)!);
            if (searchIn.HasFlag(DocumentPart.Footer)) roots.AddRange(main.FooterParts.Select(p => p.Footer).Where(f => f != null)!);
            return roots;
        }

        /// <summary>
        /// Usuwa znaczniki pisowni/gramatyki i skleja sąsiednie runy o identycznych właściwościach (rPr) zawierające tylko tekst.
        /// Nie dotyka runów z innym formatowaniem, grafiką, polami czy podziałami (w:br) – ich tekst zostaje rozdzielony jak był.
        /// </summary>
        private static void MergeAdjacentRuns(Paragraph paragraph)
        {
            foreach (var error in paragraph.Descendants<ProofError>().ToList()) error.Remove();

            Run previous = null;
            foreach (var run in paragraph.Elements<Run>().ToList())
            {
                var mergeable = previous != null
                                && IsTextOnly(previous) && IsTextOnly(run)
                                && (previous.RunProperties?.OuterXml ?? string.Empty) == (run.RunProperties?.OuterXml ?? string.Empty);
                if (!mergeable)
                {
                    previous = run;
                    continue;
                }

                var target = previous.GetFirstChild<Text>() ?? previous.AppendChild(new Text());
                target.Text += string.Concat(run.Elements<Text>().Select(t => t.Text));
                target.Space = SpaceProcessingModeValues.Preserve;
                run.Remove();
            }
        }

        private static bool IsTextOnly(Run run) => run.ChildElements.All(c => c is RunProperties || c is Text);

        /// <summary>
        /// Wpisuje tekst do w:t; znak nowej linii (LF, także CRLF) staje się w:br, tabulator – w:tab, wstawiane w tym samym runie
        /// za tym w:t. Wartości wielowierszowe z modelu łamią się w dokumencie, a formatowanie runu zostaje.
        /// </summary>
        private static void SetTextWithBreaks(Text text, string value)
        {
            text.Space = SpaceProcessingModeValues.Preserve;
            value = value.Replace("\r\n", "\n").Replace('\r', '\n');
            if (value.IndexOfAny(new[] { '\n', '\t' }) < 0)
            {
                text.Text = value;
                return;
            }

            OpenXmlElement last = text;
            var first = true;
            var buffer = new StringBuilder();
            void Flush()
            {
                if (first) { text.Text = buffer.ToString(); first = false; }
                else { var next = new Text(buffer.ToString()) { Space = SpaceProcessingModeValues.Preserve }; last.InsertAfterSelf(next); last = next; }
                buffer.Clear();
            }

            foreach (var ch in value)
            {
                if (ch == '\n' || ch == '\t')
                {
                    Flush();
                    OpenXmlElement separator = ch == '\n' ? new Break() : new TabChar();
                    last.InsertAfterSelf(separator);
                    last = separator;
                }
                else buffer.Append(ch);
            }

            Flush();
        }

        // ============================================================================================================
        // Elementy nieznane SDK (OpenXmlUnknownElement) – ta sama logika na XML
        // ============================================================================================================

        private static void ReplaceInUnknownElements(IEnumerable<OpenXmlPartRootElement> roots, string searchFor, string text)
        {
            foreach (var root in roots)
            {
                // Tylko zewnętrzne elementy nieznane (wewnętrzne są częścią ich XML) i tylko te, w których fraza w ogóle występuje.
                var unknowns = root.Descendants<OpenXmlUnknownElement>()
                    .Where(u => !(u.Parent is OpenXmlUnknownElement) && u.InnerText.Contains(searchFor))
                    .ToList();

                foreach (var unknown in unknowns)
                {
                    var xml = XElement.Parse(unknown.OuterXml, LoadOptions.PreserveWhitespace);
                    var changed = false;
                    foreach (var paragraph in xml.Descendants(W + "p").ToList())
                        changed |= ReplaceInParagraphXml(paragraph, searchFor, text);
                    if (!changed) continue;

                    var replacement = OpenXmlUnknownElement.CreateOpenXmlUnknownElement(xml.ToString(SaveOptions.DisableFormatting));
                    unknown.Parent?.ReplaceChild(replacement, unknown);
                }
            }
        }

        /// <summary>
        /// Składa tekst akapitu z jego w:t (bez akapitów zagnieżdżonych), znajduje wszystkie wystąpienia frazy i podmienia je,
        /// zachowując podział na runy (zamiennik trafia do runu, w którym fraza się zaczyna). Zwraca true, gdy coś podmieniono.
        /// </summary>
        private static bool ReplaceInParagraphXml(XElement paragraph, string searchFor, string text)
        {
            var texts = paragraph.Descendants(W + "t")
                .Where(t => t.Ancestors(W + "p").FirstOrDefault() == paragraph)
                .ToList();
            if (texts.Count == 0) return false;

            var segments = texts.Select(t => t.Value).ToArray();
            var full = string.Concat(segments);
            var starts = new int[segments.Length];
            for (var i = 1; i < segments.Length; i++) starts[i] = starts[i - 1] + segments[i - 1].Length;

            var matches = new List<int>();
            for (var from = 0; from <= full.Length;)
            {
                var idx = full.IndexOf(searchFor, from, StringComparison.Ordinal);
                if (idx < 0) break;
                matches.Add(idx);
                from = idx + searchFor.Length;
            }

            if (matches.Count == 0) return false;

            // Od końca: pozycje wcześniejszych dopasowań nie zmieniają się po modyfikacji późniejszych fragmentów.
            for (var m = matches.Count - 1; m >= 0; m--)
            {
                var s = matches[m];
                var e = s + searchFor.Length;
                var first = SegmentAt(starts, segments, s, isEnd: false);
                var last = SegmentAt(starts, segments, e, isEnd: true);
                var prefix = texts[first].Value.Substring(0, s - starts[first]);
                var suffix = texts[last].Value.Substring(e - starts[last]);

                if (first == last)
                {
                    SetTextWithBreaks(texts[first], prefix + text + suffix);
                    continue;
                }

                SetTextWithBreaks(texts[last], suffix);
                for (var k = first + 1; k < last; k++) SetTextWithBreaks(texts[k], string.Empty);
                SetTextWithBreaks(texts[first], prefix + text);
            }

            return true;
        }

        private static int SegmentAt(int[] starts, string[] segments, int position, bool isEnd)
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

        /// <summary>Jak <see cref="SetTextWithBreaks(Text, string)"/>, ale na XML: w:br / w:tab dokładane jako rodzeństwo za w:t.</summary>
        private static void SetTextWithBreaks(XElement t, string value)
        {
            value = value.Replace("\r\n", "\n").Replace('\r', '\n');
            var pieces = new List<XElement>();
            var buffer = new StringBuilder();
            void Flush()
            {
                pieces.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), buffer.ToString()));
                buffer.Clear();
            }

            foreach (var ch in value)
            {
                if (ch == '\n' || ch == '\t')
                {
                    Flush();
                    pieces.Add(new XElement(W + (ch == '\n' ? "br" : "tab")));
                }
                else buffer.Append(ch);
            }

            Flush();

            // Pierwszy fragment zostaje w istniejącym w:t (zachowuje pozycję w runie), reszta jako rodzeństwo za nim.
            t.Value = pieces[0].Value;
            t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            XElement last = t;
            foreach (var piece in pieces.Skip(1))
            {
                last.AddAfterSelf(piece);
                last = piece;
            }
        }
    }
}
