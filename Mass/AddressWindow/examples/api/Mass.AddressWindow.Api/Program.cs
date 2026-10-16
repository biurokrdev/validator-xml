using System.Text.Json.Serialization;
using Mass.AddressWindow.Api.Conversion;
using Mass.AddressWindow.Api.Documents;
using Mass.AddressWindow.Api.External;
using Mass.AddressWindow.Api.Inspection;
using Mass.AddressWindow.Pdf;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<EnvelopeProfileOptions>(builder.Configuration.GetSection(EnvelopeProfileOptions.Section));
builder.Services.Configure<MockExternalOptions>(builder.Configuration.GetSection(MockExternalOptions.Section));

builder.Services.AddSingleton<IPdfAddressWindowValidator, PdfAddressWindowValidator>();
// One mock stands in for the external application: it produces letters and renders DOCX to PDF.
builder.Services.AddSingleton<MockExternalSystem>();
builder.Services.AddSingleton<IExternalLetterSource>(sp => sp.GetRequiredService<MockExternalSystem>());
builder.Services.AddSingleton<IDocxToPdfConverter>(sp => sp.GetRequiredService<MockExternalSystem>());
builder.Services.AddSingleton<LetterInspectionService>();

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Technical failures become ProblemDetails with a Polish title; the inspection result itself never comes this way.
app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ex switch
    {
        DocumentRejectedException => (StatusCodes.Status400BadRequest, "Nie można przyjąć pliku."),
        OrderRejectedException => (StatusCodes.Status400BadRequest, "Nieprawidłowe dane zamówienia."),
        UnreadableDocumentException => (StatusCodes.Status422UnprocessableEntity, "Dokumentu nie da się odczytać."),
        DocumentConversionException { ConverterUnavailable: true } => (StatusCodes.Status503ServiceUnavailable, "Konwersja DOCX do PDF jest niedostępna."),
        DocumentConversionException => (StatusCodes.Status502BadGateway, "Konwersja DOCX do PDF nie powiodła się."),
        ExternalSourceException => (StatusCodes.Status502BadGateway, "Aplikacja zewnętrzna nie zwróciła dokumentu."),
        BadHttpRequestException bad => (bad.StatusCode, "Nieprawidłowe żądanie."),
        _ => (StatusCodes.Status500InternalServerError, "Błąd serwera."),
    };

    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = status,
        Title = title,
        Detail = ex is null || status == StatusCodes.Status500InternalServerError ? null : ex.Message,
        Instance = ctx.Request.Path,
    });
}));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
