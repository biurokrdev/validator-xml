---
name: dotnet-testing
description: >-
  Write and organise tests for .NET 8+ services: xUnit (v2, with notes on v3), FluentAssertions or
  Shouldly, NSubstitute, test naming and Arrange-Act-Assert, the unit vs integration split,
  WebApplicationFactory<Program> with overridden services, Testcontainers Postgres in a shared
  collection fixture, Respawn for database reset, snapshot tests with Verify, deterministic time
  with TimeProvider, coverage with coverlet and running in CI with `dotnet test --logger trx`. Use
  this skill whenever the user asks to add, fix or run tests, says "unit test", "integration test",
  "xunit", "mock", "NSubstitute", "fixture", "Testcontainers", "WebApplicationFactory", "Respawn",
  "Verify", "snapshot", "coverage", "flaky test", "test the endpoint", "test the repository", or
  touches a *Tests.csproj, a file ending in Tests.cs, or a CI test step.
---

# Testing .NET services

This skill produces a test suite that catches regressions where they happen: domain rules in fast
unit tests, persistence and HTTP behaviour in integration tests against the real stack (Kestrel
in-process, real Postgres in a container). The common failure mode is the opposite: hundreds of
mock-heavy tests that pass while the SQL does not translate, the JSON casing is wrong, or the
endpoint is missing `[Authorize]`. The workflow pushes every test to the cheapest level that can
actually fail for the bug it guards against.

## Workflow

1. **Decide what can break and at which level.** Pure logic (calculations, state transitions,
   validators) is a unit test with no I/O. Anything touching EF Core, routing, serialisation,
   auth, or configuration is an integration test; mocking those proves nothing about production.
2. **Reuse the existing fixtures.** Look in `tests/*/Fixtures` or `*Factory.cs` before writing a
   new container or factory; a second Postgres container per test class doubles CI time.
3. **Write the test name as the specification**: `Method_Scenario_ExpectedOutcome`
   (`Cancel_WhenAlreadyShipped_ThrowsConflict`). A reviewer should understand the rule from the
   test list alone.
4. **Arrange, Act, Assert with one behaviour per test.** Several asserts on one result are fine;
   several acts are two tests.
5. **Isolate state**: reset the database between tests (Respawn), use a fresh `FakeTimeProvider`
   per test, never depend on test order.
6. **Verify**: run `dotnet test` twice (second run must pass too; order-dependent tests fail
   here), check the new test fails when the production code is reverted, and look at the coverage
   report for the changed files.

## Project layout and packages

```
tests/
├── Directory.Build.props                 # IsPackable=false, common test packages
├── MyService.Domain.Tests/               # unit: no I/O, milliseconds
├── MyService.Application.Tests/          # unit: handlers with NSubstitute ports
└── MyService.Api.IntegrationTests/       # WebApplicationFactory + Testcontainers
    ├── Fixtures/ApiFixture.cs
    ├── Fixtures/ApiCollection.cs
    └── Orders/OrdersEndpointsTests.cs
```

Shared test packages (`tests/Directory.Build.props`): `Microsoft.NET.Test.Sdk`, `xunit`,
`xunit.runner.visualstudio`, `coverlet.collector`, `NSubstitute`, `NSubstitute.Analyzers.CSharp`,
and one assertion library. Integration tests add `Microsoft.AspNetCore.Mvc.Testing`,
`Testcontainers.PostgreSql`, `Respawn`, `Microsoft.Extensions.TimeProvider.Testing`.

Assertion library: FluentAssertions 8+ requires a paid licence for commercial use (version 7.x
stays Apache-2.0); Shouldly and the `AwesomeAssertions` fork are free. Pick one per solution so
assertion style is uniform. Examples below use FluentAssertions syntax; Shouldly reads
`result.ShouldBe(expected)`.

## Unit tests: NSubstitute and AAA

```csharp
public sealed class CancelOrderHandlerTests
{
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Handle_WhenOrderIsShipped_ThrowsConflict()
    {
        // Arrange
        var order = OrderBuilder.Shipped();
        _orders.GetAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        var sut = new CancelOrderHandler(_orders, _time);

        // Act
        var act = () => sut.HandleAsync(new CancelOrder(order.Id), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ConflictException>().WithMessage("*shipped*");
        await _orders.DidNotReceive().SaveAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}
```

- Substitute interfaces you own (ports); do not substitute `DbContext`, `HttpClient` or
  `ILogger<T>` internals. For `ILogger<T>`, use `NullLogger<T>.Instance` or a `FakeLogger` from
  `Microsoft.Extensions.Diagnostics.Testing` when the log output is the behaviour under test.
- `NSubstitute.Analyzers.CSharp` flags common misuse (configuring non-virtual members, `Received`
  on a method call that is not a substitute).
- Builders/object mothers (`OrderBuilder.Shipped()`) keep arrange blocks short and make the
  relevant detail stand out. Avoid shared mutable fixtures between unit tests.
- Use `[Theory]` with `[InlineData]` or `[MemberData]` for rule tables; name the theory for the
  rule, not the data.

## Integration tests: WebApplicationFactory + Testcontainers + Respawn

One fixture owns the container, the host and the reset logic; a collection shares it across all
test classes so the container starts once per test run.

```csharp
// Fixtures/ApiFixture.cs
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private Respawner? _respawner;

    public HttpClient Client { get; private set; } = null!;
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public FakeEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);

            services.AddAuthentication(TestAuthHandler.Scheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
        });
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();                       // must run before the host reads the connection string
        Client = CreateClient();                            // builds the host; ConfigureWebHost runs here

        using (var scope = Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        }

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table("__EFMigrationsHistory")],
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await _respawner!.ResetAsync(conn);
        Emails.Clear();
    }

    async Task IAsyncLifetime.DisposeAsync()               // explicit: WebApplicationFactory already has DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

// Fixtures/ApiCollection.cs
[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
```

```csharp
// Orders/OrdersEndpointsTests.cs
[Collection(nameof(ApiCollection))]
public sealed class OrdersEndpointsTests(ApiFixture api) : IAsyncLifetime
{
    public Task InitializeAsync() => api.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateOrder_WithValidBody_Returns201AndLocation()
    {
        // Arrange
        var request = new CreateOrderRequest(CustomerId: Guid.NewGuid(), Lines: [new("SKU-1", Quantity: 2)]);

        // Act
        var response = await api.Client.PostAsJsonAsync("/api/v1/orders", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var body = await response.Content.ReadFromJsonAsync<OrderDto>();
        body!.CreatedAt.Should().Be(api.Time.GetUtcNow());
    }

    [Fact]
    public async Task CreateOrder_WithEmptyLines_Returns400ProblemDetails()
    {
        var response = await api.Client.PostAsJsonAsync("/api/v1/orders", new CreateOrderRequest(Guid.NewGuid(), []));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        problem!.Errors.Should().ContainKey("lines");
    }
}
```

- `Program` must be visible to the test project: `public partial class Program;` at the end of
  `Program.cs` (see `dotnet-project-structure`), or `InternalsVisibleTo`.
- `UseSetting` injects the container's connection string before the host builds, so the production
  `AddInfrastructure` registration is exercised unchanged. The alternative (`RemoveAll<DbContextOptions<AppDbContext>>`
  and re-adding) silently diverges from production options such as naming conventions.
- `ConfigureTestServices` runs after the app's own registrations, so `RemoveAll` + `AddSingleton`
  reliably replaces a service. Replace only what has side effects outside the test (email, payments,
  external HTTP); keep the real database.
- Auth: a `TestAuthHandler` (`AuthenticationHandler<AuthenticationSchemeOptions>` returning a
  principal from a header like `X-Test-User`) lets tests exercise policies without issuing JWTs.
  Keep at least one test that verifies an anonymous call gets 401, otherwise the fallback policy
  can be removed unnoticed.
- Respawn deletes rows in FK order and keeps the schema, which is far cheaper than recreating the
  database per test. Tests in one collection run sequentially, so resets do not race. For parallel
  classes, create one database per class on the same container instead.
- Docker must be available where tests run (local Docker Desktop, GitHub-hosted runners have it,
  most self-hosted runners need it installed). Testcontainers removes containers via Ryuk after the
  run.

## Snapshot tests with Verify

Use `Verify.Xunit` (or `Verify.XunitV3`) for large structured outputs: OpenAPI documents, generated
SQL, rendered emails, complex DTOs.

```csharp
[Fact]
public async Task OpenApiDocument_MatchesSnapshot()
{
    var json = await api.Client.GetStringAsync("/openapi/v1.json");   // Verify.Http also accepts HttpResponseMessage directly
    await Verify(json).ScrubInlineGuids().UseDirectory("Snapshots");
}
```

Commit `*.verified.*` files, gitignore `*.received.*`. A diff in a snapshot is a contract change
and belongs in the PR review; use `VerifierSettings.ScrubMember("createdAt")` or
`ScrubInlineDateTimes` for values that change per run.

## Time: TimeProvider

Inject `TimeProvider` (BCL since .NET 8) everywhere instead of `DateTime.UtcNow`, register
`TimeProvider.System` in production and `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`)
in tests. `Time.Advance(TimeSpan.FromDays(31))` makes "subscription expires after 30 days" a
deterministic test, and `Task.Delay(delay, timeProvider)` plus `Advance` tests timeouts without
sleeping. For `PeriodicTimer`, pass the provider in the constructor. Avoid `Thread.Sleep` and
`Task.Delay` with wall-clock waits in tests; they are the leading cause of flaky CI.

## Coverage and CI

```bash
dotnet test --configuration Release --no-build \
  --logger "trx;LogFileName=results.trx" --results-directory ./TestResults \
  --collect:"XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura
```

- `coverlet.collector` produces Cobertura XML per test project; merge and render with
  `dotnet tool run reportgenerator -reports:"**/coverage.cobertura.xml" -targetdir:coverage`.
  Publish the TRX files with the CI's test reporter (GitHub `dorny/test-reporter`, Azure DevOps
  `PublishTestResults`).
- Enforce a threshold only on new code (reportgenerator history or SonarQube), not a global 80%
  that encourages testing getters.
- Separate unit and integration runs (`--filter "Category!=Integration"` with
  `[Trait("Category", "Integration")]`) so unit tests give feedback in seconds and integration
  tests run once Docker is available.
- Exclude generated code and migrations: `[ExcludeFromCodeCoverage]` on the migrations assembly or
  `<ExcludeByFile>**/Migrations/*.cs</ExcludeByFile>` in `coverlet.runsettings`.

## xUnit v3 notes

`xunit.v3` (package replaces `xunit`; keep `xunit.runner.visualstudio` 3.x) changes a few things:
test projects compile to executables, `IAsyncLifetime` now extends `IAsyncDisposable`
(`ValueTask InitializeAsync()` / `ValueTask DisposeAsync()`), so the explicit `IAsyncLifetime.DisposeAsync`
in the fixture above becomes an `override` of `WebApplicationFactory.DisposeAsync`;
`TestContext.Current.CancellationToken` gives a token to pass into the code under test; tests can
return `ValueTask`; and the project runs on Microsoft.Testing.Platform as well as VSTest. On .NET
10 SDK, `dotnet test` can target Microsoft.Testing.Platform directly via `dotnet.config`
(`[dotnet.test.runner] name = "Microsoft.Testing.Platform"`), which changes the CLI flags
(`--report-trx` instead of `--logger trx`). Migrate the whole solution at once; mixing v2 and v3 in
one solution works but duplicates shared test utilities.

## Examples

**Example 1**
Input: "Add tests for the new `POST /orders` endpoint."
Output: `OrdersEndpointsTests` in the `ApiCollection`, with tests for 201 + `Location`, 400
validation problem with `errors.lines`, 401 without the test auth header, 409 on duplicate order
number, each resetting the database in `InitializeAsync`, plus a unit test for the validator's rules
via `[Theory]`.

**Example 2**
Input: "This test is flaky: it waits 2 seconds for the retry."
Output: Replace `Task.Delay(2000)` with a `FakeTimeProvider` injected into the retry logic,
`Advance(TimeSpan.FromSeconds(2))` in the test, and an assertion on `Received(2)` calls to the
substituted client.

**Example 3**
Input: "Set up integration tests for the repository layer."
Output: The `ApiFixture` above (or a leaner `PostgresFixture` without the web host when only
`DbContext` is needed), migrations applied in `InitializeAsync`, Respawn reset per test, and tests
asserting behaviour through the real Npgsql provider as `dotnet-ef-core-postgres` requires.
