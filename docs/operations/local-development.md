# Local development

## Purpose and scope

This document is the canonical English guide for running Wasla locally (Web, Worker, API, CLI, Print Bridge pointer).

It supersedes older local-dev writeups for day-to-day setup (those files remain in the tree but are not the source of truth — see end of this page).

HTTPS / mkcert details stay in [../local-https.md](../local-https.md) (do not duplicate that guide here).

## Prerequisites

- .NET 8 SDK (projects target `net8.0`)
- SQL Server or LocalDB (default committed CentralDb connection uses LocalDB)
- Ability to edit the Windows hosts file for `wasla.local`, `app.wasla.local` and each `{slug}.app.wasla.local` you use (the repository never edits it)
- For HTTPS on custom hosts: mkcert — follow [../local-https.md](../local-https.md)

## ENCRYPTION_MASTER_KEY

Required at startup for Web, Worker, API, and most CLI commands (decrypts tenant connection strings and platform secrets).

Generate (PowerShell):

```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes)
setx ENCRYPTION_MASTER_KEY $key
```

Open a **new** terminal so the user environment variable is visible.

## Hosts

Development hosts follow the production shape (WAS-66, [../architecture/tenancy.md](../architecture/tenancy.md#host-contract-was-66)):

| Role | Development host | Production equivalent |
|------|------------------|-----------------------|
| Marketing site, signup, checkout | `localhost`, `wasla.local`, `www.wasla.local` | `wasla.tr` |
| Application entry (asks for the tenant address) | `app.wasla.local` | `app.wasla.tr` |
| Tenant | `{slug}.app.wasla.local` (example: `sushim.app.wasla.local`) | `{slug}.app.wasla.tr` |

Map the hosts you use in the hosts file to `127.0.0.1`; there is no wildcard, so add one line per tenant slug. Web launch profiles bind HTTP `5200` and HTTPS `7200` (see `Wasla.Web` launchSettings). Prefer the HTTPS tenant URL when using mkcert. In Development, links between hosts keep the request's port, for example `http://app.wasla.local:5200/auth/login`. On `localhost` the marketing pages link sign-in to `localhost` as well, so the marketing site works without any hosts entry.

Domains in Web and Api `appsettings.Development.json`: `Wasla:TenantOnboarding:MarketingBaseDomain` = `wasla.local` and `Wasla:TenantOnboarding:ApplicationBaseDomain` = `app.wasla.local`. The base `appsettings.json` names none; running Web or Api in any other environment without both set to public host names stops at startup.

Tenants created before WAS-66 have `{slug}.wasla.local` as their stored primary domain, which is no longer a tenant host. Change it to `{slug}.app.wasla.local` in your own development CentralDb (`Tenants.PrimaryDomain`, and `PendingRegistrations.PrimaryDomain` for registrations still in progress), or provision the tenant again.

## Central admin

Create via CLI (CentralDb), **not** via obsolete `CentralAdmin__Email` / `CentralAdmin__PasswordHash` env vars:

```powershell
dotnet run --project src\Wasla.Cli -- add-central-admin --email admin@wasla.local --password "YourStrongPassword!" --display-name "Central Admin"
```

Then open `/admin/login` on the central host.

`add-central-admin` does not require `ENCRYPTION_MASTER_KEY` (see [cli.md](cli.md)).

## Database bootstrap

With master key set, and the CLI in Development:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src\Wasla.Cli -- migrate-central
```

The CLI's committed `appsettings.json` names no CentralDb and no tenant SQL Server (WAS-93). Its `appsettings.Development.json` sets both to LocalDB, so local CLI runs need `DOTNET_ENVIRONMENT=Development`; without it the CLI stops with "ConnectionStrings:CentralDb is not configured" before touching any database. Environment variables (`ConnectionStrings__CentralDb`, `TenantDb__ServerInstance`) override both files, and `--sql-server` overrides the tenant server. The destructive commands (`delete-customer`, `reset-customer-db`, `reset-all-customer-dbs`) also treat an unset environment as Production and then ask for `--force-production`.

Create a tenant (creates DB + admin) **or** provision a paid signup request:

```powershell
dotnet run --project src\Wasla.Cli -- add-customer --name "Demo Restaurant" --slug demo --domain demo.app.wasla.local --admin-email admin@demo.local --admin-password "YourStrongPassword!" --admin-name "Demo Admin"

dotnet run --project src\Wasla.Cli -- provision-signup-request --registration-id <guid>
```

Tenant databases are created only on an explicitly chosen SQL Server: `--sql-server`, else `TenantDb__ServerInstance` (`TenantDb:ServerInstance`). There is no default. In Development, the CLI's and Web's `appsettings.Development.json` set `TenantDb:ServerInstance` to LocalDB.

Apply tenant schema updates after pulls:

```powershell
dotnet run --project src\Wasla.Cli -- migrate-all-customers
dotnet run --project src\Wasla.Cli -- migration-status
```

See [migrations.md](migrations.md).

## Provider mode (local)

Committed local appsettings typically use:

```json
"Platforms": {
  "ProviderMode": "Mock"
}
```

Missing/invalid `Platforms:ProviderMode` fails fast. Mock mode must not call real provider HTTP. See [../integrations/food-platforms.md](../integrations/food-platforms.md).

## Run the processes

From repo root (separate terminals; master key present):

```powershell
dotnet run --project src\Wasla.Web
dotnet run --project src\Wasla.Worker
dotnet run --project src\Wasla.Api
```

Typical local ports (launchSettings):

| Process | URLs |
|---------|------|
| Web | `http://0.0.0.0:5200`, `https://0.0.0.0:7200` |
| API | `http://localhost:59451`, `https://localhost:59450` |

Health (Web and API; currently uncommitted diagnostics wiring in working tree):

- `/health/live`
- `/health/ready` (includes CentralDb check)

Worker has **no** HTTP health endpoint.

The launch profiles run every process in Development, where Worker and Api start without `Realtime:Internal` (live notifications from them are then off, with a warning). Started in any other environment, for example a published binary without `DOTNET_ENVIRONMENT`/`ASPNETCORE_ENVIRONMENT`, Worker and Api refuse to start until the channel is configured or `Realtime__Internal__Disabled=true` is set ([deployment.md](deployment.md#live-notifications)).

### Auth nuance (local)

API Data Protection keys: no committed configuration sets `DataProtection:KeyPath`, so on Windows the API keeps its keys in `C:\Wasla-keys` (`ApiDataProtection`); on other operating systems it uses the framework's default key storage.

Do **not** assume a Web browser login cookie authenticates the API. Local Web is `{slug}.app.wasla.local` and local API is `localhost`. Cookies do not set a shared domain, and the API Data Protection key path is not the Web path, so the browser session is not shared.

`POST /api/auth/validate` checks email and password and returns success or failure. It does not call `SignInAsync` and does not create a browser login. There is no API login endpoint that issues `.Wasla.TenantAuth`.

Protected API calls need a cookie the API process can unprotect. The cookie must also pass the same per-request session check as on Web: a current, active user whose role and security stamp still match. The repository does not define a local developer flow that creates that cookie. The tests run Web and API in-process with one shared key ring and sign in through the real Web login (`ApiTenantSessionTests`). Treat the missing local login path as a developer-experience gap. Do not invent an API login route. Details: [../architecture/authentication.md](../architecture/authentication.md#when-a-web-session-reaches-the-api).

## Print Bridge (local)

Separate Windows desktop app. Point `ServerUrl` at the reachable Wasla host that serves `/api/print-bridge` (often tenant Web URL). Token from tenant Print Bridge UI or `generate-print-bridge-token`.

To run a Debug build without touching an installed client's settings, set `WASLA_PRINTBRIDGE_DATA_ROOT` to an empty scratch folder outside the repository and enable `DryRun` in that folder's `appsettings.json`. The instance then uses only that folder and leaves the protocol handler and setup pipe of the installed client alone. Only one Print Bridge runs at a time (a machine-wide single-instance lock), so exit the installed client from its tray icon first. The app window needs the WebView2 Runtime (without it the tray shows the runtime notice and keeps printing); in Debug builds F12 opens its DevTools.

Details: [../integrations/print-bridge.md](../integrations/print-bridge.md).

## Related docs

- [../local-https.md](../local-https.md)
- [cli.md](cli.md)
- [migrations.md](migrations.md)
- [observability.md](observability.md)
- [../architecture/tenancy.md](../architecture/tenancy.md)

## Superseded local writeups (not deleted)

These remain in the repo but are **not** canonical:

- `docs/LOCAL_DEVELOPMENT_EN.md` (obsolete central-admin env-var instructions)
- `docs/LOCAL_DEVELOPMENT_TR.md` (Turkish twin; do not maintain a second local-dev doc)
