// Propozycja w granicach istniejącego rozwiązania: ta sama sygnatura CommonUtilities.SearchAndReplace i ta sama podmiana
// w pojedynczym w:t, plus dwa kroki przygotowawcze – pełny zakres akapitów (pola tekstowe, tabele, formanty) oraz scalenie
// runów rozciętych przez Worda (w:proofErr) o identycznym formatowaniu. DocXReplaceWorker nie wymaga zmian.

using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace CommonDocXDataBinder.DocX.Utilities
{
    public static partial class CommonUtilities
    {
        public static void SearchAndReplace(WordprocessingDocument document, DocumentPart searchIn, string searchFor, string text)
        {
            foreach (var paragraph in ParagraphsOf(document, searchIn))
            {
                if (!paragraph.InnerText.Contains(searchFor)) continue;                 // tani filtr, większość akapitów odpada tutaj

                if (!paragraph.Descendants<Text>().Any(t => t.Text.Contains(searchFor)))
                    MergeAdjacentRuns(paragraph);                                       // fraza rozcięta na kilka runów → sklej

                foreach (var t in paragraph.Descendants<Text>().Where(t => t.Text.Contains(searchFor)).ToList())
                {
                    t.Text = t.Text.Replace(searchFor, text);                           // dotychczasowa logika
                    t.Space = SpaceProcessingModeValues.Preserve;
                }
            }
        }

        /// <summary>
        /// Akapity części wskazanej przez <paramref name="searchIn"/>. Descendants schodzi do tabel, formantów i pól tekstowych
        /// (w:txbxContent w mc:Choice i mc:Fallback), więc ramki-kształty też są objęte.
        /// </summary>
        private static IEnumerable<Paragraph> ParagraphsOf(WordprocessingDocument document, DocumentPart searchIn)
        {
            var main = document.MainDocumentPart;
            if (main == null) yield break;

            var roots = new List<OpenXmlPartRootElement>();
            if (searchIn.HasFlag(DocumentPart.Body) && main.Document != null) roots.Add(main.Document);
            if (searchIn.HasFlag(DocumentPart.Header)) roots.AddRange(main.HeaderParts.Select(p => p.Header).Where(h => h != null)!);
            if (searchIn.HasFlag(DocumentPart.Footer)) roots.AddRange(main.FooterParts.Select(p => p.Footer).Where(f => f != null)!);

            foreach (var paragraph in roots.SelectMany(r => r.Descendants<Paragraph>()).ToList())
                yield return paragraph;
        }

        /// <summary>
        /// Usuwa znaczniki pisowni/gramatyki i skleja sąsiednie runy o identycznych właściwościach (rPr) zawierające tylko tekst.
        /// Nie dotyka runów z innym formatowaniem, grafiką, polami czy podziałami – ich tekst zostaje rozdzielony jak był.
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
    }
}
