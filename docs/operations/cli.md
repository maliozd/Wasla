# Wasla CLI

## Purpose and scope

This document inventories the operational CLI (`Wasla.Cli`) used for tenant onboarding, migrations, central admins, Print Bridge helpers, and utilities.

It owns command lists, confirmation/production guards, and `ENCRYPTION_MASTER_KEY` requirements.

It does not own EF migration folder layout details beyond command names (see [migrations.md](migrations.md)).

## How to run

From the repository root:

```powershell
dotnet run --project src\Wasla.Cli -- <command> [options]
```

General help (no master key required):

```powershell
dotnet run --project src\Wasla.Cli -- help
dotnet run --project src\Wasla.Cli -- help migrate-central
```

Sources of truth:

- Early gates (help, unknown command, master key, CentralDb): `src/Wasla.Cli/Program.cs`
- Registered commands, options and handlers: `src/Wasla.Cli/CliCommandTree.cs`
- Help text and “known command” gate: `src/Wasla.Cli/CliHelpPrinter.cs`

Every command and option has exactly one name, with no aliases. `tests/Wasla.UnitTests/Cli/CliCommandInventoryTests.cs` pins the registered tree, the help gate and the help text.

## Configuration sources

`src/Wasla.Cli/CliConfiguration.cs` (WAS-93), lowest to highest precedence:

1. `appsettings.json` next to the CLI binary (not the working directory)
2. `appsettings.{Environment}.json` next to the CLI binary
3. User secrets (Development only)
4. Environment variables, for example `ConnectionStrings__CentralDb`, `TenantDb__ServerInstance`
5. Command-line arguments

So a deployment's environment variables always override the committed JSON. Before WAS-93 the CLI added `appsettings.json` again after these sources, so its values beat environment variables.

The committed base `appsettings.json` names no CentralDb and no tenant SQL Server. `appsettings.Development.json` sets both to LocalDB; anywhere else, set them through the environment. Without a CentralDb every command past `help` and `hash-password` stops before touching a database.

## ENCRYPTION_MASTER_KEY

Most commands call `AesSecretManager.ValidateMasterKeyOrThrow()` before the host builds.

**Does not require** `ENCRYPTION_MASTER_KEY`:

- `help` / `--help`
- `hash-password`
- `add-central-admin`
- `reset-central-admin-password`
- `list-central-admins`
- `generate-print-bridge-token`
- `reset-password` when `--scope central`

All other registered commands require the master key (including tenant operations, migrations, encrypt, `create-user`, Print Bridge seed/list, etc.).

## Destructive confirmation and production block

Commands:

- `delete-tenant`
- `reset-tenant-db`
- `reset-all-tenant-dbs`

Require:

- `--confirm`
- In Production: also `--force-production` (still requires `--confirm`)

Without `--confirm`, the CLI refuses. In Production without `--force-production`, the CLI refuses. The environment is `DOTNET_ENVIRONMENT`, then `ASPNETCORE_ENVIRONMENT`; when neither is set the CLI counts as Production, as the .NET host does (WAS-93).

Before dropping or recreating a tenant database, these commands also refuse (WAS-93, `src/Wasla.Cli/CliDestructiveGuards.cs`):

- CentralDb (the catalog in `ConnectionStrings:CentralDb`, compared case-insensitively) and SQL Server system databases.
- A missing or unreadable `ConnectionStrings:CentralDb`, or a stored tenant connection string that names no database.
- A tenant whose stored connection string targets a database other than its `DatabaseName`.
- A database named by more than one tenant row.

`reset-all-tenant-dbs` reports a refused tenant as failed, leaves it untouched and continues with the others.

Every command past `help` and `hash-password` stops when `ConnectionStrings:CentralDb` is not configured.

## Command inventory

### Tenant operations

| Command | Purpose |
|---------|---------|
| `add-tenant` | Create tenant registry row, membership row, tenant database, migrations, first admin. Optional `--business-phone`, `--city`, `--country` feed the dashboard setup checklist. The database name is server-assigned (`Wasla_{SlugLabel}_{random}`), and an existing database is never adopted ([tenancy](../architecture/tenancy.md#tenant-database-identity-was-93)) |
| `provision-signup-request` | Provision a paid `PendingRegistration` into a live tenant (`--dry-run`, `--force`, SQL options). `--force` only retries a registration marked Provisioned without a tenant row; it never adopts a database without this registration's ownership marker. A second attempt for the same registration while one runs is refused |
| `list-tenants` | List tenants from CentralDb |
| `delete-tenant` | Delete tenant record and drop tenant DB (destructive; `--slug` or `--tenant-id`) |
| `reset-tenant-db` | Drop/recreate one tenant DB; keep CentralDb row (destructive; `--slug` or `--tenant-id`) |
| `reset-all-tenant-dbs` | Drop/recreate all active tenant DBs (destructive) |
| `seed-tenant-admin` | Create Owner admin in an existing tenant DB if missing |
| `update-tenant-profile` | Set the restaurant business phone, city or country the setup checklist requires (`--tenant <slug-or-id>`). Creates the membership row if missing; idempotent |

### Provisioning note

`add-tenant` and `provision-signup-request` need an explicit SQL Server for the tenant database: `--sql-server`, else `TenantDb:ServerInstance`. No other key is read and there is no default; without one they stop before touching any database (`--dry-run` also fails). The committed CLI `appsettings.json` names no server.

`provision-signup-request` is the payment-gated signup → live tenant path. Do not mark registrations Provisioned by hand in the DB. Web middleware must not provision.

Both `provision-signup-request` and `add-tenant` create the tenant's single `TenantMemberships` row. The dashboard restaurant setup step reads the business phone and city or country from it. `add-tenant` records an operator-provisioned membership (`Starter`, `Active`, no trial). Tenants that an earlier CLI version created before the CLI began recording memberships have no membership row; run `update-tenant-profile` for a tenant that needs its setup checklist completed.

### Migrations

| Command | Purpose |
|---------|---------|
| `migrate-central` | Apply pending CentralDb migrations |
| `migrate-tenant` | Apply pending TenantDb migrations for one tenant (`--slug` or `--tenant-id`) |
| `migrate-all-tenants` | Apply pending TenantDb migrations for active tenants; supports `--dry-run`, `--only` |
| `migration-status` | Show CentralDb and per-tenant migration status; `--require-current` provides a strict, read-only release gate (pending, unknown or unreadable schema exits non-zero) |
| `seed-turkey-reference-data` | Seed Turkish cities/districts into CentralDb |
| `seed-address-reference-data` | Import address reference data |

Details: [migrations.md](migrations.md).

### Central admins

| Command | Purpose |
|---------|---------|
| `add-central-admin` | Create central admin in CentralDb (`--email`, `--password`, `--display-name`) |
| `reset-central-admin-password` | Reset central admin password. Also ends that admin's existing Central Admin sessions |
| `list-central-admins` | List central admins (safe fields) |

There is no command that deactivates or reactivates a central admin; see [../architecture/authentication.md](../architecture/authentication.md#central-admin-session-revalidation) for the CentralDb statement that also ends the admin's sessions.

**This is the supported setup method.** Do not use `CentralAdmin__Email` / `CentralAdmin__PasswordHash` environment variables as the setup path — they are obsolete relative to current CentralDb authentication.

### Tenant users

| Command | Purpose |
|---------|---------|
| `create-user` | Add a user with a role to an existing, active tenant (`--tenant-slug`, `--email`, `--password`, `--role`, optional `--name`) |

**Who may run it.** `create-user` is a local administrative command for the server operator, not a tenant feature. It works only where the operator already has `ENCRYPTION_MASTER_KEY` (it decrypts the tenant's stored connection string), the CentralDb connection and SQL Server access to the tenant database. Without the master key the CLI stops before it reads any configuration. The command has no acting user, so the Web panel's acting-Owner rules ([roles-and-permissions.md](../product/roles-and-permissions.md#user-management-rules-acting-owner-own-account-last-owner)) do not apply to it; it can add an Owner. Tenant Owners add users on the Web panel's Users page (`/settings/users`).

**Checks.** Nothing is written unless every check passes (`src/Wasla.Cli/CliCommands.cs`, `CreateUserAsync`):

- `--tenant-slug` matches `^[a-zA-Z0-9_-]+$` and is the slug of a tenant in CentralDb. An inactive tenant is refused.
- `--email`, trimmed, is a valid email address (the Web create form's `[EmailAddress]` check) that no user of that tenant has. The comparison follows the tenant database's collation, case-insensitive on SQL Server; the unique email index refuses an email another writer adds at the same moment.
- `--role` is `Owner`, `Manager`, `Kitchen`, `Cashier` or `Viewer`, by name, ignoring case. A number, a list such as `Owner,Manager` and the obsolete `Staff` are refused.
- `--password` passes the Web panel's password policy (`DefaultPasswordPolicy`: not blank, at least 8 characters).

The user is created active, for all branches, with only a BCrypt hash of the password. `--name` is optional: without it the user has an empty name, which the Web create form would not accept; an Owner can add it on the Users page.

**Output and secrets.** On success the command prints the email and the role. Its own messages never include the password, its hash or an exception message, so the tenant's decrypted connection string cannot reach the output. As for every CLI command, EF Core's error log (on at `Warning` in the CLI's `appsettings.json`) prints a failed CentralDb read with its SQL Server error, server and database name, never a password. `--password` is visible in shell history and in the process list while the command runs. To keep the real password out of both, create the user with a temporary password and set the real one with `reset-password --scope tenant`, which prompts for it.

Exit codes: `0` created; `2` a check failed; `3` a SQL Server or EF Core save error: CentralDb or the tenant database could not be reached or refused the write (for example through the unique email index); `1` any other failure, and a missing required option, which the parser reports before the command runs. Nothing is written unless the code is `0`.

Example (fake values):

```powershell
dotnet run --project src\Wasla.Cli -- create-user --tenant-slug demo --email kitchen@example.com --password "Fake-password-1" --role Kitchen --name "Demo Kitchen"
```

### Password reset

| Command | Purpose |
|---------|---------|
| `reset-password` | Reset central or tenant user password (`--scope central|tenant`, `--dry-run`). Central scope also ends that admin's existing Central Admin sessions; tenant scope ends that user's existing Web sessions |

### Print Bridge

| Command | Purpose |
|---------|---------|
| `generate-print-bridge-token` | Register device and print one-time agent token (`--tenant-id` or `--slug`) |
| `seed-print-job` | Create pending receipt PrintJob (dev/testing; `--slug` or `--tenant-id`) |
| `list-print-jobs` | List recent PrintJobs for a tenant (dev/testing; `--slug` or `--tenant-id`) |

### Webhook pilot

Trendyol GO Meal webhook stage pilot only ([../integrations/trendyol-go-meal-webhook-stage-pilot.md](../integrations/trendyol-go-meal-webhook-stage-pilot.md)). Both read `TrendyolGoMealWebhookPilot`. `meal-webhook-integrator` runs only when `ProviderApiBaseUrl` is exactly `https://stageapi.tgoapis.com`, and talks only to that API.

| Command | Purpose |
|---------|---------|
| `meal-webhook-integrator` | `--action create` registers the integrator for `ReceiverBaseUrl` with the pilot connection's seller credentials, writes its token to `--token-file` (new file, owner-only) and prints only the SHA-256 for `SecretSha256`; `show`, `enable`, `disable`, `test-order` use the token. `show`, `enable` and `test-order` fail closed unless Get Integrator reports exactly the expected base URL, timeout, seller and event destinations; `enable` reads the integrator back and disables it again if it differs |
| `meal-webhook-inbox` | List received events (delivery, status, outcome, matched order code and status; never payloads); `--replay <event-id>` queues a Failed or Processed event again |

### Utility

| Command | Purpose |
|---------|---------|
| `encrypt` | Encrypt plaintext with `ENCRYPTION_MASTER_KEY` |
| `hash-password` | Print BCrypt hash (no config / master key) |
| `help` | Custom help |

## Related docs

- [migrations.md](migrations.md)
- [local-development.md](local-development.md)
- [../integrations/print-bridge.md](../integrations/print-bridge.md)
