# EF Core migrations

## Purpose and scope

This document describes CentralDb vs TenantDb migration contexts, design-time factories, CLI apply commands, and provisioning boundaries.

It does not inventory every historical migration file.

## Two contexts, two folders

| Context | Runtime use | Migration folder |
|---------|-------------|------------------|
| `CentralDbContext` | Central registry (tenants, admins, signup, Print Bridge devices, …) | `src/Wasla.Infrastructure/Persistence/Central/Migrations/` |
| `TenantDbContext` | Per-tenant operational DB (orders, users, connections, print jobs, …) | `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/` |

These are **different** EF models and histories. Do not generate a TenantDb migration against CentralDb (or the reverse).

CLI command names still say `migrate-customer` / `migrate-all-customers` even though product language is **tenant**. The databases are the per-tenant TenantDb instances.

## Design-time factories

| Factory | File |
|---------|------|
| `CentralDesignTimeDbContextFactory` | `src/Wasla.Infrastructure/Persistence/Central/CentralDesignTimeDbContextFactory.cs` |
| `TenantDesignTimeDbContextFactory` | `src/Wasla.Infrastructure/Persistence/Tenant/TenantDesignTimeDbContextFactory.cs` |

Notes:

- Both prefer connection info from `src/Wasla.Api/appsettings.json` (plus environment variables) when scaffolding.
- Tenant design-time factory uses `ConnectionStrings:CustomerDbDesignTime` if set; otherwise derives a scratch catalog `OrderHub_Customer_DesignTime` from the CentralDb server. That scratch DB is **not** used by Web/Worker/API at runtime.

## EF startup project

Use **`Wasla.Cli`** as the EF startup project. Do not add EF tooling dependencies to `Wasla.Web`.

Central and Tenant migrations are separate histories. Always pass `--context` and `--output-dir`. Do not let EF choose a migration folder.

`--output-dir` is relative to the Infrastructure project.

Central:

```powershell
dotnet ef migrations add <Name> `
  --project src\Wasla.Infrastructure\Wasla.Infrastructure.csproj `
  --startup-project src\Wasla.Cli\Wasla.Cli.csproj `
  --context CentralDbContext `
  --output-dir Persistence/Central/Migrations
```

Tenant:

```powershell
dotnet ef migrations add <Name> `
  --project src\Wasla.Infrastructure\Wasla.Infrastructure.csproj `
  --startup-project src\Wasla.Cli\Wasla.Cli.csproj `
  --context TenantDbContext `
  --output-dir Persistence/Tenant/Migrations
```

A missing `--output-dir` can write the migration into the wrong history. Do not generate a migration as part of a documentation or unrelated code change.

Requires `ENCRYPTION_MASTER_KEY` for most CLI invocation paths used after help (see [cli.md](cli.md)).

## Apply commands (CLI)

| Command | Effect |
|---------|--------|
| `migrate-central` | Apply pending CentralDb migrations |
| `migrate-customer` | Apply pending TenantDb migrations for **one** tenant (`--slug` or `--customer-id`) |
| `migrate-all-customers` | Apply pending TenantDb migrations for active tenants |
| `migration-status` | Show applied/pending status for CentralDb and tenant DBs |

### Dry-run

`migrate-all-customers --dry-run` lists pending migrations **without applying**.

`migrate-all-customers` also supports `--only <slug>` (repeatable) to limit tenants.

`provision-signup-request --dry-run` validates provisioning without creating DB/tenant/user (separate from EF dry-run).

## Provisioning vs middleware

- New tenant databases receive TenantDb migrations **inside the provisioning / `add-customer` flows**, not via Web or API request middleware.
- Tenant resolution must **not** create databases or run migrations.
- After pulling schema changes for existing tenants, operators run `migrate-central` and/or `migrate-all-customers` (or `migrate-customer`) explicitly.

## Operational warnings

- Applying the wrong context’s migrations to a database corrupts that database’s `__EFMigrationsHistory` relative to the model.
- SQL errors such as “Invalid column name” after a pull usually mean tenant (or central) migrations were not applied.
- In deployed environments every tenant migration must succeed before the new Web or Worker starts. See [deployment.md](deployment.md#release-order-migrate-every-database-first).
- Do not reset or squash migrations unless explicitly requested as a dedicated task.

## Related docs

- [cli.md](cli.md)
- [local-development.md](local-development.md)
- [../architecture/tenancy.md](../architecture/tenancy.md)
