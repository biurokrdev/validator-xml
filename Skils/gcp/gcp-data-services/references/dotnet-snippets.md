# .NET snippets for GCP data services

Read this when you need the complete code for the patterns summarised in `SKILL.md`. Packages:
`Google.Cloud.Storage.V1`, `Google.Cloud.PubSub.V1`, `Google.Apis.Auth`, `Npgsql`,
`Npgsql.EntityFrameworkCore.PostgreSQL`.

## Emulator-aware client registration

```csharp
// Program.cs
builder.Services.AddSingleton(_ => new StorageClientBuilder
{
    EmulatorDetection = EmulatorDetection.EmulatorOrProduction   // honours STORAGE_EMULATOR_HOST
}.Build());

builder.Services.AddSingleton(sp =>
{
    var topic = TopicName.FromProjectTopic(builder.Configuration["Gcp:ProjectId"]!, "invoice-events");
    return new PublisherClientBuilder
    {
        TopicName = topic,
        EmulatorDetection = EmulatorDetection.EmulatorOrProduction   // honours PUBSUB_EMULATOR_HOST
    }.Build();
});
```

`docker-compose.yml` for local work:

```yaml
services:
  gcs:
    image: fsouza/fake-gcs-server
    command: ["-scheme", "http", "-port", "4443", "-external-url", "http://localhost:4443"]
    ports: ["4443:4443"]
  pubsub:
    image: gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators
    command: ["gcloud", "beta", "emulators", "pubsub", "start", "--host-port=0.0.0.0:8085"]
    ports: ["8085:8085"]
```

with `STORAGE_EMULATOR_HOST=http://localhost:4443` and `PUBSUB_EMULATOR_HOST=localhost:8085` in
`launchSettings.json`. Create the emulator topic/subscription at startup in Development only.

## IAM database authentication with EF Core

```csharp
public static class CloudSqlRegistration
{
    public static IServiceCollection AddCloudSqlDbContext(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default")!;   // no Password= in it
        var dsb = new NpgsqlDataSourceBuilder(connectionString);
        dsb.UseSnakeCaseNamingConvention();                                // if used by the model

        var useIam = !connectionString.Contains("Password=", StringComparison.OrdinalIgnoreCase);
        if (useIam)
        {
            var credential = GoogleCredential.GetApplicationDefault()
                .CreateScoped("https://www.googleapis.com/auth/sqlservice.login");
            dsb.UsePeriodicPasswordProvider(
                async (_, ct) => await credential.GetAccessTokenForRequestAsync(cancellationToken: ct),
                successRefreshInterval: TimeSpan.FromMinutes(45),
                failureRefreshInterval: TimeSpan.FromSeconds(10));
        }

        var dataSource = dsb.Build();
        services.AddSingleton(dataSource);
        services.AddDbContextPool<AppDbContext>(o => o
            .UseNpgsql(dataSource, npgsql => npgsql.EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention());
        return services;
    }
}
```

Locally: `cloud-sql-proxy --port 5432 --auto-iam-authn my-proj:europe-west1:app-pg` and a
connection string `Host=localhost;Database=app;Username=firstname.lastname@example.com`. The
proxy injects your own token, so the `useIam` branch can stay on; the token it adds is ignored
when the proxy already authenticated.

## Signed URL service: download, upload and resumable upload

```csharp
public sealed class SignedUrlService(UrlSigner signer, IOptions<StorageOptions> options)
{
    private static readonly UrlSigner.Options Short =
        UrlSigner.Options.FromDuration(TimeSpan.FromMinutes(15)).WithSigningVersion(SigningVersion.V4);

    public Task<string> DownloadUrlAsync(string objectName, string fileName, CancellationToken ct)
    {
        var template = UrlSigner.RequestTemplate
            .FromBucket(options.Value.Bucket)
            .WithObjectName(objectName)
            .WithHttpMethod(HttpMethod.Get)
            .WithQueryParameters(new Dictionary<string, IEnumerable<string>>
            {
                ["response-content-disposition"] = [$"attachment; filename=\"{fileName}\""]
            });
        return signer.SignAsync(template, Short, ct);
    }

    public Task<string> UploadUrlAsync(string objectName, string contentType, CancellationToken ct)
    {
        var template = UrlSigner.RequestTemplate
            .FromBucket(options.Value.Bucket)
            .WithObjectName(objectName)
            .WithHttpMethod(HttpMethod.Put)
            .WithContentHeaders(new Dictionary<string, IEnumerable<string>> { ["Content-Type"] = [contentType] });
        return signer.SignAsync(template, Short, ct);
    }

    // Step 1 of a browser resumable upload: the browser POSTs to this URL with the same headers
    // and receives a 201 with a `Location` header (the session URI).
    public Task<string> ResumableSessionUrlAsync(string objectName, string contentType, CancellationToken ct)
    {
        var template = UrlSigner.RequestTemplate
            .FromBucket(options.Value.Bucket)
            .WithObjectName(objectName)
            .WithHttpMethod(HttpMethod.Post)
            .WithRequestHeaders(new Dictionary<string, IEnumerable<string>> { ["x-goog-resumable"] = ["start"] })
            .WithContentHeaders(new Dictionary<string, IEnumerable<string>> { ["Content-Type"] = [contentType] });
        return signer.SignAsync(template, Short, ct);
    }
}

// registration
builder.Services.AddSingleton(_ => UrlSigner.FromCredential(GoogleCredential.GetApplicationDefault()));
```

Browser side of the resumable flow (Angular service, abridged):

```ts
const session = await fetch(signedPostUrl, { method: 'POST', headers: { 'x-goog-resumable': 'start', 'Content-Type': file.type } });
const sessionUri = session.headers.get('Location')!;
const chunk = 8 * 256 * 1024;                   // multiples of 256 KiB
for (let start = 0; start < file.size; start += chunk) {
  const end = Math.min(start + chunk, file.size);
  await fetch(sessionUri, {
    method: 'PUT',
    headers: { 'Content-Range': `bytes ${start}-${end - 1}/${file.size}` },
    body: file.slice(start, end),
  });                                           // 308 = resume incomplete, 200/201 = done
}
```

The session URI is itself the credential for the upload (no further signing), valid for a week.
The bucket needs CORS: `origin = [spa origin]`, `method = ["GET","PUT","POST","OPTIONS"]`,
`response_header = ["Content-Type","x-goog-resumable","Location","Content-Range"]`.

On Cloud Run, `UrlSigner.FromCredential` with the metadata-server credential signs through the
IAM Credentials API; the runtime service account needs `roles/iam.serviceAccountTokenCreator`
on itself. Without that role the first signing call fails with a 403 that mentions `signBlob`.

## Pub/Sub push endpoint

When the service is `--no-allow-unauthenticated` and the push service account has
`roles/run.invoker`, Cloud Run already validated the token before the request reaches Kestrel.
Validate it in code only when the service is public:

```csharp
public sealed record PushEnvelope(PushMessage Message, string Subscription);
public sealed record PushMessage(string Data, string MessageId, DateTimeOffset PublishTime,
    Dictionary<string, string>? Attributes);

app.MapPost("/pubsub/invoice-events", async (
    HttpRequest request, PushEnvelope envelope, IInvoiceEventHandler handler,
    IOptions<PubSubOptions> options, CancellationToken ct) =>
{
    if (options.Value.ValidateToken)   // only for public endpoints
    {
        var auth = request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ")) return Results.Unauthorized();
        var payload = await GoogleJsonWebSignature.ValidateAsync(auth["Bearer ".Length..],
            new GoogleJsonWebSignature.ValidationSettings { Audience = [options.Value.Audience] });
        if (payload.Email != options.Value.PushServiceAccount) return Results.Forbid();
    }

    var json = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Message.Data));
    var evt = JsonSerializer.Deserialize<InvoiceCreated>(json, JsonOptions.Web)!;

    // Idempotency: store MessageId (or the business key) with a unique constraint; on 23505 return 204.
    await handler.HandleAsync(envelope.Message.MessageId, evt, ct);
    return Results.NoContent();     // any 2xx acks; exceptions become 500 => redelivery
});
```

Keep the handler under the ack deadline (60 s in the example subscription). If the work is
longer, persist the message and return 204, then process it in a Cloud Run Job or a second
service with CPU always allocated.

## Downloading with a stream and range

```csharp
app.MapGet("/invoices/{id:guid}/pdf", async (Guid id, StorageClient storage, AppDbContext db,
    HttpResponse response, CancellationToken ct) =>
{
    var inv = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == id, ct);
    response.ContentType = "application/pdf";
    response.Headers.ContentDisposition = $"inline; filename=\"{inv.Number}.pdf\"";
    await storage.DownloadObjectAsync("acme-shop-uploads-prod", inv.ObjectName, response.Body, cancellationToken: ct);
});
```

Streaming through the API is acceptable for small files and when the response must be
authorised per request; for anything large or frequent, hand out a signed URL instead so the
bytes never cross Cloud Run (egress and instance time are both billed).
