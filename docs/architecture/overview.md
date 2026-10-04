# Architecture overview

## Purpose and scope

This page is a map of the running Wasla processes and where to read details.

It does not replace the topic documents. Source code is authoritative when a document and the code disagree. Some behaviors described in the topic docs are present in the working tree and not yet committed; those docs say so.

## Processes

```text
Browser
  → Wasla.Web          tenant UI, signup, central admin, Print Bridge HTTP
  → Wasla.Api          JSON + Print Bridge HTTP (separate host in local dev)

Wasla.Worker           order sync only (no HTTP server)
Wasla.Cli              migrations, provisioning, destructive tenant ops
Wasla.PrintBridge      Windows process; polls Web or API and prints locally

CentralDb              tenant registry, admins, devices, signup
Tenant DB (one each)   orders, users, connections, print jobs
Providers              Trendyol GO, Yemeksepeti, Getir (see integrations)
```

| Process | Role | Read next |
|---------|------|-----------|
| `Wasla.Web` | MVC/Razor tenant and public UI | [authentication.md](authentication.md), [../frontend/architecture.md](../frontend/architecture.md) |
| `Wasla.Api` | JSON and Print Bridge device API | [authentication.md](authentication.md), [../integrations/print-bridge.md](../integrations/print-bridge.md) |
| `Wasla.Worker` | Background order sync | [../orders/synchronization.md](../orders/synchronization.md) |
| `Wasla.Cli` | Operations and EF startup project | [../operations/cli.md](../operations/cli.md) |
| `Wasla.PrintBridge` | Local Windows printing | [../integrations/print-bridge.md](../integrations/print-bridge.md) |

Web controllers call Application services. They do not use EF or provider clients directly. `Program.cs` is the composition root.

## Data

Wasla uses **CentralDb plus one SQL Server database per tenant**. A tenant is one independently operated restaurant location. Tenant resolution looks up the registry. It does not create databases.

Each tenant has at most one platform connection per platform. `StoreId` is that location's identifier at the provider, not a way to put several restaurants in one tenant. Multi-location grouping above tenants is not implemented.

Details: [tenancy.md](tenancy.md).

## Product and surface boundaries

This repository implements Wasla Orders. Wasla POS is a separate product direction
with no project or module in this solution. See
[Product boundaries](../product/product-boundaries.md).

Wasla Orders keeps its management and operational surfaces separate:

| Surface | Job |
|---------|-----|
| `/orders` | Management, search, and history |
| Live Screen (`/orders/live-display`) | Operational handling; polls a snapshot |

Details: [../orders/live-screen.md](../orders/live-screen.md), [../orders/lifecycle.md](../orders/lifecycle.md).

## Where to go

| Topic | Document |
|-------|----------|
| Tenancy and encryption | [tenancy.md](tenancy.md) |
| Cookies and schemes | [authentication.md](authentication.md) |
| Words: Tenant vs order Customer | [../product/terminology.md](../product/terminology.md) |
| Roles | [../product/roles-and-permissions.md](../product/roles-and-permissions.md) |
| Providers | [../integrations/food-platforms.md](../integrations/food-platforms.md) |
| Deploy, health, logs | [../operations/deployment.md](../operations/deployment.md), [../operations/observability.md](../operations/observability.md) |

Index of every canonical doc: [../README.md](../README.md).
