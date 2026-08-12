using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services.StructureInspection;

internal static class StructureInspectorTestHost
{
    public static DocumentStructureInspector Create(
        StructureInspectionOptions? options = null,
        EditorCompatibilityOptions? compatibility = null,
        IEnumerable<IStructureAnalyzer>? extraAnalyzers = null)
    {
        var wrappedOptions = Options.Create(options ?? new StructureInspectionOptions());
        var wrappedCompatibility = Options.Create(compatibility ?? new EditorCompatibilityOptions());
        var loader = new SafeOoxmlXmlLoader(wrappedOptions);

        var analyzers = new IStructureAnalyzer[]
        {
            new EffectiveFormattingAnalyzer(),
            new NumberingAnalyzer(),
            new TableAnalyzer(),
            new DrawingAnalyzer(),
            new SectionLayoutAnalyzer(),
            new FieldAnalyzer(),
            new ReferenceAnalyzer(),
            new ContentControlAnalyzer(),
            new RevisionAnalyzer(),
            new MarkupCompatibilityAnalyzer(),
            new RunAndParagraphFeatureAnalyzer(),
            new EditorCompatibilityAnalyzer(wrappedCompatibility)
        }.Concat(extraAnalyzers ?? []).ToArray();

        return new DocumentStructureInspector(
            new OoxmlPackageReader(wrappedOptions),
            new OpcPackageAnalyzer(loader),
            new OoxmlElementIndexer(wrappedOptions, loader, new OoxmlElementClassifier()),
            analyzers,
            new SectionHeaderFooterAnalyzer(),
            new OpenXmlSchemaValidatorRunner(wrappedOptions),
            new SchemaIssueMapper(),
            new OoxmlFragmentReader(),
            loader,
            wrappedOptions,
            NullLogger<DocumentStructureInspector>.Instance);
    }

    public static DocumentStructureAnalysis Analyze(byte[] package, DocumentStructureInspector? inspector = null) =>
        (inspector ?? Create()).Analyze(package, "test.docx", CancellationToken.None);

    public static InspectedElement Element(this DocumentStructureAnalysis analysis, string localName) =>
        analysis.Elements.First(element => element.LocalName == localName);

    public static IReadOnlyList<InspectedElement> Elements(this DocumentStructureAnalysis analysis, string localName) =>
        analysis.Elements.Where(element => element.LocalName == localName).ToArray();

    public static bool HasIssue(this InspectedElement element, string code) =>
        element.Issues.Any(issue => issue.Code == code);

    public static bool HasPackageIssue(this DocumentStructureAnalysis analysis, string code) =>
        analysis.PackageIssues.Any(issue => issue.Code == code);

    public static string? PropertyValue(this InspectedElement element, string name) =>
        element.Properties.FirstOrDefault(property => property.Name == name)?.Value;
}
