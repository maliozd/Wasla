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
- `disable-central-admin`
- `enable-central-admin`
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
| `reset-central-admin-password` | Reset central admin password; signs out that admin's existing sessions |
| `list-central-admins` | List central admins (safe fields) |
| `disable-central-admin` | Disable a central admin (`--email`, `--dry-run`); signs out all of their existing sessions |
| `enable-central-admin` | Enable a disabled central admin (`--email`, `--dry-run`); sessions issued before the disable stay signed out |

**These are the supported account-management commands.** Do not use `CentralAdmin__Email` / `CentralAdmin__PasswordHash` environment variables as the setup path — they are obsolete relative to current CentralDb authentication.

A password reset (either reset command) and a disable or enable that actually changes the status give the admin a new `SecurityStamp` in the same UPDATE as the change. Every session issued before it is rejected on its next request. See [Central admin session revalidation](../architecture/authentication.md#central-admin-session-revalidation).

`disable-central-admin` and `enable-central-admin`:

- Resolve the account like `reset-password --scope central`: trimmed, case-insensitive email; no match or more than one match exits `2` without changes.
- Print the account by masked email (`m***@example.com`) and its current status. They never print the full email, display name, password hash or stamp.
- `--dry-run` shows what would change and writes nothing.
- Rotate the stamp only on a real transition (enabled to disabled, or disabled to enabled). An account already in the requested state is left untouched: no write and no new stamp (exit `0`). Repeating a command is therefore safe, but `enable-central-admin` on an account that is already enabled revokes no session.
- Are reversible and do not delete data, so they do not take `--confirm` (see [destructive confirmation](#destructive-confirmation-and-production-block)).
- Exit `3` on a database failure, without SQL details. Check the account with `list-central-admins` and rerun.

**Direct SQL and EF bulk updates bypass these safeguards.** The stamp rotation runs only in `CentralDbContext.SaveChanges`. SQL against `CentralAdminUsers` and EF `ExecuteUpdate` / `ExecuteSqlRaw` do not rotate it. Re-enabling a row that way revives every cookie issued before the disable, and a password change that way revokes nothing. Any such `UPDATE` that changes `IsActive` or `PasswordHash` must set `SecurityStamp = NEWID()` in the same statement.

If that was missed: for an account that is still disabled, re-enable it with `enable-central-admin`; the transition rotates the stamp. For an account that was already re-enabled, or whose password was changed that way, `enable-central-admin` does nothing. Rotate the stamp explicitly instead. See [Writes that bypass the rotation](../architecture/authentication.md#writes-that-bypass-the-rotation).

### Password reset

| Command | Purpose |
|---------|---------|
| `reset-password` | Reset central or tenant user password (`--scope central|tenant`, `--dry-run`). Central scope signs out that admin's existing sessions; tenant scope does not revoke tenant sessions |

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
