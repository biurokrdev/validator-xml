using D2ViewerEditor.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public static class DocumentHealthServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentHealth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DocumentHealthOptions>(configuration.GetSection(DocumentHealthOptions.SectionName));

        services.AddSingleton<FileContainerCheck>();
        services.AddSingleton<PackageHealthCheck>();
        services.AddSingleton<XmlPartsHealthCheck>();
        services.AddSingleton<WordStructureHealthCheck>();
        services.AddSingleton<LibreOfficeConverterProbe>();

        services.AddScoped<ConversionProbeRunner>();
        services.AddScoped<IDocumentHealthInspector, DocumentHealthInspector>();

        return services;
    }
}
