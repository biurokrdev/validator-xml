using System.Text.Json.Serialization;
using Mass.AddressWindow.Application;
using Mass.AddressWindow.Domain.Letters;
using Mass.AddressWindow.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// ---------------------------------------------------------------- API
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(origins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Wyjątki domenowe -> kody HTTP. Reszta -> 500 przez ProblemDetails.
app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ex switch
    {
        InvalidLetterException => (StatusCodes.Status400BadRequest, "Nie można przyjąć pliku."),
        BadHttpRequestException bad => (bad.StatusCode, "Nieprawidłowe żądanie."),
        _ => (StatusCodes.Status500InternalServerError, "Błąd serwera.")
    };

    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = status,
        Title = title,
        Detail = status == StatusCodes.Status500InternalServerError ? null : ex?.Message,
        Instance = ctx.Request.Path
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

public partial class Program; // dla testów integracyjnych (WebApplicationFactory)
