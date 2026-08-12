using D2ViewerEditor.Domain.Models;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;

public sealed class EditorCompatibilityAnalyzer : IStructureAnalyzer
{
    private readonly EditorCompatibilityOptions _options;

    public EditorCompatibilityAnalyzer(IOptions<EditorCompatibilityOptions> options)
    {
        _options = options.Value;
    }

    public void Analyze(StructureAnalysisContext context)
    {
        foreach (var node in context.Nodes)
        {
            foreach (var feature in DetectFeatures(node).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ApplyFeature(node.Element, feature);
            }
        }
    }

    private void ApplyFeature(InspectedElement element, string feature)
    {
        var level = _options.Features.GetValueOrDefault(feature, _options.DefaultLevel);

        element.EditorCompatibility.Add(new EditorCompatibilityInfo(feature, level, $"Profil: {_options.ProfileName}"));

        if (level.Equals(EditorCompatibilityLevels.Unsupported, StringComparison.OrdinalIgnoreCase))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.EditorFeatureUnsupported,
                StructureIssueSeverity.Warning,
                "Cecha nieobsługiwana przez edytor",
                $"Cecha '{feature}' jest oznaczona jako nieobsługiwana w profilu '{_options.ProfileName}'."));
            return;
        }

        if (level.Equals(EditorCompatibilityLevels.Partial, StringComparison.OrdinalIgnoreCase))
        {
            element.Issues.Add(new StructureIssue(
                StructureIssueCodes.EditorFeaturePartial,
                StructureIssueSeverity.Info,
                "Cecha obsługiwana częściowo",
                $"Cecha '{feature}' jest oznaczona jako częściowo obsługiwana w profilu '{_options.ProfileName}'."));
        }
    }

    private static IEnumerable<string> DetectFeatures(IndexedNode node)
    {
        var element = node.Element;
        var categoryFeature = element.Category switch
        {
            ElementCategories.AnchoredDrawing => EditorFeatures.DrawingAnchor,
            ElementCategories.InlineDrawing => EditorFeatures.DrawingInline,
            ElementCategories.LegacyVml or ElementCategories.LegacyDrawingContainer => EditorFeatures.DrawingVml,
            ElementCategories.SvgImage => EditorFeatures.DrawingSvg,
            ElementCategories.Chart => EditorFeatures.DrawingChart,
            ElementCategories.SmartArt => EditorFeatures.DrawingSmartArt,
            ElementCategories.GroupedShape => EditorFeatures.DrawingGroupedShape,
            ElementCategories.DrawingRelativeSize => EditorFeatures.DrawingRelativeSize,
            ElementCategories.ImageCrop => EditorFeatures.DrawingImageCrop,
            ElementCategories.DrawingTransform => EditorFeatures.DrawingTransform,
            ElementCategories.TextBoxContent => EditorFeatures.DrawingTextBox,
            ElementCategories.EmbeddedObject => EditorFeatures.EmbeddedObject,
            ElementCategories.Field => EditorFeatures.Field,
            ElementCategories.ContentControl => EditorFeatures.ContentControl,
            ElementCategories.Revision => EditorFeatures.TrackedChanges,
            ElementCategories.Table => EditorFeatures.Table,
            ElementCategories.Footnote => EditorFeatures.Footnote,
            ElementCategories.Endnote => EditorFeatures.Endnote,
            ElementCategories.Comment => EditorFeatures.Comment,
            ElementCategories.Bookmark => EditorFeatures.Bookmark,
            ElementCategories.Compatibility when element.LocalName == "AlternateContent" => EditorFeatures.AlternateContent,
            _ => null
        };

        if (categoryFeature is not null)
        {
            yield return categoryFeature;
        }

        if (element.Category != ElementCategories.AnchoredDrawing)
        {
            yield break;
        }

        foreach (var attribute in element.Attributes.Where(attribute =>
                     attribute.LocalName is "behindDoc" or "allowOverlap" or "layoutInCell"))
        {
            yield return $"{EditorFeatures.DrawingAnchor}.{attribute.LocalName}";
        }

        var wrap = node.Node.Elements()
            .FirstOrDefault(child => child.Name.LocalName.StartsWith("wrap", StringComparison.Ordinal));

        if (wrap is not null)
        {
            yield return $"drawing.{wrap.Name.LocalName}";
        }
    }
}

public static class EditorCompatibilityLevels
{
    public const string Supported = "Supported";
    public const string Partial = "Partial";
    public const string Unsupported = "Unsupported";
    public const string Unknown = "Unknown";
}

public static class EditorFeatures
{
    public const string DrawingInline = "drawing.inline";
    public const string DrawingAnchor = "drawing.anchor";
    public const string DrawingVml = "drawing.vml";
    public const string DrawingSvg = "drawing.svg";
    public const string DrawingChart = "drawing.chart";
    public const string DrawingSmartArt = "drawing.smartart";
    public const string DrawingGroupedShape = "drawing.groupedShape";
    public const string DrawingRelativeSize = "drawing.relativeSize";
    public const string DrawingImageCrop = "drawing.imageCrop";
    public const string DrawingTransform = "drawing.transform";
    public const string DrawingTextBox = "drawing.textbox";
    public const string EmbeddedObject = "embeddedObject";
    public const string AlternateContent = "markupCompatibility.alternateContent";
    public const string Field = "field";
    public const string ContentControl = "contentControl";
    public const string TrackedChanges = "trackedChanges";
    public const string Table = "table";
    public const string Footnote = "footnote";
    public const string Endnote = "endnote";
    public const string Comment = "comment";
    public const string Bookmark = "bookmark";
}
