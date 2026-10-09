---
name: dotnet-project-structure
description: >-
  Structure a .NET 8+ solution so it stays maintainable: layered/clean architecture (Domain,
  Application, Infrastructure, Api), minimal APIs vs controllers decision, DI registration via
  per-layer extension methods, Options pattern with validation, configuration and secrets,
  Directory.Build.props and central package management, nullable and warnings-as-errors, analyzers,
  logging and health checks. Use this skill whenever the user creates a new .NET service, solution or
  project, adds a project to a solution, asks "where should this class go", "how do I register this
  service", "how do I read settings", touches Program.cs, appsettings.json, *.csproj,
  Directory.Build.props, Directory.Packages.props, or says "set up the project", "scaffold",
  "bootstrap", "clean architecture", "layers", "DI", "options", "health check" or "logging setup".
---

# .NET solution structure, DI, configuration and cross-cutting setup

This skill produces a solution whose dependencies point inwards, whose startup fails fast on bad
configuration, and whose build enforces quality (nullable, analyzers, warnings as errors) from the
first commit. Most structural debt in .NET services comes from three places: the composition root
grows into a 400-line `Program.cs`, infrastructure types leak into domain code because nothing
stops them, and settings are read ad hoc with `configuration["Key"]` so a typo is discovered in
production. The workflow below prevents all three.

## Workflow

1. **Decide the size of the service before choosing a layout.** A small service (a handful of
   endpoints, one database) is fine as a single project with feature folders; four projects for
   three endpoints is ceremony. Use the layered layout below when the domain has real rules, more
   than one host (API + worker), or more than one team touching it.
2. **Create the solution skeleton with build props first.** `Directory.Build.props`,
   `Directory.Packages.props`, `global.json` and `.editorconfig` set the compiler and analyzer
   settings for every project. Adding them later means fixing hundreds of warnings at once.
3. **Add projects in dependency order**: Domain, Application, Infrastructure, Api, then tests.
   Use project references, never a reference from Domain to anything that is not the BCL.
4. **Add one `Add<Layer>()` extension method per layer** and call them from `Program.cs`. The
   composition root should read like a table of contents.
5. **Bind every settings section to an options class with validation and `ValidateOnStart()`.**
   A missing connection string must stop the host at startup, not on the first request.
6. **Wire logging, health checks and the exception handler** before writing the first endpoint.
   They are needed on day one in any container platform and are cheap to add to an empty host.
7. **Verify**: `dotnet build -warnaserror` is clean, `dotnet format --verify-no-changes` passes,
   the app starts with an intentionally broken setting and fails with a clear `OptionsValidationException`,
   and `GET /health/ready` returns 200 with the database reachable.

## Recommended folder tree

```
MyService/
├── MyService.sln
├── global.json                      # pin the SDK major: { "sdk": { "version": "8.0.0", "rollForward": "latestFeature" } }
├── Directory.Build.props            # compiler + analyzer settings for all projects
├── Directory.Packages.props         # central package versions
├── .editorconfig                    # code style + per-rule analyzer severities
├── src/
│   ├── MyService.Domain/            # entities, value objects, domain events, domain exceptions; no NuGet except abstractions
│   ├── MyService.Application/       # use cases (commands/queries/handlers), ports (interfaces), DTOs, validators
│   │   ├── Orders/                  # feature folder: CreateOrder.cs, GetOrder.cs, OrderDto.cs
│   │   └── DependencyInjection.cs   # AddApplication()
│   ├── MyService.Infrastructure/    # EF Core DbContext + configurations + migrations, HTTP clients, messaging, clock
│   │   ├── Persistence/
│   │   └── DependencyInjection.cs   # AddInfrastructure(configuration)
│   └── MyService.Api/               # Program.cs, endpoints, middleware, Options classes for the host
│       ├── Endpoints/Orders/        # OrderEndpoints.cs (MapOrderEndpoints)
│       ├── appsettings.json
│       └── DependencyInjection.cs   # AddApi(configuration): auth, OpenAPI, ProblemDetails, CORS
└── tests/
    ├── Directory.Build.props        # IsPackable=false, shared test packages
    ├── MyService.Domain.Tests/
    ├── MyService.Application.Tests/
    └── MyService.Api.IntegrationTests/
```

Dependency rule: `Domain <- Application <- Infrastructure`, and `Api` references `Application` and
`Infrastructure` only because it is the composition root. Application must not reference
Infrastructure; it defines interfaces (`IOrderRepository`, `IEmailSender`, `TimeProvider`) that
Infrastructure implements. Enforce this with a test using `NetArchTest.Rules` or `ArchUnitNET` so
a stray `using Microsoft.EntityFrameworkCore` in Domain fails CI rather than a code review.

For a small service collapse the tree into one project with `Domain/`, `Features/<Feature>/`,
`Infrastructure/` folders (vertical slices). Keep the same dependency direction inside the folders.

## Build settings: Directory.Build.props and central package management

```xml
<!-- Directory.Build.props (solution root) -->
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>
</Project>
```

```xml
<!-- Directory.Packages.props (solution root) -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="8.0.11" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.11" />
    <PackageVersion Include="FluentValidation.DependencyInjectionExtensions" Version="11.11.0" />
    <PackageVersion Include="xunit" Version="2.9.2" />
  </ItemGroup>
</Project>
```

Project files then use `<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />`
without a version. One place to bump versions, no drift between projects, and Dependabot/Renovate
understand the file. Add `tests/Directory.Build.props` that imports the parent
(`<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />`)
and sets `<IsPackable>false</IsPackable>` plus the shared test package references.

- Warnings as errors from the first commit: turning it on later costs a day of cleanup. Suppress a
  specific rule in `.editorconfig` (`dotnet_diagnostic.CA1848.severity = suggestion`) with a
  comment why, rather than lowering `AnalysisLevel`.
- Nullable reference types are a design tool, not noise: a `string?` in a DTO documents that the
  field is optional. Avoid `!` except at proven boundaries (deserialisation, test setup).
- .NET 9/10: `TargetFramework` becomes `net9.0`/`net10.0`; everything above is unchanged. Bump
  `AnalysisLevel` deliberately when upgrading, since `latest-recommended` enables new rules with
  each SDK and may break a warnings-as-errors build.

## Minimal APIs vs controllers

| Choose minimal APIs when | Choose controllers when |
|---|---|
| New service on .NET 8+, JSON-only | Existing controller codebase; consistency wins |
| You want `TypedResults` and compile-time response types in OpenAPI | You rely on filters/attributes ecosystem (`[ApiController]` model validation, action filters, OData) |
| Endpoints are grouped by feature with `MapGroup` | Content negotiation beyond JSON (XML, custom formatters) |
| Native AOT or fast startup matters | Many conventions shared via base controllers |

Both run on the same pipeline and can coexist. With minimal APIs, keep one static class per
feature exposing `MapXxxEndpoints(this IEndpointRouteBuilder)` and keep the handler bodies thin:
they parse the request, call an application service, and map the result to `TypedResults`.
See `dotnet-api-design` for conventions on status codes, ProblemDetails and validation.

## Dependency injection: one extension method per layer

```csharp
// src/MyService.Infrastructure/DependencyInjection.cs
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContextPool<AppDbContext>(o => o.UseNpgsql(configuration.GetConnectionString("Default")));
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient<ICatalogClient, CatalogClient>().AddStandardResilienceHandler();
        return services;
    }
}
```

```csharp
// src/MyService.Api/Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddApi(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });
app.MapOrderEndpoints();

app.Run();

public partial class Program;   // lets WebApplicationFactory<Program> see the entry point in tests
```

- Lifetimes: `Scoped` for anything holding a `DbContext` or per-request state, `Singleton` for
  stateless or thread-safe services (clients, caches, `TimeProvider`), `Transient` rarely. A
  singleton that captures a scoped service is a captive dependency; the host validates this in
  Development (`ValidateScopes`) but only if you run it there, so enable `ValidateOnBuild` too:
  `builder.Host.UseDefaultServiceProvider(o => { o.ValidateScopes = true; o.ValidateOnBuild = true; })`.
- Register handlers by convention (`Scrutor` `services.Scan(...)`) only when the count justifies it;
  explicit registrations are easier to review.
- Keep `IServiceProvider` out of application code. Resolving services manually hides dependencies
  and breaks the scope validation above.

## Options pattern with validation

```csharp
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required] public required string Host { get; init; }
    [Range(1, 65535)] public int Port { get; init; } = 587;
    [Required, EmailAddress] public required string From { get; init; }
}

// in AddInfrastructure / AddApi
services.AddOptions<SmtpOptions>()
    .BindConfiguration(SmtpOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

- `ValidateOnStart()` turns a bad setting into a startup failure with a message naming the
  property. Without it, the first request that resolves `IOptions<SmtpOptions>` throws.
- For rules that annotations cannot express, add `.Validate(o => o.Port != 25 || o.Host != "localhost", "reason")`
  or implement `IValidateOptions<T>`. On .NET 8+, `[OptionsValidator] public partial class SmtpOptionsValidator : IValidateOptions<SmtpOptions>`
  generates the validation at compile time (no reflection, works with AOT); register it with
  `services.AddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>()`.
- Inject `IOptions<T>` by default (singleton, read once). Use `IOptionsMonitor<T>` when the value
  may change at runtime (feature flags, reloadable files) and `IOptionsSnapshot<T>` only in scoped
  services that need a per-request consistent view. Do not inject `IConfiguration` into services;
  it hides what the service depends on.
- Name options after the section (`SmtpOptions` for `"Smtp"`), keep them immutable (`init`), and
  put them next to the code that consumes them (host options in Api, client options in
  Infrastructure).

## Configuration sources and secrets

Default source order (later wins): `appsettings.json`, `appsettings.{Environment}.json`, user
secrets (Development only), environment variables, command line. Nested keys use `:` in JSON and
`__` in environment variables (`ConnectionStrings__Default`).

- Commit `appsettings.json` with non-secret defaults and `appsettings.Development.json` with local
  values that are not secrets (local Postgres on `localhost` is fine; a real API key is not).
- Development secrets: `dotnet user-secrets init` then `dotnet user-secrets set "Smtp:Password" "..."`.
  They live outside the repo and are loaded automatically in Development.
- Production secrets: environment variables injected by the platform, or a secret manager provider
  (`Azure.Extensions.AspNetCore.Configuration.Secrets`, Google Secret Manager via mounted files or
  the `Google.Cloud.SecretManager` configuration provider). Mounted files map well with
  `AddKeyPerFile("/secrets")`.
- Never log the full configuration; `IConfigurationRoot.GetDebugView()` is for local debugging only.
- Keep environment-specific behaviour in configuration, not `if (env.IsProduction())` branches
  scattered through the code. The one legitimate branch is enabling developer tooling
  (Swagger UI, detailed errors) in Development.

## Logging

- Use `ILogger<T>` with structured message templates (`"Order {OrderId} created"`), never string
  interpolation; interpolation defeats filtering and costs an allocation even when the level is off.
- Prefer the `LoggerMessage` source generator for hot paths and for messages logged per request:
  `[LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} created")] static partial void LogOrderCreated(ILogger logger, Guid orderId);`
  CA1848 (enabled by `latest-recommended`) nudges towards this.
- In containers emit JSON to stdout: `builder.Logging.AddJsonConsole()` or Serilog
  (`Serilog.AspNetCore`, `builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration))`).
  Cloud Run, Kubernetes and most log pipelines parse JSON lines and map `severity`/`level`.
- Set levels in configuration (`"Logging:LogLevel:Microsoft.AspNetCore": "Warning"`), and add
  `"Microsoft.EntityFrameworkCore.Database.Command": "Information"` only in Development.
- Add OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`)
  when the service runs alongside others; traces answer "which call was slow" where logs cannot.
- Do not log request bodies, tokens or personal data by default. `UseHttpLogging()` is opt-in and
  needs field filtering.

## Health checks

```csharp
services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>(tags: ["ready"])          // Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore
    .AddUrlGroup(new Uri(catalogOptions.BaseUrl + "/health"), name: "catalog", tags: ["ready"]); // AspNetCore.HealthChecks.Uris
```

- Expose two endpoints: `/health/live` (process is up, no dependencies) and `/health/ready`
  (dependencies reachable). Orchestrators restart on liveness failure and stop routing on readiness
  failure; conflating them causes restart loops when the database is briefly unavailable.
- Keep the output minimal on public endpoints; the detailed `ResponseWriter` from
  `AspNetCore.HealthChecks.UI.Client` belongs behind auth or an internal port.
- Health checks run on every probe; keep them cheap (`SELECT 1`, not a full table scan) and with a
  timeout.

## Examples

**Example 1**
Input: "Create a new .NET 8 service for managing invoices with Postgres."
Output: The folder tree above with `Invoices` feature folders, `Directory.Build.props` and
`Directory.Packages.props`, `AddApplication/AddInfrastructure/AddApi` extension methods, a
`DatabaseOptions` class bound with `ValidateOnStart()`, JSON console logging, `/health/live` and
`/health/ready`, and `public partial class Program;` at the end of `Program.cs` for the integration
tests described in `dotnet-testing`. Persistence follows `dotnet-ef-core-postgres`.

**Example 2**
Input: "The SMTP password is read with `configuration["Smtp:Password"]` in five places."
Output: One `SmtpOptions` class with `[Required]` members, bound once with
`AddOptions<SmtpOptions>().BindConfiguration("Smtp").ValidateDataAnnotations().ValidateOnStart()`,
`IOptions<SmtpOptions>` injected into the mail sender, the value moved to user secrets locally and
to an environment variable `Smtp__Password` in deployment.

**Example 3**
Input: "Should this new endpoint be a controller or a minimal API?"
Output: Apply the decision table: if the project already has controllers, add a controller for
consistency; otherwise add `MapInvoiceEndpoints` using `MapGroup("/api/v1/invoices")` and
`TypedResults`, and register it in `Program.cs` next to the other `Map*Endpoints` calls.
