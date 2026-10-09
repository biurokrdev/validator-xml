---
name: gcp-data-services
description: >-
  Use and operate the GCP data services a .NET application depends on: Cloud SQL for PostgreSQL
  (sizing, HA, backups and PITR, maintenance, connection methods, IAM database authentication,
  Npgsql pooling vs PgBouncer, flags, Query Insights, migrations as Cloud Run Jobs), Cloud Storage
  (bucket design, object naming, V4 signed URLs, resumable uploads, lifecycle, CMEK, uniform
  access, Google.Cloud.Storage.V1 with ADC), local development with ADC and emulators, Pub/Sub
  push subscriptions to Cloud Run with dead-lettering, and Memorystore for caching. Use this skill
  whenever the user mentions Cloud SQL, "the database on GCP", Auth Proxy, cloud-sql-proxy,
  "too many connections", backups, PITR, a bucket, GCS, signed URL, file upload or download,
  "store the PDF", Pub/Sub, a queue or background processing on GCP, Redis/Memorystore,
  gcloud auth application-default, or any Google.Cloud.* NuGet package, even if they only say
  "save this file somewhere" or "the app cannot connect to the DB".
---

# Cloud SQL, Cloud Storage and friends from .NET

This skill produces application code and operational settings that keep Postgres connections
within limits, files private but reachable, and asynchronous work idempotent. The characteristic
GCP failures are quiet: a Cloud Run service scales to 30 instances with a default Npgsql pool of
100 and Postgres rejects connections; a bucket is made public "temporarily"; a Pub/Sub push
subscription redelivers a message forever because the endpoint returns 500 and nobody configured
a dead-letter topic. Everything below is ordered to avoid those.

## Workflow

1. **Identify the access path before writing code**: where does the code run (Cloud Run,
   developer laptop, CI, a job) and what identity does it have? On GCP, every client library uses
   Application Default Credentials (ADC); the identity is the Cloud Run service account in prod and
   the developer's ADC locally. Code that takes a key file path is already wrong.
2. **Grant the role on the narrowest resource** (bucket, secret, instance) to the service
   account, in Terraform (`gcp-terraform-iac`).
3. **Write the client as a singleton** (`StorageClient`, `PublisherClient`, `NpgsqlDataSource`)
   registered in DI; the clients hold connection pools and channels and are expensive to create.
4. **Size connections and quotas against the scaling limits** of the consumer (max instances ×
   pool size; Pub/Sub ack deadline vs processing time).
5. **Run it locally with ADC** (`gcloud auth application-default login`) or an emulator; then in
   a dev project with the real service.
6. **Verify**: the operation succeeds as the runtime service account (not your user), the
   Cloud SQL `num_backends` metric stays below the limit under load, a signed URL expires when it
   should, and a poisoned Pub/Sub message ends in the dead-letter topic after N attempts.

## Cloud SQL for PostgreSQL: instance and operations

- **Tier**: `db-custom-<vCPU>-<MiB>` (e.g. `db-custom-2-7680`); shared-core `db-f1-micro` /
  `db-g1-small` only for throwaway dev (no SLA, tiny `max_connections`). Enterprise edition is
  enough for most services; Enterprise Plus buys higher PITR retention (35 days), data cache and
  near-zero-downtime maintenance.
- **HA**: `availability_type = REGIONAL` keeps a synchronous standby in another zone; failover
  takes about a minute and doubles the price. Use it in prod, not in dev. Read replicas scale
  reads, they are not HA.
- **Backups and PITR**: automated daily backups plus `point_in_time_recovery_enabled` (WAL
  retention, up to 7 days on Enterprise). PITR restores to a *new* instance
  (`gcloud sql instances clone src dst --point-in-time 2026-10-08T06:00:00Z`), so practice it
  once before you need it. Take an on-demand backup before any risky migration:
  `gcloud sql backups create --instance=app-pg`.
- **Maintenance**: set a window (`maintenance_window`, `update_track = "stable"`) outside business
  hours and add deny periods around releases; unplanned maintenance restarts drop every
  connection, so Npgsql `EnableRetryOnFailure` matters (see `dotnet-ef-core-postgres`).
- **Storage**: SSD with auto-resize on; disk size also determines IOPS, so a small disk can be the
  bottleneck before CPU is.
- **Flags** worth setting: `cloudsql.iam_authentication=on`, `log_min_duration_statement=1000`,
  `log_lock_waits=on`, `idle_in_transaction_session_timeout=60000`. Leave `shared_buffers` and
  `max_connections` at the tier default unless measured; raising `max_connections` costs memory
  per connection and hides pooling mistakes.
- **Query Insights**: enable it (`insights_config`), tag queries with `Application Name=api` in
  the connection string so the dashboard attributes load per service, and look at it before
  adding indexes.

## Connecting

| Method | When | Notes |
|---|---|---|
| Unix socket on Cloud Run (`--add-cloudsql-instances`) | Default for Cloud Run | `Host=/cloudsql/proj:region:inst`; encrypted by the built-in proxy; needs `roles/cloudsql.client` |
| Private IP via VPC | Prod, latency-sensitive, or when Memorystore is also used | Instance without public IP, service on Direct VPC egress; `SSL Mode=Require` at least |
| Cloud SQL Auth Proxy v2 | Laptops, CI, Testcontainers-less integration tests | `cloud-sql-proxy --port 5432 --auto-iam-authn proj:region:inst`; app connects to `localhost` |
| Public IP + authorized networks | Avoid | Static office IPs rot and the surface is the internet |

There is no official Cloud SQL language connector for .NET; the socket, private IP or the proxy
covers every case. Connection string for the socket path, including pool settings:

```
Host=/cloudsql/my-proj:europe-west1:app-pg;Database=app;Username=app;Password=...;
Application Name=api;Maximum Pool Size=20;Minimum Pool Size=0;Connection Idle Lifetime=300;
Timeout=15;Command Timeout=30;Keepalive=30
```

## IAM database authentication (no password secret)

Enable the `cloudsql.iam_authentication` flag, create the user for the service account and grant
privileges in SQL; the user name is the account email without `.gserviceaccount.com`:

```bash
gcloud sql users create api-sa@my-proj.iam --instance=app-pg --type=cloud_iam_service_account
psql -c 'GRANT USAGE ON SCHEMA public TO "api-sa@my-proj.iam"; GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO "api-sa@my-proj.iam";'
```

```csharp
var credential = (await GoogleCredential.GetApplicationDefaultAsync())
    .CreateScoped("https://www.googleapis.com/auth/sqlservice.login");

var dsb = new NpgsqlDataSourceBuilder(
    "Host=/cloudsql/my-proj:europe-west1:app-pg;Database=app;Username=api-sa@my-proj.iam;Maximum Pool Size=20");
dsb.UsePeriodicPasswordProvider(
    async (_, ct) => await credential.GetAccessTokenForRequestAsync(cancellationToken: ct),
    successRefreshInterval: TimeSpan.FromMinutes(45),
    failureRefreshInterval: TimeSpan.FromSeconds(10));
var dataSource = dsb.Build();
builder.Services.AddSingleton(dataSource);
builder.Services.AddDbContextPool<AppDbContext>(o => o.UseNpgsql(dataSource));
```

The access token is the password and lives one hour, which is why Npgsql's periodic provider
refreshes it instead of reading it once. The same code works on a laptop against the Auth Proxy
with `--auto-iam-authn`, using the developer's own IAM user. Also grant the migration job its
own IAM user with DDL rights so the API user never has them.

## Connection pooling: Npgsql vs PgBouncer

- Each Cloud Run instance has its own Npgsql pool. Budget: `max-instances × Maximum Pool Size`
  must stay under the instance's `max_connections` minus a reserve for migrations, Query
  Insights and admins. Ten instances × 20 = 200 on a tier that allows 500 is fine; ten × the
  default 100 is not.
- Keep `Minimum Pool Size=0` and a short `Connection Idle Lifetime` on Cloud Run: idle instances
  are frozen, and a pool of warm connections held by 30 idle instances starves the active ones.
- Add PgBouncer (or Cloud SQL's managed connection pooling, where available for your edition) in
  transaction mode when many services or hundreds of instances share one instance. Transaction
  pooling breaks session state (prepared statements, `SET`, advisory locks); set
  `No Reset On Close=true` and `Max Auto Prepare=0` in Npgsql behind it.

## Migrations

Run EF Core migrations as a Cloud Run Job from a migration bundle, before the new revision takes
traffic, with a dedicated DDL-capable user. `gcp-cloud-run-deploy` has the job definition and
pipeline step, `dotnet-ef-core-postgres` has the migration authoring rules (why
`CREATE INDEX CONCURRENTLY` needs `suppressTransaction`, why startup-time `Migrate()` deadlocks).

## Cloud Storage

- **Bucket design**: one bucket per purpose and environment (`acme-shop-uploads-prod`,
  `acme-shop-exports-prod`); regional, co-located with Cloud Run; `uniform_bucket_level_access`
  and `public_access_prevention = enforced` always; versioning on for user data; CMEK
  (`encryption { default_kms_key_name }` plus the GCS service agent as
  `cloudkms.cryptoKeyEncrypterDecrypter`) only where a compliance requirement names it, because
  it adds KMS latency and a dependency on key availability.
- **Object naming**: `tenants/<tenant-id>/<yyyy>/<mm>/<uuid>.<ext>`. Never a user-supplied file
  name or anything personal in the key (keys appear in logs and URLs); keep the original name in
  metadata or the database. Avoid purely sequential prefixes under sustained high write rates.
- **Lifecycle**: delete noncurrent versions after `num_newer_versions = 3`, move exports to
  `NEARLINE` after 30 days and delete after 365, `abort_incomplete_multipart_upload` after 7
  days. Retention policies and bucket lock only for legal holds; they cannot be undone.
- **Roles**: `roles/storage.objectUser` (read/write objects) for the API; `objectViewer` for
  read-only consumers; never `storage.admin` on a workload.

### .NET client

```csharp
builder.Services.AddSingleton(_ => StorageClient.Create());   // ADC: SA on Cloud Run, your user locally

await storage.UploadObjectAsync("acme-shop-uploads-prod", objectName, "application/pdf", stream,
    new UploadObjectOptions { IfGenerationMatch = 0 }, ct);     // 0 = fail if it already exists
await storage.DownloadObjectAsync(bucket, objectName, output, cancellationToken: ct);
await foreach (var o in storage.ListObjectsAsync(bucket, $"tenants/{tenantId}/")) { ... }
```

Large uploads from the server use `storage.CreateObjectUploader(...)` (resumable, with progress
and `ChunkSize`); the convenience `UploadObjectAsync` already switches to resumable above a
few MiB.

### Signed URLs (V4) for browser upload and download

Let the browser talk to the bucket directly; the API only issues short-lived URLs:

```csharp
var signer = UrlSigner.FromCredential(await GoogleCredential.GetApplicationDefaultAsync());
var template = UrlSigner.RequestTemplate
    .FromBucket("acme-shop-uploads-prod")
    .WithObjectName($"tenants/{tenantId}/{Guid.NewGuid():N}.pdf")
    .WithHttpMethod(HttpMethod.Put)
    .WithContentHeaders(new Dictionary<string, IEnumerable<string>> { ["Content-Type"] = ["application/pdf"] });
var options = UrlSigner.Options.FromDuration(TimeSpan.FromMinutes(15)).WithSigningVersion(SigningVersion.V4);
string uploadUrl = await signer.SignAsync(template, options);
```

On Cloud Run there is no private key, so the signer calls the IAM `signBlob` API: grant the
service account `roles/iam.serviceAccountTokenCreator` *on itself*. Binding the content type
into the signature stops the browser from uploading something else. Set a CORS rule on the bucket
for the SPA origin. For files over ~100 MB from the browser, sign a `POST` with header
`x-goog-resumable: start`, let the browser open the resumable session and upload chunks (multiples
of 256 KiB); `references/dotnet-snippets.md` has the full flow.

## Local development

- `gcloud auth application-default login` writes ADC for your user; add
  `--impersonate-service-account=api-sa@...` (needs `roles/iam.serviceAccountTokenCreator` on
  that account) to run locally with exactly the production identity and catch missing IAM before
  deploy. Set `GOOGLE_CLOUD_PROJECT` so quota and project-scoped APIs resolve.
- Do not download service account key files; they do not expire and end up in repositories.
- Emulators: `fsouza/fake-gcs-server` (`STORAGE_EMULATOR_HOST`) and
  `gcloud beta emulators pubsub start` (`PUBSUB_EMULATOR_HOST`), with
  `EmulatorDetection = EmulatorDetection.EmulatorOrProduction` on the client builders so the same
  code runs against both. Postgres: Testcontainers or a local container; the Auth Proxy to the
  dev instance for data-shaped debugging.

## Pub/Sub for asynchronous work

Push subscriptions to Cloud Run are the simplest consumer: no polling loop, scale-to-zero, and
Cloud Run's IAM validates the OIDC token so the endpoint needs no extra auth code.

```bash
gcloud pubsub topics create invoice-events
gcloud pubsub topics create invoice-events-dlq
gcloud pubsub subscriptions create invoice-events-api --topic=invoice-events \
  --push-endpoint=https://api-xyz-ew.a.run.app/pubsub/invoice-events \
  --push-auth-service-account=pubsub-push@my-proj.iam.gserviceaccount.com \
  --ack-deadline=60 --min-retry-delay=10s --max-retry-delay=600s \
  --dead-letter-topic=invoice-events-dlq --max-delivery-attempts=5
```

- The push account needs `roles/run.invoker` on the service; the Pub/Sub service agent
  (`service-<project-number>@gcp-sa-pubsub.iam.gserviceaccount.com`) needs `pubsub.publisher` on
  the DLQ topic and `pubsub.subscriber` on the subscription, or dead-lettering silently does
  nothing.
- Handler contract: 2xx acks; anything else (or a timeout past the ack deadline) redelivers.
  Finish within the ack deadline or hand off to a job. Make the handler idempotent on
  `messageId` or a business key; at-least-once delivery means duplicates are normal.
- Alert on `subscription/dead_letter_message_count` and on `oldest_unacked_message_age`.
- Publishing from .NET: `PublisherClient.CreateAsync(TopicName.FromProjectTopic(project, topic))`
  as a singleton; `PublishAsync` batches internally. Use ordering keys only when you need them;
  they serialise throughput per key.

## Memorystore

Reach for it only when per-instance memory caching is not enough (shared rate limits,
distributed locks, session state for SignalR). Memorystore for Redis (or Valkey) lives in the
VPC, so the Cloud Run service needs Direct VPC egress; enable AUTH and in-transit encryption.
In .NET use `AddStackExchangeRedisCache` (`IDistributedCache`) or `StackExchange.Redis` directly,
one `ConnectionMultiplexer` per process. The smallest Basic tier instance is still a fixed
monthly cost, which is why `IMemoryCache` with a short TTL is the right first step on Cloud Run.

## Examples

**Example 1**
Input: "Users need to upload invoices (PDF, up to 20 MB) and later download them."
Output: bucket `acme-shop-uploads-<env>` with uniform access, versioning and a 3-version
lifecycle rule in Terraform; CORS for the SPA origin; an endpoint returning a V4 signed PUT URL
bound to `application/pdf`, 15 minutes; object key `tenants/{tenantId}/{yyyy}/{mm}/{uuid}.pdf`
stored with the invoice row; download via a signed GET URL with
`response-content-disposition=attachment`; `roles/iam.serviceAccountTokenCreator` on the API
service account for itself.

**Example 2**
Input: "Postgres says 'remaining connection slots are reserved' since we scaled the API."
Output: `Maximum Pool Size=20` and `Connection Idle Lifetime=300` in the connection string,
`--max-instances` set so instances × 20 stays under the tier's `max_connections`, a check of
`cloudsql.googleapis.com/database/postgresql/num_backends` in Monitoring, and PgBouncer only if
the budget still does not fit.

## References

- `references/dotnet-snippets.md` — read when implementing the resumable browser upload flow, a
  Pub/Sub push endpoint with manual token validation, emulator-aware client registration, or the
  complete IAM-auth `NpgsqlDataSource` wiring with EF Core.
