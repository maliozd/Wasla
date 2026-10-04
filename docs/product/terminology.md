# Terminology

## Purpose and scope

This document defines the durable product and engineering terms used in Wasla developer docs and code discussion. It owns naming for Tenant vs order Customer, databases, platform connections, and a few product surfaces referenced elsewhere.

UI localization (Turkish-friendly default, cultures `tr-TR`, `en-US`, `ar-SA`, `ru-RU`) remains supported; **developer docs are English only**.

## Tenant

A **Tenant** is one independently operated restaurant location in the current architecture. It is the data-isolation boundary, the tenant-database boundary, and the operational boundary for that location: orders, Live Screen, platform connections, Print Bridge, settings, and users.

Examples:

- Central registry row (`Wasla.Domain.Entities.Central.Tenant`)
- Host `{slug}.wasla.local` / production tenant domain
- Owner of one dedicated tenant database

A business that operates several restaurants uses a separate tenant for each location. A future grouping above those tenants is not implemented. See [tenancy.md](../architecture/tenancy.md).

Use tenant terminology for that location account in new docs and product language. Do not call the person who ordered food a Tenant.

## Branch (current settings record)

`Branch` is a tenant-database settings row (name, address, active flag) with an optional `AppUser.BranchId`. It is not a restaurant location and not an organization. Multiple locations remain separate tenants. See [tenancy.md](../architecture/tenancy.md).

## Order Customer (end customer)

On an order, **Customer** means the restaurant’s end customer (food orderer / delivery recipient), **not** the SaaS tenant.

Keep these order fields as Customer:

- `CustomerName`
- `CustomerPhone`
- `CustomerAddress`
- `CustomerNote`

**Source:** `src/Wasla.Domain/Entities/Customer/Order.cs`

Do not rename these fields to Tenant.

## CentralDb

The central SQL database that holds the tenant registry, central admin users, pending registrations, Print Bridge device registry, and related central metadata.

**Context:** `CentralDbContext`

**Path:** `src/Wasla.Infrastructure/Persistence/Central/CentralDbContext.cs`

## Tenant database

Each tenant has its own operational SQL database (orders, tenant users, platform connections, print jobs, settings).

**Context:** `TenantDbContext`

**Factory:** `TenantDbContextFactory`

**Path:** `src/Wasla.Infrastructure/Persistence/Tenant/`

Entity types for that database still use the legacy C# namespace `Wasla.Domain.Entities.Customer`. That is historical packaging, not a second SaaS “customer” registry.

### Agent warnings

Do **not** recreate `CustomerDbContext`, `CurrentCustomer`, or provision tenant DBs from request middleware. See [tenancy.md](../architecture/tenancy.md).

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

## Legacy “customer” CLI / type naming

Some CLI commands and parameters still say “customer” while meaning the SaaS tenant, for example:

- `add-customer`
- `migrate-customer`
- `delete-customer`
- `reset-customer-db`
- `--customer-id` on some Print Bridge CLI helpers

`AuthSessionResult.CustomerId` remains a legacy property name whose value is the tenant id used in the `TenantId` claim.

These names must **not** be opportunistically renamed during unrelated feature work. Prefer a dedicated rename effort if product decides to clean them up.

## Stale doc note

`docs/tenant-vs-customer.md` is partially outdated (for example it still points at Central `Customer.cs`). Prefer this file and `src/Wasla.Domain/Entities/Central/Tenant.cs` for current naming.

## Related docs

- [tenancy.md](../architecture/tenancy.md)
- [authentication.md](../architecture/authentication.md)
- [roles-and-permissions.md](roles-and-permissions.md)
