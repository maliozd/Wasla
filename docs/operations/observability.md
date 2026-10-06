# Observability

## Purpose and scope

This document describes what Wasla currently logs and exposes for operations diagnosis: trace ids, tenant context, request completion logging, health endpoints, Worker cycle noise, and provider failure log shape.

Much of the HTTP diagnostics / health wiring is **currently uncommitted in the working tree** (`src/Wasla.Infrastructure/Diagnostics/`, Web `ErrorController` / exception handling, API/Worker `Program.cs` adjustments). Documented behavior matches that source.

It does **not** describe a full APM product.

## Current capabilities

### Trace identity

- Prefer W3C `Activity.Current.TraceId` when a non-empty activity exists.
- Fallback: `HttpContext.TraceIdentifier`.
- Response header: **`X-Trace-Id`** (`RequestDiagnosticsMiddleware.TraceHeaderName`).
- Production Web `/error` page shows the trace id for support correlation.

Sources: `RequestLogState`, `RequestDiagnosticsMiddleware`, `Wasla.Web/Controllers/ErrorController.cs`.

### Tenant diagnostic context

After successful tenant resolution, middleware calls `TenantDiagnosticContext.Apply`:

- Sets Activity tag `tenant.id`
- Sets `TenantId` on the request log state
- Begins an `ILogger` scope with `TenantId`

Sources: Web/API `TenantResolutionMiddleware`, `TenantDiagnosticContext`.

Worker per-tenant sync scopes `TraceId` and `TenantId` around `Activity("Wasla.OrderSync")`.

### Request completion logging

| Host | Behavior |
|------|----------|
| **Web** | `RequestDiagnosticsMiddleware` logs completion by default (`RequestDiagnosticsOptions.LogRequestCompletion` default `true`). Path only (no query string in the template). 5xx → Warning; selected noisy paths → Debug. |
| **API** | Serilog request logging enabled with `IncludeQueryInRequestPath = false`. Middleware completion logging is **disabled** (`LogRequestCompletion = false`) to avoid double lines. Template includes `TraceId` and `TenantId`. |

### Serilog file sinks

| Process | Rolling file |
|---------|--------------|
| API | `logs/wasla-api-.log` |
| Worker | `logs/wasla-worker-.log` |

Shared console/file template includes `TraceId` and `TenantId` placeholders (`WaslaLogOutput.Template`).

### Health endpoints (Web and API)

Mapped by `WaslaHealthCheckExtensions` (currently uncommitted Diagnostics):

| Path | Meaning |
|------|---------|
| `/health/live` | Process liveness only (no dependency checks) |
| `/health/ready` | Readiness checks tagged `ready` |

Registered ready check: **`central-db`** (`CentralDatabaseHealthCheck`).

### Worker cycle logging

Quieter non-Development behavior (currently uncommitted Worker changes):

- Cycle start → Debug
- Cycle completion → Debug, or Warning if any connection failed
- Development still uses `WorkerConsole` summaries

### Sync failure vs host cancellation

When the Worker stopping token cancels work, `OrderSyncService` rethrows `OperationCanceledException` and does **not** persist that as a connection sync failure / circuit open.

### Provider failure logs

Trendyol GO / Yemeksepeti real clients log provider failures with provider name, operation, status code, and elapsed ms. They do **not** log response bodies.

### Order sync recovery diagnostics

Trendyol GO polling resumes from a per-connection checkpoint, `PlatformConnections.LastSuccessfulSync` in the tenant database (see [../orders/synchronization.md](../orders/synchronization.md#checkpoint-and-outage-recovery)). To tell whether a connection is current, catching up or stuck:

| Observation | Meaning |
|-------------|---------|
| `LastSuccessfulSync` within about one sync interval of now | Current. |
| Information `Order history is behind; fetching the current window first`, `SyncLogs` rows with `Success`, checkpoint moving forward by up to 11 hours per cycle, then `Order history recovered` with `LastSuccessfulSync` back at the current boundary in that same cycle | Catching up after an outage. New orders keep arriving through the current (hot) window; older changes appear as history recovery reaches them. |
| Error `Platform connection sync failed`, `SyncLogs` rows with `Failed`, checkpoint not moving | Stuck on one window. `Pass` says whether it was the current window or history, `WindowStartUtc` / `WindowEndUtc` name the window, `IntegrationErrors.ErrorType` the exception type, and `CheckpointUtc` the point the next attempt resumes from (minus the five-minute overlap). After five consecutive failures the circuit opens for five minutes. |
| Warning `Provider throttled the request; retrying the same page` | Trendyol GO answered HTTP 429. All Trendyol GO polling in the Worker pauses for `RetryDelayMs`. Frequent 429s mean the shared 40-per-10-seconds budget is above the provider's real limit for this traffic (see [../orders/synchronization.md](../orders/synchronization.md#capacity-model)). |

These logs and rows carry ids, time boundaries, counts and exception types only. They contain no credentials, `Authorization` header, executor e-mail, customer details or provider payloads. Do not add those when diagnosing; ask for the window boundaries and connection id instead.

## Explicit gaps

These are **not** implemented:

- **No OpenTelemetry exporter** (no OTLP/Jaeger/Zipkin wiring in source)
- **No metrics system** (no meters/dashboards as a first-class product feature)
- **Worker has no HTTP health endpoint**

Do not invent exporters or scrape endpoints that are not in source.

## Production error page (Web)

Outside Development, Web uses `UseExceptionHandler("/error")` (`WaslaExceptionHandlingExtensions`). Development keeps the developer exception page.

## Related docs

- [deployment.md](deployment.md)
- [../orders/synchronization.md](../orders/synchronization.md)
- [local-development.md](local-development.md)
