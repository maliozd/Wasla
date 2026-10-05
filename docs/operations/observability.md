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

## Explicit gaps

These are **not** implemented:

- **No OpenTelemetry exporter** (no OTLP/Jaeger/Zipkin wiring in source)
- **No metrics system** (no meters/dashboards as a first-class product feature)
- **Worker has no HTTP health endpoint**

Do not invent exporters or scrape endpoints that are not in source.

## Production error page (Web)

Outside Development, Web uses `UseExceptionHandler("/error")` (`WaslaExceptionHandlingExtensions`). Development keeps the developer exception page.

`/error` answers 500, except for `CentralAdminSessionUnavailableException` (a Central Admin session could not be validated because CentralDb is unavailable), which answers 503. See [Central admin session revalidation](../architecture/authentication.md#central-admin-session-revalidation).

## Related docs

- [deployment.md](deployment.md)
- [../orders/synchronization.md](../orders/synchronization.md)
- [local-development.md](local-development.md)
