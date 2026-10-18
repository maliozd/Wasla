# Terminology

## Purpose and scope

This document defines the durable product and engineering terms used in Wasla developer docs and code discussion. It owns naming for products, Tenant vs order Customer, databases, platform connections, and a few product surfaces referenced elsewhere. It also owns the [clean-break policy](#clean-break-policy) for retired names.

UI localization (Turkish-friendly default, cultures `tr-TR`, `en-US`, `ar-SA`, `ru-RU`) remains supported; **developer docs are English only**.

## Products and brand

- **Wasla** is the brand and the product family. **Wasla Orders** is the product this repository implements; **Wasla POS** is a separate product (see [product-boundaries.md](product-boundaries.md)).
- **Wasla Print Bridge** is the Windows printing client of Wasla Orders.
- Product names, namespaces, configuration sections, paths, cookie names, log names, CLI text and user-facing text use Wasla naming only. The former product name is retired (see [Clean-break policy](#clean-break-policy)).

## Tenant

A **Tenant** is one independently operated restaurant location in the current architecture. It is the data-isolation boundary, the tenant-database boundary, and the operational boundary for that location: orders, Live Screen, platform connections, Print Bridge, settings, and users.

Examples:

- Central registry row (`Wasla.Domain.Entities.Central.Tenant`)
- Host `{slug}.wasla.local` / production tenant domain
- Owner of one dedicated tenant database

A business that operates several restaurants uses a separate tenant for each location. A future grouping above those tenants is not implemented. See [tenancy.md](../architecture/tenancy.md).

Naming by audience:

- **Internally** (code, configuration keys, CLI commands and options, logs, developer docs) the concept is **Tenant**: `TenantId`, `TenantDb`, tenant commands.
- **In the restaurant-facing and Central Admin UI** the concept is **Restaurant**, in the localized form of the current culture (for example *Restoran* in `tr-TR`). `/admin/restaurants` is the only Central Admin route for tenant pages (moved there by WAS-138).

Do not call the person who ordered food a Tenant.

## Branch (current settings record)

`Branch` is a tenant-database settings row (name, address, active flag) with an optional `AppUser.BranchId`. It is not a restaurant location and not an organization. Multiple locations remain separate tenants. See [tenancy.md](../architecture/tenancy.md).

## Order Customer (end customer)

On an order, **Customer** means the restaurant’s end customer (food orderer / delivery recipient), **not** the SaaS tenant. `Customer` naming is valid **only** for this person.

Keep these order-customer names as Customer:

- `CustomerName`, `CustomerPhone`, `CustomerAddress`, `CustomerNote` on the `Order` entity, its DTOs, view models and API contracts
- `GuidedDemoSession.CustomerNameKey` (demo order data)
- Receipt settings `ShowCustomerName` / `ShowCustomerPhone` and the receipt "customer & delivery" content
- Resource keys and UI text that describe the person who placed the order (for example the order details and dashboard customer columns)
- Customer fields in provider payload mapping and mock provider fixtures

Do not rename these to Tenant. Conversely, a member that holds a tenant id or a restaurant name is a tenant concept even when its current name says Customer; it uses Tenant naming in code and Restaurant in the UI.

## CentralDb

The central SQL database that holds the tenant registry, central admin users, pending registrations, Print Bridge device registry, and related central metadata.

**Context:** `CentralDbContext`

**Path:** `src/Wasla.Infrastructure/Persistence/Central/CentralDbContext.cs`

## Tenant database

Each tenant has its own operational SQL database (orders, tenant users, platform connections, print jobs, settings).

**Context:** `TenantDbContext`

**Factory:** `ITenantDbContextFactory` / `TenantDbContextFactory`

**Path:** `src/Wasla.Infrastructure/Persistence/Tenant/`

**Entity namespace (target):** `Wasla.Domain.Entities.TenantDb`. Until WAS-142 lands, the tenant-database entity types still live in a namespace with a retired name; WAS-142 moves them without changing the physical schema.

### Agent warnings

Use `TenantDbContext`, `ITenantDbContextFactory` and `ICurrentTenantService` for tenant data and tenant context. Do not reintroduce retired context or service names, and do not provision tenant databases from request middleware. See [tenancy.md](../architecture/tenancy.md).

## PlatformConnection

A **PlatformConnection** is a tenant-DB record linking that restaurant location to one food-delivery platform.

Each tenant may have at most one PlatformConnection per platform. `StoreId` identifies the tenant's location in the external provider. It is provider configuration, not part of connection uniqueness. Extra physical restaurants are separate tenants, not extra `StoreId`s on one tenant.

**Sources:** `PlatformConnection` entity, `PlatformConnectionService`, unique index `IX_PlatformConnections_Platform`. See [tenancy.md](../architecture/tenancy.md).

## Live Screen vs Orders

**Orders** is the management/history surface for searching, filtering, and operating on orders (`OrdersController`, policy `CanViewOrders` / mutations under `CanManageOrders`).

**Live Screen** is the operational kitchen/live display (`/orders/live-display` and related live endpoints, policy `CanViewLiveScreen`). It shares order data services with Orders but is a separate UX and authorization surface.

Do not expand Live Screen architecture here; link only. Server policies: [roles-and-permissions.md](roles-and-permissions.md).

## Print Bridge

**Wasla Print Bridge** is the Windows local printing client. Web/Worker create print jobs; the bridge claims jobs with device authentication (`X-PrintBridge-Token`) and submits them to Windows printing. Device registry lives in CentralDb; jobs live in the tenant database.

Setup centers on Wasla Web panel URL (`ServerUrl`) and device token — not internal API path knowledge. See project Print Bridge rules for operational defaults.

Target state: the WebView2 interface is the only Print Bridge UI (WAS-65), with no classic window, fallback switch or pilot mode, and the client's settings, paths, labels and contract names use Wasla and Tenant naming only (WAS-135). Re-pairing development installations is acceptable.

## Clean-break policy

The former product name and tenant-meaning Customer names are retired in one intentional clean break. The target state: outside the [historical migration exception](#historical-migration-exception), the tracked repository (source, tests, documentation, scripts, configuration and CI files) contains no retired product branding, no tenant-as-Customer terminology and no compatibility behavior for either.

There is **no compatibility layer**:

- no aliases or fallback configuration keys, and no dual-read or dual-write period;
- no deprecated or compatibility CLI commands and options;
- no routes or redirects for retired paths;
- no old authentication cookie names and no old Data Protection or filesystem paths;
- no classic Print Bridge UI, fallback switch or old Print Bridge settings section;
- no retired identifiers kept only for tests;
- no retired strings hidden by fragmenting, encoding, character classes, concatenation or runtime reconstruction.

Working rules:

- New code, configuration, documentation and tests use only the canonical names in this document.
- Retired names that still exist in the code are removed by their owning clean-break issue (WAS-65 and WAS-135 to WAS-144), not opportunistically during unrelated work. When unrelated work touches such code, do not add new uses and do not add compatibility code.
- Session invalidation and re-pairing development Print Bridge installations are acceptable for this development-stage cutover. The break is safe only while there is no valuable production data.
- The list of retired names and their canonical replacements lives only in Linear (WAS-134). Do not copy it into the repository in any form.

### Testing policy

Clean-break tests verify the canonical surface **positively**. They never contain a retired name: not literally, not split into fragments, not encoded, not behind a character class and not reconstructed at runtime. Do not write compatibility-style negative tests (an old key is ignored, an old route returns 404), and do not create test-file allowlists or special exception folders for them.

Prefer inventory assertions that fail when anything extra appears, for example:

- the exact set of configuration keys and option sections that are bound and documented;
- the exact set of CLI commands and options, with no aliases;
- the exact set of Admin route templates, with only `/admin/restaurants` for tenants;
- the exact set of authentication cookie names and the configured Data Protection key path;
- the exact top-level Print Bridge settings sections and contract property names;
- the exact set of Print Bridge window types, so that no classic window can be started or reached;
- public type and member names, where every `Customer` name is an order-customer concept.

### Repository scan

The clean-break scan commands and the retired-term patterns are kept in Linear (WAS-134) and are not copied into the repository. Each clean-break issue runs the scan on the paths it changes. WAS-144 adds the repository-wide CI guard, which reads the retired-term patterns from a GitHub Actions repository variable so that no tracked file has to contain them.

### Historical migration exception

EF Core migration history is not reset, deleted, squashed, renamed or regenerated in the current phase. The only files allowed to keep retired identifiers are these 15 historical tenant migration designer files, because changing them would rewrite migration history:

1. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260614190906_InitialTenantSchema.Designer.cs`
2. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260623215553_AddPrintJobInstallationOwnership.Designer.cs`
3. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260623220646_AddTenantPasswordResetTokens.Designer.cs`
4. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260925124839_AddOrderCustomerNote.Designer.cs`
5. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260926100229_EnforceOnePlatformConnectionPerPlatform.Designer.cs`
6. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260928060853_AddGuidedDemoSessionSingleOpenIndex.Designer.cs`
7. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260928113614_AddUserGuidedSetupStates.Designer.cs`
8. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261001233557_AddTenantOperationalMode.Designer.cs`
9. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261006092124_AddAppUserLastLoginAt.Designer.cs`
10. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261008093450_AddAppUserSecurityStamp.Designer.cs`
11. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261013072101_AddPrintJobStatusVersion.Designer.cs`
12. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261013112832_AddOrderChangeVersion.Designer.cs`
13. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261014155220_AddOrderItemSequence.Designer.cs`
14. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261017060726_AddOrderTimeSemantics.Designer.cs`
15. `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261018073348_AddRestaurantLogo.Designer.cs`

Rules:

- The exception covers these exact files only, never a directory, glob or `*.Designer.cs` pattern. No CentralDb migration file is on the list.
- Do not edit these files.
- Not exempt: both model snapshots, every other migration file, the DbContexts, entity configurations and all runtime model code. WAS-142 moves the tenant model and its snapshot to Tenant naming without changing the physical schema.
- The list is closed and may only shrink. A file is added only by an explicit product decision recorded in WAS-134, and this list must match that record.
- Every newly generated migration must contain no retired identifier. Until WAS-142 lands, avoid generating tenant migrations; if one is unavoidable, get that decision first.
- WAS-143, the deferred destructive migration-baseline reset, later replaces these files with clean Wasla baselines and removes this exception. It is scheduled for a separate maintenance window and is not a prerequisite for the current rename work.

## Older documents

Non-canonical documents listed in [../README.md](../README.md) may still use retired names until WAS-144 rewrites or removes them. This file and `src/Wasla.Domain/Entities/Central/Tenant.cs` win for current naming.

## Related docs

- [tenancy.md](../architecture/tenancy.md)
- [authentication.md](../architecture/authentication.md)
- [roles-and-permissions.md](roles-and-permissions.md)
- [product-boundaries.md](product-boundaries.md)
