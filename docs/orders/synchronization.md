# Order synchronization

## Purpose and scope

This document describes how Wasla pulls platform orders into each tenant database and how the Worker orchestrates that work.

It owns:

- Worker cycle timing and tenant parallelism
- Per-connection eligibility, circuit breaker, and sync logging
- Provider fetch windows and pagination (Trendyol GO, Yemeksepeti)
- Order upsert idempotency and the unchanged-order short circuit

It does not own order lifecycle transitions after sync (see [lifecycle.md](lifecycle.md)), Print Bridge printing, or tenancy/connection-string resolution (see [../architecture/tenancy.md](../architecture/tenancy.md)).

Several behaviors below (pagination caps, unchanged short circuit, quieter Worker cycle logs, host-cancellation handling) are **currently uncommitted in the working tree** on branch `orders/live-screen-phase2b3-browser-notifications`. Treat them as current source behavior, not as historical committed state.

## Architecture overview

```text
OrderSyncWorker (Wasla.Worker)
  → load active tenants from CentralDb
  → Parallel.ForEachAsync (max 5 tenants)
      → IOrderSyncService.SyncCustomerWithResultAsync
          → skip if tenant Order Sync disabled
          → for each due PlatformConnection
              → plan fetch windows from the checkpoint (LastSuccessfulSync)
              → for each window, oldest first
                  → IFoodPlatformClient.FetchOrdersAsync (every page)
                  → upsert by Platform + ExternalOrderId (idempotency key)
                  → move the checkpoint to the window end
```

Each tenant has at most one connection per platform. `StoreId` is provider configuration for that location. `Platform + ExternalOrderId` above is order idempotency, not connection identity. See [../architecture/tenancy.md](../architecture/tenancy.md).

Primary sources:

- `src/Wasla.Worker/Jobs/OrderSyncWorker.cs`
- `src/Wasla.Infrastructure/Sync/OrderSyncService.cs`
- `src/Wasla.Infrastructure/Sync/OrderFetchWindowPlanner.cs`
- `src/Wasla.Infrastructure/Platform/TrendyolGo/TrendyolGoFoodPlatformClient.cs`
- `src/Wasla.Infrastructure/Platform/Yemeksepeti/YemeksepetiFoodPlatformClient.cs`

## Webhooks and polling

The WAS-17 decision for Trendyol GO is webhook first, with polling as the safety net that reconciles what webhooks miss. Webhooks are **not implemented**: polling is the only ingestion path today, for every platform. Yemeksepeti stays polling-only until its restaurant contract is confirmed.

Polling is a safety net only if it sees every change, so it must:

- Ask for every provider status, including the terminal ones (delivered, cancelled, seller-cancelled).
- Resume from the last point it fully processed after any outage, instead of a fixed lookback.
- Tolerate overlapping and repeated results without duplicating orders or side effects.

For Trendyol GO this is described in [Checkpoint and outage recovery](#checkpoint-and-outage-recovery). Yemeksepeti does not meet the second point yet (see [Known gaps](#known-gaps)).

## Worker cycle

| Constant | Value | Source |
|----------|-------|--------|
| Cycle interval | 15 seconds | `OrderSyncWorker.CycleIntervalSeconds` |
| Catastrophic cycle backoff | 30 seconds | `OrderSyncWorker.CatastrophicFailureBackoffSeconds` |
| Max parallel tenants | 5 | `OrderSyncWorker.MaxParallelCustomers` |

Behavior:

- The Worker process stays running. Web does not start or stop Worker OS processes.
- Each cycle loads **active** tenants from CentralDb (`IsActive`).
- Tenants sync in parallel up to five at a time. One tenant’s exception does not stop the others.
- Development uses `WorkerConsole` for cycle/customer summaries. Non-Development logs cycle start at Debug and cycle completion at Debug (Warning if any connection failed).
- Per-tenant sync starts a `Activity("Wasla.OrderSync")` and scopes `TraceId` / `TenantId` into logs.
- After each tenant's sync, the cycle also reads that tenant's open guided-demo practice orders (`IGuidedDemoDeliverySimulator.AdvanceDueAndPlanAsync`: one small read, plus an update only when a stage is due) and hands the next practice deadline to `GuidedDemoScheduler`. That second hosted service in the Worker moves a practice order at its deadline between cycles. It touches only `GuidedDemoSessions` and changes none of the constants above: provider calls, webhooks and real orders keep this cycle's cadence. See [../product/onboarding.md](../product/onboarding.md#practice-order-countdown).

## Tenant Order Sync gate

If the tenant’s Order Sync setting is disabled, `OrderSyncService` skips provider work for that tenant, logs at Debug, and returns `WasSyncDisabled = true`. Existing orders remain in the tenant DB and stay visible in the UI. Other tenants are unaffected.

Order Sync is separate from Auto Approve and from automatic receipt creation.

## Operational mode: Setup

A tenant whose operational mode is Setup (a new tenant before anyone completes or skips guided setup; see [../product/onboarding.md](../product/onboarding.md)) is synchronized exactly like a Live tenant. Orders are fetched, upserted idempotently and listed on Orders and in history.

The difference is in the shared side-effect services, so every ingestion path (sync today, webhooks later) obeys it:

- `OrderAutoApproveService` accepts nothing automatically while the tenant is in Setup.
- `OrderReceiptCreationService` creates no automatic receipt PrintJob while the tenant is in Setup. This covers the provider-accepted path and the automatic receipt after an operator approves an order. Manual printing (`ManualOrderPrintService`) is a separate path and is unaffected.

Each decision is taken when its trigger happens (an order is inserted, or becomes Accepted), using the mode at that moment. Going live replays nothing: orders that arrived or were accepted during Setup are never auto-approved or auto-printed afterwards. Only triggers after activation follow the Auto Approve and receipt settings.

## Platform connection eligibility

For each tenant with sync enabled, connections are loaded from TenantDb.

A connection is synced when all of the following hold:

- `IsActive`
- Circuit is not open (`CircuitOpenUntil` null or in the past)
- Due by interval (`LastSyncAttempt` null, or older than `SyncIntervalSeconds`)

Skipped connections (inactive, circuit open, or not due) are logged; they do not fail the tenant cycle.

## Fetch resilience and circuit breaker

Each fetch window uses a Polly pipeline in `OrderSyncService` (a retry fetches the whole window again from page 0):

| Setting | Value |
|---------|-------|
| Max retry attempts | 3 |
| Retry delay | 2 seconds, exponential, with jitter |
| Handled exceptions | `HttpRequestException`, `TimeoutException`, `TimeoutRejectedException` |
| Fetch timeout | 15 seconds |

On failure (after the pipeline / upsert path throws):

- `ConsecutiveFailures` increments
- When `ConsecutiveFailures >= 5`, `CircuitOpenUntil` is set to **UTC now + 5 minutes**
- A `SyncLog` with `Failed` status and an `IntegrationError` row are persisted
- The checkpoint stays at the end of the last window that completed (see [Failure and checkpoint rules](#failure-and-checkpoint-rules))
- The failure is logged; other connections and tenants continue

On success, consecutive failures and circuit open state are cleared, and the checkpoint is at the end of the last window of the run.

### Host cancellation

If `OperationCanceledException` is thrown because the Worker cancellation token is requested (host shutdown), the connection failure path is **not** taken: the exception is rethrown and is **not** persisted as a sync failure / circuit open.

## Checkpoint and outage recovery

Source: `OrderFetchWindowPlanner` and `OrderSyncService.SyncConnectionAsync`.

The checkpoint is the existing `PlatformConnection.LastSuccessfulSync` column. Everything the provider reports as modified before the checkpoint has been fetched and persisted. It is the end of the last completed fetch window, which is the moment the run planned its windows, not the moment the run finished. The tenant Platform connections page and the Central Admin tenant view show this value as the last successful sync. No schema change was needed.

| Setting | Value | Source |
|---------|-------|--------|
| Overlap before the checkpoint | 5 minutes | `OrderFetchWindowPlanner.CheckpointOverlap` |
| Lookback without a checkpoint | 1 hour | `OrderFetchWindowPlanner.InitialLookback` |
| Window length (Trendyol GO) | 1 hour | `TrendyolGoFoodPlatformClient.FetchWindowLength` |
| Windows per run | 12 | `OrderFetchWindowPlanner.MaxWindowsPerRun` |

Each run of a connection:

1. The interval is `[checkpoint − 5 minutes, now]`. A checkpoint later than now counts as now. A connection that has never completed a sync uses `[now − 1 hour, now]`, so connecting a store does not import older history.
2. A client with a maximum window (Trendyol GO) gets consecutive windows of at most one hour, oldest first. Each window starts at the instant the previous one ended, so no instant is left between two windows. A client without one (Yemeksepeti and the mock clients) gets a single fetch per run and ignores the window.
3. For each window: every page is fetched, every order is upserted, and only then does the checkpoint move to the window end.
4. A run fetches at most 12 windows (12 hours). After a longer outage the run logs `MoreRunsNeeded=True` and the next due run continues from the advanced checkpoint. No older interval is skipped. New orders placed after the outage appear once recovery reaches them.

The overlap covers clock skew between Wasla and Trendyol GO, a provider that indexes a change a little late, and the boundary instant, because the documentation does not say whether the date bounds are inclusive. Results that the overlap fetches again are idempotent (see [Overlap and repeated results](#overlap-and-repeated-results)).

### Failure and checkpoint rules

- A window fails when a page fails after retries, a response is malformed (invalid JSON or pagination metadata), the page cap is reached while more pages remain, a repeated package conflicts, or an order cannot be persisted. The checkpoint does not move past it, and later windows of that run are not fetched.
- Orders of earlier windows in the same run stay stored, and the checkpoint stays at the end of the last completed window. The next run starts 5 minutes before it.
- Orders of the failing window that were saved before the failure are fetched again by the next run and update idempotently.
- Host cancellation records no failure and leaves the checkpoint at the last value already saved, which is never past unprocessed data.
- A connection's checkpoint, credentials and orders live in its tenant database. One tenant's failure or recovery does not change another tenant's window.

### Trendyol GO request

Source: `TrendyolGoFoodPlatformClient`. Official reference: "Sipariş Paketlerini Çekme" in the Trendyol GO Yemek documentation (developers.tgoapps.com), checked 2026-10-06.

`GET /integrator/order/meal/suppliers/{supplierId}/packages`, one request per page of a window:

| Parameter | Value |
|-----------|-------|
| `packageStatuses` | `Created,Picking,Invoiced,Cancelled,UnSupplied,Shipped,Delivered`: every value the documentation lists, with its casing |
| `size` | 50 (`FetchPageSize`, the documented maximum) |
| `page` | 0, 1, 2, … |
| `storeId` | The connection's `StoreId`, when set |
| `packageModificationStartDate` | Window start, Unix epoch milliseconds |
| `packageModificationEndDate` | Window end, Unix epoch milliseconds |

`DefaultOrderStatusMapper` maps the terminal statuses: `Delivered` → Delivered, `Cancelled` → Cancelled, `UnSupplied` (seller cancellation or rejection) → Cancelled.

The documentation does not state a maximum date range, how far back packages can be queried, whether the bounds are inclusive, or the sort order. Its "Servis Limitleri" page says limits will be published later. The one-hour window is therefore the span this client has always queried, not a documented limit. The Authorization page documents at most 50 requests to the same endpoint in 10 seconds (HTTP 429 above that); 12 windows per run keep one connection's catch-up well below it. These points are part of the Stage validation and are not yet proven against the real API.

### Pagination hard cap

Page indexes start at **0**.

| Setting | Trendyol GO | Yemeksepeti |
|---------|-------------|-------------|
| Page size | 50 (`FetchPageSize`) | `Platform:Yemeksepeti:DefaultPageSize` (default **20**) |
| Hard page cap | 20 pages per window (`MaxFetchPages`) | 50 pages per fetch (`MaxFetchPages`) |
| Most results per fetch | 1000 packages per window | 1000 orders at the default page size |

Rules for both clients:

- Malformed pagination metadata (negative page/total, echoed page not advancing, empty page while more pages claimed) **fails the sync** for that connection.
- If the provider still claims more pages when the hard cap is reached, sync **fails** (`page cap reached while more pages were reported`) and the checkpoint does not move past that window. A truncated window is never stored as complete.
- Successful completion within the cap returns the collected results.

### Yemeksepeti

Source: `YemeksepetiFoodPlatformClient` (currently uncommitted changes in working tree).

| Setting | Value |
|---------|-------|
| Query window | `start_time` / `end_time` = last one hour (Unix ms) |

Rules:

- Not checkpointed: the client ignores the planned window and always asks for the last hour. A change older than one hour at the time of the next successful run can still be missed.
- OAuth token is acquired **once** (or reused from the in-memory cache) before the paginated fetch loop; the same bearer token is used for every page in that fetch.
- Same malformed-pagination and page-cap failure rules as Trendyol GO.
- `DefaultPageSize` must be positive or fetch throws before paging.

## Fetch-level duplicate handling

While a paginated fetch is collecting results, both clients key nonblank ids:

- Trendyol GO: package id (`ExternalOrderId` from the package)
- Yemeksepeti: order id (`ExternalOrderId` from the order)

Signature compared for a repeated id: provider status (`ExternalStatus`), total (invariant-culture string), customer note (`CustomerNote`), and item count (`Items.Count`).

| Repeated nonblank id | Behavior |
| --- | --- |
| Same signature | Keep one result. Do not add a second copy. |
| Different signature | Fail that connection's fetch. Do not pick one snapshot silently. |

A blank id is not entered in that map, so blank ids are not collapsed together. This dedupe is inside one provider fetch (one Trendyol GO window). It is not database idempotency: the same package returned by two windows or two runs is handled by the upsert (see [Overlap and repeated results](#overlap-and-repeated-results)).

## After the fetch

`OrderSyncService` skips an order whose `ExternalOrderId` is missing or blank. It does not insert it.

Orders that have an id are upserted by `Platform` + `ExternalOrderId` (`IdempotencyKey`). That database rule is separate from the page-level duplicate check above.

### Getir

No real HTTP pagination client is registered in Real mode. Getir remains mock (see [../integrations/food-platforms.md](../integrations/food-platforms.md)).

## Order upsert

Idempotency key: SHA-256 of `Platform:ExternalOrderId` (Base64), stored as `Order.IdempotencyKey`.

Rules:

- Preserve parent `Order` row, `Id`, and `CreatedAt`.
- New orders are inserted; then auto-approve / receipt hooks may run for eligible new rows (never while the tenant is in Setup; see [Operational mode: Setup](#operational-mode-setup)).
- Real updates replace child `OrderItems` / `OrderItemOptions` via `ExecuteDeleteAsync` then reinsert, update scalars, set `UpdatedAt`, and rewrite `RawPayloadJson`.
- Provider internal status is **merged** with local progress so stale provider statuses cannot undo operator progress (see `MergeInternalStatusForSync`).
- Missing `ExternalOrderId` skips the order (counted as skipped).

### Unchanged-order short circuit

Currently uncommitted in the working tree (`OrderSyncService.IsSemanticallyUnchanged`).

Before opening a mutation transaction:

1. Load persisted item snapshots.
2. Semantically compare parent fields and items/options to the inbound external order (including mapped internal status).
3. If unchanged: return immediately with `Unchanged = true`.

For an unchanged order the sync path does **not**:

- Begin a DB transaction for that order
- Delete/reinsert children
- Call `SaveChanges` for that order
- Change `UpdatedAt`
- Rewrite `RawPayloadJson`

Real changes still use the existing update path above.

This is **not** first-page-only fetch, and **not** always-rewrite-on-every-sync.

### Overlap and repeated results

The checkpoint overlap, the shared instant between two windows, a retried window and a run that follows a failure all return packages Wasla has already stored. None of them creates a second order or repeats a side effect:

- The upsert finds the existing row by `IdempotencyKey`. No second `Order` row is inserted.
- A repeated result with no change takes the unchanged short circuit: nothing is written.
- The auto-approve hook runs only when a row is inserted. The provider-accepted receipt is created only when the merged status changes to `Accepted`. A repeated result triggers neither, so recovery overlap alone prints no extra receipt.
- The status merge keeps a terminal (`Delivered`, `Cancelled`, `Failed`) or further-progressed status when older provider data arrives. This is the same `MergeInternalStatusForSync` rule used for every sync; the client has no status rules of its own.

`PlatformStatus` is the raw provider string and is still overwritten by the provider's latest value on a real update, even when the merge keeps the internal status.

## Logging notes

- Verbose per-connection start/complete details are Debug-level in production-oriented paths.
- Provider HTTP failures log status code and timing **without response bodies** (see platform clients).
- Do not log decrypted credentials, connection strings, or tokens.

Safe diagnostics for polling and recovery. These fields may be logged: tenant and connection ids, platform, `StoreId`, window boundaries, checkpoint, page and order counts, elapsed time, external order ids (at most ten per window, Debug), and the exception type. Credentials, the `Authorization` header, the executor e-mail, customer names, phones and addresses, and provider payloads or response bodies are never logged.

| Level | Message | Fields |
|-------|---------|--------|
| Information | `Recovering platform orders after a sync gap` (more than one window, or more runs needed) | `CustomerId`, `ConnectionId`, `Platform`, `CheckpointUtc`, `Windows`, `FromUtc`, `ThroughUtc`, `MoreRunsNeeded` |
| Error | `Platform connection sync failed` | Adds `WindowStartUtc`, `WindowEndUtc` (the failing window; empty when the failure was outside a window) and `CheckpointUtc` (the next run starts five minutes before it) |
| Warning | `Provider pagination failed` (client) | `Reason`, `PagesFetched`, `OrdersFetched`, `ReportedTotalPages` |
| Debug | `Provider returned {OrderCount} orders` and `Provider fetch completed` | Window boundaries, pages, counts, elapsed time |

See also [../operations/observability.md](../operations/observability.md#order-sync-recovery-diagnostics).

## Known gaps

- Worker startup log in Real mode still claims Yemeksepeti uses mock clients; DI registers the real Yemeksepeti HTTP client. Trust DI (`ServiceCollectionExtensions`), not that startup sentence, until the log is corrected.
- Yemeksepeti partner endpoint path still carries a TODO to confirm against official Partner API docs.
- Getir has no real fetch client yet.
- Webhooks are not implemented for any platform; polling is the only ingestion path (see [Webhooks and polling](#webhooks-and-polling)).
- Yemeksepeti polling is not checkpointed and still uses a fixed one-hour lookback.
- Trendyol GO's maximum date range, retention, bound inclusivity, sort order and the scope of its 50-requests-per-10-seconds limit are undocumented and not yet verified against the Stage API.
- No OpenTelemetry exporter or metrics for sync throughput (see [../operations/observability.md](../operations/observability.md)).

## Related docs

- [../integrations/food-platforms.md](../integrations/food-platforms.md)
- [../operations/observability.md](../operations/observability.md)
- [../operations/local-development.md](../operations/local-development.md)
