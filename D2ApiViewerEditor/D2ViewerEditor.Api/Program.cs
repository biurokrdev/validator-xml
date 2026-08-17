using D2ViewerEditor.Api.Extensions;
using D2ViewerEditor.Api.Security;
using D2ViewerEditor.Application;
using D2ViewerEditor.Application.Common.Security;
using D2ViewerEditor.Infrastructure;
using Microsoft.OpenApi.Models;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

var env = builder.Environment.EnvironmentName;
builder.Configuration.AddJsonFile($"appsettings.{env}.secrets.json", optional: true, reloadOnChange: false);

var maxRequestBodyBytes = builder.Configuration.GetValue<long?>("Kestrel:MaxRequestBodySizeBytes") ?? 150_000_000;
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = maxRequestBodyBytes;
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
});

builder.AddGcpStructuredLogging();

builder.AddEntraSecretFromGcp();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.Configure<UploadSecurityOptions>(
    builder.Configuration.GetSection(UploadSecurityOptions.SectionName));
builder.Services.Configure<ReturnUrlSecurityOptions>(
    builder.Configuration.GetSection(ReturnUrlSecurityOptions.SectionName));

var devAuthBypass = builder.Configuration.GetValue<bool>("Auth:DevBypass")
                    && !builder.Environment.IsProduction();

if (devAuthBypass)
{
    builder.Services.AddDevBypassAuthentication();
}
else
{
    builder.Services.AddEntraIdAuthentication(builder.Configuration, builder.Environment);
}

builder.Services.AddScoped<ResourcesProvider>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "D2 Viewer Editor API",
        Version = "v1",
        Description = """
            REST API do zarządzania dokumentami DOCX — otwieranie, edycja, zapis wersji,
            podpisywanie cyfrowe oraz pobieranie historii wersji.
            """,
        Contact = new OpenApiContact
        {
            Name = "D2 Team",
            Email = "d2team@example.com"
        }
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    options.IncludeXmlComments(xmlPath);

    options.TagActionsBy(api =>
    {
        api.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
        return [api.GroupName ?? controller ?? "Default"];
    });
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:4200"];
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularApp",
        policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
});

var app = builder.Build();

app.Use((context, next) =>
{
    context.Request.Scheme = "https";
    return next(context);
});

app.UseRequestObservability();
app.UseExceptionHandlingMiddleware();

app.UseCors("AllowAngularApp");

app.UseAuthentication();
app.UseAuthorization();

var swaggerEnabled = app.Configuration.GetValue<bool>("Swagger:Enabled");
if (swaggerEnabled)
{
    app.UseSwagger();

    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "D2 Viewer Editor API v1");
        c.DocumentTitle = "D2 Viewer Editor API";
        c.DisplayRequestDuration();
        c.EnableDeepLinking();
        c.EnableFilter();
    });
}

app.MapControllers();

app.Run();

public partial class Program;
