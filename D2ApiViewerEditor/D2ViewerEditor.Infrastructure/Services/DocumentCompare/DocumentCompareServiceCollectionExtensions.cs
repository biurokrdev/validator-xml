using D2ViewerEditor.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

public static class DocumentCompareServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentCompare(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DocumentCompareOptions>(configuration.GetSection(DocumentCompareOptions.SectionName));
        services.AddSingleton<IDocumentComparer, DocumentComparer>();

        return services;
    }
}
