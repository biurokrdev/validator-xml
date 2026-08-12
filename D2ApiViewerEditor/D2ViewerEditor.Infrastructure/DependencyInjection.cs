using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Infrastructure.Persistence;
using D2ViewerEditor.Infrastructure.Persistence.Repositories;
using D2ViewerEditor.Infrastructure.Services;
using D2ViewerEditor.Infrastructure.Services.Delivery;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DocumentDefaultsOptions>(
            configuration.GetSection(DocumentDefaultsOptions.SectionName));

        services.AddSingleton<IBarcodeGenerator, BarcodeGeneratorService>();
        services.AddSingleton<IGraphicConversionService, GraphicConversionService>();
        services.AddSingleton<IDocumentInputNormalizer, DocumentInputNormalizer>();
        services.AddScoped<IDocxToHtmlConverter, DocxToHtmlConverter>();
        services.AddScoped<IHtmlToDocxConverter, HtmlToDocxConverter>();
        services.AddScoped<IDigitalSignatureService, DigitalSignatureService>();

        services.AddStructureInspection(configuration);

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(connectionString))
        {
            services.AddDbContext<DocumentDbContext>(options =>
                options.UseNpgsql(connectionString));

            services.AddScoped<IDocumentRepository, DocumentRepository>();
            services.AddScoped<IDocumentDeliveryRepository, DocumentDeliveryRepository>();
        }

        var deliveryOptions = new DeliveryWorkerOptions();
        configuration.GetSection(DeliveryWorkerOptions.SectionName).Bind(deliveryOptions);
        services.Configure<DeliveryWorkerOptions>(
            configuration.GetSection(DeliveryWorkerOptions.SectionName));

        services.AddSingleton<IBackoffStrategy, ExponentialJitterBackoff>();
        services.AddScoped<DeliveryAttemptRunner>();
        services.AddHttpClient<IDeliverySender, HttpDeliverySender>(client =>
        {
            client.Timeout = deliveryOptions.HttpTimeout;
        });

        if (deliveryOptions.Enabled)
        {
            services.AddHostedService<DocumentDeliveryWorker>();
        }

        var gcsSection = configuration.GetSection(GcsStorageOptions.SectionName);
        var gcsOptions = new GcsStorageOptions();
        gcsSection.Bind(gcsOptions);
        services.Configure<GcsStorageOptions>(o =>
        {
            o.BucketName = gcsOptions.BucketName;
            o.ApiEndpoint = gcsOptions.ApiEndpoint;
            o.CredentialPath = gcsOptions.CredentialPath;
        });

        if (!string.IsNullOrEmpty(gcsOptions.BucketName))
        {
            services.AddSingleton(sp =>
            {
                if (!string.IsNullOrEmpty(gcsOptions.ApiEndpoint))
                {
                    var builder = new StorageClientBuilder
                    {
                        BaseUri = gcsOptions.ApiEndpoint.TrimEnd('/') + "/storage/v1/",
                        UnauthenticatedAccess = true
                    };
                    return builder.Build();
                }

                if (!string.IsNullOrEmpty(gcsOptions.CredentialPath))
                {
                    var credential = GoogleCredential.FromFile(gcsOptions.CredentialPath);
                    return StorageClient.Create(credential);
                }

                return StorageClient.Create();
            });

            services.AddScoped<IDocumentStorageService, GcsDocumentStorageService>();
        }

        return services;
    }
}
