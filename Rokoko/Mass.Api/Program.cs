using System.Text.Json.Serialization;
using Mass.Api.Contracts;
using Mass.Domain;
using Mass.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- baza
var provider = builder.Configuration["Database:Provider"] ?? "Postgres";

builder.Services.AddDbContext<MassDbContext>(o =>
{
    if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
    {
        // Tryb dev/e2e: bez PostgreSQL, dane żyją do restartu procesu.
        o.UseInMemoryDatabase("mass")
         .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
    }
    else
    {
        var cs = builder.Configuration.GetConnectionString("Mass")
                 ?? throw new InvalidOperationException("Brak ConnectionStrings:Mass.");
        o.UseNpgsql(cs);
    }
});

builder.Services.AddScoped<IRegisteredNumberPoolRepository, RegisteredNumberPoolRepository>();

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
        RegisteredNumberNotFoundException => (StatusCodes.Status404NotFound, "Numer nie istnieje w puli."),
        RegisteredNumberPoolExhaustedException => (StatusCodes.Status409Conflict, "Pula numerów wyczerpana."),
        RegisteredNumberStateException => (StatusCodes.Status409Conflict, "Przejście stanu niedozwolone."),
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Numer został w międzyczasie zmieniony przez kogoś innego."),
        ArgumentException => (StatusCodes.Status400BadRequest, "Nieprawidłowe dane wejściowe."),
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
app.MapGet("/health", () => Results.Ok(new { status = "ok", database = provider }));

app.Run();

public partial class Program; // dla testów integracyjnych (WebApplicationFactory)
