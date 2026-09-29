using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Infrastructure.Persistence;
using D2ViewerEditor.Infrastructure.Persistence.Repositories;
using D2ViewerEditor.Infrastructure.Services;
using D2ViewerEditor.Infrastructure.Services.Delivery;
using D2ViewerEditor.Infrastructure.Services.PdfConversion;
using D2ViewerEditor.Infrastructure.Services.DocumentCompare;
using D2ViewerEditor.Infrastructure.Services.DocumentHealth;
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
        // Domyślne ustawienia dokumentu
        services.Configure<DocumentDefaultsOptions>(
            configuration.GetSection(DocumentDefaultsOptions.SectionName));

        // Document services
        services.AddSingleton<IBarcodeGenerator, BarcodeGeneratorService>();
        // Stateless, pure-managed (no native deps) → safe as a singleton.
        services.AddSingleton<IGraphicConversionService, GraphicConversionService>();
        // Stateless, pure-managed (OpenMcdf) → safe jako singleton. Dekrypcja DOCX + detekcja .doc.
        services.AddSingleton<IDocumentInputNormalizer, DocumentInputNormalizer>();
        services.AddScoped<IDocxToHtmlConverter, DocxToHtmlConverter>();
        services.AddScoped<IHtmlToDocxConverter, HtmlToDocxConverter>();
        services.AddScoped<IDigitalSignatureService, DigitalSignatureService>();
        // ATRAPA konwersji DOCX→PDF („Generuj PDF"). Podmiana na klienta HTTP prawdziwej usługi
        // zewnętrznej = zamiana tej jednej linii (implementacja stateless → singleton wystarczy).
        services.AddSingleton<IDocxToPdfConversionService, MockDocxToPdfConversionService>();

        services.AddStructureInspection(configuration);
        // Diagnostyka „Kondycja dokumentu" (uszkodzenia + blokery konwersji PDF) — współdzieli analizator OPC z walidatorem.
        services.AddDocumentHealth(configuration);
        // „Porównanie dokumentów" — literalny diff dwóch pakietów DOCX (korzysta z czytnika kontenera diagnostyki).
        services.AddDocumentCompare(configuration);

        // Database
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(connectionString))
        {
            services.AddDbContext<DocumentDbContext>(options =>
                options.UseNpgsql(connectionString));

            services.AddScoped<IDocumentRepository, DocumentRepository>();
            services.AddScoped<IDocumentDeliveryRepository, DocumentDeliveryRepository>();
        }

        // Finish-and-send: kolejka wysyłki + worker
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

        // Google Cloud Storage
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
                    // DEV/test: fake-gcs-server — bez autoryzacji, custom endpoint
                    var builder = new StorageClientBuilder
                    {
                        BaseUri = gcsOptions.ApiEndpoint.TrimEnd('/') + "/storage/v1/",
                        UnauthenticatedAccess = true
                    };
                    return builder.Build();
                }

                if (!string.IsNullOrEmpty(gcsOptions.CredentialPath))
                {
                    // Produkcja z plikiem service account
                    var credential = GoogleCredential.FromFile(gcsOptions.CredentialPath);
                    return StorageClient.Create(credential);
                }

                // Produkcja: Workload Identity / Application Default Credentials
                return StorageClient.Create();
            });

            services.AddScoped<IDocumentStorageService, GcsDocumentStorageService>();
        }

        return services;
    }
}
