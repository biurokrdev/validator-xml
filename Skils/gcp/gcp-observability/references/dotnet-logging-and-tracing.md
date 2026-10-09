# .NET logging and tracing for Cloud Run

Read this for the complete code behind the `gcp-observability` skill: the JSON console
formatter, the exception handler, OpenTelemetry wiring and the collector sidecar.

## GcpJsonConsoleFormatter

No extra packages; works with `Microsoft.Extensions.Logging` as shipped in ASP.NET Core 8.

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

public sealed class GcpJsonConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "gcp";
    private readonly string? _projectId;

    public GcpJsonConsoleFormatter(IOptionsMonitor<ConsoleFormatterOptions> _) : base(FormatterName)
        => _projectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = entry.Formatter(entry.State, entry.Exception);
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("severity", ToSeverity(entry.LogLevel));
            json.WriteString("message", entry.Exception is null ? message : $"{message}\n{entry.Exception}");
            json.WriteString("time", DateTimeOffset.UtcNow.ToString("O"));
            json.WriteString("logger", entry.Category);
            if (entry.EventId.Id != 0) json.WriteNumber("eventId", entry.EventId.Id);

            if (Activity.Current is { } activity && _projectId is not null)
            {
                json.WriteString("logging.googleapis.com/trace", $"projects/{_projectId}/traces/{activity.TraceId}");
                json.WriteString("logging.googleapis.com/spanId", activity.SpanId.ToString());
                json.WriteBoolean("logging.googleapis.com/trace_sampled", activity.Recorded);
            }

            if (entry.Exception is not null)
            {
                json.WriteString("@type", "type.googleapis.com/google.devtools.clouderrorreporting.v1beta1.ReportedErrorEvent");
                json.WriteString("exceptionType", entry.Exception.GetType().FullName);
            }

            WriteProperties(json, entry.State);
            scopeProvider?.ForEachScope((scope, writer) => WriteProperties(writer, scope), json);

            json.WriteEndObject();
        }
        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    private static void WriteProperties(Utf8JsonWriter json, object? state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> props) return;
        foreach (var (key, value) in props)
        {
            if (key == "{OriginalFormat}") continue;
            switch (value)
            {
                case null: json.WriteNull(key); break;
                case string s: json.WriteString(key, s); break;
                case int i: json.WriteNumber(key, i); break;
                case long l: json.WriteNumber(key, l); break;
                case double d: json.WriteNumber(key, d); break;
                case decimal m: json.WriteNumber(key, m); break;
                case bool b: json.WriteBoolean(key, b); break;
                case Guid g: json.WriteString(key, g); break;
                case DateTimeOffset dto: json.WriteString(key, dto); break;
                default: json.WriteString(key, value.ToString()); break;
            }
        }
    }

    private static string ToSeverity(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "DEFAULT",
    };
}
```

Registration in `Program.cs`:

```csharp
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);   // readable locally
}
else
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.FormatterName = GcpJsonConsoleFormatter.FormatterName);
    builder.Logging.AddConsoleFormatter<GcpJsonConsoleFormatter, ConsoleFormatterOptions>();
}
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
```

Use scopes for request-wide context so every line carries it without repeating parameters:

```csharp
app.Use(async (ctx, next) =>
{
    var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
    using (logger.BeginScope(new Dictionary<string, object?>
    {
        ["TenantId"] = ctx.User.FindFirstValue("tenant"),
        ["RequestId"] = ctx.TraceIdentifier,
    }))
    {
        await next();
    }
});
```

## Exception handler that returns the trace id

```csharp
app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<IExceptionHandlerFeature>();
    var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
    var traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;

    logger.LogError(feature?.Error, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = 500,
        Title = "An unexpected error occurred.",
        Extensions = { ["traceId"] = traceId },
    });
}));
```

The log line above is the one Error Reporting groups (severity `ERROR`, stack trace in
`message`); support staff paste `traceId` into Logs Explorer and see request log, application
lines and the trace.

## OpenTelemetry setup

Packages: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`,
`OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`,
`Npgsql.OpenTelemetry`.

```csharp
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(serviceName: "api", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString())
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName,
            ["cloud.provider"] = "gcp",
            ["service.instance.id"] = Environment.GetEnvironmentVariable("K_REVISION") ?? Environment.MachineName,
        }));

otel.WithTracing(t => t
    .AddAspNetCoreInstrumentation(o =>
    {
        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
        o.RecordException = true;
    })
    .AddHttpClientInstrumentation()
    .AddNpgsql()
    .AddSource("MyApp")                         // your own ActivitySource
    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(
        builder.Environment.IsProduction() ? 0.1 : 1.0)))
    .AddOtlpExporter());                        // reads OTEL_EXPORTER_OTLP_ENDPOINT / _HEADERS / _PROTOCOL

otel.WithMetrics(m => m
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddRuntimeInstrumentation()
    .AddOtlpExporter());
```

Cloud Run sets `K_SERVICE`, `K_REVISION` and `K_CONFIGURATION`; using the revision as the
instance id makes a bad revision visible in trace and metric filters. Custom spans:

```csharp
private static readonly ActivitySource Source = new("MyApp");

using var activity = Source.StartActivity("ValidateXml");
activity?.SetTag("xml.schema", schemaName);
activity?.SetTag("xml.size_bytes", length);
```

## Collector sidecar on Cloud Run

`otel-collector.yaml` (baked into a small image based on
`otel/opentelemetry-collector-contrib`):

```yaml
receivers:
  otlp:
    protocols:
      grpc: { endpoint: 0.0.0.0:4317 }
      http: { endpoint: 0.0.0.0:4318 }
processors:
  batch: { timeout: 5s, send_batch_size: 200 }
  resourcedetection: { detectors: [gcp], timeout: 5s }
exporters:
  googlecloud:
    project: my-proj
  # Optional second destination when the organisation runs Elastic APM:
  # otlp/elastic:
  #   endpoint: https://apm.example.com:443
  #   headers: { Authorization: "Bearer ${env:ELASTIC_APM_TOKEN}" }
service:
  pipelines:
    traces:  { receivers: [otlp], processors: [resourcedetection, batch], exporters: [googlecloud] }
    metrics: { receivers: [otlp], processors: [resourcedetection, batch], exporters: [googlecloud] }
```

Multi-container service definition (`gcloud run services replace service.yaml`; Terraform's
`google_cloud_run_v2_service` supports multiple `containers` blocks the same way):

```yaml
apiVersion: serving.knative.dev/v1
kind: Service
metadata:
  name: api
  annotations:
    run.googleapis.com/launch-stage: GA
spec:
  template:
    metadata:
      annotations:
        run.googleapis.com/container-dependencies: '{"api":["otel-collector"]}'
        run.googleapis.com/cpu-throttling: "false"
    spec:
      serviceAccountName: api-sa@my-proj.iam.gserviceaccount.com
      containers:
        - name: api
          image: europe-west1-docker.pkg.dev/my-proj/apps/api:abc123
          ports: [{ containerPort: 8080 }]
          env:
            - { name: OTEL_EXPORTER_OTLP_ENDPOINT, value: "http://localhost:4317" }
            - { name: OTEL_EXPORTER_OTLP_PROTOCOL, value: "grpc" }
            - { name: GOOGLE_CLOUD_PROJECT, value: "my-proj" }
        - name: otel-collector
          image: europe-west1-docker.pkg.dev/my-proj/apps/otel-collector:1.0
          resources: { limits: { cpu: "0.5", memory: "256Mi" } }
```

The `container-dependencies` annotation starts the collector before the API; `cpu-throttling:
false` keeps CPU allocated so the batch processor can flush after responses. The service account
needs `roles/cloudtrace.agent` and `roles/monitoring.metricWriter`. For a lighter setup without
a sidecar, replace `AddOtlpExporter()` with the Google Cloud exporter package and accept that
spans are flushed in-process (set `--no-cpu-throttling` or export synchronously with a small
batch delay).

## Log-based metric and exclusion in Terraform

```hcl
resource "google_logging_metric" "payment_failures" {
  name   = "payment_failures"
  filter = "resource.type=\"cloud_run_revision\" jsonPayload.EventName=\"PaymentFailed\""
  metric_descriptor {
    metric_kind = "DELTA"
    value_type  = "INT64"
    labels { key = "provider"; value_type = "STRING" }
  }
  label_extractors = { provider = "EXTRACT(jsonPayload.Provider)" }
}

resource "google_logging_project_exclusion" "health_checks" {
  name   = "exclude-health-check-requests"
  filter = "resource.type=\"cloud_run_revision\" logName:\"requests\" httpRequest.requestUrl=~\"/health/\" severity<WARNING"
}

resource "google_logging_project_sink" "to_bigquery" {
  name                   = "app-logs-to-bq"
  destination            = "bigquery.googleapis.com/projects/${var.project_id}/datasets/app_logs"
  filter                 = "resource.type=\"cloud_run_revision\" severity>=INFO"
  unique_writer_identity = true
  bigquery_options { use_partitioned_tables = true }
}

resource "google_bigquery_dataset_iam_member" "sink_writer" {
  dataset_id = google_bigquery_dataset.app_logs.dataset_id
  role       = "roles/bigquery.dataEditor"
  member     = google_logging_project_sink.to_bigquery.writer_identity
}
```
