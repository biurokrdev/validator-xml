using D2ViewerEditor.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

/// <summary>
/// Rejestracja „Porównania dokumentów". Komparator jest bezstanowy (singleton); korzysta z czytnika
/// kontenera diagnostyki kondycji (<c>FileContainerCheck</c>) i bezpiecznego loadera XML walidatora,
/// więc wymaga wcześniejszych <c>AddStructureInspection</c> i <c>AddDocumentHealth</c>.
/// </summary>
public static class DocumentCompareServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentCompare(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DocumentCompareOptions>(configuration.GetSection(DocumentCompareOptions.SectionName));
        services.AddSingleton<IDocumentComparer, DocumentComparer>();

        return services;
    }
}
