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

- Registration / handlers: `src/Wasla.Cli/Program.cs`
- Help text and “known command” gate: `src/Wasla.Cli/CliHelpPrinter.cs`

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

All other registered commands require the master key (including tenant/customer ops, migrations, encrypt, Print Bridge seed/list, etc.).

## Destructive confirmation and production block

Commands:

- `delete-customer`
- `reset-customer-db`
- `reset-all-customer-dbs`

Require:

- `--confirm`
- In Production: also `--force-production` (still requires `--confirm`)

Without `--confirm`, the CLI refuses. In Production without `--force-production`, the CLI refuses.

## Command inventory

### Tenant / customer operations

| Command | Purpose |
|---------|---------|
| `add-customer` | Create tenant registry row, membership row, tenant database, migrations, first admin. Optional `--business-phone`, `--city`, `--country` feed the dashboard setup checklist |
| `provision-signup-request` | Provision a paid `PendingRegistration` into a live tenant (`--dry-run`, `--force`, SQL options) |
| `list-customers` | List tenants from CentralDb |
| `delete-customer` | Delete tenant record and drop tenant DB (destructive) |
| `reset-customer-db` | Drop/recreate one tenant DB; keep CentralDb row (destructive) |
| `reset-all-customer-dbs` | Drop/recreate all active tenant DBs (destructive) |
| `seed-customer-admin` | Create Owner admin in an existing tenant DB if missing |
| `update-customer-profile` | Set the restaurant business phone, city or country the setup checklist requires (`--tenant <slug-or-id>`). Creates the membership row if missing; idempotent |

CLI help still labels these as “Customer commands”; product terminology is **tenant**. The command names remain `*-customer*` / `list-customers`.

### Provisioning note

`provision-signup-request` is the payment-gated signup → live tenant path. Do not mark registrations Provisioned by hand in the DB. Web middleware must not provision.

Both `provision-signup-request` and `add-customer` create the tenant's single `TenantMemberships` row. The dashboard restaurant setup step reads the business phone and city or country from it. `add-customer` records an operator-provisioned membership (`Starter`, `Active`, no trial). Tenants created by `add-customer` before this change have no membership row; run `update-customer-profile` for a tenant that needs its setup checklist completed.

### Migrations

| Command | Purpose |
|---------|---------|
| `migrate-central` | Apply pending CentralDb migrations |
| `migrate-customer` | Apply pending TenantDb migrations for one tenant (legacy command name) |
| `migrate-all-customers` | Apply pending TenantDb migrations for active tenants; supports `--dry-run`, `--only` |
| `migration-status` | Show CentralDb and per-tenant migration status |
| `seed-turkey-reference-data` | Seed Turkish cities/districts into CentralDb |
| `seed-address-reference-data` | Import address reference data |

Details: [migrations.md](migrations.md).

### Central admins

| Command | Purpose |
|---------|---------|
| `add-central-admin` | Create central admin in CentralDb (`--email`, `--password`, `--display-name`) |
| `reset-central-admin-password` | Reset central admin password |
| `list-central-admins` | List central admins (safe fields) |

**This is the supported setup method.** Do not use `CentralAdmin__Email` / `CentralAdmin__PasswordHash` environment variables as the setup path — they are obsolete relative to current CentralDb authentication.

### Password reset

| Command | Purpose |
|---------|---------|
| `reset-password` | Reset central or tenant user password (`--scope central|tenant`, `--dry-run`) |

### Print Bridge

| Command | Purpose |
|---------|---------|
| `generate-print-bridge-token` | Register device and print one-time agent token |
| `seed-print-job` | Create pending receipt PrintJob (dev/testing) |
| `list-print-jobs` | List recent PrintJobs for a tenant (dev/testing) |

### Utility

| Command | Purpose |
|---------|---------|
| `encrypt` | Encrypt plaintext with `ENCRYPTION_MASTER_KEY` |
| `hash-password` | Print BCrypt hash (no config / master key) |
| `help` | Custom help |

## Known inconsistency: `create-user`

`Program.cs` registers a `create-user` command on `RootCommand` with a handler.

`CliHelpPrinter.IsKnownCommand` does **not** include `create-user`. The early gate in `Program.cs` treats unknown first tokens as errors and prints general help **before** `RootCommand.InvokeAsync`.

Therefore `create-user` is **not** a working supported command today. Documented here as a known inconsistency only — do not present it as operational.

Use `seed-customer-admin` or tenant user management in Web for admin/user creation paths that actually run.

## Related docs

- [migrations.md](migrations.md)
- [local-development.md](local-development.md)
- [../integrations/print-bridge.md](../integrations/print-bridge.md)
