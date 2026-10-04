# Order lifecycle

**Owns:** canonical order statuses, operator transitions, provider calls, Worker sync upsert/merge, and note field semantics.

**Does not own:** Live Screen UI ([live-screen.md](./live-screen.md)), frontend module layout ([../frontend/architecture.md](../frontend/architecture.md)), PrintJob push ([../future/printjob-status-signalr.md](../future/printjob-status-signalr.md)).

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

### Sync status merge

On existing orders, mapped external status is merged via `MergeInternalStatusForSync` before write:

1. If existing is `Delivered`, keep existing.
2. If existing is `Cancelled` or `Failed`, keep existing.
3. If incoming is `Cancelled` or `Failed`, take incoming (unless already locked by 1–2).
4. Otherwise compare operational progress ranks (`New` → `Accepted` → `Preparing` → `ReadyForPickup` → `OnTheWay` → `Delivered`). Incoming with a **lower** rank is ignored so stale provider/mocks do not undo operator progress.

### Child replace on real updates

When a semantic change is detected, sync opens a transaction, updates scalars (including `CustomerNote`, money, platform status, etc.), sets `UpdatedAt`, deletes existing `OrderItemOption` / `OrderItem` rows with `ExecuteDeleteAsync`, then inserts items/options from the provider payload and `SaveChanges`.

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
- SignalR / push for PrintJob status is planning-only; see [../future/printjob-status-signalr.md](../future/printjob-status-signalr.md). Live Screen polling is unrelated and remains canonical until a deliberate phase.

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
