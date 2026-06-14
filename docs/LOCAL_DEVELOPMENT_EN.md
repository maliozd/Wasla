# Wasla â€“ Local Development (EN)

This document explains how to run the Wasla MVP demo flow locally.

## Prerequisites

- .NET SDK (repo targets `net8.0`)
- SQL Server / LocalDB (default CentralDb uses LocalDB)
- `dotnet-ef` tool (for migrations)

## 1) Master key (required)

Wasla requires a master key for encrypting/decrypting sensitive data.

PowerShell:

```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes)
setx ENCRYPTION_MASTER_KEY $key
```

Open a new terminal so the env var is reloaded.

## 1.1) Central admin (Web `/admin`)

Central admin uses **separate** cookie authentication from tenant users. Configure it with **user-level environment variables** (do not commit real secrets). The password must be stored as a **BCrypt hash**, not plaintext.

Generate a hash (no master key required):

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- hash-password --password "YourPasswordHere!"
```

Then set (example):

```powershell
[Environment]::SetEnvironmentVariable("CentralAdmin__Email", "admin@wasla.local", "User")
[Environment]::SetEnvironmentVariable("CentralAdmin__PasswordHash", "<paste BCrypt hash from previous command>", "User")
```

Restart Visual Studio / Rider / your terminal so the Web app picks up user environment variables. Open `/admin/login` and sign in.

## 2) Build

From repo root:

```powershell
dotnet clean
dotnet restore
dotnet build
```

## 3) Database migrations (CLI)

CentralDb and each CustomerDb have **separate** EF Core migration histories. After pulling new code, apply migrations so the schema matches the app (e.g. new columns such as `SupplierId`, `ExecutorEmail` on `PlatformConnections`).

| Command | When to use it |
|--------|-----------------|
| `migrate-central` | After a **CentralDb** model/migration change (registry / `Customer` table in the central database). |
| `migrate-customer` | Update **one** tenantâ€™s customer database (e.g. `dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-customer --slug demo`). |
| `migrate-all-customers` | After a **CustomerDb** migration change â€” applies to **all active** customers (use this most often in dev when you have multiple tenants). |
| `migration-status` | Inspect latest applied and pending migrations for Central and each active customer. |

Examples (from repo root, with `ENCRYPTION_MASTER_KEY` set):

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-central
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-customer --slug demo
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-customer --customer-id "00000000-0000-0000-0000-000000000000"
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-all-customers
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migration-status
```

Optional: `migrate-all-customers --dry-run` (list pending only) and `--only slug1 --only slug2` (filter slugs).

**Troubleshooting:** If the Web app or Worker throws SQL errors like **Invalid column name** (e.g. missing new columns), run `migrate-all-customers` (and `migrate-central` if the central registry schema changed), then restart the app.

If you added new CustomerDb entities (e.g. `UserNotificationSettings`) and you see errors like:
- `Invalid column name 'NewOrderSoundEnabled'`
- `Invalid object name 'UserNotificationSettings'`

Run:

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-all-customers
```

## 3.1) Destructive customer reset/delete (CLI)

These commands are **development/staging friendly** and **destructive**. They require `--confirm` and refuse to run in Production unless `--force-production` is provided.

### Delete customer completely (CentralDb record + drop CustomerDb)

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- delete-customer --slug ahmet --confirm
```

### Reset only a customer database (keeps CentralDb customer record)

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- reset-customer-db --slug ahmet --confirm
```

After a reset, recreate an Owner admin user:

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- seed-customer-admin --slug ahmet --admin-email admin@ahmet.com --admin-password "Demo123!" --admin-name "Ahmet Admin"
```

### Reset all active customer databases

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- reset-all-customer-dbs --confirm
```

### Recommended clean local flow

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- delete-customer --slug ahmet --confirm
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- add-customer --name "Ahmet Restaurant" --slug ahmet --domain ahmet.wasla.local --admin-email admin@ahmet.com --admin-password "Demo123!" --admin-name "Ahmet Admin"
```

## 4) Create a customer (CLI)

Creates a tenant/customer record, creates+migrates the CustomerDb, and creates an admin user.

Example:

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- add-customer `
  --name "Demo Restaurant" `
  --slug demo `
  --domain demo.local `
  --admin-email admin@demo.local `
  --admin-password "Password123!" `
  --admin-name "Demo Admin" `
  --sql-auth trusted
```

> Note: Tenant resolution uses `PrimaryDomain`. For local testing, map the domain in your hosts file.

Windows hosts:
- `C:\Windows\System32\drivers\etc\hosts`
- Add: `127.0.0.1 demo.local`

## 5) Run Web

```powershell
dotnet run --project .\src\Wasla.Web\Wasla.Web.csproj
```

Browse:
- `https://demo.local:<port>/auth/login`

After login:
- `/dashboard`
- `/platform-connections`
- `/orders`
- `/branches`

## 6) Add a platform connection

In Web:
- Select platform
- Enter StoreId
- Enter ApiKey / ApiSecret

Duplicate rule:
- Same Platform + same StoreId is blocked.

## 7) Run Worker (sync)

```powershell
dotnet run --project .\src\Wasla.Worker\Wasla.Worker.csproj
```

Run it again:
- Orders should **not** duplicate
- The Order row must not be deleted/reinserted
- Items/options should not duplicate

