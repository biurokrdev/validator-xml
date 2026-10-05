using Mass.AddressWindow.Domain.Inspection;
using Mass.AddressWindow.Infrastructure.Inspection;
using Mass.AddressWindow.Pdf;
using Microsoft.Extensions.DependencyInjection;

namespace Mass.AddressWindow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // Walidator jest bezstanowy, a domyślny profil to koperta C65 (jedno i dwa okienka).
        services.AddSingleton<IPdfAddressWindowValidator>(_ => new PdfAddressWindowValidator());
        services.AddSingleton<IAddressWindowInspector, PdfAddressWindowInspector>();
        return services;
    }
}
