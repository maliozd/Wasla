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

### Authentication rate-limit rejections

Login and credential-check attempts over their limit ([../architecture/authentication.md](../architecture/authentication.md#credential-rate-limiting)) are logged by Web (default host logging) and Api (Serilog) under the category `Wasla.Security.AuthenticationRateLimiting`:

| Level | Message | When |
|-------|---------|------|
| Warning | `Authentication rate limit reached; rejecting requests until the window resets.` | First rejection of a partition in its window |
| Debug | `Authentication rate limit rejected another request.` with `RejectedInWindow` | Every further rejection in that window |

Fields: `Policy` (`auth-tenant-login`, `auth-central-admin-login`, `auth-api-credential-validation`, `signup-submission`, `signup-checkout`), `TenantId` (the resolved tenant for tenant policies, empty otherwise), `ClientTag` and `RetryAfterSeconds`. `ClientTag` is 12 hex characters derived from the client network with a random per-process key. It groups one client's rejections within one process, but it cannot be turned back into an address and differs between processes and restarts. Nothing else is logged: no address, partition key, email, password, cookie or header.

Reading them: many Warnings with different `ClientTag`s for one `TenantId` point to an attack spread over many networks, which these limits do not stop (no per-account backoff yet). Warnings for one tag across many tenants point to one network trying many tenants. A sudden rise right after a reverse proxy was added usually means `TrustedProxies` is missing ([deployment.md](deployment.md#reverse-proxy-and-client-address)). At startup each process also logs whether trusted proxies are configured.

A signup refused by the per-email cap logs Information `Pending registration refused: the owner email has the maximum number of active registrations.` without the email. Tenant resolution logs Information `No tenant for host {Host}` once per lookup, so with the unknown-host cache an unknown host appears at most once per TTL, not once per request ([../architecture/tenancy.md](../architecture/tenancy.md#unknown-host-cache)).

### Live notification metrics

The live channel ([../architecture/realtime.md](../architecture/realtime.md)) records metrics with the .NET metrics API
(meter **`Wasla.Realtime`**, `RealtimeMetrics` in `src/Wasla.Infrastructure/Realtime/`), added in WAS-25. Wasla.Web
records the hub, connection, session-check, listener and Origin instruments; Wasla.Worker and Wasla.Api record the
internal channel's sends. Nothing is exported: read them on the server with `dotnet-counters` (below).

| Instrument | Type, unit | Tags | Meaning |
|------------|-----------|------|---------|
| `wasla.realtime.connections.active` | up-down counter, `{connection}` | `wasla.realtime.streams` | Open hub connections whose groups are joined (Web) |
| `wasla.realtime.connection.attempts` | counter, `{connection}` | `wasla.realtime.result`, `wasla.realtime.attempt` | Authenticated connections that reached the hub: `admitted`, `refused` (no stream for the role), `failed` (joining failed). `attempt` is what the browser client declares in the hub URL: `initial`, `reconnect` (after a drop, a failed start or a back/forward-cache restore) or `unknown`; a diagnostic label only, it decides nothing |
| `wasla.realtime.connection.closures` | counter, `{connection}` | `wasla.realtime.reason` | Admitted connections that ended: `closed` (client stopped, page closed, cookie expired, or the server's client timeout), `error` (ended with an exception), or the session check's reason: `session_invalid`, `role_changed`, `tenant_unresolved`, `check_unavailable`, `check_failed`. Opened minus closed equals active |
| `wasla.realtime.session_checks` | counter, `{check}` | `wasla.realtime.result` | Periodic checks of open connections (every `Realtime:SessionRevalidationSeconds`): `valid` or the closure reasons above |
| `wasla.realtime.session_check.duration` | histogram, `s` | `wasla.realtime.result` | Time of one check (host resolution and one `AppUsers` read) |
| `wasla.realtime.notifications` | counter, `{notification}` | `wasla.realtime.channel`, `wasla.realtime.stream`, `wasla.realtime.result` | Notifications handed on after a commit. Channel `hub` (Web sends to the tenant's group): `sent`, `timeout` (the 2 s bound), `cancelled`, `failed`, `unsupported`. Channel `internal` (Worker or Api posts to Web): `sent`, `timeout`, `cancelled`, `rejected` (Web answered an error, for example a wrong secret), `unreachable`, `failed`, `paused` (dropped during the 30 s pause), `unsupported` |
| `wasla.realtime.notification.duration` | histogram, `s` | as above | Time to hand on one notification: the hub's group send in Web, or the internal request until Web acknowledged it (which includes Web's hub send). Recorded for attempted sends only |
| `wasla.realtime.internal.pauses` | counter, `{pause}` | `wasla.realtime.reason` | Times the internal sender started a 30 s pause, by the failure that caused it (`timeout`, `rejected`, `unreachable`, `cancelled`, `failed`) |
| `wasla.realtime.internal.requests` | counter, `{request}` | `wasla.realtime.result` | Requests to Web's loopback listener: `accepted`, `unauthorized` (missing or wrong secret), `invalid`, `not_found`, `method_not_allowed` |
| `wasla.realtime.origin_refusals` | counter, `{request}` | none | Hub requests refused because their `Origin` is another host |

Tag values: `wasla.realtime.channel` is `hub` or `internal`; `wasla.realtime.stream` is `print_status`,
`live_screen` or `other`; `wasla.realtime.streams` is `print_status`, `live_screen` or `live_screen+print_status`.
Every tag has only the values listed here. No metric carries a tenant, user, order, job or connection id, a host name,
a URL, an exception message or a secret; `RealtimeMetricsTests` checks this. The existing warnings (failed sends, the
internal pause, refused secrets, closed connections) keep their `TenantId` field where they had it, and never log the
secret.

**Delivery lag.** `wasla.realtime.notification.duration` is the send time on the server, not the time until a screen
shows the change: that also includes the network, the browser's coalescing (up to about 1 s, see
[../architecture/realtime.md](../architecture/realtime.md#live-screen-invalidation-was-24)) and the snapshot read.
Notifications carry no send time, because restaurant devices' clocks are not synchronized with the server and the
difference would be measured mostly as skew; `version` is an ordering value, never a time. End-to-end lag is measured
in the browser tests, where the server and the browser share one clock. Snapshot reads themselves show in ASP.NET
Core's `http.server.request.duration` for the route of `/orders/live-data`.

**ASP.NET Core's own metrics** cover the transport: meter `Microsoft.AspNetCore.Http.Connections` has
`signalr.server.active_connections` and `signalr.server.connection.duration` (by `signalr.transport`, which also shows
how many clients fall back to Server-Sent Events or long polling), and `Microsoft.AspNetCore.Hosting` has
`http.server.request.duration` (negotiate refusals appear there as 401 or 403 on `/hubs/tenant-events/negotiate`).

**Reading them.** On the server, as the account that runs the process, with the .NET diagnostics tool (no
application change, no open port):

```text
dotnet tool install --global dotnet-counters
dotnet-counters ps
dotnet-counters monitor --name Wasla.Web --counters Wasla.Realtime,Microsoft.AspNetCore.Http.Connections,System.Runtime
dotnet-counters monitor --name Wasla.Worker --counters Wasla.Realtime
dotnet-counters collect --name Wasla.Web --counters Wasla.Realtime,Microsoft.AspNetCore.Http.Connections --format csv --output realtime.csv
```

`monitor` shows counters as rates and histograms as percentiles; `collect` writes them to a file for a busy period.
The thresholds that should start multi-instance work are in
[../architecture/realtime-scale-out.md](../architecture/realtime-scale-out.md#10-thresholds-that-trigger-the-scale-out-work).

## Explicit gaps

These are **not** implemented:

- **No OpenTelemetry exporter** (no OTLP/Jaeger/Zipkin wiring in source)
- **No metrics exporter or dashboards.** The only Wasla meter is `Wasla.Realtime` ([above](#live-notification-metrics)), read on the server with `dotnet-counters`
- **Worker has no HTTP health endpoint**

Do not invent exporters or scrape endpoints that are not in source.

## Production error page (Web)

Outside Development, Web uses `UseExceptionHandler("/error")` (`WaslaExceptionHandlingExtensions`). Development keeps the developer exception page.

## Related docs

- [deployment.md](deployment.md)
- [../orders/synchronization.md](../orders/synchronization.md)
- [local-development.md](local-development.md)
