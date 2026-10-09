---
name: gcp-cloud-run-deploy
description: >-
  Containerise and deploy ASP.NET Core (.NET 8) services and Angular SPAs to Google Cloud Run:
  multi-stage Dockerfiles (chiseled, non-root, port 8080), Artifact Registry, the gcloud run deploy
  flags that actually matter (CPU/memory, concurrency, min/max instances, CPU throttling, timeout,
  ingress, VPC egress, service account, secrets), Cloud SQL connectivity, startup/liveness probes,
  revisions and traffic splitting for canary and rollback, Cloud Run Jobs for migrations and batch,
  and CI/CD with GitHub Actions (Workload Identity Federation) or Cloud Build. Use this skill
  whenever the user mentions Cloud Run, "deploy to GCP", a Dockerfile for .NET or Angular,
  Artifact Registry, gcloud run, cold starts, canary or rollback of a revision, "run the
  migrations in the cloud", cloudbuild.yaml, a GitHub Actions workflow that pushes to Google
  Cloud, or simply asks "how do we ship this service", even if Cloud Run is not named.
---

# Deploying services to Cloud Run

This skill produces a container image that starts fast, runs as non-root on port 8080, and a
Cloud Run service whose scaling, networking and identity are set deliberately instead of by
default. Most Cloud Run incidents are configuration, not code: the default compute service account
with Editor rights, concurrency left at 80 for a CPU-bound service, no startup probe so a broken
revision receives 100% traffic, or migrations run from `Program.cs` by five instances at once. The
workflow below catches those before the first `gcloud run deploy`.

## Workflow

1. **Decide the runtime shape before writing the Dockerfile.** Is the service request-driven
   (service), a one-off or scheduled task (job), or does it need background work after the
   response (service with `--no-cpu-throttling`, or better a job/Pub/Sub consumer)? Cloud Run
   throttles CPU outside requests by default, so background `Task.Run` work silently stalls.
2. **Write the multi-stage Dockerfile** (sections below), build it locally with
   `docker build -t api:local .` and run it with `-p 8080:8080 -e ASPNETCORE_ENVIRONMENT=Development`.
   A container that does not start locally will not start on Cloud Run, and the local loop is
   100x faster than a deploy.
3. **Create the identity first**: a dedicated service account per service with only the roles
   it needs (`roles/cloudsql.client`, `roles/secretmanager.secretAccessor` on specific secrets).
   Deploying with the default compute service account "works" and is the hardest thing to undo
   later. Prefer creating it in Terraform (`gcp-terraform-iac`).
4. **Push to Artifact Registry** tagged with the git SHA, never only `latest`. Immutable tags make
   rollback a matter of pointing at an old revision, and make "what is running" answerable.
5. **Deploy with explicit flags** (table below), using `--no-traffic --tag canary` for anything
   that is not a trivial change, then shift traffic.
6. **Run schema migrations as a Cloud Run Job** before promoting traffic, not on app startup.
7. **Verify**: `gcloud run services describe api --region=... --format=yaml` shows the expected
   service account, env, secrets and probes; `curl -H "Authorization: Bearer $(gcloud auth
   print-identity-token)" https://<url>/health/live` returns 200; and the revision's logs in
   Cloud Logging contain no startup warnings (see `gcp-observability`).

## Dockerfile for a .NET 8 API

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Copy project files first so `restore` is cached until dependencies change.
COPY Directory.Packages.props ./
COPY src/MyApp.Api/MyApp.Api.csproj src/MyApp.Api/
COPY src/MyApp.Infrastructure/MyApp.Infrastructure.csproj src/MyApp.Infrastructure/
RUN dotnet restore src/MyApp.Api/MyApp.Api.csproj -r linux-x64
COPY . .
RUN dotnet publish src/MyApp.Api/MyApp.Api.csproj -c Release -o /app --no-restore \
    -r linux-x64 --self-contained false /p:UseAppHost=false /p:PublishReadyToRun=true

FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled-extra AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "MyApp.Api.dll"]
```

- `jammy-chiseled` images contain no shell, no package manager and run as user `app` (UID 1654)
  by default; the attack surface and image size are a fraction of the full image. Use the
  `-extra` variant when you need ICU (culture-aware formatting) or tzdata; otherwise set
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` and accept invariant culture.
- Cloud Run injects `PORT=8080`; ASP.NET Core 8 images already listen on 8080 via
  `ASPNETCORE_HTTP_PORTS`. Keep them aligned and do not configure Kestrel for HTTPS: Cloud Run
  terminates TLS.
- `PublishReadyToRun` and `TieredPGO` cut cold-start JIT time, which is the dominant cost of a
  cold request on Cloud Run. `DOTNET_gcServer=0` (workstation GC) uses less memory on a 1-vCPU
  instance; server GC pays off only with 2+ vCPU and high concurrency.
- Keep `appsettings.Production.json` free of secrets; configuration comes from env vars and
  `--set-secrets` (ASP.NET maps `ConnectionStrings__Default` to `ConnectionStrings:Default`).

## Dockerfile for an Angular SPA

```dockerfile
FROM node:20-alpine AS build
WORKDIR /src
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build -- --configuration=production

FROM nginxinc/nginx-unprivileged:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/my-app/browser /usr/share/nginx/html
EXPOSE 8080
```

```nginx
server {
  listen 8080;
  root /usr/share/nginx/html;
  gzip on; gzip_types text/css application/javascript application/json image/svg+xml;
  location ~* \.(?:js|css|woff2|png|svg)$ { expires 1y; add_header Cache-Control "public, immutable"; }
  location / { try_files $uri /index.html; add_header Cache-Control "no-cache"; }
}
```

- `nginx-unprivileged` listens on 8080 as a non-root user out of the box. Hashed bundles are
  immutable; `index.html` is never cached so a new deploy is visible immediately.
- Environment-specific values (API base URL) belong in a `config.json` fetched at startup, not
  baked into the bundle, so one image serves every environment.
- Alternative for public, high-traffic SPAs: upload `dist/` to a Cloud Storage bucket behind an
  external Application Load Balancer with a backend bucket and Cloud CDN, with the API attached
  to the same load balancer via a serverless NEG (same origin, no CORS). The trade-off: deep links
  return HTTP 404 with `index.html` as body (the bucket cannot rewrite), which confuses uptime
  checks and SEO. Cloud Run + nginx is simpler when traffic is modest.

## Artifact Registry

```bash
gcloud artifacts repositories create apps --repository-format=docker --location=europe-west1
gcloud auth configure-docker europe-west1-docker.pkg.dev
docker build -t europe-west1-docker.pkg.dev/$PROJECT/apps/api:$GIT_SHA .
docker push europe-west1-docker.pkg.dev/$PROJECT/apps/api:$GIT_SHA
```

Keep the repository in the same region as Cloud Run (pull latency affects cold starts). Add a
cleanup policy (keep the last 10 tagged versions, delete untagged after 7 days) because images
accumulate at every CI run and storage is billed. Enable vulnerability scanning on the repository.

## `gcloud run deploy`: flags that matter

```bash
gcloud run deploy api \
  --image=europe-west1-docker.pkg.dev/$PROJECT/apps/api:$GIT_SHA \
  --region=europe-west1 \
  --service-account=api-sa@$PROJECT.iam.gserviceaccount.com \
  --cpu=1 --memory=512Mi --concurrency=80 \
  --min-instances=0 --max-instances=10 --cpu-boost --timeout=60 \
  --ingress=internal-and-cloud-load-balancing --no-allow-unauthenticated \
  --network=vpc-main --subnet=serverless-ew1 --vpc-egress=private-ranges-only \
  --add-cloudsql-instances=$PROJECT:europe-west1:app-pg \
  --set-env-vars=ASPNETCORE_ENVIRONMENT=Production,GOOGLE_CLOUD_PROJECT=$PROJECT \
  --set-secrets=ConnectionStrings__Default=db-connection:latest \
  --startup-probe=httpGet.path=/health/startup,httpGet.port=8080,periodSeconds=2,failureThreshold=30 \
  --liveness-probe=httpGet.path=/health/live,periodSeconds=30 \
  --labels=app=myapp,env=prod
```

| Flag | Why it matters |
|---|---|
| `--cpu` / `--memory` | A .NET API is comfortable at 1 vCPU / 512Mi. Below 1 vCPU, concurrency is capped and JIT is slow. Watch `container/memory/utilizations`; OOM kills show up as 503s with no app log. |
| `--concurrency` | Requests per instance. 80 is fine for async I/O-bound .NET; drop to 10–20 for CPU-heavy work (PDF rendering, XML validation) or instances will queue and time out. |
| `--min-instances` | 0 for dev; 1+ in prod when cold starts (1–3 s for .NET) are unacceptable. Each warm instance is billed continuously. |
| `--max-instances` | Protects the database: `max-instances × Maximum Pool Size` must stay below Postgres `max_connections` (see `gcp-data-services`). |
| `--cpu-boost` | Extra CPU during startup; cheaper than min-instances for cutting cold-start time. |
| `--no-cpu-throttling` | CPU allocated between requests. Needed for background work, SignalR, OpenTelemetry batch exporters flushing after the response. Costs more; prefer a job or Pub/Sub consumer when the work is substantial. |
| `--timeout` | Request timeout, up to 3600 s. Keep it short for APIs so stuck requests fail fast; raise only for the endpoint that needs it, or move it to a job. |
| `--ingress` | `internal-and-cloud-load-balancing` when a load balancer / IAP sits in front; `all` only for genuinely public services. |
| `--no-allow-unauthenticated` | Callers need `roles/run.invoker`; service-to-service calls send an ID token from the metadata server. Public only when a browser calls the service directly. |
| `--network/--subnet/--vpc-egress` | Direct VPC egress: reach Cloud SQL private IP, Memorystore, internal services. `private-ranges-only` keeps internet egress on Google's path; `all-traffic` routes everything through the VPC (needed for a static egress IP via Cloud NAT). Prefer this over `--vpc-connector`, which costs per connector instance and has lower throughput. |
| `--set-secrets` | Mounts Secret Manager versions as env vars (or files with `=/path:ver`). Pin to a version number in prod when you want deploys to be the only moment secrets change; `latest` is convenient in dev. |
| `--execution-environment=gen2` | Full Linux compatibility and faster CPU; gen1 has marginally faster cold starts. Pick gen2 when in doubt. |
| `--session-affinity` | Sticky sessions for SignalR / WebSockets; still design for instance loss. |

## Cloud SQL from Cloud Run

Two supported paths; pick one per environment:

- **Unix socket** (`--add-cloudsql-instances`): Cloud Run runs the Auth Proxy for you and exposes
  `/cloudsql/<project>:<region>:<instance>`. Connection string:
  `Host=/cloudsql/proj:europe-west1:app-pg;Database=app;Username=app;Password=...`. No VPC
  needed, traffic encrypted by the proxy. Simplest; slightly higher latency per new connection
  (pooling hides it).
- **Private IP** over Direct VPC egress: the instance has only a private address, the service
  joins the VPC subnet, connection string uses the IP (`Host=10.20.0.3;SSL Mode=Require;...`).
  Lowest latency, no proxy limits, required when you also use Memorystore. More Terraform
  (Private Service Access peering).

Either way, the service account needs `roles/cloudsql.client`, and IAM database authentication
avoids a password secret entirely (`gcp-data-services` has the Npgsql setup).

## Probes

- **Startup probe** (`/health/startup`): until it passes, the instance receives no traffic. Point
  it at a check that opens a DB connection and loads configuration, so a revision with a wrong
  secret never becomes serving and the deploy fails loudly instead of returning 500s. The default
  is a TCP probe on the port, which passes as soon as Kestrel listens, long before the app is
  usable.
- **Liveness probe** (`/health/live`): cheap, no dependencies; a failure restarts the instance.
  Do not include the database here or a DB blip restarts every instance in a loop.
- Register both with `MapHealthChecks` and exclude them from request logging and tracing to keep
  logs readable.

## Revisions, traffic splitting and rollback

Every deploy creates an immutable revision; traffic assignment is separate from deployment.

```bash
gcloud run deploy api --image=...:$GIT_SHA --region=europe-west1 --no-traffic --tag=canary
# canary URL: https://canary---api-<hash>-ew.a.run.app  (smoke-test it)
gcloud run services update-traffic api --region=europe-west1 --to-tags=canary=10
gcloud run services update-traffic api --region=europe-west1 --to-latest
# rollback in seconds, no build needed:
gcloud run revisions list --service=api --region=europe-west1
gcloud run services update-traffic api --region=europe-west1 --to-revisions=api-00041-xyz=100
```

Traffic split is per request, not per user; a canary that changes the schema or a response
contract must stay backward compatible with the previous revision because both run at once. Add
`--revision-suffix=$GIT_SHORT_SHA` so revision names say what they contain.

## Cloud Run Jobs: migrations and batch

Jobs run a container to completion with no HTTP server, with retries and parallel tasks. Use them
for EF Core migrations, nightly imports and anything longer than a request timeout.

```bash
# image built from: dotnet ef migrations bundle -r linux-x64 --self-contained -o efbundle
gcloud run jobs deploy migrate-db \
  --image=europe-west1-docker.pkg.dev/$PROJECT/apps/api-migrations:$GIT_SHA \
  --region=europe-west1 --service-account=migrate-sa@$PROJECT.iam.gserviceaccount.com \
  --set-cloudsql-instances=$PROJECT:europe-west1:app-pg \
  --set-secrets=ConnectionStrings__Default=db-connection-migrator:latest \
  --tasks=1 --max-retries=0 --task-timeout=15m
gcloud run jobs execute migrate-db --region=europe-west1 --wait
```

- `--max-retries=0` for migrations: a half-applied migration retried blindly is worse than a
  failed pipeline. The bundle reads the connection string from the application's configuration
  (env vars), so the chiseled image needs no shell.
- Use a separate, more privileged DB user for the migrator than for the API (DDL vs DML).
- Schedule recurring jobs with Cloud Scheduler calling the Jobs API
  (`gcloud scheduler jobs create http ... --uri=https://run.googleapis.com/v2/projects/.../jobs/x:run --oauth-service-account-email=...`).
- See `dotnet-ef-core-postgres` for why `Database.Migrate()` at startup is wrong on Cloud Run.

## CI/CD

GitHub Actions with Workload Identity Federation (no exported keys):

```yaml
permissions: { contents: read, id-token: write }
steps:
  - uses: actions/checkout@v4
  - id: auth
    uses: google-github-actions/auth@v2
    with:
      workload_identity_provider: projects/123456789/locations/global/workloadIdentityPools/github/providers/github
      service_account: ci-deploy@my-project.iam.gserviceaccount.com
      token_format: access_token
  - uses: docker/login-action@v3
    with: { registry: europe-west1-docker.pkg.dev, username: oauth2accesstoken, password: ${{ steps.auth.outputs.access_token }} }
  - uses: docker/build-push-action@v6
    with: { context: ., push: true, tags: europe-west1-docker.pkg.dev/my-project/apps/api:${{ github.sha }} }
  - uses: google-github-actions/deploy-cloudrun@v2
    with:
      service: api
      region: europe-west1
      image: europe-west1-docker.pkg.dev/my-project/apps/api:${{ github.sha }}
      flags: --no-traffic --tag=canary
```

Cloud Build is the alternative when the organisation keeps CI inside GCP: a `cloudbuild.yaml`
with build, push and `gcloud run deploy` steps, triggered from the repository, running as a
dedicated build service account (not the legacy Cloud Build SA). Both full pipelines, including
the migration job step and traffic promotion, are in `references/ci-pipelines.md`.

The CI service account needs `roles/artifactregistry.writer`, `roles/run.developer`,
`roles/iam.serviceAccountUser` on the runtime service accounts (to deploy "as" them), and
nothing project-wide. Keep Terraform-managed infrastructure (service, IAM, SQL) in
`gcp-terraform-iac`; let CI change only the image and traffic, or let Terraform ignore the
image field (`lifecycle { ignore_changes = [template[0].containers[0].image] }`).

## Cost

- Request-based billing (default, CPU throttled) charges only during requests; with min-instances
  0 an idle dev service costs nothing. `--no-cpu-throttling` switches to instance-based billing.
- Higher `--concurrency` means fewer instances for the same traffic; measure p95 latency against
  concurrency before raising it.
- Use `--cpu-boost` before `--min-instances`; one warm 1 vCPU / 512Mi instance is roughly
  €40/month, often more than the whole dev environment.
- Artifact Registry storage and Cloud Logging ingestion are the surprise lines: cleanup policies
  and log exclusions (`gcp-observability`) fix both.
- Committed use discounts apply to Cloud Run when prod has a steady min-instances floor.

## Examples

**Example 1**
Input: "Deploy the new validator API to Cloud Run, it needs the Postgres DB and the API key from
Secret Manager."
Output: Dockerfile as above with `/health/startup` and `/health/live` mapped; a `validator-sa`
service account with `roles/cloudsql.client` and `secretAccessor` on the two secrets;
`gcloud run deploy validator-api ... --add-cloudsql-instances ... --set-secrets
ConnectionStrings__Default=validator-db:latest,Validator__ApiKey=validator-api-key:latest
--concurrency=20` (XML validation is CPU-bound) and a `validator-migrate` job executed before
traffic promotion.

**Example 2**
Input: "The release from this morning returns 500s, get us back to the previous version."
Output: `gcloud run revisions list --service=api` to find the last healthy revision, then
`gcloud run services update-traffic api --to-revisions=<prev>=100`, followed by a note to add a
startup probe that touches the database so the next broken revision never receives traffic.

## References

- `references/ci-pipelines.md` — read when writing or reviewing the full GitHub Actions workflow
  (build, push, migrate, canary, promote) or the Cloud Build equivalent, including the one-off
  commands to bootstrap Workload Identity Federation.
