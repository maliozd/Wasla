# Deployment

## Purpose and scope

This document lists what must be configured to run Wasla in a non-local environment: processes, databases, secrets, domains, provider mode, Print Bridge, logging, and health endpoints.

It corrects stale names from older checklists (e.g. treating `BaseUrl` as the Print Bridge setup field, or OrderHub as the current product name). It does **not** embed real secrets or placeholder passwords.

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

## Databases

- **CentralDb**: tenant registry, memberships, signup/provisioning, central admins, Print Bridge device registry. Connection: `ConnectionStrings:CentralDb` / `ConnectionStrings__CentralDb`.
- **Per-tenant DBs**: operational data for one restaurant location. Connection strings stored encrypted on the tenant row; decrypted with `ENCRYPTION_MASTER_KEY`. Several locations are several tenants. See [../architecture/tenancy.md](../architecture/tenancy.md).
- CLI provisioning / migrations use `CustomerDb:ServerInstance` (`CustomerDb__ServerInstance`) when creating or migrating tenant databases.

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

## Required secrets and keys

| Item | Used by | Notes |
|------|---------|-------|
| `ENCRYPTION_MASTER_KEY` | Web, Worker, API, most CLI | Base64 32-byte key. Required at startup for secret decrypt. Never commit. |
| Data Protection key folder | Web, API | Persist ASP.NET Data Protection keys across restarts/instances. |

### Data Protection path debt (API)

Committed `src/Wasla.Api/appsettings.json` still contains:

`DataProtection:KeyPath` = `C:\OrderHub-keys`

That path is **current legacy configuration debt**. Prefer overriding with `DataProtection__KeyPath` to a Wasla-named folder in real deployments. If the configured value is empty, API `Program.cs` defaults to `C:\Wasla-keys`.

Do not change the committed path as part of documentation-only work.

Web persists keys only when `DataProtection:KeyPath` is set.

### Auth nuance for deployers

API uses cookie authentication with its own scheme registration. Do **not** assume a Web login session cookie automatically authorizes API requests across hosts. Plan API access separately from Web sessions.

Central admins are created with CLI `add-central-admin`, not `CentralAdmin__Email` / `CentralAdmin__PasswordHash`.

## Domains and HTTPS

- Public / marketing host and `{slug}.<MarketingBaseDomain>` tenant hosts.
- Config section for onboarding remains `OrderHub:CustomerOnboarding` (legacy section name debt): `MarketingBaseDomain`, `ServerInstance`, `SqlAuth`, optional `TrialDays`.
- Production should force HTTPS (Web enables HSTS + HTTPS redirection outside Development).
- Print Bridge and browsers need a trusted certificate chain on real domains.

Useful URL overrides (env `__` form):

| Variable | Role |
|----------|------|
| `OrderHub__ApiBaseUrl` | Public API URL used when generating Print Bridge setup helpers |
| `OrderHub__CustomerWebBaseUrl` / `OrderHub__PublicWebBaseUrl` | Optional fixed public Web URLs behind proxies |

Do not leave production pointing at `localhost` or LocalDB.

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

**Download package.** The tenant setup page (including guided setup's first install) offers the portable Windows ZIP through `GET /print-bridge/download/package` (same origin, no query values, `CanManageDeviceSecurity`). The ZIP is not in the repository: build it with `scripts/release/package-print-bridge.ps1` and either set `OrderHub:PrintBridgeDownload:PackagePath` to the file or place it at `wwwroot/downloads/wasla-print-bridge/` under the name in `OrderHub:PrintBridgeDownload:PackageFileName` (default `Wasla.PrintBridge-win-x64.zip`). Without the file the page shows a "coming soon" state and no download link. The package is portable (extract and run `Wasla.PrintBridge.exe`; the first run registers the `wasla-printbridge://` link) and needs the .NET 8 Desktop Runtime unless built with `-SelfContained`. The package also contains the opt-in WebView2 desktop app (`shell-ui/`, WebView2 SDK assemblies and loaders); it is off unless `Ui.Shell` is `WebView2`, and then needs the Microsoft Edge WebView2 Runtime, which the package does not install (WAS-55).

**Moving a device to another server.** Open the new setup link when no receipt is being printed. While a print job is still being completed, Print Bridge refuses the link and says so. Nothing is changed and the code is not used up, so the same link works once printing has finished (WAS-58). Edit `appsettings.json` by hand only while Print Bridge is closed.

**Exiting Print Bridge.** Use **Exit** in the tray menu. Without a print job in progress the process ends within about a second. With one, Exit waits up to 10 seconds for it to be printed and reported, then ends anyway; a job still unfinished at that point stays in Printing on the server and needs attention in Wasla (WAS-56, WAS-59). To confirm the exit: the tray icon is gone, Task Manager (Details) shows no `Wasla.PrintBridge.exe`, and the log ends with "Print Bridge shutdown complete; the application exits.".

See [../integrations/print-bridge.md](../integrations/print-bridge.md).

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
OrderHub__CustomerOnboarding__MarketingBaseDomain
OrderHub__CustomerOnboarding__ServerInstance
OrderHub__CustomerOnboarding__SqlAuth
OrderHub__ApiBaseUrl
CustomerDb__ServerInstance   # CLI
```

## Known gaps

- No OpenTelemetry exporter / metrics stack (see [observability.md](observability.md)).
- Worker has no HTTP health endpoint for orchestrators.
- Legacy names remain in config (`OrderHub` sections, `C:\OrderHub-keys`, `migrate-customer` command names).

## Related docs

- [observability.md](observability.md)
- [cli.md](cli.md)
- [migrations.md](migrations.md)
- [local-development.md](local-development.md)
- [../integrations/food-platforms.md](../integrations/food-platforms.md)
- [../integrations/print-bridge.md](../integrations/print-bridge.md)
