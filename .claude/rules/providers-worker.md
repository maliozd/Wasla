---
paths:
  - "src/Wasla.Worker/**"
  - "src/Wasla.Infrastructure/Platform/**"
  - "src/Wasla.Integrations/**"
  - "src/Wasla.Infrastructure/Sync/**"
  - "src/Wasla.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs"
  - "src/Wasla.{Web,Api,Worker,Cli}/appsettings*.json"
  - "deploy/linux/env/**"
  - "tests/Wasla.UnitTests/Platform/**"
  - "tests/Wasla.Integrations.ConsumerTests/**"
---

# Provider and Worker rules

These rules add detail for provider clients and order synchronization to
`AGENTS.md` section 9. The canonical documents are
`docs/integrations/food-platforms.md` and `docs/orders/synchronization.md`.

## Provider mode and configuration

- `ProviderModeResolver` is the only parser of `Platforms:ProviderMode`. A
  missing or invalid value throws at startup; keep it that way.
- The provider clients are composed only by `AddWaslaIntegrations` in
  `Wasla.Integrations`, which `AddWaslaInfrastructure` calls. In Mock mode it
  registers only the mock clients. The real Trendyol GO and Yemeksepeti HTTP
  clients and the Trendyol rate limiter are registered only in the Real
  branch. Keep them there, so Mock mode never creates a real client or calls a
  provider host.
- `Wasla.Integrations` references no Orders or POS project. Orders converts
  the library's enums at its boundary (`ProviderEnumConversions`).
- Do not tighten Real mode validation unless the task asks for it.
- Provider options live under `Platform:TrendyolGo` and `Platform:Yemeksepeti`
  (singular `Platform`). The Trendyol GO Meal webhook pilot uses its own
  `TrendyolGoMealWebhookPilot` section. Getir has a mock client only.
- The webhook pilot's options, validation and API routing rule stay in Orders
  (`src/Wasla.Infrastructure/Platform/TrendyolGo/Webhooks/`). The Trendyol GO
  client gets its API from the library's `ITrendyolGoApiRouter`, before it
  reads credentials.

## Worker and synchronization

- The Worker process keeps running. Each tenant and each connection is
  isolated, and a failing connection opens its own circuit breaker; keep that
  isolation when changing the sync loop.
- When a tenant's Order Sync is off, the sync skips that tenant and logs the
  skip.
- Update an existing order in place inside the upsert transaction. Replace its
  `OrderItems` and `OrderItemOptions` with `ExecuteDeleteAsync` and re-add them;
  never delete or reinsert the parent `Order`.
- The idempotency key is derived from `Platform` and `ExternalOrderId`, backed
  by unique indexes on the key and on (`Platform`, `ExternalOrderId`).

## Mock orders

- A newly generated mock order starts with the provider's initial status, which
  maps to New ("Yeni"). Auto Approve may then move it on like any other order.
- Generated updates of existing mock orders move them only to Delivered or
  Cancelled. Do not add random intermediate statuses unless the task asks for
  it. Intermediate provider statuses appear only as echoes of operator actions
  (`MockProviderOrderStatusStore`).

## Worker logging

- Keep log entries structured with Serilog message-template properties.
- The Development console summary (`WorkerConsole`) may be cleaner than the
  log, but stays line-based: no interactive TUI and no repeated console
  clearing.
- Do not hide warnings or errors. Verbose cycle details (`ConnectionId`,
  `LastSuccessfulSyncAt`, `LastSyncAttemptAt`, `CircuitOpenUntil`,
  `SampleExternalOrderIds`) belong at Debug.
- Keep provider exceptions, failed syncs, circuit-breaker events and Real-mode
  HTTP errors visible; do not lower them to Debug. The levels in use are in
  `docs/operations/observability.md` and `docs/orders/synchronization.md`.
