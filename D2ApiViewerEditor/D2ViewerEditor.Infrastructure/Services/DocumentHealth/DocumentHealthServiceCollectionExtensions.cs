using D2ViewerEditor.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>
/// Rejestracja diagnostyki „Kondycja dokumentu". Etapy analizy są bezstanowe (singletony);
/// próby konwersji i inspektor są scoped, bo konwerter DOCX→HTML edytora jest zarejestrowany
/// jako scoped. Wymaga wcześniejszego <c>AddStructureInspection</c> (analizator OPC, loader XML,
/// walidator schematu są współdzielone z Walidatorem struktury).
/// </summary>
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
