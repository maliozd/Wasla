# Local development

## Purpose and scope

This document is the canonical English guide for running Wasla locally (Web, Worker, API, CLI, Print Bridge pointer).

It supersedes older local-dev writeups for day-to-day setup (those files remain in the tree but are not the source of truth — see end of this page).

HTTPS / mkcert details stay in [../local-https.md](../local-https.md) (do not duplicate that guide here).

## Prerequisites

- .NET 8 SDK (projects target `net8.0`)
- SQL Server or LocalDB (default committed CentralDb connection uses LocalDB)
- Ability to edit the Windows hosts file for `*.wasla.local`
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

Central / public style hosts used in development:

- `localhost`
- `wasla.local`
- `www.wasla.local`

Tenant hosts:

- `{slug}.wasla.local` (example: `sushim.wasla.local`)

Map these in the hosts file to `127.0.0.1`. Web launch profiles bind HTTP `5200` and HTTPS `7200` (see `Wasla.Web` launchSettings). Prefer the HTTPS tenant URL when using mkcert.

Marketing / signup base domain in Web `appsettings.json`: `OrderHub:CustomerOnboarding:MarketingBaseDomain` = `wasla.local` (section name is legacy `OrderHub`; product is Wasla).

## Central admin

Create via CLI (CentralDb), **not** via obsolete `CentralAdmin__Email` / `CentralAdmin__PasswordHash` env vars:

```powershell
dotnet run --project src\Wasla.Cli -- add-central-admin --email admin@wasla.local --password "YourStrongPassword!" --display-name "Central Admin"
```

Then open `/admin/login` on the central host.

`add-central-admin` does not require `ENCRYPTION_MASTER_KEY` (see [cli.md](cli.md)).

## Database bootstrap

With master key set:

```powershell
dotnet run --project src\Wasla.Cli -- migrate-central
```

Create a tenant (creates DB + admin) **or** provision a paid signup request:

```powershell
dotnet run --project src\Wasla.Cli -- add-customer --name "Demo Restaurant" --slug demo --domain demo.wasla.local --admin-email admin@demo.local --admin-password "YourStrongPassword!" --admin-name "Demo Admin"

dotnet run --project src\Wasla.Cli -- provision-signup-request --registration-id <guid>
```

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

### Auth nuance (local)

API Data Protection keys: committed API `appsettings.json` still sets `DataProtection:KeyPath` to `C:\OrderHub-keys` (**legacy path debt**). If unset, API `Program.cs` falls back to `C:\Wasla-keys`.

Do **not** assume a Web browser login cookie authenticates the API. Local Web is `{slug}.wasla.local` and local API is `localhost`. Cookies do not set a shared domain, and the API Data Protection key path is not the Web path, so the browser session is not shared.

`POST /api/auth/validate` checks email and password and returns success or failure. It does not call `SignInAsync` and does not create a browser login. There is no API login endpoint that issues `.Wasla.TenantAuth`.

Protected API calls need a cookie the API process can unprotect. The repository does not define a local developer flow that creates that cookie. Unit tests issue one inside a test host (`ApiTenantAuthenticationTests`). Treat the missing local login path as a developer-experience gap. Do not invent an API login route. Details: [../architecture/authentication.md](../architecture/authentication.md).

## Print Bridge (local)

Separate Windows desktop app. Point `ServerUrl` at the reachable Wasla host that serves `/api/print-bridge` (often tenant Web URL). Token from tenant Print Bridge UI or `generate-print-bridge-token`.

To run a Debug build without touching an installed client's settings, set `WASLA_PRINTBRIDGE_DATA_ROOT` to an empty scratch folder outside the repository and enable `DryRun` in that folder's `appsettings.json`. The instance then uses only that folder and leaves the protocol handler and setup pipe of the installed client alone. Only one Print Bridge runs at a time (a machine-wide single-instance lock), so exit the installed client from its tray icon first. Set `Ui.Shell` to `WebView2` there to try the WebView2 desktop app (needs the WebView2 Runtime); in Debug builds F12 opens its DevTools.

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
