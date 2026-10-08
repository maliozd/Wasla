# Order synchronization

## Purpose and scope

This document describes how Wasla pulls platform orders into each tenant database and how the Worker orchestrates that work.

It owns:

- Worker cycle timing and tenant parallelism
- Per-connection eligibility, circuit breaker, and sync logging
- Provider fetch windows and pagination (Trendyol GO, Yemeksepeti)
- Order upsert idempotency and the unchanged-order short circuit

It does not own order lifecycle transitions after sync (see [lifecycle.md](lifecycle.md)), Print Bridge printing, or tenancy/connection-string resolution (see [../architecture/tenancy.md](../architecture/tenancy.md)).

This document describes committed source, not uncommitted working-tree changes. Pagination caps, the unchanged-order short circuit, quieter Worker cycle logs and host-cancellation handling are committed on `dev`.

## Architecture overview

```text
OrderSyncWorker (Wasla.Worker)
  → load active tenants from CentralDb
  → OrderSyncCycleRunner
      Phase 1, every tenant (max 5 at a time):
      → IOrderSyncService.SyncCustomerWithResultAsync
          → skip if tenant Order Sync disabled
          → for each due PlatformConnection: the current window, ending now
              → IFoodPlatformClient.FetchOrdersAsync (every page; each Trendyol GO request waits for the shared limiter)
              → upsert by Platform + ExternalOrderId (idempotency key)
              → move the checkpoint to now, unless the window was a hot window ahead of a history gap
      Phase 2, tenants whose history is behind, round-robin, ≤ 12 rounds, ≤ 30 s:
      → IOrderSyncService.BackfillCustomerAsync: the oldest missing window per behind connection
          → fetch every page, upsert, move the checkpoint to the window end
```

Each tenant has at most one connection per platform. `StoreId` is provider configuration for that location. `Platform + ExternalOrderId` above is order idempotency, not connection identity. See [../architecture/tenancy.md](../architecture/tenancy.md).

Primary sources:

- `src/Wasla.Worker/Jobs/OrderSyncWorker.cs`
- `src/Wasla.Infrastructure/Sync/OrderSyncService.cs`
- `src/Wasla.Infrastructure/Sync/OrderFetchWindowPlanner.cs`
- `src/Wasla.Infrastructure/Sync/OrderSyncCycleRunner.cs`
- `src/Wasla.Infrastructure/Platform/TrendyolGo/TrendyolGoFoodPlatformClient.cs`
- `src/Wasla.Infrastructure/Platform/TrendyolGo/TrendyolRequestRateLimiter.cs`
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
| History recovery per cycle | at most 12 rounds of one window per behind connection | `OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle` |
| History recovery time budget | 30 seconds (no new round or turn starts after it) | `OrderSyncCycleRunner.BackfillBudget` |

Behavior:

- The Worker process stays running. Web does not start or stop Worker OS processes.
- Each cycle loads **active** tenants from CentralDb (`IsActive`).
- `OrderSyncCycleRunner` runs the cycle in two phases. Phase 1 is every tenant's current pass, up to five tenants at a time, so every eligible tenant gets its current-window check before any history recovery starts. Phase 2 is history recovery ([Current window first, then history](#current-window-first-then-history)) for tenants whose current pass reported a history gap: round-robin, one window per behind connection per turn, at most 12 rounds and only within the 30-second budget, starting from a different tenant each cycle. A tenant whose turn fails stops for this cycle. One tenant’s exception does not stop the others.
- The next cycle starts after both phases, as before (15 seconds after the previous start, or at once if the cycle took longer).
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

Each fetch window uses a Polly pipeline in `OrderSyncService` (a retry fetches the whole window again from page 0, and every request of it takes a new limiter permit):

| Setting | Value |
|---------|-------|
| Max retry attempts | 3 |
| Retry delay | 2 seconds, exponential, with jitter |
| Handled exceptions | `HttpRequestException` except HTTP 429, `TimeoutException`, `TimeoutRejectedException` |
| Whole-fetch timeout | 15 seconds for clients without a fetch window (Yemeksepeti, mocks). None for Trendyol GO: it may queue for the shared request limiter, and each of its HTTP requests keeps the `HttpClient` timeout (`Platform:TrendyolGo:RequestTimeout`, default 15 seconds) |

HTTP 429 is not retried by Polly: the Trendyol GO client has already retried that page (see [HTTP 429](#http-429)).

On failure (after the pipeline / upsert path throws):

- Everything the failed attempt left unsaved in the tenant pass's change tracker is discarded first: an order graph whose insert failed, an update's scalar changes and new items after its transaction rolled back, an unsaved checkpoint or counter change. Order, item and option entities are detached; the connection returns to its stored values. A failed order is therefore never stored by the failure report or by a later connection in the same pass, and its new-order side effects (auto-approve, receipt) run once, when a later attempt inserts it.
- The failure report is saved through a **new context for the same tenant**, which can write only these rows:
  - `ConsecutiveFailures` increments
  - When `ConsecutiveFailures >= 5`, `CircuitOpenUntil` is set to **UTC now + 5 minutes**
  - A `SyncLog` with `Failed` status and an `IntegrationError` row are persisted (sanitized exception message and type; no payloads, credentials or customer data)
- If saving the report itself fails, that exception reaches the Worker's per-tenant handler; the failed order changes were already discarded and are not stored.
- The checkpoint stays at the end of the last window that completed (see [Failure and checkpoint rules](#failure-and-checkpoint-rules))
- The failure is logged; other connections and tenants continue

On success, consecutive failures and circuit open state are cleared. The checkpoint is at the end of the last completed window that started at it (a hot window ahead of a history gap does not move it).

### Host cancellation

If `OperationCanceledException` is thrown because the Worker cancellation token is requested (host shutdown), the connection failure path is **not** taken: the exception is rethrown and is **not** persisted as a sync failure / circuit open.

## Checkpoint and outage recovery

Source: `OrderFetchWindowPlanner` and `OrderSyncService.SyncConnectionAsync`.

The checkpoint is the existing `PlatformConnection.LastSuccessfulSync` column. Everything the provider reports as modified before the checkpoint has been fetched and persisted. It is the end of the last completed fetch window that started at the checkpoint, which is the moment that window was planned, not the moment the run finished. It is saved as soon as each window completes. The tenant Platform connections page and the Central Admin tenant view show this value as the last successful sync, so after an outage it shows how far history has been recovered. No schema change was needed.

| Setting | Value | Source |
|---------|-------|--------|
| Overlap before the checkpoint | 5 minutes | `OrderFetchWindowPlanner.CheckpointOverlap` |
| Lookback without a checkpoint | 1 hour | `OrderFetchWindowPlanner.InitialLookback` |
| Window length (Trendyol GO) | 1 hour | `TrendyolGoFoodPlatformClient.FetchWindowLength` |
| Recovery workload cap | 12 windows per connection per Worker cycle | `OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle` |

The recovery workload cap bounds how much history one connection recovers per cycle. It is **not** rate-limit protection: each window can take up to 20 page requests, so 12 windows can be 240 requests. The request rate is governed only by the [request limiter](#trendyol-go-request-limiter).

### Current window first, then history

Current pass (phase 1) of a connection:

1. A connection that has never completed a sync uses `[now − 1 hour, now]` and moves the checkpoint to now. Connecting a store does not import older history.
2. When `[checkpoint − 5 minutes, now]` fits one window (the checkpoint is at most 55 minutes old, so it already lies inside the window ending now), that window is fetched and the checkpoint moves to now. A checkpoint later than now counts as now. A client without a maximum window (Yemeksepeti and the mock clients) always takes this path with a single fetch.
3. Otherwise the history is behind (an outage). The **hot window** `[now − 1 hour, now]` is fetched and persisted first, so new and recently changed orders keep arriving. The checkpoint does **not** move: the gap before the hot window is still unprocessed. The connection reports `BackfillPending`.
4. If the hot window fails, the connection does no history recovery this cycle, and the checkpoint does not move.

The current pass stores its `now` as `LastSyncAttempt`, and a window that covers everything since the checkpoint ends at that same instant. A windowed connection is therefore **behind** exactly while `LastSuccessfulSync < LastSyncAttempt` (`OrderSyncService.IsHistoryBehind`).

Backfill turn (phase 2), for a connection that is active, has its circuit closed, succeeded on its last attempt (`ConsecutiveFailures = 0`) and is behind:

1. The oldest missing window `[checkpoint − 5 minutes, checkpoint + 55 minutes]` (at most one hour, never past now) is fetched, every order is upserted, and the checkpoint moves to its end and is saved.
2. Each turn re-reads the 5-minute overlap, so the checkpoint advances 55 minutes per turn. Windows move strictly oldest first and never skip time.
3. Turns continue until the connection is no longer behind. The last turn is `[checkpoint − 5 minutes, now]`: it reaches the hot window's end (or later), so the checkpoint becomes current **in the same cycle** ("Order history recovered"), and the next current pass is an ordinary one-window pass. The checkpoint never jumps to the hot window's end on the strength of the hot window alone: the last turn fetches the part history shares with the hot window again, and idempotent upserts absorb it.
4. If the budget or the 12-round cap ends recovery first, or a turn fails, the checkpoint stays at the end of the last completed historical window and the connection stays behind. The next cycle continues from there.

With the defaults, one cycle recovers at most 12 × 55 minutes = 11 hours of history per connection, if the 30-second budget and the request limiter allow. Recovery time therefore depends on pages per window, the request limiter, the 12-window cap, the 30-second phase budget and the Worker schedule (see [Capacity model](#capacity-model)).

The overlap covers clock skew between Wasla and Trendyol GO, a provider that indexes a change a little late, and the boundary instant, because the documentation does not say whether the date bounds are inclusive. Results that the overlap fetches again are idempotent (see [Overlap and repeated results](#overlap-and-repeated-results)).

### Failure and checkpoint rules

- A window fails when a page fails after retries, a response is malformed (invalid JSON or pagination metadata), the page cap is reached while more pages remain, HTTP 429 persists after the client's retries, a repeated package conflicts, or an order cannot be persisted. The checkpoint does not move past it, and that connection's recovery stops for this cycle.
- Earlier completed windows stay stored, and their checkpoint is already saved. The next attempt starts 5 minutes before it.
- Orders of a successful hot window stay stored even if a later historical window fails.
- Orders of the failing window that were saved before the failure are fetched again by the next attempt and update idempotently. The order whose save failed is not stored at all (an update stays at its previous state), so the next attempt inserts or updates it normally.
- Host cancellation, including while waiting for the request limiter or a 429 delay, records no failure and leaves the checkpoint at the last value already saved, which is never past unprocessed data.
- A connection's checkpoint, credentials and orders live in its tenant database. One tenant's failure or recovery does not change another tenant's window.
- No tenant change is held unsaved while a request waits: the attempt time is saved before the first provider call, a window is fetched completely (and throttled) before anything from it is written, and no transaction is open during a provider call.

### Trendyol GO request limiter

Source: `TrendyolRequestRateLimiter`, registered as a **singleton** in Real mode. Every Trendyol GO client instance (typed HTTP clients are transient) and every tenant in the Worker process share one budget. The client cannot be built without it.

- **Budget:** at most **40 requests in any rolling 10 seconds** (`DefaultPermitLimit`, `DefaultWindow`).
- **Algorithm:** a rolling log of grant times. A request starts at once while fewer than 40 started in the last 10 seconds, so a low-volume request never waits. Otherwise it waits asynchronously until the oldest grant leaves the window. Waiters are served one at a time, in arrival order.
- **What counts:** every HTTP attempt to the packages endpoint takes one permit before it is sent. That includes every page of a window, every 429 retry and every page of a Polly whole-window retry. Order actions (accept, invoice, reject) and Yemeksepeti are not limited by it.
- **Waiting:** `Task.Delay` on the injected `TimeProvider`, cancelled by the Worker's stopping token. No thread is blocked and no database transaction is open while waiting.
- **Scope:** one process. Several Worker instances would each have their own 40, which could exceed a global quota. They need a distributed limiter (see [Known gaps](#known-gaps)).

### HTTP 429

When a page request answers 429 Too Many Requests, the Trendyol GO client:

1. Reads `Retry-After` as seconds or as an HTTP date, whichever .NET parsed. The wait is that value, at least 0 and at most **60 seconds** (`MaxThrottleDelay`). Without a usable header it waits **10 seconds** (`DefaultThrottleDelay`).
2. Pauses **every** Trendyol GO request in the process until then (`TrendyolRequestRateLimiter.Defer`), because the quota may be shared.
3. Takes a new permit and retries the **same page** of the same window.
4. Gives up after **2 retries** (3 attempts per page, `MaxThrottledRetries`). The window then fails with HTTP 429: the checkpoint does not move and nothing of the window is recorded as successful. Polly does not retry a 429 again.

The warning `Provider throttled the request; retrying the same page` carries the page, attempt, delay and its source (`retry-after` or `fallback`), never the response body or headers.

### Capacity model

Assumptions until Trendyol GO confirms the scope of its limit in writing:

- The Authorization page documents 50 requests per 10 seconds to the same endpoint. It does not say whether that is per supplier, per integrator or per source IP.
- The Worker therefore shares one conservative budget of 40 per 10 seconds (4 requests per second) across all Trendyol GO tenants in the process.
- Stage load testing cannot settle the scope: it needs written provider confirmation.

| Tenants (one Trendyol GO connection, one page per current window) | Requests per pass | Quota time for one pass |
|---|---|---|
| 20 | 20 | Immediate (within the first 40) |
| 300 | 300 | The 300th request starts after 70 seconds (40 at once, then 40 every 10 seconds); about 75 seconds at a steady 4 per second, plus response times |

- With 20 tenants the current pass of every tenant fits one burst, and phase 2 can use the rest of each 10-second window for history recovery.
- With 300 tenants a complete polling pass takes over a minute of quota alone. If the quota is global, polling cannot be the primary real-time path for 300 tenants.
- **Webhooks are the intended primary real-time path.** Polling is the reconciliation, outage-recovery and webhook safety net.
- More Worker instances do not increase a global external quota. They need a distributed limiter, and tenant leases so two Workers do not poll the same tenant.
- Recovery duration depends on pages per window, the request limiter, the 12-window cap, the 30-second phase budget and the Worker schedule.

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

The documentation does not state a maximum date range, how far back packages can be queried, whether the bounds are inclusive, or the sort order. Its "Servis Limitleri" page says limits will be published later. The one-hour window is therefore the span this client has always queried, not a documented limit. The Authorization page documents at most 50 requests to the same endpoint in 10 seconds (HTTP 429 above that); the [request limiter](#trendyol-go-request-limiter) keeps the whole process at 40. These points are part of the Stage validation and are not yet proven against the real API.

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

Source: `YemeksepetiFoodPlatformClient`.

| Setting | Value |
|---------|-------|
| Query window | `start_time` / `end_time` = last one hour (Unix ms) |

Rules:

- Not checkpointed: the client ignores the planned window and always asks for the last hour. A change older than one hour at the time of the next successful run can still be missed.
- OAuth token is acquired **once** (or reused from the in-memory cache) before the paginated fetch loop; the same bearer token is used for every page in that fetch. With the WAS-88 change (commit `9b12f99`), a cached token is bound to the platform connection and its exact credentials, and a 401/403 discards it; see [Yemeksepeti OAuth tokens](../integrations/food-platforms.md#yemeksepeti-oauth-tokens).
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

Source: `OrderSyncService.IsSemanticallyUnchanged`.

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

The checkpoint overlap, the hot window and the history windows that cover the same time, a retried window and an attempt that follows a failure all return packages Wasla has already stored. None of them creates a second order or repeats a side effect:

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
| Information | `Order history is behind; fetching the current window first` (each current pass while a gap remains) | `CustomerId`, `ConnectionId`, `Platform`, `CheckpointUtc`, `CurrentWindowStartUtc`, `CurrentWindowEndUtc` |
| Information | `Order history recovered` (a backfill turn closed the gap) | `CustomerId`, `ConnectionId`, `Platform`, `CheckpointUtc` |
| Warning | `Provider throttled the request; retrying the same page` (client, HTTP 429) | `StatusCode`, `Page`, `Attempt`, `RetryDelayMs`, `RetryDelaySource` |
| Error | `Platform connection sync failed` | Adds `Pass` (`Current` or `Backfill`), `WindowStartUtc`, `WindowEndUtc` (the failing window; empty when the failure was outside a window) and `CheckpointUtc` (the next run starts five minutes before it) |
| Warning | `Provider pagination failed` (client) | `Reason`, `PagesFetched`, `OrdersFetched`, `ReportedTotalPages` |
| Debug | `Provider returned {OrderCount} orders` and `Provider fetch completed` | Window boundaries, pages, counts, elapsed time |

See also [../operations/observability.md](../operations/observability.md#order-sync-recovery-diagnostics).

## Known gaps

- Yemeksepeti partner endpoint path still carries a TODO to confirm against official Partner API docs.
- Getir has no real fetch client yet.
- Webhooks are not implemented for any platform; polling is the only ingestion path (see [Webhooks and polling](#webhooks-and-polling)).
- Yemeksepeti polling is not checkpointed and still uses a fixed one-hour lookback.
- Trendyol GO's maximum date range, retention, bound inclusivity, sort order and the scope of its 50-requests-per-10-seconds limit are undocumented and not yet verified against the Stage API. The scope needs written confirmation from Trendyol GO.
- The request limiter and the cycle scheduling are per Worker process. Running several Worker instances needs a distributed request limiter and tenant leases; neither exists. For 300+ tenants, webhooks (not polling) must carry real-time orders.
- A historical window that always fails (for example more than 1,000 packages in one hour, beyond the page cap) is retried every cycle and stops recovery for that connection until it is handled. The Error log names the window.
- No OpenTelemetry exporter or metrics for sync throughput (see [../operations/observability.md](../operations/observability.md)).

## Related docs

- [../integrations/food-platforms.md](../integrations/food-platforms.md)
- [../operations/observability.md](../operations/observability.md)
- [../operations/local-development.md](../operations/local-development.md)
