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
              → IFoodPlatformClient.FetchOrdersAsync
              → upsert by Platform + ExternalOrderId (idempotency key)
```

Each tenant has at most one connection per platform. `StoreId` is provider configuration for that location. `Platform + ExternalOrderId` above is order idempotency, not connection identity. See [../architecture/tenancy.md](../architecture/tenancy.md).

Primary sources:

- `src/Wasla.Worker/Jobs/OrderSyncWorker.cs`
- `src/Wasla.Infrastructure/Sync/OrderSyncService.cs`
- `src/Wasla.Infrastructure/Platform/TrendyolGo/TrendyolGoFoodPlatformClient.cs`
- `src/Wasla.Infrastructure/Platform/Yemeksepeti/YemeksepetiFoodPlatformClient.cs`

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

## Tenant Order Sync gate

If the tenant’s Order Sync setting is disabled, `OrderSyncService` skips provider work for that tenant, logs at Debug, and returns `WasSyncDisabled = true`. Existing orders remain in the tenant DB and stay visible in the UI. Other tenants are unaffected.

Order Sync is separate from Auto Approve and from automatic receipt creation.

## Platform connection eligibility

For each tenant with sync enabled, connections are loaded from TenantDb.

A connection is synced when all of the following hold:

- `IsActive`
- Circuit is not open (`CircuitOpenUntil` null or in the past)
- Due by interval (`LastSyncAttempt` null, or older than `SyncIntervalSeconds`)

Skipped connections (inactive, circuit open, or not due) are logged; they do not fail the tenant cycle.

## Fetch resilience and circuit breaker

Per connection fetch uses a Polly pipeline in `OrderSyncService`:

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
- The failure is logged; other connections and tenants continue

On success, consecutive failures and circuit open state are cleared.

### Host cancellation

If `OperationCanceledException` is thrown because the Worker cancellation token is requested (host shutdown), the connection failure path is **not** taken: the exception is rethrown and is **not** persisted as a sync failure / circuit open.

## Provider pagination and sync window

Both real HTTP clients use a **one-hour** lookback window (`UtcNow - 1 hour`). Page indexes start at **0**.

### Trendyol GO

Source: `TrendyolGoFoodPlatformClient` (currently uncommitted changes in working tree).

| Setting | Value |
|---------|-------|
| Page size | 50 (`FetchPageSize`) |
| Hard page cap | 20 (`MaxFetchPages`) |
| Theoretical max packages | 1000 (20 × 50) |
| Query window | `packageModificationStartDate` = now − 1 hour (Unix ms) |

Rules:

- Malformed pagination metadata (negative page/total, echoed page not advancing, empty page while more pages claimed) **fails the sync** for that connection.
- If the provider still claims more pages when the hard cap is reached, sync **fails** (`page cap reached while more pages were reported`).
- Successful completion within the cap returns the collected packages.

### Yemeksepeti

Source: `YemeksepetiFoodPlatformClient` (currently uncommitted changes in working tree).

| Setting | Value |
|---------|-------|
| Page size | `Platform:Yemeksepeti:DefaultPageSize` (default **20**) |
| Hard page cap | 50 (`MaxFetchPages`) |
| Theoretical max orders | 1000 (50 × 20 at default page size) |
| Query window | `start_time` / `end_time` = last one hour (Unix ms) |

Rules:

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

A blank id is not entered in that map, so blank ids are not collapsed together. This dedupe is inside the provider fetch. It is not database idempotency.

## After the fetch

`OrderSyncService` skips an order whose `ExternalOrderId` is missing or blank. It does not insert it.

Orders that have an id are upserted by `Platform` + `ExternalOrderId` (`IdempotencyKey`). That database rule is separate from the page-level duplicate check above.

### Getir

No real HTTP pagination client is registered in Real mode. Getir remains mock (see [../integrations/food-platforms.md](../integrations/food-platforms.md)).

## Order upsert

Idempotency key: SHA-256 of `Platform:ExternalOrderId` (Base64), stored as `Order.IdempotencyKey`.

Rules:

- Preserve parent `Order` row, `Id`, and `CreatedAt`.
- New orders are inserted; then auto-approve / receipt hooks may run for eligible new rows.
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

## Logging notes

- Verbose per-connection start/complete details are Debug-level in production-oriented paths.
- Provider HTTP failures log status code and timing **without response bodies** (see platform clients).
- Do not log decrypted credentials, connection strings, or tokens.

## Known gaps

- Worker startup log in Real mode still claims Yemeksepeti uses mock clients; DI registers the real Yemeksepeti HTTP client. Trust DI (`ServiceCollectionExtensions`), not that startup sentence, until the log is corrected.
- Yemeksepeti partner endpoint path still carries a TODO to confirm against official Partner API docs.
- Getir has no real fetch client yet.
- No OpenTelemetry exporter or metrics for sync throughput (see [../operations/observability.md](../operations/observability.md)).

## Related docs

- [../integrations/food-platforms.md](../integrations/food-platforms.md)
- [../operations/observability.md](../operations/observability.md)
- [../operations/local-development.md](../operations/local-development.md)
