using System.Reflection;
using FluentValidation;
using D2ViewerEditor.Application.Common.Behaviours;
using D2ViewerEditor.Application.Common.Security;
using D2ViewerEditor.Application.Features.StructureInspection.Common;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace D2ViewerEditor.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
        });

        services.AddValidatorsFromAssembly(assembly);

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehaviour<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));

        services.AddScoped<IDocumentAccessGuard, DocumentAccessGuard>();

        services.AddOptions<StructureInspectionRetentionOptions>();

        services.AddOptions<UploadSecurityOptions>();
        services.AddOptions<ReturnUrlSecurityOptions>();
        services.AddSingleton<IFileScanner, NoOpFileScanner>();
        services.AddSingleton<IFileUploadSecurityService, FileUploadSecurityService>();
        services.AddSingleton<IReturnUrlValidator, ReturnUrlValidator>();

        return services;
    }
}
