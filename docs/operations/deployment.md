# Deployment

## Purpose and scope

This document lists what must be configured to run Wasla in a non-local environment: processes, databases, secrets, domains, provider mode, Print Bridge, logging, and health endpoints.

It corrects stale names from older checklists (e.g. treating `BaseUrl` as the Print Bridge setup field, or the former product name as the current one). It does **not** embed real secrets or placeholder passwords.

Older matrix: `docs/deployment-config.md` (superseded for day-to-day ops; not deleted).

## Processes

| Process | Role |
|---------|------|
| `Wasla.Web` | MVC/Razor UI, tenant resolution, signup/admin surfaces, Print Bridge HTTP APIs (where hosted) |
| `Wasla.Api` | JSON API, Print Bridge APIs, Swagger in Development |
| `Wasla.Worker` | Background order synchronization (must keep running) |
| SQL Server | CentralDb + one database per tenant |
| `Wasla.PrintBridge` | Windows desktop client on restaurant PCs (not a server deployable in the same sense) |

Web must **not** start/stop Worker OS processes, Windows services, Docker containers, or systemd units.

### Web content root and web root

Start `Wasla.Web` with its content root set to the folder that contains its `wwwroot`: the publish folder for a published build (run it from that folder or pass `--contentRoot`), the project folder for `dotnet run --project src/Wasla.Web`. ASP.NET Core leaves `WebRootPath` null when the content root has no `wwwroot`, and then serves no CSS, scripts or notification sounds, except in Development, where the static web assets manifest still maps the project's `wwwroot` (for example when the binary is started from `bin/`). Notification settings read the sound list through the same web root file provider and fall back to the default `/sounds/<name>.mp3` entries instead of failing (WAS-122); the files themselves are only served when the web root is correct.

## Databases

- **CentralDb**: tenant registry, memberships, signup/provisioning, central admins, Print Bridge device registry. Connection: `ConnectionStrings:CentralDb` / `ConnectionStrings__CentralDb`.
- **Per-tenant DBs**: operational data for one restaurant location. Connection strings stored encrypted on the tenant row; decrypted with `ENCRYPTION_MASTER_KEY`. Several locations are several tenants. See [../architecture/tenancy.md](../architecture/tenancy.md).
- CLI provisioning and Central Admin provisioning use `TenantDb:ServerInstance` (`TenantDb__ServerInstance`) when creating tenant databases.
- Tenant databases are created only on an explicitly configured SQL Server (WAS-93): `--sql-server`, then `TenantDb:ServerInstance` (WAS-136). No other key is read. There is no built-in default and the committed base `appsettings.json` files name none (LocalDB is set only in the Development files), so a deployment that sets neither cannot provision: Central Admin and `provision-signup-request` report "No SQL Server instance is configured" before any database work. Before WAS-93 the CLI's base `appsettings.json`, loaded after environment variables, replaced the environment's tenant server with LocalDB.
- The CLI's environment variables override its JSON files, and its base `appsettings.json` no longer names a CentralDb: set `ConnectionStrings__CentralDb` for every CLI run outside Development ([cli.md](cli.md#configuration-sources)). Before WAS-93 the CLI's base file supplied LocalDB and also overrode `ConnectionStrings__CentralDb` from the environment.

Apply schema with CLI — see [migrations.md](migrations.md). Provisioning applies tenant migrations inside provisioning; not via middleware.

## Release order: migrate every database first

Web, Worker and Api share one EF model. Tenant migrations run only through the CLI (or inside provisioning), never at application startup. A new binary started against a tenant database that is missing a column or table fails on that tenant: for example, order auto-approve, receipt creation and manual print all load `TenantOperationalSettings` rows and break when a new column is missing.

For every release that contains a CentralDb or TenantDb migration:

1. Stop, or keep on the previous version, every Web, Worker and Api instance.
2. Run `migrate-central`, then `migrate-all-customers`.
3. If either command exits non-zero, **stop the rollout**. `migrate-all-customers` continues past a failed tenant and then exits `1`; fix the failed tenants and rerun until it exits `0`.
4. Confirm with `migration-status` that every active tenant shows `Pending: 0`.
5. Only then deploy and start the new Web, Worker and Api versions.

Never start the new Web or Worker against partially migrated tenants. The application does not check this itself; the release procedure must enforce it.

Example: `AddAppUserLastLoginAt` (WAS-46) adds the nullable `AppUsers.LastLoginAt` column. Tenant login reads only the columns it needs, and a failed last-login write is logged without blocking the login, so a tenant that missed the migration can still sign in. That is a safety margin, not a reason to change the order: the Users page, user management and password reset read full `AppUsers` rows and fail on that tenant until `migrate-all-customers` has run.

Example without that margin: `AddAppUserSecurityStamp` (WAS-89) adds `AppUsers.SecurityStamp` and gives every existing user a new value. Tenant login and the check every tenant request makes against its session read that column, so on a tenant that missed the migration no one can sign in and every signed-in request fails closed with the error page until `migrate-all-customers` has run. Sessions issued before the release carry no stamp, so every tenant user signs in again once. See [../architecture/authentication.md](../architecture/authentication.md#tenant-session-revalidation).

For this release, stop the previous Web version before `migrate-all-customers` runs (do not use the "keep on the previous version" option of step 1 for Web), and never run it next to the new Web. The previous version changes passwords and active states without replacing the stamp, so a password it changes, or a user it deactivates and reactivates, keeps that user's sessions on the new Web valid. A user it creates after the migration gets the column's all-zero default stamp, which never opens a session, so that user cannot sign in until their password is reset or changed on the new version.

Example: `AddOrderItemSequence` (WAS-44) adds `OrderItems.Sequence` and `OrderItemOptions.Sequence` (`int`, default 0) and backfills nothing. Order sync, order details, the Orders list, the Live Screen snapshot, receipt creation and reprints all use these columns. On a tenant that missed the migration, those fail until `migrate-all-customers` has run. Existing rows keep 0 and are shown in the fallback order (stored time, then Id) described in [../frontend/design-system.md](../frontend/design-system.md#tables-and-sorting). Order sync compares item order, so on its first poll after the release, every order the provider still returns whose fallback order differs from the provider's is updated once. That is one write and one live notification per order, which stores its positions; no receipt is created. The column has a database default, so a previous build that does not know it can still write items to a migrated tenant. That build compares items without their order and writes positions as 0, so do not run it next to the new Worker.

Example in CentralDb: `AddCentralAdminSecurityStamp` (WAS-37) adds `CentralAdminUsers.SecurityStamp` and gives every existing central admin a new value. Admin login and the check every Central Admin request makes read that column, so a new Web started before `migrate-central` cannot sign in central admins, and every signed-in Admin request fails closed with the error page. Central Admin cookies issued before the release carry no stamp, so every central admin signs in again once. After `migrate-central`, reset central admin passwords only with the new CLI: an older CLI changes the hash without replacing the stamp, so the admin's existing sessions would survive that reset. See [../architecture/authentication.md](../architecture/authentication.md#central-admin-session-revalidation).

## Required secrets and keys

| Item | Used by | Notes |
|------|---------|-------|
| `ENCRYPTION_MASTER_KEY` | Web, Worker, API, most CLI | Base64 32-byte key. Required at startup for secret decrypt. Never commit. |
| Data Protection key folder | Web, API | Persist ASP.NET Data Protection keys across restarts/instances. |

### Data Protection key folder (API)

No committed configuration sets `DataProtection:KeyPath` for the API. When it is unset the API keeps its keys in `C:\Wasla-keys` on Windows and uses the framework's default key storage on other operating systems (`src/Wasla.Api/Security/ApiDataProtection.cs`, WAS-141). Set `DataProtection__KeyPath` explicitly in real deployments.

Web persists keys only when `DataProtection:KeyPath` is set.

### Auth nuance for deployers

API registers the same tenant cookie scheme as Web. Do **not** assume a Web login session cookie automatically authorizes API requests across hosts: the cookie is host-only, and by default the two processes have different Data Protection keys. Plan API access separately from Web sessions.

Sharing the key ring, by pointing both `DataProtection__KeyPath` values at one folder, is allowed. When the keys are shared, any Web tenant cookie is readable by the API. Since WAS-94 the API revalidates such a session exactly as Web does (current user, active state, security stamp, role and the tenant from the host) and applies the same role policies; see [../architecture/authentication.md](../architecture/authentication.md#when-a-web-session-reaches-the-api).

Two consequences for a release or a rollback:

- **Never run a pre-WAS-94 API with keys shared with Web.** That API accepts a deactivated, demoted or password-reset user's old Web cookie and has no role checks on its writes. Rolling the API back to such a build restores that exposure. If the API must be rolled back, give it its own key folder first.
- **The API now reads `AppUsers.SecurityStamp` on every cookie-authenticated request.** Against a tenant database that misses the `AddAppUserSecurityStamp` migration, those requests fail closed with 503. Requests without a tenant cookie, and Print Bridge device requests, are not affected. The [release order](#release-order-migrate-every-database-first) already covers this.

Central admins are created with CLI `add-central-admin`, not `CentralAdmin__Email` / `CentralAdmin__PasswordHash`.

## Domains and HTTPS

- Public / marketing host and `{slug}.<MarketingBaseDomain>` tenant hosts.
- Onboarding settings are in `Wasla:TenantOnboarding`: `MarketingBaseDomain`, `SqlAuth` (`trusted` or `sql:username:password`), optional `TrialDays`, `PendingRegistrationExpiryDays` and `MaxActivePendingRegistrationsPerOwnerEmail`. The tenant SQL Server is `TenantDb:ServerInstance` (see [Databases](#databases)).
- Production should force HTTPS (Web enables HSTS + HTTPS redirection outside Development).
- Print Bridge and browsers need a trusted certificate chain on real domains.

Useful URL overrides (env `__` form):

| Variable | Role |
|----------|------|
| `Wasla__ApiBaseUrl` | Public API URL for Print Bridge setup helpers (committed as `http://localhost:59451`; no current page reads it) |
| `Wasla__TenantWebBaseUrl`, else `Wasla__PublicWebBaseUrl` | Optional fixed Web URL that the Print Bridge setup page hands out as `ServerUrl` behind proxies; without either it uses the request's scheme and host |

Do not leave production pointing at `localhost` or LocalDB.

## Reverse proxy and client address

The repository contains no reverse-proxy configuration. Web and Api take the client address from the TCP connection unless the proxies in front of them are listed under `TrustedProxies` (`src/Wasla.Infrastructure/Security/TrustedProxies.cs`, WAS-90). Login rate limits are partitioned by that address ([../architecture/authentication.md](../architecture/authentication.md#credential-rate-limiting)), so this setting decides whether clients are told apart.

| Key | Meaning |
|-----|---------|
| `TrustedProxies:KnownProxies` | Proxy addresses, for example `127.0.0.1` (and `::1` if it connects over IPv6) for a proxy on the same server |
| `TrustedProxies:KnownNetworks` | Proxy networks in CIDR form, for example `10.0.0.0/24` |
| `TrustedProxies:ForwardLimit` | Proxy hops to read from the right of `X-Forwarded-For`; default `1` (one proxy) |

With no entries the forwarded-headers middleware is not added at all: `X-Forwarded-For` and `X-Forwarded-Proto` are ignored, and a client cannot choose its address by sending them. With entries, both headers are applied only when the connecting peer is one of them. The configured list replaces ASP.NET Core's loopback defaults. `X-Forwarded-Host` is never applied: tenants are resolved from the `Host` header, which the proxy must pass through unchanged.

The proxy must append the address it saw, for example nginx `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;` (or overwrite the header with `$remote_addr`), and send `X-Forwarded-Proto` when it terminates TLS. Otherwise HTTPS redirection and `Request.IsHttps` checks (for example the Print Bridge setup exchange) see plain HTTP. With `ForwardLimit` 1 only the rightmost entry counts, so whatever a client put in front of it is ignored.

The process refuses to start when:

- an entry does not parse, is an unspecified address (`0.0.0.0`, `::`), or is a `/0` network (every address);
- `ForwardLimit` is outside 1–10;
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is `true`. That switch makes ASP.NET Core trust forwarded headers from every peer. List the proxies instead.

At startup each process logs either "No trusted proxies configured…" or the number of trusted proxies and networks.

If a proxy is deployed but not listed, every request appears to come from the proxy. All clients of a tenant then share one login allowance, which can lock legitimate users out for a window. Configure `TrustedProxies` in the same change that puts a proxy in front of Web or Api (production proxy decisions: WAS-77). Rate-limit counters are kept per process. Several instances behind a load balancer each count separately, and none of this is a distributed limit.

## Provider mode

`Platforms__ProviderMode` = `Mock` or `Real` — **required** on Web, Worker, and API. Same value across processes.

Real mode registrations today:

- Trendyol GO → real HTTP
- Yemeksepeti → real HTTP
- Getir → still mock

Provider base URLs (application-level, not tenant secrets):

- `Platform__TrendyolGo__BaseUrl`
- `Platform__Yemeksepeti__BaseUrl` (and related Yemeksepeti options)

Details: [../integrations/food-platforms.md](../integrations/food-platforms.md).

## Print Bridge

Desktop clients use **`ServerUrl`** + device token. Config section may still be named `OrderHub` (legacy). ProgramData: `C:\ProgramData\Wasla\PrintBridge`.

**Download package.** The tenant setup page (including guided setup's first install) offers the portable Windows ZIP through `GET /print-bridge/download/package` (same origin, no query values, `CanManageDeviceSecurity`). The ZIP is not in the repository: build it with `scripts/release/package-print-bridge.ps1` and either set `Wasla:PrintBridgeDownload:PackagePath` to the file or place it at `wwwroot/downloads/wasla-print-bridge/` under the name in `Wasla:PrintBridgeDownload:PackageFileName` (default `Wasla.PrintBridge-win-x64.zip`). A relative `PackagePath` is resolved against Web's content root. The default location needs a web root: start Web with its content root set to the folder that contains `wwwroot` (the publish folder, or the project folder for `dotnet run`). Without one, ASP.NET Core leaves `WebRootPath` null and the default package counts as missing (WAS-128); a configured `PackagePath` still works. Without the file the page shows a "coming soon" state and no download link anywhere on it, and the download route returns 404. The package is portable (extract and run `Wasla.PrintBridge.exe`; the first run registers the `wasla-printbridge://` link) and needs the .NET 8 Desktop Runtime unless built with `-SelfContained`. The package also contains the opt-in WebView2 desktop app (`shell-ui/`, WebView2 SDK assemblies and loaders); it is off unless `Ui.Shell` is `WebView2`, and then needs the Microsoft Edge WebView2 Runtime, which the package does not install (WAS-55).

**Moving a device to another server.** Open the new setup link when no receipt is being printed. While a print job is still being completed, Print Bridge refuses the link and says so. Nothing is changed and the code is not used up, so the same link works once printing has finished (WAS-58). Edit `appsettings.json` by hand only while Print Bridge is closed.

**Exiting Print Bridge.** Use **Exit** in the tray menu. Without a print job in progress the process ends within about a second. With one, Exit waits up to 10 seconds for it to be printed and reported, then ends anyway; a job still unfinished at that point stays in Printing on the server and needs attention in Wasla (WAS-56, WAS-59). To confirm the exit: the tray icon is gone, Task Manager (Details) shows no `Wasla.PrintBridge.exe`, and the log ends with "Print Bridge shutdown complete; the application exits.".

See [../integrations/print-bridge.md](../integrations/print-bridge.md).

## Live notifications

Receipt print status (WAS-23) and Live Screen order invalidation (WAS-24) reach open tenant pages through a SignalR hub in Wasla.Web (`/hubs/tenant-events`). Every Live Screen keeps one connection open while it is shown. Wasla.Worker and Wasla.Api pass their changes to Web over a loopback-only internal channel. Design and failure behavior: [../architecture/realtime.md](../architecture/realtime.md).

**Reverse proxy (Nginx).** The hub is served on each tenant's own host, like every tenant page: no extra domain, CORS or certificate. The proxy must allow the WebSocket upgrade on the hub path and keep the connection open longer than the default 60 seconds of inactivity (SignalR sends a keep-alive every 15 seconds). For example, in the server block that forwards `{slug}.app.wasla.tr` to Web:

```nginx
location /hubs/ {
    proxy_pass http://127.0.0.1:5200;   # Web's local port
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;                       # tenant resolution reads Host
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 120s;
    proxy_buffering off;                               # Server-Sent Events fallback
}
```

The `Host` and forwarded headers are the same as for every other location ([Reverse proxy and client address](#reverse-proxy-and-client-address)). Without the upgrade headers the browser falls back to Server-Sent Events or long polling, which still work. If every transport is blocked, open detail panels fall back to reading the print status every 10 seconds. The proxy must not forward `/internal/` (Web does not serve it on its public port anyway).

**Internal channel.** Set the same values for Web, Worker and Api, as environment variables:

| Variable | Value |
|----------|-------|
| `Realtime__Internal__Port` | A free local port, for example `5290`. Web listens on `127.0.0.1` only; do not open it in the firewall or the proxy. |
| `Realtime__Internal__Secret` | A random secret of at least 32 printable ASCII characters without spaces, for example 32 random bytes in Base64. Store it with the other server-side secrets (WAS-77); never commit it. |
| `Realtime__Internal__TimeoutSeconds` | Optional, default 3. |
| `Realtime__Internal__Disabled` | Only for an environment that is polling-only on purpose: `true`, with no `Port`. Set it for Web, Worker and Api. |

**Startup policy (WAS-103).** Outside Development, Worker and Api refuse to start when `Realtime:Internal` is missing; the error names `Port`, `Secret` and `Disabled`. Configure the channel, or set `Realtime__Internal__Disabled=true` to run polling-only. Development keeps starting without the section (with a warning). In every environment and every process, an incomplete channel (`Port` without a secret of at least 32 characters, or `Secret` without `Port`) or an invalid one (a port outside 1–65535, a timeout outside 0.1–10, a secret with spaces or non-ASCII characters, or `Disabled=true` together with `Port`) refuses to start. Web starts without its listener (missing or disabled) and logs a warning naming the key. Errors and warnings name keys, never the secret. The full table and the reasons, including why Api follows the Worker's policy: [../architecture/realtime.md](../architecture/realtime.md#startup-policy-was-103).

Without the channel nothing is pushed from Worker and Api (ingested orders, automatic approvals and receipts, Bridge reports sent to Api). A Web without its listener keeps connected Live Screens polling every 10 seconds, so new platform orders, their sound and browser notification show within 10 seconds, as before WAS-24; with the listener on, connected Live Screens poll every 30 seconds and rely on the channel. Open print panels show Worker and Api changes on their fallback reads only while disconnected, otherwise on reopen or reload. Each process checks only its own configuration: a Worker or Api whose port or secret differs from Web's starts, its sends are refused or unreachable, and connected Live Screens can then show Worker orders up to 30 seconds late. Set the same values for all three processes and check the startup logs below. To rotate the secret, change it for all three processes and restart them; notifications between the restarts are refused and logged, nothing else fails.

**Session checks.** `Realtime__SessionRevalidationSeconds` (Web, default 60, 1–600) sets how often an open connection's session is checked again ([../architecture/authentication.md](../architecture/authentication.md#long-lived-live-connections)).

**One Web instance.** Connections live in Web's memory. Running more than one Web instance behind a load balancer needs a SignalR backplane and sticky routing, neither of which is set up; the internal channel would also reach only one instance. Run exactly one Web. The decision, the multi-instance topology and the thresholds that should trigger it: [../architecture/realtime-scale-out.md](../architecture/realtime-scale-out.md).

**Metrics.** Web, Worker and Api record the `Wasla.Realtime` meter (connections, reconnects, closures, session checks, sends and their duration, internal-channel failures and pauses). Read it on the server with `dotnet-counters`; nothing is exported ([observability.md](observability.md#live-notification-metrics)). A Web restart drops every live connection; browsers reconnect over about the next 45 seconds with random spreading and read their state once connected.

**Release order.** The tenant migration `AddPrintJobStatusVersion` (WAS-23) adds `PrintJobs.StatusVersion` (bigint, existing rows 0). Receipt creation, Print Bridge reports and the order detail pages read or write it, so follow [Release order](#release-order-migrate-every-database-first): stop every Web, Worker and Api instance of the previous version (a previous Worker or Api would create jobs at version 0 and change status without raising it, which breaks the order of versions for those orders), migrate every tenant, and only then start the new Web, Worker and Api. Rolling back the application leaves the column in place and unused.

The tenant migration `AddOrderChangeVersion` (WAS-24) adds `Orders.ChangeVersion` (bigint, existing rows 0). Ingestion, every order action and the Live Screen snapshot read or write it, so the same order applies: stop every previous Web, Worker and Api, migrate every tenant, then start the new versions. A previous process would change orders without raising the version or announcing them, so open Live Screens would show those changes only at their next poll, and versions would no longer count every change.

At startup each process logs its channel state (category `Wasla.Realtime`). Wasla.Worker and Wasla.Api: an information line when `Realtime:Internal` is configured, a warning naming `Realtime:Internal:Disabled` when the environment is polling-only, and in Development a warning that `Realtime:Internal` is not configured. Wasla.Web: the listener's loopback address when it accepts internal notifications, otherwise a warning that `Realtime:Internal:Port` is not set or `Realtime:Internal:Disabled` is true, and that connected Live Screens keep polling every 10 seconds.

## Logging and health

| Process | File sink (Serilog) | Health HTTP |
|---------|---------------------|-------------|
| API | `logs/wasla-api-.log` | `/health/live`, `/health/ready` |
| Worker | `logs/wasla-worker-.log` | **None** |
| Web | default host logging (no dedicated `wasla-web-` Serilog file sink in `Program.cs`) | `/health/live`, `/health/ready` |

Ready checks include CentralDb (`central-db` tag). Live checks are process-only.

Production Web unexpected errors go to `/error` (exception handler). Details: [observability.md](observability.md).

Observability pieces (trace header, TenantId context, API Serilog request logging) are **currently uncommitted in the working tree** in Diagnostics / Program files — verify they are present in the build you deploy.

## Environment variable cheat sheet (no secrets)

```text
ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT
ConnectionStrings__CentralDb
ENCRYPTION_MASTER_KEY
DataProtection__KeyPath
Platforms__ProviderMode
Platform__TrendyolGo__BaseUrl
Platform__Yemeksepeti__BaseUrl
Wasla__TenantOnboarding__MarketingBaseDomain
Wasla__TenantOnboarding__SqlAuth
Wasla__ApiBaseUrl
Wasla__TenantWebBaseUrl                      # optional, Print Bridge ServerUrl behind a proxy
TrustedProxies__KnownProxies__0              # Web and API behind a reverse proxy
AuthenticationRateLimiting__TenantLogin__PermitLimit   # optional; see authentication.md
Wasla__TenantOnboarding__MaxActivePendingRegistrationsPerOwnerEmail   # optional, default 3; see tenancy.md
TenantResolution__MissCache__Ttl             # optional, default 00:01:00; see tenancy.md
Realtime__Internal__Port                     # Web, Worker, Api: loopback channel to Web; see Live notifications
Realtime__Internal__Secret                   # secret, at least 32 characters
Realtime__Internal__Disabled                 # true only for a polling-only environment (no Port); required then outside Development
Realtime__SessionRevalidationSeconds         # Web, optional, default 60
TenantDb__ServerInstance                     # CLI and Web: SQL Server for new tenant databases; no default
```

## Known gaps

- No OpenTelemetry exporter or metrics stack; the `Wasla.Realtime` meter is read locally with `dotnet-counters` (see [observability.md](observability.md#live-notification-metrics)).
- Worker has no HTTP health endpoint for orchestrators.
- Legacy names remain outside the Web, Api, Worker and CLI configuration: the Print Bridge desktop settings section (WAS-135) and the `migrate-customer` command names (WAS-137).

## Related docs

- [observability.md](observability.md)
- [cli.md](cli.md)
- [migrations.md](migrations.md)
- [local-development.md](local-development.md)
- [../integrations/food-platforms.md](../integrations/food-platforms.md)
- [../integrations/print-bridge.md](../integrations/print-bridge.md)
