using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace EfektRuno.Templating.Workers;

/// <param name="UnresolvedPlaceholders">Well-formed tokens with no matching JSON key - left in the document.</param>
/// <param name="SuspiciousFragments">Broken delimiters such as "%nadawca%&gt;" (missing "&lt;").</param>
public sealed record RenderReport(IReadOnlyList<string> UnresolvedPlaceholders, IReadOnlyList<string> SuspiciousFragments);

/// <summary>
/// Dispatches sections to workers and runs value replacement. Default dispatch: registered name,
/// otherwise &lt;%?%&gt;/&lt;%^%&gt; -> ConditionWorker, rows of a table -> SimpleTableWorker, anything else -> StampWorker.
/// </summary>
public sealed class WorkerPipeline
{
    private static readonly Regex BrokenDelimiters = new(
        @"<?%[#?^/]?[\p{L}\p{N}_.\-]+%>?|<%|%>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly WorkerPipeline? _parent;
    private readonly IReadOnlyDictionary<string, IDocxWorker> _overrides;

    public WorkerPipeline(DocxTemplateOptions? options = null)
    {
        Options = options ?? new DocxTemplateOptions();
        Values = new ValueWorker(Options);
        _overrides = new Dictionary<string, IDocxWorker>();
    }

    private WorkerPipeline(WorkerPipeline parent, IReadOnlyDictionary<string, IDocxWorker> overrides)
    {
        _parent = parent;
        _overrides = overrides;
        Options = parent.Options;
        Values = parent.Values;
        Table = parent.Table;
        Stamp = parent.Stamp;
        Condition = parent.Condition;
    }

    public DocxTemplateOptions Options { get; }

    public ValueWorker Values { get; }

    public SimpleTableWorker Table { get; init; } = new();

    public StampWorker Stamp { get; init; } = new();

    public ConditionWorker Condition { get; init; } = new();

    /// <summary>Custom worker per section name, e.g. ["podpis"] = new SignatureWorker().</summary>
    public Dictionary<string, IDocxWorker> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Child pipeline with extra name overrides (used by a stamp for its sub-workers).</summary>
    public WorkerPipeline With(IReadOnlyDictionary<string, IDocxWorker> overrides)
    {
        return new WorkerPipeline(this, overrides);
    }

    public IDocxWorker Resolve(Section section)
    {
        for (WorkerPipeline? pipeline = this; pipeline != null; pipeline = pipeline._parent)
        {
            if (pipeline._overrides.TryGetValue(section.Name, out IDocxWorker? worker)
                || pipeline.ByName.TryGetValue(section.Name, out worker))
                return worker;
        }

        if (section.Kind is TokenKind.If or TokenKind.IfNot)
            return Condition;

        return section.IsRowRange ? Table : Stamp;
    }

    /// <summary>Processes every section inside the scope, then the remaining value tokens.</summary>
    public void Run(OpenXmlElement scope, DataContext context)
    {
        while (Section.FindFirst(scope) is { } section)
            Resolve(section).Fill(section, context, this);

        Values.Fill(scope, context);
    }

    /// <summary>Whole document: body, headers and footers against one JSON root.</summary>
    public RenderReport Fill(WordprocessingDocument document, JsonNode? data)
    {
        var unresolved = new List<string>();
        var suspicious = new List<string>();
        var context = new DataContext(data, null);

        foreach (OpenXmlPartRootElement root in Roots(document))
        {
            PlaceholderNormalizer.Normalize(root);
            Run(root, context);
            RepairStructure(root);
            Lint(root, unresolved, suspicious);
            root.Save();
        }

        RenumberDrawings(document);
        return new RenderReport(unresolved.Distinct().ToList(), suspicious.Distinct().ToList());
    }

    /// <summary>Only the sections called <paramref name="name"/>, fed directly with <paramref name="values"/>.</summary>
    public void FillNamed(WordprocessingDocument document, string name, JsonNode? values, IDocxWorker worker)
    {
        // A JsonNode has a single parent, hence the clone.
        var context = new DataContext(new JsonObject { [name] = values?.DeepClone() }, null);

        foreach (OpenXmlPartRootElement root in Roots(document))
        {
            PlaceholderNormalizer.Normalize(root);
            while (Section.FindFirst(root, name) is { } section)
                worker.Fill(section, context, this);
            RepairStructure(root);
            root.Save();
        }

        RenumberDrawings(document);
    }

    private static IEnumerable<OpenXmlPartRootElement> Roots(WordprocessingDocument document)
    {
        MainDocumentPart main = document.MainDocumentPart
            ?? throw new TemplateException("Szablon nie zawiera części głównej dokumentu.");

        var roots = new List<OpenXmlPartRootElement?> { main.Document };
        roots.AddRange(main.HeaderParts.Select(p => (OpenXmlPartRootElement?)p.Header));
        roots.AddRange(main.FooterParts.Select(p => (OpenXmlPartRootElement?)p.Footer));
        return roots.OfType<OpenXmlPartRootElement>();
    }

    private static void RepairStructure(OpenXmlElement root)
    {
        foreach (DocumentFormat.OpenXml.Wordprocessing.Table table in root
                     .Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>()
                     .Where(t => !t.Elements<TableRow>().Any()).ToList())
            table.Remove();

        // w:tc must end with a paragraph, otherwise Word refuses to open the file.
        foreach (TableCell cell in root.Descendants<TableCell>())
        {
            OpenXmlElement? lastBlock = cell.ChildElements.LastOrDefault(
                c => c is Paragraph or DocumentFormat.OpenXml.Wordprocessing.Table or SdtBlock);
            if (lastBlock is null or DocumentFormat.OpenXml.Wordprocessing.Table)
                cell.AppendChild(new Paragraph());
        }
    }

    /// <summary>Cloned pictures/shapes carry the same wp:docPr/@id - Word may report the file as damaged.</summary>
    private static void RenumberDrawings(WordprocessingDocument document)
    {
        uint id = 0;
        foreach (OpenXmlPartRootElement root in Roots(document))
        {
            foreach (DW.DocProperties properties in root.Descendants<DW.DocProperties>())
                properties.Id = ++id;
            root.Save();
        }
    }

    private static void Lint(OpenXmlElement root, List<string> unresolved, List<string> suspicious)
    {
        foreach (Paragraph paragraph in root.Descendants<Paragraph>())
        {
            string text = string.Concat(PlaceholderNormalizer.OwnTexts(paragraph).Select(t => t.Text));
            if (!text.Contains('%'))
                continue;

            unresolved.AddRange(TemplateToken.Pattern.Matches(text).Select(m => m.Value));

            string rest = TemplateToken.Pattern.Replace(text, " ");
            suspicious.AddRange(BrokenDelimiters.Matches(rest).Select(m => m.Value));
        }
    }
}
