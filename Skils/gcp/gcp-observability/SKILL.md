---
name: gcp-observability
description: >-
  Set up logging, metrics, tracing and alerting for services on Google Cloud: structured JSON
  logs from .NET that Cloud Logging parses (severity, trace/span correlation, Error Reporting),
  Cloud Run request logs vs application logs, log-based metrics, dashboards, uptime checks,
  alert policies with notification channels, SLOs and burn-rate alerts, Cloud Trace with
  OpenTelemetry, log retention, sinks to BigQuery/GCS, exclusions to control cost, export to an
  external Elastic/Kibana stack, and a runbook/on-call structure. Use this skill whenever the
  user mentions Cloud Logging, Logs Explorer, "I can't find the logs", Cloud Monitoring, an
  alert, SLO, uptime check, Cloud Trace, OpenTelemetry, Error Reporting, correlating a request
  across services, log costs, log retention, Kibana or Elastic on GCP, on-call, a runbook, or
  asks "why is this slow in production" or "how do we know when it breaks", even if they only
  say "add logging" or "configure ILogger".
---

# Observability on GCP for .NET services

This skill produces logs that Cloud Logging can filter by severity and trace, metrics and alerts
that page on symptoms users feel (error ratio, latency, availability) rather than on CPU, and
traces that connect a slow request to the SQL statement behind it. Without structure, Cloud Run
logs are a stream of `textPayload` lines at severity `DEFAULT`: no filtering by level, no link
between a request log and the exception it caused, and alerts that fire on nothing or on
everything. The order below gets the cheap, high-value pieces (structured logs, one error-ratio
alert, one latency SLO) in place before dashboards.

## Workflow

1. **Make the application emit structured JSON to stdout** with GCP field names (section
   below). This is the one change that makes everything else possible: filtering by `severity`,
   grouping errors in Error Reporting and jumping from a log line to its trace. Do it before
   adding any exporter or agent.
2. **Decide what "broken" and "slow" mean** per service: an availability target (non-5xx ratio)
   and a latency target (p95 or p99 under N ms) over 28 days. These become the SLOs and the only
   alerts that page.
3. **Create the alert policies and notification channels in Terraform** (`gcp-terraform-iac`),
   each with a `documentation` block linking to a runbook; an alert without a runbook is a
   notification, not an alert.
4. **Add tracing with OpenTelemetry** once logs and alerts exist; sample in production, propagate
   context into outgoing HTTP calls and Npgsql, and keep health checks out.
5. **Control cost**: exclude health-check request logs and debug noise at the sink, set retention
   per bucket, route long-term analysis to BigQuery.
6. **Verify**: in Logs Explorer, `resource.type="cloud_run_revision" severity>=ERROR` shows your
   exceptions with stack traces and a trace link; Error Reporting groups them; the 5xx alert
   fires in a test (deploy a revision returning 500 to a canary tag) and the notification arrives;
   a trace in Cloud Trace shows the API span with child Npgsql spans.

## Structured logging from .NET

Cloud Run captures stdout/stderr; a line that parses as JSON becomes `jsonPayload` and these
keys are promoted to first-class fields:

| JSON key | Effect |
|---|---|
| `severity` | `DEBUG`, `INFO`, `NOTICE`, `WARNING`, `ERROR`, `CRITICAL`; filters and Error Reporting rely on it |
| `message` | Shown as the log line; put the exception `ToString()` here so Error Reporting sees the stack |
| `time` | RFC 3339 timestamp; otherwise ingestion time is used |
| `logging.googleapis.com/trace` | `projects/<project-id>/traces/<trace-id>`; links the entry to the request log and to Cloud Trace |
| `logging.googleapis.com/spanId` | 16-hex span id |
| `logging.googleapis.com/trace_sampled` | `true` when the trace is exported |
| `logging.googleapis.com/labels` | map of string labels (e.g. `tenant`) for cheap filtering |
| `logging.googleapis.com/sourceLocation` | `file`, `line`, `function` |
| `httpRequest` | Request metadata; normally only Cloud Run's own request log sets it |

Map `LogLevel.Trace/Debug` to `DEBUG`, `Information` to `INFO`, `Warning` to `WARNING`, `Error`
to `ERROR`, `Critical` to `CRITICAL`. Add every structured property of the log event as a top-level
key (so `logger.LogInformation("Invoice {InvoiceId} paid", id)` yields `jsonPayload.InvoiceId`),
and keep the category in `logger`.

Trace correlation: Cloud Run sends `traceparent` (W3C) and `X-Cloud-Trace-Context` on every
request; ASP.NET Core reads `traceparent` into `Activity.Current`, so the formatter takes
`Activity.Current.TraceId` and `SpanId`. The project id comes from the `GOOGLE_CLOUD_PROJECT`
env var set at deploy time (`gcp-cloud-run-deploy`).

Three ways to produce the JSON, in order of preference:

1. A custom `ConsoleFormatter` registered with `AddConsole` + `AddConsoleFormatter` (no
   dependencies, full control; complete class in `references/dotnet-logging-and-tracing.md`).
2. Serilog with a GCP-shaped `ITextFormatter` to the console sink when the project already uses
   Serilog enrichers.
3. `Google.Cloud.Diagnostics.AspNetCore3`, which writes through the Logging API instead of
   stdout and adds Error Reporting and Trace clients. Easy, but it needs API permissions, buffers
   in-process (lost on instance kill) and is being superseded by OpenTelemetry.

Registration for option 1:

```csharp
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.FormatterName = "gcp");
builder.Logging.AddConsoleFormatter<GcpJsonConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning); // Cloud Run already logs requests
```

Error Reporting picks up Cloud Run entries with severity `ERROR` or above whose `message`
contains a stack trace; add `"@type": "type.googleapis.com/google.devtools.clouderrorreporting.v1beta1.ReportedErrorEvent"`
to force grouping for errors without one. Use `UseExceptionHandler` so unhandled exceptions are
logged once, with the trace id, and return a problem-details body with that id for support.

## Request logs vs application logs

Cloud Run writes one entry per request to log name `run.googleapis.com/requests` with
`httpRequest.status`, `latency`, `userAgent`, and the same `trace` field; application output goes
to `run.googleapis.com/stdout` / `stderr`. Logs Explorer shows them interleaved; clicking the
trace id on a request shows the application lines for that request. Useful queries:

```
resource.type="cloud_run_revision" resource.labels.service_name="api" severity>=ERROR
resource.type="cloud_run_revision" logName:"requests" httpRequest.status>=500
resource.type="cloud_run_revision" jsonPayload.InvoiceId="42"
resource.type="cloud_run_revision" trace="projects/my-proj/traces/4bf92f3577b34da6a3ce929d0e0e4736"
```

`gcloud logging read '<filter>' --limit=50 --format=json` gives the same from a terminal; Cloud
Run `--log-http` style debugging is unnecessary once request logs exist.

## Metrics, dashboards, uptime checks

- Built-in Cloud Run metrics cover most needs: `run.googleapis.com/request_count` (by
  `response_code_class`), `request_latencies`, `container/instance_count`,
  `container/cpu/utilizations`, `container/memory/utilizations`,
  `container/startup_latencies`. Cloud SQL: `database/postgresql/num_backends`,
  `database/cpu/utilization`, `database/disk/utilization`, `database/replication/replica_lag`.
- Log-based metrics turn business events into time series without instrumenting code:
  a counter on `jsonPayload.EventName="PaymentFailed"` with a label extracted from
  `jsonPayload.Provider`; a distribution on `jsonPayload.DurationMs` for an internal step. They
  are evaluated at ingestion, so excluded logs never become metrics.
- Custom metrics via OpenTelemetry `Meter` + the Google Cloud exporter when you need histograms
  with high cardinality control; prefer log-based metrics until then.
- One dashboard per service with four rows: traffic and error ratio, latency p50/p95/p99,
  instances and CPU/memory, dependencies (DB connections, Pub/Sub backlog). Store it as
  `google_monitoring_dashboard` with the JSON exported from a hand-built dashboard.
- Uptime checks hit `/health/live` from several regions; they need a reachable URL, so for an
  authenticated service put the check behind the load balancer path or use a synthetic monitor
  with an ID token. Alert on the check failing from two or more regions, not one.

## Alert policies and SLOs

Alert on symptoms with a window long enough to ignore blips. Error-ratio alert in Terraform
(PromQL conditions read best and survive metric renames):

```hcl
resource "google_monitoring_notification_channel" "oncall" {
  display_name = "On-call (PagerDuty)"
  type         = "pagerduty"
  sensitive_labels { service_key = var.pagerduty_key }
}

resource "google_monitoring_alert_policy" "api_5xx_ratio" {
  display_name = "api prod: 5xx ratio > 2% for 5m"
  combiner     = "OR"
  severity     = "ERROR"
  conditions {
    display_name = "5xx ratio"
    condition_prometheus_query_language {
      query = <<-EOT
        sum(rate(run_googleapis_com:request_count{monitored_resource="cloud_run_revision",service_name="api",response_code_class="5xx"}[5m]))
        / sum(rate(run_googleapis_com:request_count{monitored_resource="cloud_run_revision",service_name="api"}[5m])) > 0.02
      EOT
      duration            = "300s"
      evaluation_interval = "60s"
    }
  }
  notification_channels = [google_monitoring_notification_channel.oncall.id]
  alert_strategy { auto_close = "1800s" }
  documentation {
    mime_type = "text/markdown"
    content   = <<-EOT
      Runbook: https://wiki.example.com/runbooks/api-5xx
      First check: `gcloud run revisions list --service=api --region=europe-west1` — was a revision deployed in the last hour? Roll back with `update-traffic --to-revisions`.
    EOT
  }
  user_labels = { env = "prod", service = "api" }
}
```

The same with `gcloud`: `gcloud alpha monitoring policies create --policy-from-file=policy.json`,
where the JSON mirrors the fields above. Add a second policy on `container/memory/utilizations`
> 90% (OOM precursor) and one on Cloud SQL `num_backends` near `max_connections`; keep the paging
set small and route everything else to a chat channel.

SLOs encode the targets and give you burn-rate alerts, which page sooner for fast burns and
tolerate slow drift:

```hcl
resource "google_monitoring_custom_service" "api" { service_id = "api-prod"; display_name = "api (prod)" }

resource "google_monitoring_slo" "api_availability" {
  service             = google_monitoring_custom_service.api.service_id
  slo_id              = "availability-28d"
  display_name        = "99.5% non-5xx over 28 days"
  goal                = 0.995
  rolling_period_days = 28
  request_based_sli {
    good_total_ratio {
      good_service_filter  = "metric.type=\"run.googleapis.com/request_count\" resource.type=\"cloud_run_revision\" resource.label.\"service_name\"=\"api\" metric.label.\"response_code_class\"!=\"5xx\""
      total_service_filter = "metric.type=\"run.googleapis.com/request_count\" resource.type=\"cloud_run_revision\" resource.label.\"service_name\"=\"api\""
    }
  }
}
# burn-rate alert: filter = "select_slo_burn_rate(\"${google_monitoring_slo.api_availability.name}\", \"60m\")", threshold 10 (fast) and 2 over 6h (slow)
```

A latency SLO uses `distribution_cut` on `request_latencies` with `range { max = 500 }` for
"requests under 500 ms". Two SLOs per service are usually enough.

## Tracing with OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("api", serviceVersion: Assembly.GetEntryAssembly()!.GetName().Version!.ToString()))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        .AddNpgsql()
        .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1)))
        .AddOtlpExporter());    // OTEL_EXPORTER_OTLP_ENDPOINT -> collector sidecar or Elastic APM
```

- `ParentBased` honours Cloud Run's sampling decision on the incoming trace so request log,
  application log and trace agree; 10% of the rest is plenty in prod, 100% in dev.
- Export either through an OpenTelemetry Collector sidecar container on the Cloud Run service
  (`googlecloud` exporter, no per-request credentials in the app, also the path to Elastic APM
  by adding a second exporter), or directly with a Google Cloud trace exporter package
  (`OpenTelemetry.Exporter.Stackdriver` from the contrib repository; check the current package
  name, it has moved). The sidecar needs `--no-cpu-throttling` or spans queued after the
  response are never flushed; the collector configuration is in the reference file.
- The service account needs `roles/cloudtrace.agent`; the Npgsql instrumentation needs the
  `Npgsql.OpenTelemetry` package and shows every SQL statement as a span, which is how N+1
  becomes visible in production.

## Retention, sinks, exclusions, cost

- `_Default` log bucket keeps 30 days (free); raise retention per bucket
  (`google_logging_project_bucket_config`, up to 3650 days, billed) only where audit needs it.
  `_Required` keeps admin activity for 400 days and cannot be changed.
- Ingestion is the cost driver (per GiB above the monthly free allowance). Exclude at the
  `_Default` sink: health-check request logs
  (`logName:"requests" httpRequest.requestUrl=~"/health/"`), `severity<INFO` in prod, and chatty
  framework categories. Excluded entries are gone, so never exclude `severity>=WARNING`.
- Sinks (`google_logging_project_sink`, `unique_writer_identity = true`): BigQuery with
  `use_partitioned_tables = true` for analysis and 90-day queries at low cost; Cloud Storage for
  cold compliance archive; Pub/Sub when a third-party stack consumes logs. Grant the sink's
  `writer_identity` the dataset or bucket role, or the sink errors silently (check
  `logging.googleapis.com/exports_error_count`).
- Audit logs: enable Data Access logs only for the services where you need them (Cloud SQL,
  Secret Manager); they are large.

## Exporting to Elastic / Kibana

When the organisation already runs Elastic, keep Cloud Logging as the system of record (it is
where Cloud Run, Error Reporting and alerts live) and forward a filtered subset:
sink → Pub/Sub topic → Elastic Agent with the GCP integration (Pub/Sub input) or the Google
Dataflow "Pub/Sub to Elasticsearch" template. Filter the sink to `severity>=INFO` application
logs and request logs with status >= 400 to keep both bills sane. For traces and metrics, point
the OTLP exporter at the Elastic APM Server endpoint with the `Authorization: Bearer <token>`
header (`OTEL_EXPORTER_OTLP_HEADERS`), or add an `otlp` exporter next to `googlecloud` in the
collector sidecar to send both. Keep field names GCP-shaped in the app; map `severity` to
`log.level` in the Elastic ingest pipeline rather than emitting two formats.

## Runbooks and on-call

Each paging alert links to a runbook with: what the alert means for users; the dashboard to
open; the first three checks (recent revision? `gcloud run revisions list`; dependency down?
Cloud SQL and Pub/Sub dashboards; error pattern? Error Reporting); the rollback command; who to
escalate to and when. Severity levels: SEV1 user-facing outage (page, rollback first, diagnose
after), SEV2 degraded (page in hours, fix forward), SEV3 (ticket). Every SEV1/2 gets a blameless
post-incident note with the timeline from logs, the trace id of a failing request, and one
action item that would have caught it earlier (usually a probe, an alert or a log line).

## Examples

**Example 1**
Input: "Our errors show up as plain text with no level and I cannot filter them."
Output: the `GcpJsonConsoleFormatter` registered as the only logging provider, `severity` mapped
from `LogLevel`, `logging.googleapis.com/trace` from `Activity.Current`, exception text in
`message`, `GOOGLE_CLOUD_PROJECT` set on the service, and the query
`severity>=ERROR resource.labels.service_name="api"` to confirm.

**Example 2**
Input: "Page us when the API is unhealthy, but not for every single 500."
Output: an availability SLO (99.5%, 28 days) on non-5xx request ratio with fast (1h, burn rate
10) and slow (6h, burn rate 2) burn-rate alert policies to the PagerDuty channel, a 5xx-ratio
warning to the team chat channel, and a runbook link in each policy's `documentation`.

## References

- `references/dotnet-logging-and-tracing.md` — read when implementing the JSON console
  formatter, the exception handler that returns the trace id, the OpenTelemetry setup with the
  collector sidecar on Cloud Run, or the Cloud Run multi-container service YAML.
