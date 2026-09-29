using System.Globalization;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>
/// Etap „Struktura": reguły WordprocessingML, których Word wymaga, żeby otworzyć dokument bez
/// komunikatu o naprawie, oraz konstrukcje znane z tego, że wywracają konwertery DOCX→PDF inne niż
/// Word (altChunk, niedomknięte pola, obrazy bez części, korespondencja seryjna w settings.xml).
/// Reguły działają na surowym XML (System.Xml.Linq), nie na modelu SDK — mają działać właśnie
/// wtedy, gdy SDK odmawia współpracy.
/// </summary>
public sealed class WordStructureHealthCheck
{
    private const long MaxExtentEmu = 20_116_800; // 22 cale — maksimum Worda dla wymiaru obiektu.
    private const int MinPageTwips = 144;         // 0,1 cala
    private const int MaxPageTwips = 31_680;      // 22 cale

    private static readonly XNamespace Mc = OoxmlNamespaces.MarkupCompatibility;

    private static readonly HashSet<string> StoryBoundaries = new(StringComparer.Ordinal)
    {
        "body", "tc", "txbxContent", "hdr", "ftr", "footnote", "endnote", "comment", "docPartBody"
    };

    private static readonly HashSet<string> FieldStoryRoots = new(StringComparer.Ordinal)
    {
        "body", "txbxContent", "hdr", "ftr", "footnote", "endnote", "comment", "docPartBody"
    };

    private static readonly HashSet<string> MarkerElements = new(StringComparer.Ordinal)
    {
        "pPr", "tcPr", "trPr", "tblPr", "tblGrid", "tblPrEx", "sdtPr", "sdtEndPr", "customXmlPr",
        "bookmarkStart", "bookmarkEnd", "proofErr", "permStart", "permEnd",
        "commentRangeStart", "commentRangeEnd",
        "moveFromRangeStart", "moveFromRangeEnd", "moveToRangeStart", "moveToRangeEnd",
        "customXmlInsRangeStart", "customXmlInsRangeEnd", "customXmlDelRangeStart", "customXmlDelRangeEnd"
    };

    private static readonly string[] RevisionElements =
    [
        "ins", "del", "moveFrom", "moveTo", "rPrChange", "pPrChange", "tblPrChange", "trPrChange",
        "tcPrChange", "sectPrChange", "numberingChange", "cellIns", "cellDel", "cellMerge"
    ];

    private static readonly string[] VmlShapeElements =
    [
        "shape", "group", "rect", "oval", "line", "roundrect", "polyline", "arc", "curve"
    ];

    private readonly DocumentHealthOptions _options;

    public WordStructureHealthCheck(IOptions<DocumentHealthOptions> options)
    {
        _options = options.Value;
    }

    public DocumentHealthStatistics Run(
        HealthPackageContext context,
        IReadOnlyDictionary<string, XDocument> parts,
        HealthFindingCollector findings,
        CancellationToken cancellationToken)
    {
        var stats = new Counters();
        var stories = CollectStoryParts(context, parts);

        CountImages(context, stats, findings);

        var footnoteIds = CollectNoteIds(context, parts, "footnotes+xml", "footnote", findings, stats, isFootnote: true);
        var endnoteIds = CollectNoteIds(context, parts, "endnotes+xml", "endnote", findings, stats, isFootnote: false);
        var commentIds = CollectNoteIds(context, parts, "comments+xml", "comment", findings, stats, isFootnote: false);
        var numbering = CollectNumbering(context, parts);
        var styles = CollectStyles(context, parts);

        foreach (var (partPath, document) in stories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (document.Root is null)
            {
                continue;
            }

            var isMain = context.IsMainPart(partPath);
            var story = new StoryScope(context, partPath, document.Root, findings, stats);

            stats.Elements += document.Root.DescendantsAndSelf().Count();

            if (isMain)
            {
                CheckBody(story);
            }

            CheckSectionProperties(story, isMain);
            CheckTables(story);
            CheckParagraphsAndRuns(story);
            CheckFields(story);
            CheckRanges(story);
            CheckDrawings(story);
            CheckVml(story);
            CheckHyperlinksAndChunks(story);
            CheckAlternateContent(story);
            CheckNoteReferences(story, footnoteIds, endnoteIds, commentIds);
            CheckNumberingReferences(story, numbering);
            CheckStyleReferences(story, styles);
            CheckContentControls(story);
            CountRevisions(story);
        }

        CheckSettings(context, parts, findings);
        CheckFontTable(context, parts, findings, stats);
        CheckDocumentScale(stats, findings);

        return stats.ToStatistics(context);
    }

    // ── Zbieranie kontekstu ───────────────────────────────────────────────────

    private static List<(string Path, XDocument Document)> CollectStoryParts(
        HealthPackageContext context,
        IReadOnlyDictionary<string, XDocument> parts)
    {
        var result = new List<(string, XDocument)>();

        if (parts.TryGetValue(context.MainDocumentPartPath, out var main))
        {
            result.Add((context.MainDocumentPartPath, main));
        }

        foreach (var fragment in new[] { "header+xml", "footer+xml", "footnotes+xml", "endnotes+xml", "comments+xml", "glossary+xml" })
        {
            foreach (var path in context.PartsOfType(fragment).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (parts.TryGetValue(path, out var document) && !context.IsMainPart(path))
                {
                    result.Add((path, document));
                }
            }
        }

        return result;
    }

    private void CountImages(HealthPackageContext context, Counters stats, HealthFindingCollector findings)
    {
        foreach (var (path, contentType) in context.Opc.ContentTypes)
        {
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var entry = context.Package.Find(path);

            if (entry is null)
            {
                continue;
            }

            stats.ImageParts++;
            stats.ImageBytes += entry.Length;

            if (entry.Length > _options.LargeImageBytes)
            {
                findings.Add(DocumentHealthCodes.ImageLarge, StructureIssueSeverity.Warning, HealthStage.Structure,
                    "Bardzo duży obraz",
                    $"Część '{path}' ma {entry.Length / (1024 * 1024)} MB. Konwertery rasteryzują obrazy w pamięci — pojedyncze pliki tej wielkości wydłużają konwersję do minut albo kończą ją błędem pamięci.",
                    path, WordOpenImpact.None, PdfConversionImpact.Possible,
                    "Zmniejsz rozdzielczość obrazu (Word: Kompresuj obrazy) przed wysyłką.");
            }
        }
    }

    private static HashSet<string>? CollectNoteIds(
        HealthPackageContext context,
        IReadOnlyDictionary<string, XDocument> parts,
        string contentTypeFragment,
        string elementName,
        HealthFindingCollector findings,
        Counters stats,
        bool isFootnote)
    {
        var path = context.PartsOfType(contentTypeFragment).FirstOrDefault();

        if (path is null || !parts.TryGetValue(path, out var document) || document.Root is null)
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var note in document.Root.Elements().Where(element => IsW(element, elementName)))
        {
            var id = WAttr(note, "id");
            var type = WAttr(note, "type");

            if (id is null)
            {
                continue;
            }

            if (!ids.Add(id))
            {
                findings.Add(DocumentHealthCodes.NoteIdDuplicate, StructureIssueSeverity.Error, HealthStage.Structure,
                    $"Zduplikowany identyfikator w {Path.GetFileName(path)}",
                    $"Element <w:{elementName} w:id=\"{id}\"> występuje więcej niż raz. Word rozstrzyga odwołania po identyfikatorze i zgłasza nieczytelną zawartość.",
                    HealthLocations.Of(path, note), WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Nadaj unikalne identyfikatory w generatorze dokumentu.");
            }

            if (type is null or "normal")
            {
                if (elementName == "footnote") stats.Footnotes++;
                else if (elementName == "endnote") stats.Endnotes++;
                else stats.Comments++;
            }
        }

        _ = isFootnote;

        return ids;
    }

    private sealed record NumberingIndex(Dictionary<string, string?> NumToAbstract, HashSet<string> Abstracts, bool PartMissing);

    private static NumberingIndex CollectNumbering(HealthPackageContext context, IReadOnlyDictionary<string, XDocument> parts)
    {
        var target = context.Opc.Relationships.FindTargetByType(context.MainDocumentPartPath, "numbering");

        if (target is null || !parts.TryGetValue(target, out var document) || document.Root is null)
        {
            return new NumberingIndex(new Dictionary<string, string?>(StringComparer.Ordinal), [], PartMissing: true);
        }

        var nums = new Dictionary<string, string?>(StringComparer.Ordinal);
        var abstracts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in document.Root.Elements())
        {
            if (IsW(element, "abstractNum") && WAttr(element, "abstractNumId") is { } abstractId)
            {
                abstracts.Add(abstractId);
            }
            else if (IsW(element, "num") && WAttr(element, "numId") is { } numId)
            {
                var abstractRef = element.Elements().FirstOrDefault(child => IsW(child, "abstractNumId"));
                nums[numId] = abstractRef is null ? null : WAttr(abstractRef, "val");
            }
        }

        return new NumberingIndex(nums, abstracts, PartMissing: false);
    }

    private sealed record StyleIndex(HashSet<string> Ids, bool PartMissing);

    private static StyleIndex CollectStyles(HealthPackageContext context, IReadOnlyDictionary<string, XDocument> parts)
    {
        var target = context.Opc.Relationships.FindTargetByType(context.MainDocumentPartPath, "styles");

        if (target is null || !parts.TryGetValue(target, out var document) || document.Root is null)
        {
            return new StyleIndex([], PartMissing: true);
        }

        var ids = new HashSet<string>(
            document.Root.Elements()
                .Where(element => IsW(element, "style"))
                .Select(element => WAttr(element, "styleId"))
                .Where(id => id is not null)!,
            StringComparer.Ordinal);

        return new StyleIndex(ids, PartMissing: false);
    }

    // ── Reguły ────────────────────────────────────────────────────────────────

    private static void CheckBody(StoryScope story)
    {
        var body = story.Root.Elements().FirstOrDefault(element => IsW(element, "body"));

        if (body is null)
        {
            story.Add(DocumentHealthCodes.BodyMissing, StructureIssueSeverity.Error,
                "Brak w:body w głównej części",
                $"Korzeń <{HealthLocations.QualifiedName(story.Root)}> nie zawiera elementu w:body. Bez treści dokumentu Word nie ma czego otworzyć.",
                story.Root, WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                "Wyeksportuj dokument ponownie z aplikacji źródłowej.");
            return;
        }

        var content = ContentChildren(body).Where(child => !IsW(child, "sectPr")).ToList();

        if (content.Count == 0)
        {
            story.Add(DocumentHealthCodes.BodyEmpty, StructureIssueSeverity.Warning,
                "Dokument nie ma treści",
                "w:body nie zawiera akapitów ani tabel. Word otworzy pustą stronę; aplikacja odrzuca taki dokument kodem DOCUMENT_CONTENT_EMPTY, więc nie dojdzie do konwersji.",
                body, WordOpenImpact.None, PdfConversionImpact.Blocking,
                "Sprawdź, czy aplikacja źródłowa nie wysyła pustego szablonu.");
        }
    }

    private static void CheckSectionProperties(StoryScope story, bool isMain)
    {
        var sectionProperties = story.Root.Descendants().Where(element => IsW(element, "sectPr")).ToList();

        if (isMain)
        {
            story.Stats.Sections += sectionProperties.Count;

            if (sectionProperties.Count == 0)
            {
                story.Add(DocumentHealthCodes.SectionPropertiesMissing, StructureIssueSeverity.Info,
                    "Brak w:sectPr (właściwości strony)",
                    "Dokument nie definiuje rozmiaru strony ani marginesów. Word i konwertery przyjmą wartości domyślne (Letter / A4 w zależności od ustawień), więc układ stron może się różnić.",
                    null, WordOpenImpact.None, PdfConversionImpact.Possible);
            }
        }

        foreach (var sectPr in sectionProperties)
        {
            var parent = sectPr.Parent;
            var placementOk = parent is not null &&
                              (IsW(parent, "pPr") || IsW(parent, "docPartBody") ||
                               (IsW(parent, "body") && parent.Elements().Last() == sectPr));

            if (!placementOk)
            {
                story.Add(DocumentHealthCodes.SectionPropertiesMisplaced, StructureIssueSeverity.Error,
                    "w:sectPr w niedozwolonym miejscu",
                    $"w:sectPr może być tylko ostatnim dzieckiem w:body albo dzieckiem w:pPr; tu jego rodzicem jest <{(parent is null ? "?" : HealthLocations.QualifiedName(parent))}>{(parent is not null && IsW(parent, "body") ? " i nie jest ostatnim elementem" : string.Empty)}. Word zgłasza nieczytelną zawartość.",
                    sectPr, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Przenieś w:sectPr na koniec w:body (sekcja końcowa) albo do w:pPr ostatniego akapitu sekcji.");
            }

            CheckPageGeometry(story, sectPr);
        }
    }

    private static void CheckPageGeometry(StoryScope story, XElement sectPr)
    {
        var pageSize = sectPr.Elements().FirstOrDefault(element => IsW(element, "pgSz"));
        var margins = sectPr.Elements().FirstOrDefault(element => IsW(element, "pgMar"));

        int? width = null, height = null;

        if (pageSize is not null)
        {
            width = ParseInt(WAttr(pageSize, "w"));
            height = ParseInt(WAttr(pageSize, "h"));

            var invalid = (width is not null && (width < MinPageTwips || width > MaxPageTwips)) ||
                          (height is not null && (height < MinPageTwips || height > MaxPageTwips));

            if (invalid)
            {
                story.Add(DocumentHealthCodes.PageSizeOutOfRange, StructureIssueSeverity.Error,
                    "Rozmiar strony poza zakresem Worda",
                    $"w:pgSz deklaruje {width?.ToString(CultureInfo.InvariantCulture) ?? "?"} × {height?.ToString(CultureInfo.InvariantCulture) ?? "?"} twips. Word akceptuje 0,1–22 cala (144–31680 twips); poza zakresem zgłasza błąd i naprawia dokument, a konwertery mogą wygenerować pusty PDF albo odmówić.",
                    pageSize, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Ustaw realny rozmiar strony (A4 = 11906 × 16838 twips).");
            }
        }

        if (margins is null || width is null || height is null)
        {
            return;
        }

        var left = ParseInt(WAttr(margins, "left")) ?? 0;
        var right = ParseInt(WAttr(margins, "right")) ?? 0;
        var top = Math.Abs(ParseInt(WAttr(margins, "top")) ?? 0);
        var bottom = Math.Abs(ParseInt(WAttr(margins, "bottom")) ?? 0);

        if (left + right >= width || top + bottom >= height)
        {
            story.Add(DocumentHealthCodes.PageMarginsExceedPage, StructureIssueSeverity.Warning,
                "Marginesy większe niż strona",
                $"Suma marginesów ({left}+{right} × {top}+{bottom} twips) nie zostawia miejsca na treść przy stronie {width} × {height}. Word ostrzega i koryguje; konwertery mogą zapętlić paginację albo dać pusty PDF.",
                margins, WordOpenImpact.None, PdfConversionImpact.Likely,
                "Zmniejsz marginesy w sekcji.");
        }
    }

    private void CheckTables(StoryScope story)
    {
        foreach (var table in story.Root.Descendants().Where(element => IsW(element, "tbl")))
        {
            story.Stats.Tables++;
            var depth = table.Ancestors().Count(ancestor => IsW(ancestor, "tbl")) + 1;
            story.Stats.MaxTableNesting = Math.Max(story.Stats.MaxTableNesting, depth);

            if (depth > _options.TableNestingWarningDepth)
            {
                story.Add(DocumentHealthCodes.TableNestingDeep, StructureIssueSeverity.Warning,
                    "Głębokie zagnieżdżenie tabel",
                    $"Tabela na poziomie zagnieżdżenia {depth}. Word to renderuje, ale LibreOffice i biblioteki układające tabele gubią szerokości kolumn albo przerywają konwersję przy głębokich zagnieżdżeniach.",
                    table, WordOpenImpact.None, PdfConversionImpact.Likely,
                    "Spłaszcz układ: zagnieżdżone tabele zastąp scalonymi komórkami.");
            }

            var rows = ContentChildren(table).Where(child => IsW(child, "tr")).ToList();

            if (rows.Count == 0)
            {
                story.Add(DocumentHealthCodes.TableWithoutRows, StructureIssueSeverity.Error,
                    "Tabela bez wierszy",
                    "w:tbl nie zawiera żadnego w:tr. Word zgłasza nieczytelną zawartość i usuwa tabelę przy naprawie.",
                    table, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Usuń pustą tabelę albo dodaj wiersz z komórką i akapitem.");
            }

            if (!table.Elements().Any(child => IsW(child, "tblGrid")))
            {
                story.Add(DocumentHealthCodes.TableGridMissing, StructureIssueSeverity.Warning,
                    "Tabela bez siatki kolumn (w:tblGrid)",
                    "Word odtwarza siatkę z szerokości komórek; LibreOffice i część konwerterów opiera układ na w:tblGrid i bez niego zwęża kolumny do minimum.",
                    table, WordOpenImpact.None, PdfConversionImpact.Possible,
                    "Zapisz dokument w Wordzie (dopisze siatkę) albo generuj w:tblGrid z w:gridCol dla każdej kolumny.");
            }

            foreach (var row in rows)
            {
                var cells = ContentChildren(row).Where(child => IsW(child, "tc")).ToList();

                if (cells.Count == 0)
                {
                    story.Add(DocumentHealthCodes.TableRowWithoutCells, StructureIssueSeverity.Error,
                        "Wiersz tabeli bez komórek",
                        "w:tr nie zawiera żadnego w:tc. Word zgłasza nieczytelną zawartość.",
                        row, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                        "Usuń pusty wiersz albo dodaj komórkę z akapitem.");
                    continue;
                }

                foreach (var cell in cells)
                {
                    var blocks = ContentChildren(cell).ToList();
                    var last = blocks.LastOrDefault();

                    if (last is null || !IsW(last, "p"))
                    {
                        story.Add(DocumentHealthCodes.TableCellWithoutParagraph, StructureIssueSeverity.Error,
                            "Komórka tabeli nie kończy się akapitem",
                            last is null
                                ? "w:tc nie zawiera żadnego akapitu. Word wymaga, żeby ostatnim elementem komórki był w:p — inaczej zgłasza nieczytelną zawartość."
                                : $"Ostatnim elementem komórki jest <{HealthLocations.QualifiedName(last)}>, a Word wymaga w:p po każdej tabeli zagnieżdżonej i na końcu komórki. To najczęstszy błąd dokumentów generowanych programowo.",
                            cell, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                            "Dodaj pusty <w:p/> na końcu komórki (po zagnieżdżonej tabeli).");
                    }
                }
            }
        }

        foreach (var container in story.Root.DescendantsAndSelf().Where(element =>
                     IsW(element, "body") || IsW(element, "tc") || IsW(element, "txbxContent") ||
                     IsW(element, "hdr") || IsW(element, "ftr") || IsW(element, "footnote") ||
                     IsW(element, "endnote") || IsW(element, "comment") || IsW(element, "sdtContent")))
        {
            XElement? previous = null;

            foreach (var child in ContentChildren(container))
            {
                if (previous is not null && IsW(previous, "tbl") && IsW(child, "tbl"))
                {
                    story.Add(DocumentHealthCodes.TablesAdjacent, StructureIssueSeverity.Warning,
                        "Dwie tabele bez akapitu pomiędzy",
                        "Kolejne w:tbl bez rozdzielającego w:p. Word skleja je w jedną tabelę; konwertery robią to różnie (dwie tabele, jedna, albo błąd układu).",
                        child, WordOpenImpact.None, PdfConversionImpact.Possible,
                        "Wstaw pusty akapit między tabelami.");
                }

                previous = child;
            }
        }
    }

    private static void CheckParagraphsAndRuns(StoryScope story)
    {
        foreach (var paragraph in story.Root.Descendants().Where(element => IsW(element, "p")))
        {
            story.Stats.Paragraphs++;

            foreach (var ancestor in paragraph.Ancestors())
            {
                if (IsW(ancestor, "p"))
                {
                    story.Add(DocumentHealthCodes.ParagraphNested, StructureIssueSeverity.Error,
                        "Akapit zagnieżdżony w akapicie",
                        $"w:p leży wewnątrz innego w:p (poza polem tekstowym). Schemat tego nie dopuszcza; Word zgłasza nieczytelną zawartość.{HealthLocations.Preview(paragraph)}",
                        paragraph, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                        "Popraw generator: akapity są rodzeństwem, nie dziećmi.");
                    break;
                }

                if (OoxmlNamespaces.IsWordprocessing(ancestor.Name.NamespaceName) && StoryBoundaries.Contains(ancestor.Name.LocalName))
                {
                    break;
                }
            }
        }

        foreach (var run in story.Root.Descendants().Where(element => IsW(element, "r")))
        {
            var insideParagraph = false;

            foreach (var ancestor in run.Ancestors())
            {
                if (IsW(ancestor, "p"))
                {
                    insideParagraph = true;
                    break;
                }

                if (OoxmlNamespaces.IsWordprocessing(ancestor.Name.NamespaceName) && StoryBoundaries.Contains(ancestor.Name.LocalName))
                {
                    break;
                }
            }

            if (!insideParagraph)
            {
                story.Add(DocumentHealthCodes.RunOutsideParagraph, StructureIssueSeverity.Error,
                    "Run poza akapitem",
                    $"w:r nie leży w żadnym w:p (rodzic: <{HealthLocations.QualifiedName(run.Parent!)}>). Word zgłasza nieczytelną zawartość i gubi ten tekst przy naprawie.{HealthLocations.Preview(run)}",
                    run, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Opakuj run w w:p.");
            }
        }

        foreach (var text in story.Root.Descendants().Where(element => IsW(element, "t")))
        {
            if (text.Parent is { } parent && !IsW(parent, "r"))
            {
                story.Add(DocumentHealthCodes.TextOutsideRun, StructureIssueSeverity.Error,
                    "Tekst poza runem",
                    $"w:t ma rodzica <{HealthLocations.QualifiedName(parent)}> zamiast w:r. Word zgłasza nieczytelną zawartość.",
                    text, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Opakuj tekst w w:r.");
            }
        }
    }

    private static void CheckFields(StoryScope story)
    {
        var stacks = new Dictionary<XElement, Stack<XElement>>();

        foreach (var fieldChar in story.Root.Descendants().Where(element => IsW(element, "fldChar")))
        {
            var type = WAttr(fieldChar, "fldCharType");
            var root = fieldChar.Ancestors().FirstOrDefault(ancestor =>
                OoxmlNamespaces.IsWordprocessing(ancestor.Name.NamespaceName) && FieldStoryRoots.Contains(ancestor.Name.LocalName)) ?? story.Root;

            if (!stacks.TryGetValue(root, out var stack))
            {
                stack = new Stack<XElement>();
                stacks[root] = stack;
            }

            switch (type)
            {
                case "begin":
                    story.Stats.Fields++;
                    stack.Push(fieldChar);
                    break;
                case "separate":
                    if (stack.Count == 0)
                    {
                        ReportUnbalancedField(story, fieldChar, "w:fldChar separate bez wcześniejszego begin");
                    }

                    break;
                case "end":
                    if (stack.Count == 0)
                    {
                        ReportUnbalancedField(story, fieldChar, "w:fldChar end bez otwartego pola");
                    }
                    else
                    {
                        stack.Pop();
                    }

                    break;
            }
        }

        foreach (var stack in stacks.Values)
        {
            foreach (var open in stack)
            {
                ReportUnbalancedField(story, open, "w:fldChar begin bez domykającego end");
            }
        }

        story.Stats.Fields += story.Root.Descendants().Count(element => IsW(element, "fldSimple"));
    }

    private static void ReportUnbalancedField(StoryScope story, XElement fieldChar, string what)
    {
        var paragraph = fieldChar.Ancestors().FirstOrDefault(ancestor => IsW(ancestor, "p"));
        story.Add(DocumentHealthCodes.FieldUnbalanced, StructureIssueSeverity.Error,
            "Niedomknięte pole (fldChar)",
            $"{what}. Word interpretuje resztę treści jako kod pola (tekst „znika”) albo zgłasza nieczytelną zawartość; LibreOffice i konwertery potrafią się na tym zapętlić.{(paragraph is null ? string.Empty : HealthLocations.Preview(paragraph))}",
            fieldChar, WordOpenImpact.Repair, PdfConversionImpact.Likely,
            "Każde pole musi mieć sekwencję begin → (instrText) → separate → wynik → end w tej samej opowieści (body/nagłówek/pole tekstowe).");
    }

    private static void CheckRanges(StoryScope story)
    {
        CheckRangePairs(story, "bookmarkStart", "bookmarkEnd", DocumentHealthCodes.BookmarkUnclosed,
            "Niedomknięta zakładka",
            "Zakładka {0} nie ma pary. Word toleruje (przy naprawie usuwa zakładkę); niektóre konwertery gubią odwołania i spisy treści.",
            PdfConversionImpact.Possible);
        CheckRangePairs(story, "commentRangeStart", "commentRangeEnd", DocumentHealthCodes.CommentRangeUnbalanced,
            "Niedomknięty zakres komentarza",
            "Zakres komentarza {0} nie ma pary. Word toleruje; konwertery eksportujące komentarze mogą zgłosić błąd.",
            PdfConversionImpact.Possible);
    }

    private static void CheckRangePairs(
        StoryScope story,
        string startName,
        string endName,
        string code,
        string title,
        string descriptionFormat,
        PdfConversionImpact pdfImpact)
    {
        var starts = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var ends = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in story.Root.Descendants())
        {
            if (IsW(element, startName) && WAttr(element, "id") is { } startId)
            {
                starts.TryAdd(startId, element);
            }
            else if (IsW(element, endName) && WAttr(element, "id") is { } endId)
            {
                ends.Add(endId);
            }
        }

        foreach (var (id, element) in starts)
        {
            if (!ends.Contains(id))
            {
                story.Add(code, StructureIssueSeverity.Warning, title,
                    string.Format(CultureInfo.InvariantCulture, descriptionFormat, $"w:id=\"{id}\" (brak {endName})"),
                    element, WordOpenImpact.None, pdfImpact, "Domknij zakres albo usuń osierocony znacznik.");
            }
        }

        foreach (var id in ends.Where(id => !starts.ContainsKey(id)))
        {
            story.Add(code, StructureIssueSeverity.Warning, title,
                string.Format(CultureInfo.InvariantCulture, descriptionFormat, $"w:id=\"{id}\" (brak {startName})"),
                null, WordOpenImpact.None, pdfImpact, "Domknij zakres albo usuń osierocony znacznik.");
        }
    }

    private void CheckDrawings(StoryScope story)
    {
        var docPrIds = new Dictionary<string, XElement>(StringComparer.Ordinal);

        foreach (var drawing in story.Root.Descendants().Where(element =>
                     OoxmlNamespaces.IsWordprocessingDrawing(element.Name.NamespaceName) &&
                     element.Name.LocalName is "inline" or "anchor"))
        {
            story.Stats.Drawings++;

            var extent = drawing.Elements().FirstOrDefault(element =>
                OoxmlNamespaces.IsWordprocessingDrawing(element.Name.NamespaceName) && element.Name.LocalName == "extent");

            if (extent is null)
            {
                story.Add(DocumentHealthCodes.DrawingExtentMissing, StructureIssueSeverity.Error,
                    "Grafika bez wymiarów (wp:extent)",
                    $"wp:{drawing.Name.LocalName} nie ma elementu wp:extent. Schemat go wymaga; Word zgłasza nieczytelną zawartość.",
                    drawing, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Dodaj wp:extent cx/cy w EMU (914400 EMU = 1 cal).");
            }
            else
            {
                var cx = ParseLong((string?)extent.Attribute("cx"));
                var cy = ParseLong((string?)extent.Attribute("cy"));

                if (cx is null or <= 0 || cy is null or <= 0 || cx > MaxExtentEmu || cy > MaxExtentEmu)
                {
                    story.Add(DocumentHealthCodes.DrawingExtentInvalid, StructureIssueSeverity.Warning,
                        "Nieprawidłowe wymiary grafiki",
                        $"wp:extent cx=\"{cx}\" cy=\"{cy}\" EMU — wymiar zerowy, ujemny albo większy niż 22 cale. Word rysuje obiekt o rozmiarze zerowym jako niewidoczny; konwertery mogą odrzucić obiekt albo całą stronę.",
                        extent, WordOpenImpact.None, PdfConversionImpact.Possible,
                        "Ustaw realne wymiary obrazu.");
                }
            }

            var docPr = drawing.Elements().FirstOrDefault(element =>
                OoxmlNamespaces.IsWordprocessingDrawing(element.Name.NamespaceName) && element.Name.LocalName == "docPr");
            var docPrId = docPr is null ? null : (string?)docPr.Attribute("id");

            if (docPrId is not null && !docPrIds.TryAdd(docPrId, drawing))
            {
                story.Add(DocumentHealthCodes.DrawingDocPrDuplicate, StructureIssueSeverity.Warning,
                    "Zduplikowany identyfikator wp:docPr",
                    $"Dwie grafiki w tej części mają wp:docPr id=\"{docPrId}\". Word zwykle renumeruje po cichu, ale w połączeniu z formantami i polami zgłasza nieczytelną zawartość; konwertery mogą pomylić kotwice.",
                    drawing, WordOpenImpact.None, PdfConversionImpact.Possible,
                    "Nadaj unikalne id każdemu wp:docPr w części.");
            }
        }

        var checkedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var blip in story.Root.Descendants().Where(element =>
                     OoxmlNamespaces.IsDrawingMain(element.Name.NamespaceName) && element.Name.LocalName == "blip"))
        {
            var embed = RelAttr(blip, "embed");
            var link = RelAttr(blip, "link");

            if (embed is null && link is null)
            {
                story.Add(DocumentHealthCodes.ImageBlipWithoutSource, StructureIssueSeverity.Error,
                    "Obraz bez źródła (a:blip bez r:embed i r:link)",
                    "a:blip nie wskazuje ani części obrazu, ani zasobu zewnętrznego. Word zgłasza nieczytelną zawartość i usuwa obraz przy naprawie.",
                    blip, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Ustaw r:embed na relationship typu image wskazujący część z bajtami obrazu.");
                continue;
            }

            if (embed is null)
            {
                story.Add(DocumentHealthCodes.ImageLinkedExternal, StructureIssueSeverity.Info,
                    "Obraz linkowany, nie osadzony",
                    $"a:blip r:link=\"{link}\" — bajty obrazu są poza pakietem. Word pokaże obraz tylko, gdy ma dostęp do ścieżki; konwertery serwerowe pokażą pusty prostokąt.",
                    blip, WordOpenImpact.None, PdfConversionImpact.Possible,
                    "Osadź obraz w dokumencie (Word: Wstaw → Obrazy, bez „Połącz z plikiem”).");
                continue;
            }

            CheckImageRelationship(story, blip, embed, checkedTargets);
        }
    }

    private void CheckVml(StoryScope story)
    {
        var checkedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vmlShapes = 0;

        foreach (var element in story.Root.Descendants().Where(element => OoxmlNamespaces.IsVml(element.Name.NamespaceName)))
        {
            if (element.Name.NamespaceName == OoxmlNamespaces.Vml && VmlShapeElements.Contains(element.Name.LocalName) &&
                !element.Ancestors().Any(ancestor => ancestor.Name == Mc + "Fallback"))
            {
                vmlShapes++;
            }

            if (element.Name.LocalName == "imagedata" && RelAttr(element, "id") is { } imageId)
            {
                CheckImageRelationship(story, element, imageId, checkedTargets);
            }

            if (element.Name.NamespaceName == OoxmlNamespaces.VmlOffice && element.Name.LocalName == "OLEObject")
            {
                var objectId = RelAttr(element, "id");

                if (objectId is not null && story.Context.Opc.Relationships.Find(story.PartPath, objectId) is null)
                {
                    story.Add(DocumentHealthCodes.EmbeddedObjectRelationshipMissing, StructureIssueSeverity.Error,
                        "Obiekt OLE bez relationshipu",
                        $"o:OLEObject r:id=\"{objectId}\" nie ma relationshipu w części. Word zgłasza nieczytelną zawartość.",
                        element, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                        "Dodaj relationship typu oleObject albo usuń obiekt.");
                }
            }
        }

        var objects = story.Root.Descendants().Count(element => IsW(element, "object"));

        if (objects > 0)
        {
            story.Add(DocumentHealthCodes.EmbeddedObjectPresent, StructureIssueSeverity.Info,
                "Osadzone obiekty OLE",
                $"Dokument zawiera {objects} obiekt(ów) w:object (np. arkusz Excela, równanie, PDF). Konwertery serwerowe nie uruchamiają aplikacji źródłowej — użyją zapisanego podglądu (v:imagedata) albo pominą obiekt.",
                null, WordOpenImpact.None, PdfConversionImpact.Possible);
        }

        if (vmlShapes > 0)
        {
            story.Add(DocumentHealthCodes.LegacyVmlPresent, StructureIssueSeverity.Info,
                "Kształty VML (format legacy)",
                $"Dokument zawiera {vmlShapes} kształt(ów) VML poza gałęziami mc:Fallback. Word renderuje VML; konwertery obsługują go częściowo (pola tekstowe, linie, znaki wodne).",
                null, WordOpenImpact.None, PdfConversionImpact.Possible);
        }
    }

    private void CheckImageRelationship(StoryScope story, XElement element, string relationshipId, HashSet<string> checkedTargets)
    {
        var relationship = story.Context.Opc.Relationships.Find(story.PartPath, relationshipId);

        if (relationship is null)
        {
            story.Add(DocumentHealthCodes.ImageRelationshipMissing, StructureIssueSeverity.Error,
                "Obraz wskazuje nieistniejący relationship",
                $"<{HealthLocations.QualifiedName(element)}> odwołuje się do r:id=\"{relationshipId}\", którego nie ma w pliku .rels części '{story.PartPath}'. Word zgłasza nieczytelną zawartość; konwertery przerywają albo zostawiają pusty obraz.",
                element, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                "Dodaj relationship typu image w pliku .rels części albo usuń obraz. Typowa przyczyna: kopiowanie XML między częściami bez przeniesienia relationshipów.");
            return;
        }

        if (relationship.Status != StructureRelationshipStatus.Resolved || relationship.ResolvedTarget is null)
        {
            // Brak części docelowej i cele zewnętrzne raportuje warstwa pakietu OPC.
            return;
        }

        var target = relationship.ResolvedTarget;

        if (!checkedTargets.Add(target))
        {
            return;
        }

        var entry = story.Context.Package.Find(target);

        if (entry?.Bytes is null)
        {
            return;
        }

        var declared = story.Context.ContentTypeOf(target);

        if (entry.Bytes.Length == 0)
        {
            story.Add(DocumentHealthCodes.ImagePartEmpty, StructureIssueSeverity.Error,
                "Część obrazu jest pusta",
                $"Część '{target}' ma 0 bajtów. Word zgłasza uszkodzony obraz; konwertery dekodujące obrazy przerywają konwersję.",
                element, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                "Osadź obraz ponownie.");
            return;
        }

        var sniffed = ImageSignatureSniffer.Sniff(entry.Bytes);

        if (sniffed is null)
        {
            if (!ImageSignatureSniffer.IsOpaqueContentType(declared))
            {
                story.Add(DocumentHealthCodes.ImageUnrecognized, StructureIssueSeverity.Warning,
                    "Bajty obrazu nie pasują do żadnego formatu",
                    $"Część '{target}' (content type '{declared}') nie zaczyna się od sygnatury PNG/JPEG/GIF/BMP/TIFF/EMF/WMF/WebP/SVG. Word może wyświetlić czerwony krzyżyk; konwertery zwykle przerywają na błędzie dekodera.",
                    element, WordOpenImpact.None, PdfConversionImpact.Likely,
                    "Osadź obraz ponownie z poprawnego pliku źródłowego.");
            }

            return;
        }

        if (declared is not null && declared.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            !ImageSignatureSniffer.IsEquivalent(declared, sniffed))
        {
            story.Add(DocumentHealthCodes.ImageContentTypeMismatch, StructureIssueSeverity.Warning,
                "Content type obrazu nie zgadza się z bajtami",
                $"Część '{target}' deklaruje '{declared}', a bajty to {sniffed}. Word dekoduje po zawartości, ale część konwerterów DOCX→PDF dekoduje po zadeklarowanym typie i gubi obraz albo przerywa konwersję (ADR-0108 r.11).",
                element, WordOpenImpact.None, PdfConversionImpact.Possible,
                "Popraw content type w [Content_Types].xml albo rozszerzenie części (zapis w Wordzie robi to automatycznie).");
        }
    }

    private static void CheckHyperlinksAndChunks(StoryScope story)
    {
        foreach (var hyperlink in story.Root.Descendants().Where(element => IsW(element, "hyperlink")))
        {
            var id = RelAttr(hyperlink, "id");

            if (id is not null && story.Context.Opc.Relationships.Find(story.PartPath, id) is null)
            {
                story.Add(DocumentHealthCodes.HyperlinkRelationshipMissing, StructureIssueSeverity.Error,
                    "Hiperłącze bez relationshipu",
                    $"w:hyperlink r:id=\"{id}\" nie ma relationshipu w części. Word zgłasza nieczytelną zawartość.{HealthLocations.Preview(hyperlink)}",
                    hyperlink, WordOpenImpact.Repair, PdfConversionImpact.Possible,
                    "Dodaj relationship typu hyperlink (TargetMode=\"External\") albo zamień na w:anchor.");
            }
        }

        foreach (var chunk in story.Root.Descendants().Where(element => IsW(element, "altChunk")))
        {
            story.Stats.AltChunks++;
            var id = RelAttr(chunk, "id");
            var relationship = id is null ? null : story.Context.Opc.Relationships.Find(story.PartPath, id);

            if (relationship is null)
            {
                story.Add(DocumentHealthCodes.AltChunkRelationshipMissing, StructureIssueSeverity.Error,
                    "w:altChunk bez relationshipu",
                    $"w:altChunk r:id=\"{id}\" nie wskazuje istniejącego relationshipu. Word zgłasza nieczytelną zawartość.",
                    chunk, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Dodaj relationship typu aFChunk albo usuń element.");
                continue;
            }

            var targetType = relationship.ResolvedTarget is null ? null : story.Context.ContentTypeOf(relationship.ResolvedTarget);
            story.Add(DocumentHealthCodes.AltChunkPresent, StructureIssueSeverity.Warning,
                "Treść osadzona przez w:altChunk",
                $"Dokument dołącza zewnętrzną treść ({targetType ?? "nieznany typ"}) przez w:altChunk — Word scala ją przy otwarciu, ale konwertery serwerowe (LibreOffice, biblioteki OOXML) zwykle pomijają altChunk albo przerywają konwersję. To jedna z najczęstszych przyczyn „pustego PDF”.",
                chunk, WordOpenImpact.None, PdfConversionImpact.Likely,
                "Otwórz i zapisz dokument w Wordzie — zapis zamienia altChunk na zwykłą treść WordprocessingML.");
        }
    }

    private static void CheckAlternateContent(StoryScope story)
    {
        foreach (var alternate in story.Root.Descendants(Mc + "AlternateContent"))
        {
            if (!alternate.Elements(Mc + "Fallback").Any())
            {
                story.Add(DocumentHealthCodes.AlternateContentWithoutFallback, StructureIssueSeverity.Warning,
                    "mc:AlternateContent bez gałęzi Fallback",
                    "Treść alternatywna nie ma mc:Fallback. Konsument, który nie rozumie żadnej gałęzi mc:Choice (starszy konwerter), pominie element w całości.",
                    alternate, WordOpenImpact.None, PdfConversionImpact.Possible,
                    "Zapis w Wordzie dopisuje gałąź Fallback (VML) dla pól tekstowych i kształtów.");
            }
        }
    }

    private static void CheckNoteReferences(
        StoryScope story,
        HashSet<string>? footnoteIds,
        HashSet<string>? endnoteIds,
        HashSet<string>? commentIds)
    {
        CheckReferences(story, "footnoteReference", footnoteIds, "footnotes.xml");
        CheckReferences(story, "endnoteReference", endnoteIds, "endnotes.xml");
        CheckReferences(story, "commentReference", commentIds, "comments.xml");
    }

    private static void CheckReferences(StoryScope story, string referenceName, HashSet<string>? ids, string partLabel)
    {
        foreach (var reference in story.Root.Descendants().Where(element => IsW(element, referenceName)))
        {
            var id = WAttr(reference, "id");

            if (id is null)
            {
                continue;
            }

            if (ids is null)
            {
                story.Add(DocumentHealthCodes.NoteReferenceTargetMissing, StructureIssueSeverity.Error,
                    $"Odwołanie do {partLabel}, której nie ma",
                    $"w:{referenceName} w:id=\"{id}\" wskazuje wpis, ale pakiet nie ma czytelnej części {partLabel}. Word zgłasza nieczytelną zawartość.",
                    reference, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    $"Dodaj część {partLabel} z relationshipem albo usuń odwołania.");
                continue;
            }

            if (!ids.Contains(id))
            {
                story.Add(DocumentHealthCodes.NoteReferenceTargetMissing, StructureIssueSeverity.Error,
                    $"Odwołanie do nieistniejącego wpisu w {partLabel}",
                    $"w:{referenceName} w:id=\"{id}\" nie ma odpowiednika w {partLabel}. Word zgłasza nieczytelną zawartość i usuwa odwołanie.",
                    reference, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Uzgodnij identyfikatory odwołań i wpisów w generatorze dokumentu.");
            }
        }
    }

    private static void CheckNumberingReferences(StoryScope story, NumberingIndex numbering)
    {
        var reportedNums = new HashSet<string>(StringComparer.Ordinal);

        foreach (var numPr in story.Root.Descendants().Where(element => IsW(element, "numPr")))
        {
            var numIdElement = numPr.Elements().FirstOrDefault(element => IsW(element, "numId"));
            var numId = numIdElement is null ? null : WAttr(numIdElement, "val");

            if (numId is null || numId == "0")
            {
                continue;
            }

            if (numbering.PartMissing)
            {
                if (reportedNums.Add("*"))
                {
                    story.Add(DocumentHealthCodes.NumberingInstanceMissing, StructureIssueSeverity.Warning,
                        "Listy bez części numbering.xml",
                        "Akapity mają w:numPr, ale pakiet nie ma czytelnej części numbering (albo relationshipu do niej). Word pokaże akapity bez numeracji; część konwerterów zgłasza błąd odwołania.",
                        numPr, WordOpenImpact.None, PdfConversionImpact.Possible,
                        "Dodaj część numbering.xml z definicjami list.");
                }

                continue;
            }

            if (!numbering.NumToAbstract.TryGetValue(numId, out var abstractId))
            {
                if (reportedNums.Add(numId))
                {
                    story.Add(DocumentHealthCodes.NumberingInstanceMissing, StructureIssueSeverity.Warning,
                        "Odwołanie do nieistniejącej listy",
                        $"w:numId w:val=\"{numId}\" nie ma w:num w numbering.xml. Word pokaże akapit bez numeru; LibreOffice bywa na tym niestabilny.",
                        numPr, WordOpenImpact.None, PdfConversionImpact.Possible,
                        "Dodaj w:num o tym identyfikatorze albo usuń w:numPr.");
                }

                continue;
            }

            if (abstractId is null || !numbering.Abstracts.Contains(abstractId))
            {
                if (reportedNums.Add($"abstract:{numId}"))
                {
                    story.Add(DocumentHealthCodes.AbstractNumberingMissing, StructureIssueSeverity.Warning,
                        "Lista wskazuje nieistniejącą definicję (abstractNum)",
                        $"w:num w:numId=\"{numId}\" odwołuje się do w:abstractNumId=\"{abstractId ?? "?"}\", którego nie ma. Word ignoruje numerację; starsze wersje LibreOffice przerywały import.",
                        numPr, WordOpenImpact.None, PdfConversionImpact.Likely,
                        "Uzupełnij w:abstractNum albo popraw odwołanie.");
                }
            }
        }
    }

    private static void CheckStyleReferences(StoryScope story, StyleIndex styles)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var used = false;

        foreach (var reference in story.Root.Descendants().Where(element =>
                     IsW(element, "pStyle") || IsW(element, "rStyle") || IsW(element, "tblStyle")))
        {
            var id = WAttr(reference, "val");

            if (id is null)
            {
                continue;
            }

            used = true;

            if (!styles.PartMissing && !styles.Ids.Contains(id) && missing.Add(id))
            {
                story.Add(DocumentHealthCodes.StyleMissing, StructureIssueSeverity.Info,
                    "Odwołanie do nieistniejącego stylu",
                    $"<{HealthLocations.QualifiedName(reference)} w:val=\"{id}\"> — stylu nie ma w styles.xml. Word i konwertery użyją stylu domyślnego; formatowanie może się różnić od zamierzonego.",
                    reference, WordOpenImpact.None, PdfConversionImpact.None);
            }
        }

        if (used && styles.PartMissing)
        {
            story.Add(DocumentHealthCodes.StylesPartMissing, StructureIssueSeverity.Warning,
                "Odwołania do stylów bez części styles.xml",
                "Treść używa w:pStyle/w:rStyle/w:tblStyle, ale pakiet nie ma czytelnej części styles. Word zastosuje domyślne; konwertery mogą zgłosić błąd odwołania.",
                null, WordOpenImpact.None, PdfConversionImpact.Possible,
                "Dodaj część styles.xml.");
        }
    }

    private static void CheckContentControls(StoryScope story)
    {
        foreach (var sdt in story.Root.Descendants().Where(element => IsW(element, "sdt")))
        {
            story.Stats.ContentControls++;

            if (!sdt.Elements().Any(element => IsW(element, "sdtContent")))
            {
                story.Add(DocumentHealthCodes.ContentControlWithoutContent, StructureIssueSeverity.Error,
                    "Formant bez w:sdtContent",
                    "w:sdt nie ma elementu w:sdtContent. Schemat go wymaga; Word zgłasza nieczytelną zawartość.",
                    sdt, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Dodaj w:sdtContent (może być pusty) albo usuń formant.");
            }
        }
    }

    private static void CountRevisions(StoryScope story)
    {
        story.Stats.TrackedRevisions += story.Root.Descendants().Count(element =>
            OoxmlNamespaces.IsWordprocessing(element.Name.NamespaceName) && RevisionElements.Contains(element.Name.LocalName));

        if (story.Stats.TrackedRevisions > 0 && !story.Findings.Has(DocumentHealthCodes.TrackedChangesPresent))
        {
            story.Add(DocumentHealthCodes.TrackedChangesPresent, StructureIssueSeverity.Info,
                "Śledzone zmiany w dokumencie",
                $"Dokument zawiera {story.Stats.TrackedRevisions} znacznik(ów) rewizji (w:ins/w:del/…). Word pokaże je jako zmiany; konwertery renderują je różnie (z przekreśleniami albo po akceptacji). Edytor akceptuje rewizje przy imporcie (ADR-0088).",
                null, WordOpenImpact.None, PdfConversionImpact.Possible,
                "Zaakceptuj wszystkie zmiany w Wordzie przed konwersją, jeśli PDF ma pokazać stan końcowy.");
        }

        if (story.Stats.Comments > 0 && !story.Findings.Has(DocumentHealthCodes.CommentsPresent))
        {
            story.Add(DocumentHealthCodes.CommentsPresent, StructureIssueSeverity.Info,
                "Komentarze w dokumencie",
                $"Dokument ma {story.Stats.Comments} komentarz(y). Nie blokują konwersji; część konwerterów umieszcza je na marginesie PDF.",
                null, WordOpenImpact.None, PdfConversionImpact.None);
        }
    }

    private static void CheckSettings(HealthPackageContext context, IReadOnlyDictionary<string, XDocument> parts, HealthFindingCollector findings)
    {
        var target = context.Opc.Relationships.FindTargetByType(context.MainDocumentPartPath, "settings");

        if (target is null || !parts.TryGetValue(target, out var document) || document.Root is null)
        {
            return;
        }

        foreach (var element in document.Root.Elements())
        {
            if (IsW(element, "mailMerge"))
            {
                findings.Add(DocumentHealthCodes.MailMergeSettings, StructureIssueSeverity.Warning, HealthStage.Structure,
                    "Ustawienia korespondencji seryjnej",
                    "settings.xml zawiera w:mailMerge (źródło danych). Word przy otwarciu pyta o połączenie ze źródłem — w konwersji bezobsługowej (serwer, LibreOffice headless) to okno dialogowe blokuje proces do przekroczenia limitu czasu.",
                    HealthLocations.Of(target, element), WordOpenImpact.None, PdfConversionImpact.Likely,
                    "Word: Korespondencja → Rozpocznij korespondencję seryjną → Zwykły dokument programu Word, potem zapisz.");
            }
            else if (IsW(element, "updateFields") && IsTrue(WAttr(element, "val")))
            {
                findings.Add(DocumentHealthCodes.UpdateFieldsOnOpen, StructureIssueSeverity.Info, HealthStage.Structure,
                    "Aktualizacja pól przy otwarciu (w:updateFields)",
                    "Word wyświetla przy otwarciu pytanie o aktualizację pól (np. spisu treści). W konwersji bezobsługowej to okno może zatrzymać proces.",
                    HealthLocations.Of(target, element), WordOpenImpact.None, PdfConversionImpact.Possible,
                    "Zaktualizuj pola i usuń w:updateFields z settings.xml.");
            }
            else if (IsW(element, "attachedTemplate"))
            {
                findings.Add(DocumentHealthCodes.AttachedTemplate, StructureIssueSeverity.Info, HealthStage.Structure,
                    "Dołączony szablon zewnętrzny",
                    "settings.xml wskazuje w:attachedTemplate. Word próbuje odczytać szablon ze ścieżki (może nie istnieć na serwerze konwersji); zwykle bez wpływu na treść.",
                    HealthLocations.Of(target, element), WordOpenImpact.None, PdfConversionImpact.Possible);
            }
            else if (IsW(element, "documentProtection") && IsTrue(WAttr(element, "enforcement")))
            {
                findings.Add(DocumentHealthCodes.DocumentProtection, StructureIssueSeverity.Info, HealthStage.Structure,
                    "Ochrona dokumentu (tylko do odczytu / formularz)",
                    $"settings.xml wymusza ochronę w:edit=\"{WAttr(element, "edit") ?? "?"}\". Nie blokuje otwarcia ani konwersji; edytor może nie zapisać zmian zgodnie z oczekiwaniami.",
                    HealthLocations.Of(target, element), WordOpenImpact.None, PdfConversionImpact.None);
            }
        }
    }

    private static void CheckFontTable(HealthPackageContext context, IReadOnlyDictionary<string, XDocument> parts, HealthFindingCollector findings, Counters stats)
    {
        var target = context.Opc.Relationships.FindTargetByType(context.MainDocumentPartPath, "fontTable");

        if (target is null || !parts.TryGetValue(target, out var document) || document.Root is null)
        {
            return;
        }

        var obfuscated = false;

        foreach (var embed in document.Root.Descendants().Where(element =>
                     OoxmlNamespaces.IsWordprocessing(element.Name.NamespaceName) &&
                     element.Name.LocalName is "embedRegular" or "embedBold" or "embedItalic" or "embedBoldItalic"))
        {
            stats.EmbeddedFonts++;
            var id = RelAttr(embed, "id");
            var relationship = id is null ? null : context.Opc.Relationships.Find(target, id);

            if (relationship is null)
            {
                findings.Add(DocumentHealthCodes.EmbeddedFontMissing, StructureIssueSeverity.Warning, HealthStage.Structure,
                    "Osadzony font bez relationshipu",
                    $"w:{embed.Name.LocalName} r:id=\"{id}\" nie ma relationshipu w fontTable.xml. Word użyje zastępczego fontu; konwertery mogą zgłosić błąd odwołania.",
                    HealthLocations.Of(target, embed), WordOpenImpact.None, PdfConversionImpact.Possible,
                    "Usuń wpis osadzenia albo osadź font ponownie (Word: Opcje → Zapisywanie → Osadź czcionki).");
            }

            obfuscated |= WAttr(embed, "fontKey") is not null;
        }

        if (obfuscated)
        {
            findings.Add(DocumentHealthCodes.EmbeddedFontObfuscated, StructureIssueSeverity.Info, HealthStage.Structure,
                "Osadzone fonty zaciemnione (w:fontKey)",
                $"Pakiet osadza {stats.EmbeddedFonts} font(ów) w formacie ODTTF z kluczem. Word je odczytuje; konwertery bez obsługi ODTTF zastępują font domyślnym (inne łamanie wierszy niż w Wordzie).",
                target, WordOpenImpact.None, PdfConversionImpact.Possible);
        }
    }

    private void CheckDocumentScale(Counters stats, HealthFindingCollector findings)
    {
        if (stats.Elements > _options.ElementCountWarning)
        {
            findings.Add(DocumentHealthCodes.DocumentVeryLarge, StructureIssueSeverity.Warning, HealthStage.Structure,
                "Bardzo duży dokument",
                $"Części treści mają łącznie {stats.Elements} elementów XML ({stats.Paragraphs} akapitów, {stats.Tables} tabel). Konwersja może przekroczyć limit czasu usługi konwertującej.",
                null, WordOpenImpact.None, PdfConversionImpact.Possible,
                "Podziel dokument albo zwiększ limit czasu konwersji.");
        }

        if (stats.ImageBytes > _options.TotalImageBytesWarning)
        {
            findings.Add(DocumentHealthCodes.ImagesTotalLarge, StructureIssueSeverity.Warning, HealthStage.Structure,
                "Obrazy zajmują bardzo dużo miejsca",
                $"{stats.ImageParts} części obrazów o łącznym rozmiarze {stats.ImageBytes / (1024 * 1024)} MB. Konwertery ładują wszystkie do pamięci — ryzyko przekroczenia limitów pamięci lub czasu.",
                null, WordOpenImpact.None, PdfConversionImpact.Possible,
                "Skompresuj obrazy przed wysyłką.");
        }
    }

    // ── Pomocnicze ────────────────────────────────────────────────────────────

    /// <summary>
    /// Dzieci-treść kontenera: rozwija w:sdt → w:sdtContent i w:customXml, pomija właściwości
    /// i znaczniki zakresów (zakładki, komentarze, proofErr), które Word dopuszcza w dowolnym miejscu.
    /// </summary>
    private static IEnumerable<XElement> ContentChildren(XElement container)
    {
        foreach (var child in container.Elements())
        {
            if (!OoxmlNamespaces.IsWordprocessing(child.Name.NamespaceName))
            {
                if (child.Name == Mc + "AlternateContent")
                {
                    continue;
                }

                yield return child;
                continue;
            }

            if (MarkerElements.Contains(child.Name.LocalName))
            {
                continue;
            }

            if (child.Name.LocalName == "sdt")
            {
                var content = child.Elements().FirstOrDefault(element => IsW(element, "sdtContent"));

                if (content is not null)
                {
                    foreach (var nested in ContentChildren(content))
                    {
                        yield return nested;
                    }
                }

                continue;
            }

            if (child.Name.LocalName == "customXml")
            {
                foreach (var nested in ContentChildren(child))
                {
                    yield return nested;
                }

                continue;
            }

            yield return child;
        }
    }

    private static bool IsW(XElement element, string localName) =>
        element.Name.LocalName == localName && OoxmlNamespaces.IsWordprocessing(element.Name.NamespaceName);

    private static string? WAttr(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName == localName && OoxmlNamespaces.IsWordprocessing(attribute.Name.NamespaceName))?.Value;

    private static string? RelAttr(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName == localName && OoxmlNamespaces.IsOfficeRelationship(attribute.Name.NamespaceName))?.Value;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool IsTrue(string? value) =>
        value is null || value is "1" or "true" or "on";

    private sealed class Counters
    {
        public int Elements;
        public int Paragraphs;
        public int Tables;
        public int MaxTableNesting;
        public int Drawings;
        public int Fields;
        public int Sections;
        public int Footnotes;
        public int Endnotes;
        public int Comments;
        public int TrackedRevisions;
        public int ContentControls;
        public int AltChunks;
        public int EmbeddedFonts;
        public int ImageParts;
        public long ImageBytes;

        public DocumentHealthStatistics ToStatistics(HealthPackageContext context) => new(
            context.Package.Entries.Count,
            context.Raw.XmlParts.Count,
            ImageParts,
            ImageBytes,
            Elements,
            Paragraphs,
            Tables,
            MaxTableNesting,
            Drawings,
            Fields,
            Sections,
            Footnotes,
            Endnotes,
            Comments,
            TrackedRevisions,
            ContentControls,
            AltChunks,
            EmbeddedFonts);
    }

    /// <summary>Zakres jednej „opowieści" (część XML): kontekst pakietu, ścieżka, korzeń, kolektor i liczniki.</summary>
    private sealed class StoryScope
    {
        public StoryScope(HealthPackageContext context, string partPath, XElement root, HealthFindingCollector findings, Counters stats)
        {
            Context = context;
            PartPath = partPath;
            Root = root;
            Findings = findings;
            Stats = stats;
        }

        public HealthPackageContext Context { get; }
        public string PartPath { get; }
        public XElement Root { get; }
        public HealthFindingCollector Findings { get; }
        public Counters Stats { get; }

        public void Add(
            string code,
            StructureIssueSeverity severity,
            string title,
            string description,
            XElement? element,
            WordOpenImpact wordImpact,
            PdfConversionImpact pdfImpact,
            string? remedy = null)
        {
            Findings.Add(code, severity, HealthStage.Structure, title, description,
                element is null ? PartPath : HealthLocations.Of(PartPath, element), wordImpact, pdfImpact, remedy);
        }
    }
}
