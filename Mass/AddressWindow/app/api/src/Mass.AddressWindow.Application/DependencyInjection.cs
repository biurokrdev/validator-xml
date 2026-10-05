using Mass.AddressWindow.Application.Letters;
using Microsoft.Extensions.DependencyInjection;

namespace Mass.AddressWindow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<InspectLetterHandler>();
        return services;
    }
}
