---
name: dotnet-api-design
description: >-
  Design and implement HTTP APIs in ASP.NET Core (minimal APIs or controllers): resource naming,
  status codes, ProblemDetails (RFC 9457) with a global IExceptionHandler, request validation
  (FluentValidation or DataAnnotations via endpoint filters), pagination/filtering/sorting,
  idempotent POST, API versioning (Asp.Versioning), OpenAPI (Swashbuckle or
  Microsoft.AspNetCore.OpenApi), JWT bearer auth and policies, CORS, rate limiting,
  CancellationToken plumbing and resilient typed HttpClients for outbound calls. Use this skill
  whenever the user adds or changes an endpoint, route, controller, "MapGet/MapPost", request or
  response DTO, says "REST", "API", "400 vs 422", "ProblemDetails", "validation", "paging",
  "swagger", "openapi", "versioning", "JWT", "CORS", "rate limit", "retry", "Polly", "HttpClient",
  or asks "how should this endpoint respond", even if they only ask to "add an endpoint".
---

# HTTP API design in ASP.NET Core

This skill produces APIs that clients can consume without reading the source: predictable URLs,
correct status codes, machine-readable errors, stable pagination and explicit versioning. The
expensive API mistakes are contract mistakes: a 200 with `{ "error": ... }` that clients must
special-case, a list endpoint that returns everything, a POST that creates duplicates on retry.
Fix those at design time; they are nearly impossible to change once clients depend on them.

## Workflow

1. **Name the resource and list its operations before writing code.** Write the URL table
   (`GET /orders`, `GET /orders/{id}`, `POST /orders`, `POST /orders/{id}/cancel`) with status codes
   and error cases. This table is the contract; review it with the consumer if there is one.
2. **Check existing conventions** in the project: route prefix, versioning scheme, response
   envelope, error format, auth policies. A new endpoint that deviates costs more than one that is
   slightly imperfect but consistent.
3. **Define request and response DTOs** as records separate from domain entities. Binding an entity
   directly exposes every property to mass assignment and couples the contract to the schema.
4. **Implement the endpoint thin**: bind, validate, call the application layer, map to
   `TypedResults`. Business rules live in `Application` (see `dotnet-project-structure`).
5. **Add validation, auth policy, OpenAPI metadata and the cancellation token** in the same commit
   as the endpoint, not "later".
6. **Verify**: run the integration tests from `dotnet-testing` for the happy path and each error
   path (400, 401/403, 404, 409), inspect the OpenAPI document for the new operation and confirm the
   error body is `application/problem+json`.

## Resources, URLs and methods

- Plural nouns, lowercase, hyphen-separated: `/api/v1/purchase-orders/{id}/lines`. No verbs in
  paths except for actions that are not CRUD: `POST /orders/{id}/cancel`.
- `GET` is safe and cacheable, `PUT` replaces, `PATCH` partially updates (JSON Merge Patch
  `application/merge-patch+json` is simpler than JSON Patch for most APIs), `DELETE` is idempotent
  (second call returns 204 or 404, never 500).
- Identifiers in URLs are opaque to clients; prefer `Guid` (UUIDv7) over sequential ints when ids
  are exposed, because enumeration of `/orders/1..n` is a real attack.
- Return the created resource's URL in `Location` on 201 and, usually, the body too.
- Use `[AsParameters]` records for query strings with more than two parameters.

## Status codes

| Situation | Code |
|---|---|
| Read succeeded | 200 |
| Created | 201 + `Location` |
| Accepted for async processing | 202 + status URL |
| Success without body (delete, cancel) | 204 |
| Malformed request (unparseable JSON, wrong type) | 400 |
| Semantically invalid (validation rules) | 400 (ASP.NET default) or 422; pick one per API |
| Not authenticated / authenticated but forbidden | 401 / 403 |
| Not found (also for resources the caller may not see) | 404 |
| Conflict (duplicate key, optimistic concurrency, state transition not allowed) | 409 |
| Rate limited | 429 + `Retry-After` |
| Unexpected error | 500 with no internals in the body |

Never return 200 with an error payload; clients and proxies key on the status code.

## ProblemDetails (RFC 9457) and the global exception handler

```csharp
// registration
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path;
    ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
// pipeline
app.UseExceptionHandler();
app.UseStatusCodePages();   // turns bare 404/405 into ProblemDetails too
```

```csharp
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var (status, title) = ex switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            ConflictException or DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflict"),
            OperationCanceledException when http.RequestAborted.IsCancellationRequested
                => (StatusCodes.Status499ClientClosedRequest, "Request cancelled"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);

        http.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = ex,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status < 500 ? ex.Message : null,   // never leak internals on 500
            },
        });
    }
}
```

- Use exceptions for exceptional cases crossing layers (`NotFoundException`, `ConflictException`
  thrown by application services). For expected outcomes on the endpoint itself, return
  `Results<Ok<OrderDto>, NotFound>` directly; it is cheaper and documents itself in OpenAPI.
- Give domain errors a stable `type` URI (`https://example.com/problems/insufficient-stock`) and
  put machine-readable data in `Extensions`; clients branch on `type`, not on `detail` text.
- Chain exception handlers for specific types if the switch grows; ASP.NET calls them in
  registration order until one returns `true`.
- .NET 9 adds `ExceptionHandlerOptions.StatusCodeSelector` for a plain exception-to-status map
  without a handler class; .NET 8 sets the status manually as above.

## Request validation

With FluentValidation (`FluentValidation.DependencyInjectionExtensions`), register validators once
(`services.AddValidatorsFromAssemblyContaining<CreateOrderValidator>()`) and run them in an
endpoint filter so handlers never see an invalid model:

```csharp
public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var model = ctx.Arguments.OfType<T>().FirstOrDefault();
        if (model is null) return TypedResults.BadRequest();

        var result = await validator.ValidateAsync(model, ctx.HttpContext.RequestAborted);
        return result.IsValid
            ? await next(ctx)
            : TypedResults.ValidationProblem(result.ToDictionary());   // 400 + application/problem+json with "errors"
    }
}

orders.MapPost("/", CreateOrder).AddEndpointFilter<ValidationFilter<CreateOrderRequest>>();
```

- Validate shape and syntax here (required, ranges, formats). Rules that need the database
  ("customer exists") belong in the application layer and surface as 404/409, because the filter
  cannot run in the same transaction.
- Controllers with `[ApiController]` validate DataAnnotations automatically and return
  `ValidationProblemDetails`. Minimal APIs on .NET 8/9 do not; .NET 10 adds
  `builder.Services.AddValidation()` which validates DataAnnotations on minimal API parameters.
- Keep `errors` keys in the same casing as the JSON (`camelCase`), so clients can map them to
  fields: configure `ValidatorOptions.Global.PropertyNameResolver` or post-process the dictionary.

## Pagination, filtering, sorting

```csharp
public sealed record PageRequest(int Page = 1, int PageSize = 50, string? Sort = null, string? Status = null);
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

orders.MapGet("/", async ([AsParameters] PageRequest q, IOrderQueries queries, CancellationToken ct) =>
{
    var pageSize = Math.Clamp(q.PageSize, 1, 200);
    return TypedResults.Ok(await queries.ListAsync(q with { PageSize = pageSize }, ct));
});
```

- Always cap `pageSize` and apply a default; an uncapped list endpoint is a denial-of-service
  endpoint.
- Sort syntax `?sort=-createdAt,customerName`; whitelist sortable fields and map them to columns
  explicitly. Never build an `OrderBy` from a raw string.
- Filters as named query parameters (`?status=open&createdFrom=2026-01-01`), not a mini query
  language, unless the API is explicitly a search API.
- For large or frequently changing tables, prefer keyset (cursor) pagination:
  `?cursor=<opaque>&limit=50`, returning `nextCursor`. Offset pagination skips or duplicates rows
  when data changes between pages and gets slower as the offset grows. Query side details are in
  `dotnet-ef-core-postgres`.
- `TotalCount` costs a second query; make it optional (`?includeTotal=true`) on big tables.

## Idempotency for POST

Retries happen (resilience handlers, mobile networks, users double-clicking). A POST that creates
a resource needs an `Idempotency-Key` header (IETF draft `draft-ietf-httpapi-idempotency-key-header`):

1. Client sends a UUID in `Idempotency-Key`.
2. Server looks the key up (scoped to the authenticated caller) in a store with a TTL (Postgres
   table or Redis). Unknown key: process, then store status + body + a hash of the request.
3. Same key, same request hash: replay the stored response without re-executing.
4. Same key, different hash: 422 with a ProblemDetails explaining the mismatch.
5. Same key, still processing: 409.

Implement it as an endpoint filter applied to the group; store and execute inside the same
transaction when the store is the database, otherwise a crash between "process" and "store" causes
a duplicate on retry. Natural keys (unique index on an external order number) are a cheaper
alternative when the domain has one: the duplicate surfaces as a 409 from the unique violation.

## Versioning

Use `Asp.Versioning.Http` (and `Asp.Versioning.Mvc.ApiExplorer` for OpenAPI grouping):

```csharp
builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.AssumeDefaultVersionWhenUnspecified = true;
    o.ReportApiVersions = true;                       // api-supported-versions header
    o.ApiVersionReader = new UrlSegmentApiVersionReader();
}).AddApiExplorer(o => { o.GroupNameFormat = "'v'VVV"; o.SubstituteApiVersionInUrl = true; });

var v1 = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).ReportApiVersions().Build();
var orders = app.MapGroup("/api/v{version:apiVersion}/orders").WithApiVersionSet(v1);
```

- Version in the URL segment is the most visible and the easiest to route and cache; header
  versioning is cleaner but confuses browsers and curl users. Pick one for the whole API.
- Bump the major version only for breaking changes (removed/renamed field, changed type or
  semantics, new required input). Adding optional fields is not breaking; document that clients
  must ignore unknown properties.
- Mark old versions `Deprecated` with the `Sunset` header and a date; remove after the window.

## OpenAPI

- .NET 8: `Swashbuckle.AspNetCore` (`AddSwaggerGen` / `UseSwagger` / `UseSwaggerUI`). .NET 9+:
  `Microsoft.AspNetCore.OpenApi` (`AddOpenApi()` / `MapOpenApi()`) generates the document; pair with
  `Scalar.AspNetCore` or Swagger UI for the browser page. .NET 10 emits OpenAPI 3.1 by default and
  can include XML doc comments.
- Use `TypedResults` with `Results<Ok<T>, NotFound, ValidationProblem>` so response types appear in
  the document without `Produces` attributes; add `.ProducesProblem(409)` for cases thrown from
  handlers. Give operations `.WithName("GetOrder")` (used by `CreatedAtRoute`) and `.WithSummary(...)`.
- Expose the UI only in Development or behind auth; the document itself is usually fine to publish.
- Treat the generated document as a contract artifact: diff it in CI (`dotnet swagger tofile` or
  the `Microsoft.Extensions.ApiDescription.Server` build-time generation) to catch unintended
  breaking changes.

## Auth, CORS, rate limiting, cancellation

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = builder.Configuration["Auth:Authority"];
        o.Audience = builder.Configuration["Auth:Audience"];
        o.TokenValidationParameters.ValidateIssuer = true;
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("orders:write", p => p.RequireAuthenticatedUser().RequireClaim("scope", "orders:write"))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()); // deny by default

orders.MapPost("/", CreateOrder).RequireAuthorization("orders:write");
app.MapHealthChecks("/health/live").AllowAnonymous();
```

- A fallback policy makes every endpoint require authentication unless it opts out with
  `AllowAnonymous()`; forgetting `[Authorize]` is one of the most common security findings.
- Authorize on policies named after capabilities (`orders:write`), not roles sprinkled in code.
- CORS: `AddCors(o => o.AddPolicy("web", p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()))`
  with explicit origins from configuration; `AllowAnyOrigin()` cannot be combined with credentials
  and is wrong for anything behind a login. `app.UseCors("web")` goes before `UseAuthentication`.
- Rate limiting (built-in, `Microsoft.AspNetCore.RateLimiting`): `AddRateLimiter` with a policy
  partitioned by user id or client IP, `RejectionStatusCode = 429`, and `OnRejected` setting
  `Retry-After`; apply with `.RequireRateLimiting("per-user")`. See
  `references/cross-cutting-setup.md` for a complete snippet.
- Cancellation: accept `CancellationToken ct` in every handler (bound to
  `HttpContext.RequestAborted`) and pass it to every async call down to EF Core and `HttpClient`.
  Cancelled work frees threads and database connections. After a write has committed, do not pass
  the request token to follow-up work that must complete (outbox publish, audit): use
  `CancellationToken.None` deliberately and comment why.

## Outbound calls: typed HttpClient with resilience

```csharp
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>((sp, client) =>
{
    var o = sp.GetRequiredService<IOptions<CatalogOptions>>().Value;
    client.BaseAddress = new Uri(o.BaseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan;          // let the resilience pipeline own timeouts
})
.AddStandardResilienceHandler(o =>                        // Microsoft.Extensions.Http.Resilience
{
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
    o.Retry.MaxRetryAttempts = 3;
});
```

- `AddHttpClient` + `IHttpClientFactory` handles connection pooling and DNS refresh; `new HttpClient()`
  per call exhausts sockets, a static one never sees DNS changes.
- The standard handler adds rate limiter, total timeout, retry with backoff, circuit breaker and
  attempt timeout in the right order. Retries only apply to idempotent methods and transient
  status codes by default; do not enable retry on POST unless the downstream supports an
  idempotency key.
- Typed clients are transient; do not capture them in singletons (capture `IHttpClientFactory`
  instead).
- Propagate the caller's `CancellationToken` and, where the downstream supports it, a correlation
  header (`Microsoft.AspNetCore.HeaderPropagation`).
- Deserialise with `ReadFromJsonAsync<T>(ct)` and handle non-success explicitly
  (`EnsureSuccessStatusCode` throws `HttpRequestException` with the status code in `.StatusCode`).

## Examples

**Example 1**
Input: "Add an endpoint to create an order."
Output: `POST /api/v1/orders` in `OrderEndpoints.MapOrderEndpoints`, `CreateOrderRequest` record
with a `CreateOrderValidator`, `ValidationFilter<CreateOrderRequest>` on the endpoint, handler
returning `Results<Created<OrderDto>, ValidationProblem, Conflict>` via `TypedResults.CreatedAtRoute`,
`.RequireAuthorization("orders:write")`, `.WithName("CreateOrder")`, `CancellationToken` passed to the
application service, and an integration test for 201, 400 and 409.

**Example 2**
Input: "Clients complain that errors come back as different shapes."
Output: `AddProblemDetails` + `GlobalExceptionHandler` + `UseStatusCodePages`, replace ad hoc
`Results.BadRequest(new { error })` with `TypedResults.ValidationProblem` / `TypedResults.Problem`,
and a test asserting `Content-Type: application/problem+json` for 400, 404, 409 and 500.

**Example 3**
Input: "Our list endpoint times out on large customers."
Output: Cap `pageSize`, add `sort` whitelist, switch to keyset pagination with an opaque cursor,
make `totalCount` opt-in, and verify the SQL uses the index as described in `dotnet-ef-core-postgres`.

## References

- `references/cross-cutting-setup.md` — read when wiring rate limiting, CORS, JWT with multiple
  schemes, header propagation or an idempotency filter and you want a complete, compilable snippet.
