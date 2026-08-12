using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class NumberingAnalyzer : IStructureAnalyzer
{
    public void Analyze(StructureAnalysisContext context)
    {
        var numberingPart = context.FindRelatedPart("numbering", "numbering+xml");

        if (numberingPart is null)
        {
            AnnotateMissingNumberingPart(context);
            return;
        }

        var root = context.XmlLoader.Load(numberingPart.Content).Root;

        if (root is null)
        {
            return;
        }

        var abstractNumberings = ParseAbstractNumberings(root);
        var instances = ParseInstances(root);

        foreach (var node in context.WordprocessingNodes("p"))
        {
            AnalyzeParagraph(node, instances, abstractNumberings);
        }
    }

    private static void AnalyzeParagraph(
        IndexedNode node,
        IReadOnlyDictionary<int, NumberingInstance> instances,
        IReadOnlyDictionary<int, AbstractNumbering> abstractNumberings)
    {
        var numberingProperties = OoxmlXml.Child(OoxmlXml.Child(node.Node, "pPr"), "numPr");

        if (numberingProperties is null)
        {
            return;
        }

        var element = node.Element;
        var numberingId = OoxmlXml.ChildInt(numberingProperties, "numId");
        var levelIndex = OoxmlXml.ChildInt(numberingProperties, "ilvl") ?? 0;

        element.Properties.Add(new StructureProperty("Numeracja numId", numberingId?.ToString(), "w:numPr"));
        element.Properties.Add(new StructureProperty("Poziom listy", levelIndex.ToString(), "w:numPr"));

        if (numberingId is null or 0)
        {
            return;
        }

        if (!instances.TryGetValue(numberingId.Value, out var instance))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.NumberingInstanceNotFound,
                StructureIssueSeverity.Error,
                "Brak instancji numeracji",
                $"Akapit odwołuje się do numId={numberingId}, ale numbering.xml nie zawiera pasującego w:num."));
            return;
        }

        element.Properties.Add(new StructureProperty(
            "Numeracja abstrakcyjna",
            instance.AbstractNumberingId?.ToString(),
            PropertySources.Numbering,
            $"numId={numberingId}"));

        if (instance.AbstractNumberingId is null ||
            !abstractNumberings.TryGetValue(instance.AbstractNumberingId.Value, out var abstractNumbering))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.AbstractNumberingNotFound,
                StructureIssueSeverity.Error,
                "Brak definicji abstractNum",
                $"numId={numberingId} wskazuje nieistniejące abstractNumId={instance.AbstractNumberingId?.ToString() ?? "null"}."));
            return;
        }

        if (!abstractNumbering.Levels.TryGetValue(levelIndex, out var level))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.NumberingLevelNotFound,
                StructureIssueSeverity.Warning,
                "Brak poziomu numeracji",
                $"Akapit używa poziomu {levelIndex}, ale abstractNumId={abstractNumbering.Id} nie ma takiego w:lvl."));
            return;
        }

        instance.Overrides.TryGetValue(levelIndex, out var levelOverride);
        var reference = $"abstractNumId={abstractNumbering.Id}; ilvl={levelIndex}";

        element.Properties.Add(new StructureProperty("Format listy", level.Format, PropertySources.Numbering, reference));
        element.Properties.Add(new StructureProperty("Wzorzec etykiety", level.Text, PropertySources.Numbering, reference));
        element.Properties.Add(new StructureProperty(
            "Wartość początkowa",
            (levelOverride?.StartOverride ?? level.Start).ToString(),
            PropertySources.Numbering,
            levelOverride?.StartOverride is null ? reference : $"startOverride, numId={numberingId}"));
        element.Properties.Add(new StructureProperty("Sufiks etykiety", level.Suffix, PropertySources.Numbering, reference));
        element.Properties.Add(new StructureProperty("Wyrównanie etykiety", level.Justification, PropertySources.Numbering, reference));
        element.Properties.Add(new StructureProperty("Wcięcia poziomu", level.Indentation, PropertySources.Numbering, reference));
        element.Properties.Add(new StructureProperty("Czcionka punktora", level.BulletFont, PropertySources.Numbering, reference));

        if (levelOverride?.HasLevelDefinition == true)
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.NumberingLevelOverride,
                StructureIssueSeverity.Info,
                "Nadpisanie poziomu numeracji",
                $"numId={numberingId}, ilvl={levelIndex} ma własną definicję poziomu (w:lvlOverride). Edytor musi zastosować nadpisanie, a nie samą definicję abstrakcyjną."));
        }
    }

    private static void AnnotateMissingNumberingPart(StructureAnalysisContext context)
    {
        foreach (var node in context.WordprocessingNodes("numPr"))
        {
            node.Element.Issues.Add(new StructureIssue(
                StructureIssueCodes.NumberingPartNotFound,
                StructureIssueSeverity.Error,
                "Brak części z numeracją",
                "Akapit zawiera w:numPr, ale pakiet nie ma osiągalnej części numbering.xml."));
        }
    }

    private static Dictionary<int, AbstractNumbering> ParseAbstractNumberings(XElement root)
    {
        var result = new Dictionary<int, AbstractNumbering>();

        foreach (var abstractNum in OoxmlXml.Children(root, "abstractNum"))
        {
            var id = OoxmlXml.AttributeInt(abstractNum, "abstractNumId");

            if (id is null)
            {
                continue;
            }

            var levels = new Dictionary<int, NumberingLevel>();

            foreach (var level in OoxmlXml.Children(abstractNum, "lvl"))
            {
                var levelIndex = OoxmlXml.AttributeInt(level, "ilvl");

                if (levelIndex is null)
                {
                    continue;
                }

                levels[levelIndex.Value] = new NumberingLevel(
                    OoxmlXml.ChildInt(level, "start") ?? 1,
                    OoxmlXml.ChildVal(level, "numFmt"),
                    OoxmlXml.ChildVal(level, "lvlText"),
                    OoxmlXml.ChildVal(level, "suff"),
                    OoxmlXml.ChildVal(level, "lvlJc"),
                    OoxmlXml.DescribeAttributes(OoxmlXml.Child(OoxmlXml.Child(level, "pPr"), "ind")),
                    OoxmlXml.Attribute(OoxmlXml.Child(OoxmlXml.Child(level, "rPr"), "rFonts"), "ascii"));
            }

            result[id.Value] = new AbstractNumbering(id.Value, levels);
        }

        return result;
    }

    private static Dictionary<int, NumberingInstance> ParseInstances(XElement root)
    {
        var result = new Dictionary<int, NumberingInstance>();

        foreach (var num in OoxmlXml.Children(root, "num"))
        {
            var numberingId = OoxmlXml.AttributeInt(num, "numId");

            if (numberingId is null)
            {
                continue;
            }

            var overrides = new Dictionary<int, LevelOverride>();

            foreach (var levelOverride in OoxmlXml.Children(num, "lvlOverride"))
            {
                var levelIndex = OoxmlXml.AttributeInt(levelOverride, "ilvl");

                if (levelIndex is not null)
                {
                    overrides[levelIndex.Value] = new LevelOverride(
                        OoxmlXml.ChildInt(levelOverride, "startOverride"),
                        OoxmlXml.Child(levelOverride, "lvl") is not null);
                }
            }

            result[numberingId.Value] = new NumberingInstance(
                OoxmlXml.ChildInt(num, "abstractNumId"),
                overrides);
        }

        return result;
    }

    private sealed record AbstractNumbering(int Id, IReadOnlyDictionary<int, NumberingLevel> Levels);

    private sealed record NumberingLevel(
        int Start,
        string? Format,
        string? Text,
        string? Suffix,
        string? Justification,
        string? Indentation,
        string? BulletFont);

    private sealed record NumberingInstance(int? AbstractNumberingId, IReadOnlyDictionary<int, LevelOverride> Overrides);

    private sealed record LevelOverride(int? StartOverride, bool HasLevelDefinition);
}
