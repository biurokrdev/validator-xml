// Docelowa wersja CommonUtilities.ReplaceInUnknownElements: podmiana frazy w elementach, których SDK nie rozpoznaje
// (OpenXmlUnknownElement – np. nowsze przestrzenie nazw Worda). XML elementu jest parsowany przez LINQ to XML,
// tekst każdego akapitu składany z jego w:t (fraza rozcięta na kilka runów też jest znajdowana), a zamiennik
// wpisywany do pierwszego w:t dopasowania. EscapeXmlText i XmlReader nie są potrzebne.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml;

namespace CommonDocXDataBinder.DocX.Utilities
{
    public static partial class CommonUtilities
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

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
                    SetText(texts[first], prefix + text + suffix);
                    continue;
                }

                SetText(texts[last], suffix);
                for (var k = first + 1; k < last; k++) SetText(texts[k], string.Empty);
                SetText(texts[first], prefix + text);
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

        private static void SetText(XElement t, string value)
        {
            t.Value = value;
            t.SetAttributeValue(XNamespace.Xml + "space", "preserve");   // spacje na brzegach nie mogą zniknąć
        }
    }
}
