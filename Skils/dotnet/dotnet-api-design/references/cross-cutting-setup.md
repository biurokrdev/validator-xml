# Cross-cutting setup snippets for ASP.NET Core APIs

Read this when you need a complete, compilable snippet for rate limiting, CORS, JWT with several
schemes, header propagation or an idempotency endpoint filter. Everything targets .NET 8; notes
mark differences for .NET 9/10.

## Rate limiting (built-in)

```csharp
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, ct) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            ctx.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
        return ValueTask.CompletedTask;
    };

    // Per authenticated user, falling back to client IP for anonymous callers.
    o.AddPolicy("per-user", http =>
    {
        var key = http.User.Identity?.IsAuthenticated == true
            ? http.User.FindFirst("sub")?.Value ?? "anonymous"
            : http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 100,
            TokensPerPeriod = 100,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    // Global safety net.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetConcurrencyLimiter("global", _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = 1000,
            QueueLimit = 100,
        }));
});

app.UseRateLimiter();                                  // after UseRouting / before endpoints
orders.RequireRateLimiting("per-user");
app.MapHealthChecks("/health/live").DisableRateLimiting();
```

Behind a reverse proxy, configure `ForwardedHeaders` first so `RemoteIpAddress` is the client, not
the proxy; otherwise every caller shares one partition.

## CORS

```csharp
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];

builder.Services.AddCors(o => o.AddPolicy("web", p => p
    .WithOrigins(origins)                 // exact scheme + host + port, no trailing slash
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()                   // only if cookies or Authorization header are used cross-site
    .WithExposedHeaders("Location", "api-supported-versions")));

app.UseCors("web");                       // before UseAuthentication
```

`AllowAnyOrigin()` with `AllowCredentials()` throws at runtime by design. `SetIsOriginAllowedToAllowWildcardSubdomains()`
covers `https://*.example.com` when tenants have subdomains.

## JWT bearer with policies and a second scheme (API keys for machine clients)

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = builder.Configuration["Auth:Authority"];       // OIDC discovery fetches signing keys
        o.Audience = builder.Configuration["Auth:Audience"];
        o.MapInboundClaims = false;                                   // keep "sub", "scope" as-is
        o.TokenValidationParameters.ValidateIssuer = true;
        o.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("orders:read", p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
        ctx.User.FindAll("scope").SelectMany(c => c.Value.Split(' ')).Contains("orders:read")))
    .AddPolicy("machine", p => p.AddAuthenticationSchemes("ApiKey").RequireAuthenticatedUser())
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(
        JwtBearerDefaults.AuthenticationScheme, "ApiKey").RequireAuthenticatedUser().Build());
```

`scope` is usually a space-separated string in one claim, which is why `RequireClaim("scope", "x")`
fails on tokens with multiple scopes; the assertion above splits it.

## Header propagation (correlation ids to downstream services)

```csharp
// Microsoft.AspNetCore.HeaderPropagation
builder.Services.AddHeaderPropagation(o =>
{
    o.Headers.Add("X-Correlation-Id", ctx => ctx.HeaderValue.Count > 0
        ? ctx.HeaderValue
        : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString());
});
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>()
    .AddHeaderPropagation()
    .AddStandardResilienceHandler();

app.UseHeaderPropagation();
```

With OpenTelemetry enabled, W3C `traceparent` is propagated automatically by the `HttpClient`
instrumentation; header propagation is then only needed for custom headers (tenant id, locale).

## Idempotency endpoint filter

Store interface and filter. The store implementation decides the TTL and whether keys are scoped
per caller (they should be).

```csharp
public sealed record StoredResponse(int StatusCode, string Body, string RequestHash);

public interface IIdempotencyStore
{
    Task<StoredResponse?> GetAsync(string key, CancellationToken ct);
    Task<bool> TryBeginAsync(string key, string requestHash, CancellationToken ct);   // false if already in progress
    Task CompleteAsync(string key, StoredResponse response, CancellationToken ct);
}

public sealed class IdempotencyFilter(IIdempotencyStore store) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues) || keyValues.Count != 1)
            return TypedResults.Problem(statusCode: 400, title: "Idempotency-Key header is required");

        var caller = http.User.FindFirst("sub")?.Value ?? "anonymous";
        var key = $"{caller}:{keyValues[0]}";
        var ct = http.RequestAborted;

        http.Request.EnableBuffering();
        using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        http.Request.Body.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        if (await store.GetAsync(key, ct) is { } stored)
        {
            if (stored.RequestHash != hash)
                return TypedResults.Problem(statusCode: 422, title: "Idempotency-Key reused with a different payload");

            http.Response.StatusCode = stored.StatusCode;
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(stored.Body, ct);
            return null;   // response already written
        }

        if (!await store.TryBeginAsync(key, hash, ct))
            return TypedResults.Problem(statusCode: 409, title: "Request with this Idempotency-Key is still processing");

        var result = await next(ctx);

        if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 } && result is IValueHttpResult { Value: { } value })
        {
            var json = JsonSerializer.Serialize(value, value.GetType(), JsonSerializerOptions.Web);
            await store.CompleteAsync(key, new StoredResponse(((IStatusCodeHttpResult)result).StatusCode!.Value, json, hash),
                CancellationToken.None);   // must complete even if the client went away
        }
        return result;
    }
}
```

Notes:

- `JsonSerializerOptions.Web` exists from .NET 9; on .NET 8 use
  `new JsonSerializerOptions(JsonSerializerDefaults.Web)` cached in a static field.
- When the store is a Postgres table, use an `INSERT ... ON CONFLICT DO NOTHING` for `TryBeginAsync`
  so two concurrent requests with the same key cannot both begin.
- Expire keys after 24 hours; clients regenerate the key for a genuinely new request.

## Output caching for GET (optional)

```csharp
builder.Services.AddOutputCache(o => o.AddPolicy("short", p => p.Expire(TimeSpan.FromSeconds(30)).SetVaryByQuery("page", "pageSize")));
app.UseOutputCache();
orders.MapGet("/", ListOrders).CacheOutput("short");
```

Only cache responses that do not depend on the caller; output caching ignores `Authorization` by
default unless `VaryByHeader` is set, which is a data-leak risk on authenticated endpoints.
