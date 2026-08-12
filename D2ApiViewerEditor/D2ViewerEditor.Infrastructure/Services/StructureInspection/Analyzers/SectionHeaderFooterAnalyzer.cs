using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class SectionHeaderFooterAnalyzer
{
    private static readonly string[] Kinds = ["Header", "Footer"];
    private static readonly string[] Types = ["Default", "First", "Even"];

    public IReadOnlyList<DocumentSectionInfo> Analyze(StructureAnalysisContext context)
    {
        var sectionNodes = context.WordprocessingNodes("sectPr")
            .Where(node => node.Element.PartPath.Equals(context.MainDocumentPartPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Element.Order)
            .ToArray();

        var evenAndOddHeaders = IsEvenAndOddHeadersEnabled(context);
        var effectiveBindings = new Dictionary<(string Kind, string Type), HeaderFooterBinding>();
        var sections = new List<DocumentSectionInfo>();

        for (var index = 0; index < sectionNodes.Length; index++)
        {
            sections.Add(AnalyzeSection(context, sectionNodes[index], index + 1, evenAndOddHeaders, effectiveBindings));
        }

        AnnotatePartRoots(context, sections);
        AnnotateOrphanedParts(context, sections);

        return sections;
    }

    private static DocumentSectionInfo AnalyzeSection(
        StructureAnalysisContext context,
        IndexedNode sectionNode,
        int sectionNumber,
        bool evenAndOddHeaders,
        Dictionary<(string Kind, string Type), HeaderFooterBinding> effectiveBindings)
    {
        var firstPageDifferent = OoxmlXml.IsToggleEnabled(OoxmlXml.Child(sectionNode.Node, "titlePg"));
        var sectionIssues = new List<StructureIssue>();
        var directReferences = ReadDirectReferences(context, sectionNode, sectionIssues);
        var bindings = new List<HeaderFooterBinding>();

        foreach (var kind in Kinds)
        {
            foreach (var type in Types)
            {
                var key = (kind, type);
                var isActive = type switch
                {
                    "First" => firstPageDifferent,
                    "Even" => evenAndOddHeaders,
                    _ => true
                };

                HeaderFooterBinding binding;

                if (directReferences.TryGetValue(key, out var referenceNode))
                {
                    binding = ResolveDirectBinding(context, kind, type, sectionNumber, isActive, referenceNode);
                }
                else if (effectiveBindings.TryGetValue(key, out var inherited))
                {
                    binding = inherited with { Source = "Inherited", IsActive = isActive, ReferenceElementId = null, Issues = [] };
                }
                else
                {
                    binding = new HeaderFooterBinding(kind, type, "Missing", null, isActive,
                        null, null, null, null, null, null, null, false, []);
                }

                bindings.Add(binding);
                sectionIssues.AddRange(binding.Issues);

                if (binding.Source != "Missing")
                {
                    effectiveBindings[key] = binding;
                }
            }
        }

        sectionNode.Element.Properties.Add(new StructureProperty("Sekcja", sectionNumber.ToString(), PropertySources.DocumentStructure));
        sectionNode.Element.Properties.Add(new StructureProperty("Inna pierwsza strona", firstPageDifferent ? "tak" : "nie", "w:titlePg"));
        sectionNode.Element.Properties.Add(new StructureProperty("Inne parzyste/nieparzyste", evenAndOddHeaders ? "tak" : "nie", "w:evenAndOddHeaders"));

        return new DocumentSectionInfo(
            sectionNumber,
            sectionNode.Element.Id,
            sectionNode.Element.DisplayPath,
            firstPageDifferent,
            evenAndOddHeaders,
            bindings,
            sectionIssues);
    }

    private static Dictionary<(string Kind, string Type), IndexedNode> ReadDirectReferences(
        StructureAnalysisContext context,
        IndexedNode sectionNode,
        ICollection<StructureIssue> sectionIssues)
    {
        var result = new Dictionary<(string Kind, string Type), IndexedNode>();

        foreach (var reference in sectionNode.Node.Elements().Where(child =>
                     OoxmlNamespaces.IsWordprocessing(child.Name.NamespaceName) &&
                     child.Name.LocalName is "headerReference" or "footerReference"))
        {
            var element = context.FindElement(reference);

            if (element is null)
            {
                continue;
            }

            var kind = reference.Name.LocalName == "headerReference" ? "Header" : "Footer";
            var type = NormalizeType(OoxmlXml.Attribute(reference, "type"));

            if (result.TryAdd((kind, type), new IndexedNode(element, reference)))
            {
                continue;
            }

            var issue = new StructureIssue(
                StructureIssueCodes.HeaderFooterReferenceDuplicate,
                StructureIssueSeverity.Error,
                "Zduplikowana referencja nagłówka/stopki",
                $"Sekcja zawiera więcej niż jedną referencję typu '{type}' dla: {kind}.");

            element.Issues.Add(issue);
            sectionIssues.Add(issue);
        }

        return result;
    }

    private static HeaderFooterBinding ResolveDirectBinding(
        StructureAnalysisContext context,
        string kind,
        string type,
        int sectionNumber,
        bool isActive,
        IndexedNode referenceNode)
    {
        var issues = new List<StructureIssue>();
        var relationshipId = OoxmlXml.RelationshipAttribute(referenceNode.Node, "id");
        StructureRelationship? relationship = null;

        if (string.IsNullOrWhiteSpace(relationshipId))
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.HeaderFooterRelationshipIdMissing,
                StructureIssueSeverity.Error,
                "Brak r:id w referencji",
                $"Sekcja {sectionNumber}: referencja {kind} '{type}' nie ma atrybutu r:id."));
        }
        else
        {
            relationship = context.Opc.Relationships.Find(context.MainDocumentPartPath, relationshipId);

            if (relationship is null)
            {
                issues.Add(new StructureIssue(
                    StructureIssueCodes.HeaderFooterRelationshipNotFound,
                    StructureIssueSeverity.Error,
                    "Brak relationshipu nagłówka/stopki",
                    $"Relationship '{relationshipId}' użyty przez sekcję {sectionNumber} nie istnieje dla '{context.MainDocumentPartPath}'."));
            }
        }

        var expectedSuffix = kind == "Header" ? "header" : "footer";

        if (relationship is not null && !OoxmlNamespaces.IsRelationshipType(relationship.Type, expectedSuffix))
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.HeaderFooterRelationshipTypeInvalid,
                StructureIssueSeverity.Error,
                "Nieoczekiwany typ relationshipu",
                $"Relationship '{relationship.Id}' pełni rolę: {kind}, ale ma typ '{relationship.Type}'."));
        }

        if (relationship?.Status == StructureRelationshipStatus.External)
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.HeaderFooterExternalRelationship,
                StructureIssueSeverity.Error,
                "Zewnętrzny relationship nagłówka/stopki",
                $"Relationship {kind} używa TargetMode='External', co nie jest normalnym wiązaniem części nagłówka/stopki."));
        }

        var partPath = relationship?.ResolvedTarget;
        var partExists = partPath is not null && context.Package.Entries.ContainsKey(partPath);

        if (relationship is not null && relationship.Status != StructureRelationshipStatus.External && !partExists)
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.HeaderFooterPartNotFound,
                StructureIssueSeverity.Error,
                $"Brak części: {kind}",
                $"Relationship '{relationship.Id}' wskazuje '{partPath}', ale takiej części nie ma w pakiecie."));
        }

        referenceNode.Element.Issues.AddRange(issues);
        referenceNode.Element.Properties.Add(new StructureProperty("Sekcja", sectionNumber.ToString(), "w:sectPr"));
        referenceNode.Element.Properties.Add(new StructureProperty("Typ referencji", type, "w:type"));
        referenceNode.Element.Properties.Add(new StructureProperty("Rozwiązana część", partPath, PropertySources.ResolvedReference, relationshipId));

        return new HeaderFooterBinding(
            kind,
            type,
            "Direct",
            sectionNumber,
            isActive,
            referenceNode.Element.Id,
            relationshipId,
            relationship?.Type,
            relationship?.TargetMode,
            relationship?.Target,
            partPath,
            partPath is null ? null : context.FindPartRoot(partPath)?.Id,
            partExists,
            issues);
    }

    private static void AnnotatePartRoots(StructureAnalysisContext context, IReadOnlyList<DocumentSectionInfo> sections)
    {
        foreach (var section in sections)
        {
            foreach (var binding in section.HeaderFooterBindings.Where(binding => binding.PartRootElementId is not null))
            {
                var root = context.Nodes.FirstOrDefault(node => node.Element.Id == binding.PartRootElementId)?.Element;

                root?.Properties.Add(new StructureProperty(
                    $"Używane przez sekcję {section.Number}",
                    $"{binding.Kind} {binding.Type} ({binding.Source})",
                    PropertySources.ResolvedReference));
            }
        }
    }

    private static void AnnotateOrphanedParts(StructureAnalysisContext context, IReadOnlyList<DocumentSectionInfo> sections)
    {
        var usedParts = sections
            .SelectMany(section => section.HeaderFooterBindings)
            .Where(binding => binding.PartPath is not null)
            .Select(binding => binding.PartPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var part in context.Package.XmlParts.Values.Where(IsHeaderOrFooterPart))
        {
            if (usedParts.Contains(part.Path))
            {
                continue;
            }

            context.FindPartRoot(part.Path)?.Issues.Add(new StructureIssue(
                StructureIssueCodes.HeaderFooterPartOrphaned,
                StructureIssueSeverity.Warning,
                "Nieużywana część nagłówka/stopki",
                $"Część '{part.Path}' istnieje, ale żadna sekcja jej efektywnie nie używa."));
        }
    }

    private static bool IsHeaderOrFooterPart(RawPackagePart part) =>
        part.ContentType?.Contains("header", StringComparison.OrdinalIgnoreCase) == true ||
        part.ContentType?.Contains("footer", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsEvenAndOddHeadersEnabled(StructureAnalysisContext context)
    {
        var setting = context.WordprocessingNodes("evenAndOddHeaders").FirstOrDefault();

        return setting is not null && OoxmlXml.IsToggleEnabled(setting.Node);
    }

    private static string NormalizeType(string? value) => value?.ToLowerInvariant() switch
    {
        "first" => "First",
        "even" => "Even",
        _ => "Default"
    };
}
