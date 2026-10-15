# Order lifecycle

**Owns:** canonical order statuses, operator transitions, provider calls, Worker sync upsert/merge, and note field semantics.

**Does not own:** Live Screen UI ([live-screen.md](./live-screen.md)), frontend module layout ([../frontend/architecture.md](../frontend/architecture.md)), live receipt print status ([../architecture/realtime.md](../architecture/realtime.md)).

---

## Purpose

Document how Wasla stores and advances restaurant orders from platform sync and operator actions. Source is authoritative; this file does not invent a state machine beyond what the code enforces.

---

## Current implementation

### Canonical statuses

`Wasla.Domain.Enums.OrderStatus` (`src/Wasla.Domain/Enums/OrderStatus.cs`):

| Value | Enum |
| --- | --- |
| 0 | `New` |
| 1 | `Accepted` |
| 2 | `Preparing` |
| 3 | `ReadyForPickup` |
| 4 | `OnTheWay` |
| 5 | `Delivered` |
| 6 | `Cancelled` |
| 7 | `Failed` |

Raw platform strings remain on `Order.PlatformStatus`. Mapping into these values is `DefaultOrderStatusMapper` (`src/Wasla.Infrastructure/Platform/Mapping/DefaultOrderStatusMapper.cs`), per platform (Yemeksepeti, Getir, Trendyol GO, plus mock/legacy labels). Unknown Trendyol strings default to `New` with a warning log.

### Order entity notes

`src/Wasla.Domain/Entities/Customer/Order.cs`:

- `CustomerNote` — order-level instruction from the end customer (delivery/prep request). Normalized and capped at 2000 characters on sync.
- Item instructions use `OrderItem.Notes` (`src/Wasla.Domain/Entities/Customer/OrderItem.cs`), not `CustomerNote`.

End-customer fields (`CustomerName`, `CustomerPhone`, `CustomerAddress`) refer to the restaurant’s buyer, not the SaaS tenant.

### Idempotency and parent row

- Idempotency key = SHA-256 Base64 of `"{Platform}:{ExternalOrderId}"`, stored as `Order.IdempotencyKey`.
- Upsert looks up by that key (`OrderSyncService.UpsertOrderAsync`).
- On update, the parent `Order` row is kept: same `Id`, same entity identity; `CreatedAt` is not rewritten by the update path (only domain timestamps such as `AcceptedAt` / `DeliveredAt` / `CancelledAt` / `UpdatedAt` change when a real update runs).

### Operator transitions (`OrderActionService`)

HTTP surface: `Wasla.Web` `OrdersController` under `/orders/{id}/…` (approve, reject, start-preparing, mark-ready, hand-to-courier, mark-delivered). Implementation: `src/Wasla.Infrastructure/Services/OrderActionService.cs`.

| Current status required | Action | Next status | Provider call |
| --- | --- | --- | --- |
| `New` | Approve | `Accepted` | `AcceptOrderAsync` |
| `New` | Reject | `Cancelled` | `RejectOrderAsync` |
| `Accepted` | Start preparing | `Preparing` | **None** (local kitchen step; shared client has no prepare hook yet) |
| `Preparing` | Mark ready | `ReadyForPickup` | `MarkInvoicedAsync` (shared name; means ready for all platforms) |
| `ReadyForPickup` | — (no restaurant action) | `OnTheWay` via provider sync | — |
| `OnTheWay` | — (no restaurant action) | `Delivered` via provider sync | — |

Mark ready is the restaurant's last action. The platform courier reports pickup (`OnTheWay`, e.g. Yemeksepeti `DISPATCHED`) and delivery (`Delivered`). For every current platform (Trendyol GO, Yemeksepeti, Getir) both arrive through provider synchronization (and later webhooks), and the status merge below applies them. No Live Screen, detail panel or Orders view renders a Hand to courier or Delivered action. `POST /orders/{id}/hand-to-courier` and `POST /orders/{id}/mark-delivered` still exist but refuse these orders with `Orders.PickupReportedByPlatform` / `Orders.DeliveryReportedByPlatform` (HTTP 400, client key `ordersInvalidStatusForAction`) before any provider call or status change; see `OrderDeliveryPolicy`. The Trendyol GO manual shipped/delivered client calls are kept for a future restaurant-courier fulfillment mode.

Rules observed in source:

- Wrong current status → fail (`Orders.InvalidStatusForAction`).
- Provider failure → local status is **not** advanced.
- Connection selection: first active `PlatformConnection` for the order’s platform (order does not store connection id yet).
- Legacy `TryUpdateStatusAsync` only allows changes while status is still `New` and does **not** call the provider (older path; approve/reject/lifecycle methods are the operational API).

There is no free-form transition graph in code beyond the rows above.

### Concurrent actions (WAS-104)

Of several simultaneous actions on one order (two operators, two tabs, a retried request, an operator and Worker auto-approve), exactly one runs; it calls the provider once, raises `ChangeVersion` once and publishes one `OrderChanged`. This holds across every Web and Worker process that shares the tenant database.

1. **Claim before the provider.** Every action first takes the order's claim: a SQL Server application lock in the tenant database (`sp_getapplock`, resource `Wasla.OrderAction.{orderId}`, session-owned, no wait), held on the action's own connection until it has called the provider and written the result (`OrderActionLock`). An action that finds the claim held is refused at once with `Orders.InvalidStatusForAction` (HTTP 400, client key `ordersInvalidStatusForAction`): it reads nothing, calls no provider, writes and publishes nothing. When the claim is free, the status is read and checked inside it, so an action that arrives after the winner finished sees the new status and is refused the same way.
2. **Single-winner write.** The status change is a conditional update (`… WHERE Id = @id AND InternalStatus = @required`) in the short transaction that raises `ChangeVersion`. Only the first matching write changes the row, and a status Worker sync applied during the provider call (for example the platform cancelling the order) is never overwritten: the action then returns `Orders.InvalidStatusForAction`, logs a warning and publishes nothing. The transition's timestamp (`AcceptedAt`, `CancelledAt`, `DeliveredAt`) is only set when empty.
3. **No request cancellation after the claim.** Once an action holds the claim, its provider call and local write no longer use the request's cancellation token, so a closed browser tab does not leave a provider change without its local status. The provider HTTP clients bound the call with their own timeouts.

Failure handling:

- **Provider failure:** nothing is written or published and the claim is released; a retry is a new attempt.
- **Process stops while holding the claim:** the lock ends with the database session, so the order is not left claimed. A release that fails discards the pooled connection, which ends the session.
- **Provider succeeded, local write failed** (for example the tenant database is unreachable right after the call): the order keeps its old status, and a later retry calls the provider again. No current provider call carries an idempotency key (Trendyol GO lifecycle calls are plain `PUT`s; the Yemeksepeti lifecycle calls are not implemented and fail). Closing this gap needs a durable claim record on the order (a schema change), which is not part of WAS-104.
- **Worker sync upsert** is not claimed. The conditional write keeps an action from overwriting sync, and sync merges into the status committed when its update transaction locks the order row, so it never overwrites an action committed meanwhile (WAS-123, see [Child replace on real updates](#child-replace-on-real-updates)).

The claim needs SQL Server. SQLite-backed unit tests run without it (the conditional write still applies); any other provider is refused. Tests: `tests/Wasla.UnitTests/Orders/OrderActionConcurrencySqlServerTests.cs` ([../operations/testing.md](../operations/testing.md#sql-server-concurrency-tests)).

### Sync status merge

On existing orders, mapped external status is merged via `MergeInternalStatusForSync` before write:

1. If existing is `Delivered`, keep existing.
2. If existing is `Cancelled` or `Failed`, keep existing.
3. If incoming is `Cancelled` or `Failed`, take incoming (unless already locked by 1–2).
4. Otherwise compare operational progress ranks (`New` → `Accepted` → `Preparing` → `ReadyForPickup` → `OnTheWay` → `Delivered`). Incoming with a **lower** rank is ignored so stale provider/mocks do not undo operator progress.

### Child replace on real updates

When a semantic change is detected, sync opens a transaction and first locks the order row by writing `UpdatedAt`, then reads the row again (WAS-123). An operator action, auto-approve or another Worker process may have committed a status since sync's first read; the merge, the transition timestamps and the receipt rule start from the status read inside the transaction, and the row stays locked until the commit. If the row read again is already semantically equal to the payload, the transaction is rolled back and the order counts as unchanged: no write, no `ChangeVersion` raise, no notification, no receipt. Otherwise sync updates scalars (including `CustomerNote`, money, platform status, etc.), sets `UpdatedAt`, deletes existing `OrderItemOption` / `OrderItem` rows with `ExecuteDeleteAsync`, then inserts items/options from the provider payload, each with its position in the provider's list (`Sequence`, see [synchronization.md](./synchronization.md#order-upsert)), and `SaveChanges`.

### Unchanged-order short circuit

Implemented in `src/Wasla.Infrastructure/Sync/OrderSyncService.cs`.

Behavior when `IsSemanticallyUnchanged` returns true:

- No mutation transaction
- No child delete/reinsert
- No `SaveChanges` for that order
- No `UpdatedAt` bump
- No `RawPayloadJson` rewrite (raw-payload-only differences are intentionally ignored; payload is diagnostic, not canonical)

Real field/item changes still take the existing update path above (which does rewrite `RawPayloadJson`).

Compared fields include merged internal status, platform status, order code, customer fields, note, money, payment fields, `CreatedAtPlatform`, and item/option multisets (not `Subtotal` / `ExternalItemId`, which are not persisted).

### Platform connections

Sync iterates each tenant's platform connections. Each tenant may have at most one PlatformConnection per platform, so an order action cannot choose among several connections for the same platform. The action loads the active connection for `order.Platform`. If that connection is missing or inactive, the action fails (`Orders.ActionFailed`) and does not call the provider. `StoreId` identifies the location in the provider and is not part of connection uniqueness. Several restaurant locations are separate tenants. See [../architecture/tenancy.md](../architecture/tenancy.md).

---

## Future

- Shared prepare provider hook for `Preparing` (commented as deferred in `OrderActionService`).
- Every successful lifecycle action and every ingested order change is announced to open Live Screens as an invalidation after its save ([../architecture/realtime.md](../architecture/realtime.md#where-order-changes-are-published-was-24)); the snapshot stays canonical.

---

## Source map

| Concern | Location |
| --- | --- |
| Status enum | `src/Wasla.Domain/Enums/OrderStatus.cs` |
| Order / notes | `src/Wasla.Domain/Entities/Customer/Order.cs`, `OrderItem.cs` |
| Operator actions | `src/Wasla.Infrastructure/Services/OrderActionService.cs` |
| HTTP actions | `src/Wasla.Web/Areas/Tenant/Controllers/OrdersController.cs` |
| Status mapping | `src/Wasla.Infrastructure/Platform/Mapping/DefaultOrderStatusMapper.cs` |
| Sync upsert / merge / short circuit | `src/Wasla.Infrastructure/Sync/OrderSyncService.cs` |
