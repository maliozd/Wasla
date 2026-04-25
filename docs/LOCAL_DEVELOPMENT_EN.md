# OrderHub – Local Development (EN)

This document explains how to run the OrderHub MVP demo flow locally.

## Prerequisites

- .NET SDK (repo targets `net8.0`)
- SQL Server / LocalDB (default CentralDb uses LocalDB)
- `dotnet-ef` tool (for migrations)

## 1) Master key (required)

OrderHub requires a master key for encrypting/decrypting sensitive data.

PowerShell:

```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes)
setx ENCRYPTION_MASTER_KEY $key
```

Open a new terminal so the env var is reloaded.

## 2) Build

From repo root:

```powershell
dotnet clean
dotnet restore
dotnet build
```

## 3) Create a customer (CLI)

Creates a tenant/customer record, creates+migrates the CustomerDb, and creates an admin user.

Example:

```powershell
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- add-customer `
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

## 4) Run Web

```powershell
dotnet run --project .\src\OrderHub.Web\OrderHub.Web.csproj
```

Browse:
- `https://demo.local:<port>/auth/login`

After login:
- `/dashboard`
- `/platform-connections`
- `/orders`
- `/branches`

## 5) Add a platform connection

In Web:
- Select platform
- Enter StoreId
- Enter ApiKey / ApiSecret

Duplicate rule:
- Same Platform + same StoreId is blocked.

## 6) Run Worker (sync)

```powershell
dotnet run --project .\src\OrderHub.Worker\OrderHub.Worker.csproj
```

Run it again:
- Orders should **not** duplicate
- The Order row must not be deleted/reinserted
- Items/options should not duplicate

