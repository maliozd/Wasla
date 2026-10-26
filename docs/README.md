# Wasla developer documentation

Canonical developer and agent documentation is **English**. Turkish, English, Arabic, and Russian remain UI languages. This folder is not a second product manual.

Source code wins when a document is wrong. Do not “fix” code to match a stale doc. Retired product and tenant names are not tolerated debt: they are removed under the [clean-break policy](product/terminology.md#clean-break-policy), with no aliases or compatibility layer. Do not add new uses of them.

Older files in this folder (`LOCAL_DEVELOPMENT_*.md`, `deployment-config.md`, `tenant-vs-customer.md`, `wasla-design-language-v1.md`, `orders-printjob-status-signalr.md`, scratch `.txt` files) are **not** canonical. They stay until a later cleanup. Prefer the root `README.md` and the map below.

[product/onboarding.md](product/onboarding.md) covers guided setup and the tenant's operational mode only. Signup and provisioning entry points are summarized in [operations/cli.md](operations/cli.md) and [architecture/tenancy.md](architecture/tenancy.md).

## Map

| Path | Owns |
|------|------|
| [architecture/overview.md](architecture/overview.md) | Process map and links |
| [architecture/tenancy.md](architecture/tenancy.md) | One location per tenant, CentralDb, databases, one connection per platform |
| [architecture/authentication.md](architecture/authentication.md) | Cookie schemes and Web/API session limits |
| [architecture/realtime.md](architecture/realtime.md) | Live tenant notifications (SignalR hub, internal channel, browser fallback) |
| [architecture/realtime-scale-out.md](architecture/realtime-scale-out.md) | Decision: one Web instance for live notifications; multi-instance topology and its triggers |
| [architecture/billing.md](architecture/billing.md) | Provider-neutral billing boundary, CentralDb plan prices, subscription and payment records, states, invariants, iyzico mapping intent, open price decision |
| [orders/lifecycle.md](orders/lifecycle.md) | Statuses, operator actions, sync merge |
| [orders/synchronization.md](orders/synchronization.md) | Worker, pagination, upsert |
| [orders/live-screen.md](orders/live-screen.md) | Operational UI versus Orders |
| [integrations/food-platforms.md](integrations/food-platforms.md) | Trendyol GO, Yemeksepeti, Getir, Mock/Real |
| [integrations/provider-webhooks.md](integrations/provider-webhooks.md) | Decision: provider webhook evidence, polling as reconciliation, provider questions (WAS-17) |
| [integrations/trendyol-go-meal-webhook-stage-pilot.md](integrations/trendyol-go-meal-webhook-stage-pilot.md) | Trendyol GO Meal webhook stage pilot: one configured seller, receiver, inbox, processing, stage test |
| [integrations/print-bridge.md](integrations/print-bridge.md) | Desktop printing client |
| [frontend/architecture.md](frontend/architecture.md) | Razor, Bootstrap, vanilla JS |
| [frontend/design-system.md](frontend/design-system.md) | Tokens; `wasla-foundation.css` is the foundation |
| [operations/local-development.md](operations/local-development.md) | Local runbook |
| [operations/cli.md](operations/cli.md) | CLI commands and destructive guards |
| [operations/migrations.md](operations/migrations.md) | Central vs tenant EF |
| [operations/deployment.md](operations/deployment.md) | Hosts, keys, config |
| [operations/linux-vds-runbook.md](operations/linux-vds-runbook.md) | Single Ubuntu server: systemd units, Nginx, paths, SQL identities, first deployment, update, rollback, backups (`deploy/linux/`) |
| [operations/observability.md](operations/observability.md) | Logs, trace id, health |
| [operations/tenant-operations-center.md](operations/tenant-operations-center.md) | Central Admin tenant overview, list and per-tenant health |
| [operations/testing.md](operations/testing.md) | What to run, and what not to claim |
| [product/product-boundaries.md](product/product-boundaries.md) | Wasla Orders, Wasla POS, and shared-infrastructure boundaries |
| [product/pos-mvp.md](product/pos-mvp.md) | Wasla POS MVP (planned, not implemented): local-first installed product, workflows, payments through the fiscal cash register (ÖKC), POS roles, scope, payment boundary, UX and non-functional requirements |
| [product/pos-first-slice.md](product/pos-first-slice.md) | Wasla POS first vertical slice (selling offline, then syncing), acceptance criteria and backlog |
| [architecture/pos-application-boundary.md](architecture/pos-application-boundary.md) | Decision: Wasla POS as a local-first installed product (POS Edge, local SQLite, POS Terminal, cloud sync) in this repository; data authority, sync, restore and cloud recovery, printing, the fiscal cash register (ÖKC) boundary, devices and certificate lifecycle, licensing |
| [product/terminology.md](product/terminology.md) | Product names, Tenant vs order customer, clean-break policy and the historical migration exception |
| [product/roles-and-permissions.md](product/roles-and-permissions.md) | Server-side role matrix |
| [product/onboarding.md](product/onboarding.md) | Per-user guided setup, tenant operational mode (Setup / Live), going live, Dashboard setup readiness |

## Task → documents

| Task | Read first |
|------|------------|
| Any change | [architecture/overview.md](architecture/overview.md) |
| Product scope or POS | [product/product-boundaries.md](product/product-boundaries.md), [product/pos-mvp.md](product/pos-mvp.md), [architecture/pos-application-boundary.md](architecture/pos-application-boundary.md), [product/pos-first-slice.md](product/pos-first-slice.md) |
| Tests / verification | [operations/testing.md](operations/testing.md) |
| Hosts, databases, secrets | [architecture/tenancy.md](architecture/tenancy.md) |
| Login, roles, password reset | [architecture/tenancy.md](architecture/tenancy.md), [architecture/authentication.md](architecture/authentication.md), [product/roles-and-permissions.md](product/roles-and-permissions.md) |
| Live Screen | [orders/live-screen.md](orders/live-screen.md), [orders/lifecycle.md](orders/lifecycle.md), [frontend/architecture.md](frontend/architecture.md), [frontend/design-system.md](frontend/design-system.md) |
| Guided setup or order training | [product/onboarding.md](product/onboarding.md), [orders/live-screen.md](orders/live-screen.md) |
| Orders management UI | [orders/lifecycle.md](orders/lifecycle.md), [frontend/architecture.md](frontend/architecture.md) |
| Provider or sync | [architecture/tenancy.md](architecture/tenancy.md), [orders/synchronization.md](orders/synchronization.md), [integrations/food-platforms.md](integrations/food-platforms.md), [integrations/provider-webhooks.md](integrations/provider-webhooks.md) |
| Print Bridge | [architecture/tenancy.md](architecture/tenancy.md), [integrations/print-bridge.md](integrations/print-bridge.md) |
| Live updates, SignalR | [architecture/realtime.md](architecture/realtime.md), [architecture/realtime-scale-out.md](architecture/realtime-scale-out.md), [architecture/authentication.md](architecture/authentication.md#long-lived-live-connections), [operations/observability.md](operations/observability.md#live-notification-metrics) |
| Signup or provisioning | [architecture/tenancy.md](architecture/tenancy.md), [operations/cli.md](operations/cli.md) |
| Billing, subscriptions, payment provider | [architecture/billing.md](architecture/billing.md), [architecture/tenancy.md](architecture/tenancy.md), [operations/migrations.md](operations/migrations.md) |
| CLI | [operations/cli.md](operations/cli.md), [architecture/tenancy.md](architecture/tenancy.md) |
| Migrations | [architecture/tenancy.md](architecture/tenancy.md), [operations/migrations.md](operations/migrations.md) |
| Deploy or config | [operations/deployment.md](operations/deployment.md), [operations/linux-vds-runbook.md](operations/linux-vds-runbook.md), [operations/observability.md](operations/observability.md) |
| Central Admin tenant operations | [operations/tenant-operations-center.md](operations/tenant-operations-center.md), [architecture/authentication.md](architecture/authentication.md) |
| Visual UI | [frontend/architecture.md](frontend/architecture.md), [frontend/design-system.md](frontend/design-system.md) |
| Copy or RTL | [frontend/architecture.md](frontend/architecture.md) |
| Renaming Tenant/Customer | [product/terminology.md](product/terminology.md) |
