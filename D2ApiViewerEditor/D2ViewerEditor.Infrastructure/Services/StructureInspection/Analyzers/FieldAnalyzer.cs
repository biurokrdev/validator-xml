using System.Text;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class FieldAnalyzer : IStructureAnalyzer
{
    private static readonly HashSet<string> KnownFieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAGE", "NUMPAGES", "SECTION", "SECTIONPAGES", "TOC", "HYPERLINK", "REF", "PAGEREF",
        "SEQ", "IF", "INCLUDEPICTURE", "INCLUDETEXT", "DATE", "TIME", "AUTHOR", "TITLE", "SUBJECT",
        "FILENAME", "MERGEFIELD", "FORMTEXT", "FORMCHECKBOX", "FORMDROPDOWN", "DOCPROPERTY",
        "STYLEREF", "NOTEREF", "SYMBOL", "QUOTE", "ADDRESSBLOCK", "GREETINGLINE"
    };

    public void Analyze(StructureAnalysisContext context)
    {
        foreach (var node in context.WordprocessingNodes("fldSimple"))
        {
            AddInstructionProperties(node.Element, OoxmlXml.Attribute(node.Node, "instr"), "w:fldSimple");
        }

        foreach (var partNodes in context.Nodes
                     .Where(node => OoxmlNamespaces.IsWordprocessing(node.Element.NamespaceUri))
                     .GroupBy(node => node.Element.PartPath, StringComparer.OrdinalIgnoreCase))
        {
            AnalyzeComplexFields(partNodes.OrderBy(node => node.Element.Order));
        }
    }

    private static void AnalyzeComplexFields(IEnumerable<IndexedNode> partNodes)
    {
        var openFields = new Stack<FieldState>();

        foreach (var node in partNodes)
        {
            switch (node.Element.LocalName)
            {
                case "fldChar":
                    HandleFieldCharacter(node, openFields);
                    break;

                case "instrText":
                    HandleInstruction(node, openFields);
                    break;

                case "t" or "delText" when openFields.TryPeek(out var field) && field.HasSeparator:
                    field.Result.Append(node.Node.Value);
                    break;
            }
        }

        while (openFields.TryPop(out var incomplete))
        {
            incomplete.Begin.Element.Issues.Add(new StructureIssue(
                StructureIssueCodes.FieldNotClosed,
                StructureIssueSeverity.Error,
                "Pole niedomknięte",
                "Pole złożone ma znacznik begin, ale w tej części pakietu nie ma pasującego end."));
            AddInstructionProperties(incomplete.Begin.Element, incomplete.Instruction.ToString(), "pole złożone");
        }
    }

    private static void HandleFieldCharacter(IndexedNode node, Stack<FieldState> openFields)
    {
        switch (OoxmlXml.Attribute(node.Node, "fldCharType")?.ToLowerInvariant())
        {
            case "begin":
                if (openFields.Count > 0)
                {
                    node.Element.Issues.Add(new StructureIssue(
                        StructureIssueCodes.FieldNested,
                        StructureIssueSeverity.Info,
                        "Pole zagnieżdżone",
                        $"Pole zaczyna się wewnątrz innego pola (zagnieżdżenie na poziomie {openFields.Count + 1})."));
                }

                openFields.Push(new FieldState(node));
                break;

            case "separate":
                if (openFields.TryPeek(out var current))
                {
                    current.HasSeparator = true;
                }
                else
                {
                    node.Element.Issues.Add(new StructureIssue(
                        StructureIssueCodes.FieldSeparatorWithoutBegin,
                        StructureIssueSeverity.Error,
                        "Separator pola bez początku",
                        "w:fldChar type='separate' występuje bez otwartego pola."));
                }

                break;

            case "end":
                if (openFields.TryPop(out var completed))
                {
                    CompleteField(completed, node);
                }
                else
                {
                    node.Element.Issues.Add(new StructureIssue(
                        StructureIssueCodes.FieldEndWithoutBegin,
                        StructureIssueSeverity.Error,
                        "Koniec pola bez początku",
                        "w:fldChar type='end' występuje bez otwartego pola."));
                }

                break;
        }
    }

    private static void HandleInstruction(IndexedNode node, Stack<FieldState> openFields)
    {
        if (openFields.TryPeek(out var current))
        {
            current.Instruction.Append(node.Node.Value);
            return;
        }

        node.Element.Issues.Add(new StructureIssue(
            StructureIssueCodes.FieldInstructionOutsideField,
            StructureIssueSeverity.Warning,
            "Instrukcja pola poza polem",
            "w:instrText występuje bez otwartego pola złożonego."));
    }

    private static void CompleteField(FieldState field, IndexedNode end)
    {
        var element = field.Begin.Element;

        AddInstructionProperties(element, field.Instruction.ToString(), "pole złożone");
        element.Properties.Add(new StructureProperty("Wynik pola", Normalize(field.Result.ToString()), "wynik pola"));
        element.Properties.Add(new StructureProperty("Pole ma separator", field.HasSeparator ? "tak" : "nie", "pole złożone"));
        end.Element.Properties.Add(new StructureProperty("Zamyka pole", element.Id, "pole złożone"));

        if (!field.HasSeparator)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.FieldWithoutSeparator,
                StructureIssueSeverity.Info,
                "Pole bez separatora",
                "Pole nie ma znacznika separate ani zapamiętanego wyniku. Część pól legalnie go pomija."));
        }
    }

    private static void AddInstructionProperties(InspectedElement element, string? instruction, string source)
    {
        var normalized = Normalize(instruction);

        if (normalized.Length == 0)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.FieldInstructionEmpty,
                StructureIssueSeverity.Warning,
                "Pusta instrukcja pola",
                "Pole nie zawiera czytelnej instrukcji."));
            return;
        }

        var fieldType = ParseFieldType(normalized);

        element.Properties.Add(new StructureProperty("Instrukcja pola", normalized, source));
        element.Properties.Add(new StructureProperty("Typ pola", fieldType, source));

        if (fieldType is not null && !KnownFieldTypes.Contains(fieldType))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.FieldUncommonType,
                StructureIssueSeverity.Info,
                "Nietypowy typ pola",
                $"Typ pola '{fieldType}' nie jest w katalogu typowych pól. Przed implementacją zachowania edytora obejrzyj surową instrukcję."));
        }
    }

    private static string? ParseFieldType(string instruction)
    {
        var trimmed = instruction.TrimStart();

        if (trimmed.Length == 0)
        {
            return null;
        }

        var end = trimmed.IndexOfAny([' ', '\\', '\t', '\r', '\n']);

        return (end < 0 ? trimmed : trimmed[..end]).Trim().ToUpperInvariant();
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class FieldState
    {
        public FieldState(IndexedNode begin) => Begin = begin;

        public IndexedNode Begin { get; }
        public bool HasSeparator { get; set; }
        public StringBuilder Instruction { get; } = new();
        public StringBuilder Result { get; } = new();
    }
}
