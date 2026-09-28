# Wasla developer documentation

Canonical developer and agent documentation is **English**. Turkish, English, Arabic, and Russian remain UI languages. This folder is not a second product manual.

Source code wins when a document is wrong. Do not “fix” code to match a stale doc. Label legacy `OrderHub` names as current config debt when they still exist in source.

Older files in this folder (`LOCAL_DEVELOPMENT_*.md`, `deployment-config.md`, `tenant-vs-customer.md`, `wasla-design-language-v1.md`, `orders-printjob-status-signalr.md`, scratch `.txt` files) are **not** canonical. They stay until a later cleanup. Prefer the root `README.md` and the map below.

`docs/future/` is planning only. It is not current architecture.

`docs/product/onboarding.md` is not written yet. Signup and provisioning entry points are summarized in [operations/cli.md](operations/cli.md) and [architecture/tenancy.md](architecture/tenancy.md).

## Map

| Path | Owns |
|------|------|
| [architecture/overview.md](architecture/overview.md) | Process map and links |
| [architecture/tenancy.md](architecture/tenancy.md) | One location per tenant, CentralDb, databases, one connection per platform |
| [architecture/authentication.md](architecture/authentication.md) | Cookie schemes and Web/API session limits |
| [orders/lifecycle.md](orders/lifecycle.md) | Statuses, operator actions, sync merge |
| [orders/synchronization.md](orders/synchronization.md) | Worker, pagination, upsert |
| [orders/live-screen.md](orders/live-screen.md) | Operational UI versus Orders |
| [integrations/food-platforms.md](integrations/food-platforms.md) | Trendyol GO, Yemeksepeti, Getir, Mock/Real |
| [integrations/print-bridge.md](integrations/print-bridge.md) | Desktop printing client |
| [frontend/architecture.md](frontend/architecture.md) | Razor, Bootstrap, vanilla JS |
| [frontend/design-system.md](frontend/design-system.md) | Tokens; `wasla-foundation.css` is the foundation |
| [operations/local-development.md](operations/local-development.md) | Local runbook |
| [operations/cli.md](operations/cli.md) | CLI commands and destructive guards |
| [operations/migrations.md](operations/migrations.md) | Central vs tenant EF |
| [operations/deployment.md](operations/deployment.md) | Hosts, keys, config |
| [operations/observability.md](operations/observability.md) | Logs, trace id, health |
| [operations/testing.md](operations/testing.md) | What to run, and what not to claim |
| [product/product-boundaries.md](product/product-boundaries.md) | Wasla Orders, Wasla POS, and shared-infrastructure boundaries |
| [product/terminology.md](product/terminology.md) | Tenant vs order customer |
| [product/roles-and-permissions.md](product/roles-and-permissions.md) | Server-side role matrix |
| [future/printjob-status-signalr.md](future/printjob-status-signalr.md) | Future PrintJob SignalR only |

## Task → documents

| Task | Read first |
|------|------------|
| Any change | [architecture/overview.md](architecture/overview.md) |
| Product scope or POS | [product/product-boundaries.md](product/product-boundaries.md) |
| Tests / verification | [operations/testing.md](operations/testing.md) |
| Hosts, databases, secrets | [architecture/tenancy.md](architecture/tenancy.md) |
| Login, roles, password reset | [architecture/tenancy.md](architecture/tenancy.md), [architecture/authentication.md](architecture/authentication.md), [product/roles-and-permissions.md](product/roles-and-permissions.md) |
| Live Screen | [orders/live-screen.md](orders/live-screen.md), [orders/lifecycle.md](orders/lifecycle.md), [frontend/architecture.md](frontend/architecture.md), [frontend/design-system.md](frontend/design-system.md) |
| Orders management UI | [orders/lifecycle.md](orders/lifecycle.md), [frontend/architecture.md](frontend/architecture.md) |
| Provider or sync | [architecture/tenancy.md](architecture/tenancy.md), [orders/synchronization.md](orders/synchronization.md), [integrations/food-platforms.md](integrations/food-platforms.md) |
| Print Bridge | [architecture/tenancy.md](architecture/tenancy.md), [integrations/print-bridge.md](integrations/print-bridge.md) |
| Signup or provisioning | [architecture/tenancy.md](architecture/tenancy.md), [operations/cli.md](operations/cli.md) |
| CLI | [operations/cli.md](operations/cli.md), [architecture/tenancy.md](architecture/tenancy.md) |
| Migrations | [architecture/tenancy.md](architecture/tenancy.md), [operations/migrations.md](operations/migrations.md) |
| Deploy or config | [operations/deployment.md](operations/deployment.md), [operations/observability.md](operations/observability.md) |
| Visual UI | [frontend/architecture.md](frontend/architecture.md), [frontend/design-system.md](frontend/design-system.md) |
| Copy or RTL | [frontend/architecture.md](frontend/architecture.md) |
| Renaming Tenant/Customer | [product/terminology.md](product/terminology.md) |
