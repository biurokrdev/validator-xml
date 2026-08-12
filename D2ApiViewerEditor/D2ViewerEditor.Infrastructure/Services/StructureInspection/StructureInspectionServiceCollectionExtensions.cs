using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public static class StructureInspectionServiceCollectionExtensions
{
    public static IServiceCollection AddStructureInspection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<StructureInspectionOptions>(
            configuration.GetSection(StructureInspectionOptions.SectionName));
        services.Configure<EditorCompatibilityOptions>(
            configuration.GetSection(EditorCompatibilityOptions.SectionName));
        services.Configure<StructureInspectionRetentionOptions>(
            configuration.GetSection(StructureInspectionOptions.SectionName));

        services.AddSingleton<SafeOoxmlXmlLoader>();
        services.AddSingleton<OoxmlPackageReader>();
        services.AddSingleton<OpcPackageAnalyzer>();
        services.AddSingleton<OoxmlElementClassifier>();
        services.AddSingleton<OoxmlElementIndexer>();
        services.AddSingleton<OoxmlFragmentReader>();
        services.AddSingleton<OpenXmlSchemaValidatorRunner>();
        services.AddSingleton<SchemaIssueMapper>();
        services.AddSingleton<SectionHeaderFooterAnalyzer>();

        services.AddSingleton<IStructureAnalyzer, EffectiveFormattingAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, NumberingAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, TableAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, DrawingAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, SectionLayoutAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, FieldAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, ReferenceAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, ContentControlAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, RevisionAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, MarkupCompatibilityAnalyzer>();
        services.AddSingleton<IStructureAnalyzer, RunAndParagraphFeatureAnalyzer>();

        services.AddSingleton<IStructureAnalyzer, EditorCompatibilityAnalyzer>();

        services.AddSingleton<IDocumentStructureInspector, DocumentStructureInspector>();
        services.AddSingleton<IDocumentStructureInspectionStore, InMemoryDocumentStructureInspectionStore>();

        return services;
    }
}
