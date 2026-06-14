# OrderHub â€” Deployment configuration checklist

This document lists the settings required for **production** and **stage** deployments of OrderHub.

It is based on the current repository configuration audit. Committed `appsettings.Production.json` files are **thin** (mostly provider mode and Trendyol GO URL). If server environment variables are missing, applications may inherit **local/dev defaults** from base `appsettings.json` (LocalDB, `wasla.local`, `localhost`, `Mock` provider mode).

**Do not commit real secrets.** Set sensitive values on the server via environment variables, a secret store, or a protected config layer.

---

## 1. Environment variable matrix

ASP.NET Core maps nested JSON keys to environment variables using `__` (double underscore).

Example: `OrderHub:CustomerOnboarding:MarketingBaseDomain` â†’ `OrderHub__CustomerOnboarding__MarketingBaseDomain`

### Database

| Variable | Required for | Notes |
|----------|--------------|-------|
| `ConnectionStrings__CentralDb` | **Web**, **Worker**, **Api**, **Cli** | Central registry database. Must point to production/stage SQL Server, not LocalDB. |

**Cli additional key** (customer DB provisioning / migrations):

| Variable | Required for | Notes |
|----------|--------------|-------|
| `CustomerDb__ServerInstance` | **Cli** | SQL Server instance used when creating or migrating customer databases. Maps to `CustomerDb:ServerInstance` in Cli `appsettings.json`. |

### Customer onboarding / tenant creation

Used when public signup is enabled (Web). Maps to `OrderHub:CustomerOnboarding` in Web `appsettings.json`.

| Variable | Required for | Notes |
|----------|--------------|-------|
| `OrderHub__CustomerOnboarding__MarketingBaseDomain` | **Web** | Base domain for marketing host and signup subdomains (`{slug}.{domain}`). |
| `OrderHub__CustomerOnboarding__ServerInstance` | **Web** | SQL Server instance for new customer database creation. |
| `OrderHub__CustomerOnboarding__SqlAuth` | **Web** | `trusted` (Windows auth) or `sql:username:password`. **Do not put real passwords in committed config** â€” prefer env var on server. |
| `OrderHub__CustomerOnboarding__TrialDays` | **Web** (optional) | Trial length in days. Default in code: `14`. |

### Security

| Variable | Required for | Notes |
|----------|--------------|-------|
| `ENCRYPTION_MASTER_KEY` | **Web**, **Worker**, **Api**, **Cli** (most commands) | Base64-encoded 32-byte key. **Required at startup** for encrypting/decrypting customer connection strings and platform secrets. Not stored in appsettings. |
| `DataProtection__KeyPath` | **Web**, **Api** (recommended) | Folder for persistent ASP.NET Data Protection keys. See [Data Protection](#4-data-protection). |
| `CentralAdmin__Email` | â€” | Referenced in local dev docs only. **Current code** authenticates central admins from **CentralDb** (`CentralAdminUsers` table), not from these env vars. |
| `CentralAdmin__PasswordHash` | â€” | Same as above. Provision admins with Cli: `add-central-admin`. |

**Central admin provisioning (current implementation):**

```powershell
dotnet run --project src/OrderHub.Cli -- add-central-admin --email __REPLACE_WITH_ADMIN_EMAIL__ --password __REPLACE_WITH_ADMIN_PASSWORD__ --display-name "OrderHub Admin"
```

Requires `ConnectionStrings__CentralDb` and `ENCRYPTION_MASTER_KEY` where applicable.

### URLs / domains

| Variable | Required for | Notes |
|----------|--------------|-------|
| `OrderHub__ApiBaseUrl` | **Web** (Print Bridge setup) | Public URL of the **Api** project. Used when generating Print Bridge example config. Must not remain `localhost` in production. |
| `OrderHub__CustomerWebBaseUrl` | **Web** (optional) | Tenant Web base URL shown in Print Bridge UI. Falls back to current request host if unset. |
| `OrderHub__PublicWebBaseUrl` | **Web** (optional) | Alternate key for public Web URL if `CustomerWebBaseUrl` is not set. |

There is no separate `CustomerWebBaseUrl` / `PublicWebBaseUrl` in committed appsettings today; set via environment variables if you need a fixed public URL behind a reverse proxy.

### Provider mode

| Variable | Required for | Notes |
|----------|--------------|-------|
| `Platforms__ProviderMode` | **Web**, **Worker**, **Api** | `Mock` or `Real`. **Required** â€” missing value causes startup failure in provider resolution. Web and Worker **must use the same value**. See [Provider mode](#5-provider-mode). |

### Provider settings (URLs and options)

Per-tenant API keys/secrets are stored **encrypted in the database** after setup in the tenant UI. The following are **application-level** provider endpoints from current appsettings:

#### Trendyol GO (`Platform:TrendyolGo`)

| Variable | Required for | Notes |
|----------|--------------|-------|
| `Platform__TrendyolGo__BaseUrl` | **Web**, **Worker**, **Api** (Real mode) | Production: `https://api.tgoapis.com`. Stage: `https://stageapi.tgoapis.com`. |
| `Platform__TrendyolGo__AgentName` | Optional | Default: `OrderHub`. |
| `Platform__TrendyolGo__RequestTimeout` | Optional | e.g. `00:00:15` |

#### Yemeksepeti (`Platform:Yemeksepeti`)

Present in **Worker** and **Api** base `appsettings.json`. Override on server if needed.

| Variable | Required for | Notes |
|----------|--------------|-------|
| `Platform__Yemeksepeti__BaseUrl` | **Worker**, **Api** (Real mode) | Default in repo: `https://yemeksepeti.partner.deliveryhero.io` |
| `Platform__Yemeksepeti__TokenPath` | Optional | Default: `/v2/oauth/token` |
| `Platform__Yemeksepeti__DefaultPageSize` | Optional | Default: `20` |
| `Platform__Yemeksepeti__RequestTimeoutSeconds` | Optional | Default: `30` |

#### GetirYemek

No dedicated `Platform:GetirYemek` section in appsettings. In **Real** mode, GetirYemek may still use a **mock client** until a real integration is implemented. Platform credentials for Getir are not in appsettings.

### Print Bridge

Print Bridge is a **desktop client** (`OrderHub.PrintBridge`). It does not use the server env matrix above at deploy time.

| Item | Where configured | Notes |
|------|------------------|-------|
| Server base URL | Print Bridge client `OrderHub:BaseUrl` | Tenant HTTPS URL, e.g. `https://restaurant.example.com` |
| Device token | Print Bridge client `OrderHub:AgentToken` | Generated in **tenant Web UI** (Print Bridge devices page). Never commit tokens. |
| Example / download package | `wwwroot/downloads/orderhub-print-bridge/` | Sample `appsettings.sample.json` ships with empty `BaseUrl` and `AgentToken`. |
| Web setup helper | `OrderHub__ApiBaseUrl` | If this stays `http://localhost:59451`, generated setup snippets will show localhost â€” **override on production Web**. |

---

## 2. Example values (placeholders only)

Replace all `__REPLACE_...__` values on the server. **Do not commit these examples with real secrets.**

### Production example (PowerShell)

```powershell
# Environment
$env:ASPNETCORE_ENVIRONMENT = "Production"   # Web, Api
$env:DOTNET_ENVIRONMENT = "Production"       # Worker, Cli

# Database
$env:ConnectionStrings__CentralDb = "__REPLACE_WITH_PRODUCTION_SQL_CONNECTION_STRING__"

# Security
$env:ENCRYPTION_MASTER_KEY = "__REPLACE_WITH_BASE64_32_BYTE_KEY__"
$env:DataProtection__KeyPath = "__REPLACE_WITH_WRITABLE_KEY_FOLDER__"

# Domains / URLs (Web)
$env:OrderHub__CustomerOnboarding__MarketingBaseDomain = "example.com"
$env:OrderHub__CustomerOnboarding__ServerInstance = "__REPLACE_WITH_SQL_SERVER_INSTANCE__"
$env:OrderHub__CustomerOnboarding__SqlAuth = "trusted"
# Or SQL auth (prefer secret store over plain env if possible):
# $env:OrderHub__CustomerOnboarding__SqlAuth = "sql:__REPLACE_WITH_DB_USER__:__REPLACE_WITH_DB_PASSWORD__"

$env:OrderHub__ApiBaseUrl = "https://api.example.com"
$env:OrderHub__CustomerWebBaseUrl = "https://app.example.com/"

# Provider
$env:Platforms__ProviderMode = "Real"
$env:Platform__TrendyolGo__BaseUrl = "https://api.tgoapis.com"
$env:Platform__Yemeksepeti__BaseUrl = "https://yemeksepeti.partner.deliveryhero.io"

# Cli (migrations / provisioning)
$env:CustomerDb__ServerInstance = "__REPLACE_WITH_SQL_SERVER_INSTANCE__"
```

### Stage example (differences from production)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Staging"      # or Production + stage-specific env vars
$env:Platforms__ProviderMode = "Real"        # or Mock for isolated UI testing
$env:Platform__TrendyolGo__BaseUrl = "https://stageapi.tgoapis.com"
$env:OrderHub__CustomerOnboarding__MarketingBaseDomain = "stage.example.com"
$env:OrderHub__ApiBaseUrl = "https://api-stage.example.com"
```

### Per-project minimum summary

| Project | Must set on server |
|---------|-------------------|
| **Web** | `ENCRYPTION_MASTER_KEY`, `ConnectionStrings__CentralDb`, `Platforms__ProviderMode`, `DataProtection__KeyPath` (recommended), domain/URL overrides if not using reverse-proxy host inference |
| **Worker** | `ENCRYPTION_MASTER_KEY`, `ConnectionStrings__CentralDb`, `Platforms__ProviderMode`, provider URLs in Real mode |
| **Api** | `ENCRYPTION_MASTER_KEY`, `ConnectionStrings__CentralDb`, `Platforms__ProviderMode`, `DataProtection__KeyPath` (recommended), provider URLs in Real mode |
| **Cli** | `ConnectionStrings__CentralDb`, `CustomerDb__ServerInstance`, `ENCRYPTION_MASTER_KEY` (for commands that decrypt customer data) |

---

## 3. Values that must be overridden in production

These values are **acceptable for local development** but **must not leak** into production/stage if you expect real SQL, real domains, and real provider behavior.

| Dev/local value | Where it appears | Production risk |
|-----------------|------------------|-----------------|
| `(localdb)\MSSQLLocalDB` | Web, Worker, Api, Cli `appsettings.json` | App connects to LocalDB instead of server SQL |
| `wasla.local` | `OrderHub:CustomerOnboarding:MarketingBaseDomain` | Signup creates `*.wasla.local` tenants |
| `http://localhost:59451` | `OrderHub:ApiBaseUrl` | Print Bridge setup shows localhost |
| `Platforms:ProviderMode` = `Mock` | Base appsettings | No real order sync; mock data only |
| `https://stageapi.tgoapis.com` | Base Trendyol GO URL (Worker/Api) | Stage API used if Production overlay not loaded |
| `../../.certs/orderhub-local.pfx` | Web `appsettings.Development.json` | Dev HTTPS cert path; not for production |
| PFX password in Development config | Web `appsettings.Development.json` | Dev-only; never deploy Development config as Production |
| `C:\OrderHub-keys` | Api `appsettings.json` | Windows-specific path; set explicitly per environment |
| `*.wasla.local` launch URLs | Web `launchSettings.json` | IDE-only; not used on server |
| `trusted` SQL auth for onboarding | Default `SqlAuth` | May be wrong on Linux/cloud SQL; use explicit SQL auth if needed |

**Important:** `appsettings.Production.json` in the repo currently overrides **provider mode** and **Trendyol production URL** only. It does **not** override connection strings, marketing domain, or API base URL. Treat environment variables (or server-specific config) as the source of truth for those.

---

## 4. Data Protection

ASP.NET Core Data Protection encrypts cookies and other protected payloads. Both **Web** and **Api** register Data Protection with application name `OrderHub`.

### Requirements

- **Use a persistent key path in production** so keys survive app restarts and deployments.
- **Web:** reads `DataProtection:KeyPath` from configuration. If missing or unusable, keys may be ephemeral (users can be logged out after restart).
- **Api:** uses `DataProtection:KeyPath` from config; if empty, code falls back to `C:\OrderHub-keys` (Windows-oriented default).
- **Path must not be committed to Git.** Store keys on server disk or a shared volume accessible to all instances.
- **Service account** running Web/Api must have **read/write** permission on the key directory.
- Use the **same key ring** across Web and Api if they must share protected payloads (same `SetApplicationName("OrderHub")` is already used).

Example (placeholder):

```text
DataProtection__KeyPath=__REPLACE_WITH_WRITABLE_KEY_FOLDER__
```

---

## 5. Provider mode

Configuration key: `Platforms:ProviderMode` â†’ env `Platforms__ProviderMode`

| Value | Behavior |
|-------|----------|
| `Mock` | Mock provider clients. Test/demo data. **No real platform HTTP** for mocked integrations. |
| `Real` | Real clients where implemented. Trendyol GO uses HTTP in Real mode. |

### Rules

1. **Web and Worker must use the same mode** for consistent order sync and UI behavior.
2. Value is **required** â€” empty or invalid values cause startup errors (`ProviderModeResolver`).
3. **Do not set `Real`** until tenant platform connections have valid credentials in the database.
4. **GetirYemek:** even in `Real` mode, the current codebase may still register a **mock** GetirYemek client. Plan live Getir testing accordingly.
5. **Yemeksepeti / Trendyol:** Real mode uses configured `Platform:*` URLs; tenant-specific API keys are stored encrypted per connection.

Committed `appsettings.Production.json` sets `ProviderMode` to `Real` when `ASPNETCORE_ENVIRONMENT=Production` / `DOTNET_ENVIRONMENT=Production`. Verify the environment name is set correctly on the server.

---

## 6. Stage vs production

### Environment names

| Host | Typical variable | Value |
|------|------------------|-------|
| Web, Api | `ASPNETCORE_ENVIRONMENT` | `Production` or `Staging` |
| Worker, Cli | `DOTNET_ENVIRONMENT` | `Production` or `Staging` |

Use explicit environment values on the server. Do not rely on IDE `launchSettings.json`.

### Staging vs production today

- There is **no** `appsettings.Staging.json` in the repository yet.
- Stage and production differences (domains, Trendyol stage URL vs prod URL) should be set via **environment variables** on each server.
- **Recommendation for later:** add `appsettings.Staging.json` if stage and production need different default provider URLs without duplicating env var sets.

### Suggested split

| Concern | Stage | Production |
|---------|-------|------------|
| Marketing domain | `stage.example.com` | `example.com` |
| Trendyol GO URL | `https://stageapi.tgoapis.com` | `https://api.tgoapis.com` |
| Provider mode | `Real` for integration tests, `Mock` for UI-only | `Real` when go-live |
| SQL | Separate stage SQL instance | Production SQL instance |

---

## 7. Post-deployment smoke test checklist

Run after deploying Web, Worker, and Api with production/stage environment variables set.

### Web

- [ ] Starts with `ASPNETCORE_ENVIRONMENT=Production` (or `Staging`)
- [ ] Public homepage opens on marketing host
- [ ] Pricing section and signup page open
- [ ] Customer login page opens on a tenant subdomain
- [ ] Admin area (`/admin`) opens
- [ ] Tenant subdomain resolves (DNS + reverse proxy)
- [ ] New signup does **not** create `*.wasla.local` domains (check `MarketingBaseDomain` override)
- [ ] No LocalDB connection errors in logs

### Security & data

- [ ] `ENCRYPTION_MASTER_KEY` is set (process starts without missing-key error)
- [ ] `DataProtection__KeyPath` exists and is writable by the app pool / service account
- [ ] Central admin can log in (user exists in CentralDb via Cli)
- [ ] Restart Web â€” tenant/admin sessions behave as expected (persistent Data Protection)

### Database

- [ ] `ConnectionStrings__CentralDb` points to production/stage SQL Server
- [ ] CentralDb migrations applied (`migrate-central` via Cli)
- [ ] Existing customer DBs reachable (decryption works with same master key)

### Worker

- [ ] Starts with `DOTNET_ENVIRONMENT=Production`
- [ ] Logs show expected `ProviderMode` (`Mock` or `Real`)
- [ ] In `Real` mode: sync attempts use real Trendyol URL, not unintended stage/local endpoints
- [ ] Disabled tenant sync settings still respected

### Api

- [ ] Starts and connects to CentralDb
- [ ] `Platforms__ProviderMode` matches Worker
- [ ] Log folder (`logs/`) is writable if file logging is enabled

### Print Bridge

- [ ] Tenant Print Bridge devices page loads
- [ ] New device token can be generated
- [ ] Setup / example config does **not** show `localhost` as production API URL
- [ ] Client configured with tenant `BaseUrl` + generated `AgentToken` can poll API

### Cli (operational)

- [ ] `list-customers` works against production CentralDb
- [ ] `migration-status` reports expected schema versions

---

## 8. Related documentation

- [LOCAL_DEVELOPMENT_EN.md](./LOCAL_DEVELOPMENT_EN.md) â€” local setup, master key, migrations
- [LOCAL_DEVELOPMENT_TR.md](./LOCAL_DEVELOPMENT_TR.md) â€” Turkish local setup guide
- [local-https.md](./local-https.md) â€” mkcert and `*.wasla.local` (development only)

---

## Quick reference: config file layers

| File | Role |
|------|------|
| `appsettings.json` | Base defaults (currently many local/dev values) |
| `appsettings.Development.json` | Local HTTPS cert, stage Trendyol URL, detailed errors |
| `appsettings.Production.json` | Provider `Real` + Trendyol production URL (Web, Worker, Api) |
| Environment variables | **Server source of truth** for secrets, SQL, domains, Data Protection |
| `launchSettings.json` | IDE only â€” not used in server deployment |
