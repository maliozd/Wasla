# Food platform integrations

## Purpose and scope

This document describes how Wasla connects to food-delivery platforms for order fetch and related configuration.

It owns:

- `Platforms:ProviderMode` (Mock / Real)
- DI registration of `IFoodPlatformClient` implementations
- Platform connection identity (one connection per platform; `StoreId` is configuration)
- High-level Trendyol GO / Yemeksepeti / Getir behavior

It does not own Worker cycle timing or upsert details (see [../orders/synchronization.md](../orders/synchronization.md)), Print Bridge, or tenancy.

Pagination and unchanged-upsert behavior referenced here reflect **currently uncommitted** working-tree changes unless noted otherwise.

## Provider mode

Canonical config key: `Platforms:ProviderMode`.

| Value | Meaning |
|-------|---------|
| `Mock` | Mock clients only. No real provider HTTP. |
| `Real` | Real HTTP clients where implemented; others stay mock. |

Rules (source: `ProviderModeResolver`):

- Required. Missing or invalid values **fail fast**.
- No silent default to Mock.
- Do not use legacy `UseMocks` / `Platform:UseMocks` / `Platforms:UseMocks` keys.

Web, Worker, and API should use the **same** ProviderMode in a given environment.

## DI registrations (Real vs Mock)

Source: `src/Wasla.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`.

### Mock mode

| Platform enum | Client |
|---------------|--------|
| Yemeksepeti | `MockYemeksepetiFoodPlatformClient` |
| GetirYemek | `MockGetirYemekFoodPlatformClient` |
| TrendyolYemek | `MockTrendyolYemekFoodPlatformClient` |

No `HttpClient` is registered against tgoapis.com or Yemeksepeti partner hosts in Mock mode.

### Real mode

| Platform enum | Client | Notes |
|---------------|--------|-------|
| TrendyolYemek | `TrendyolGoFoodPlatformClient` | Real HTTP (Basic auth + Trendyol GO meal packages API) |
| Yemeksepeti | `YemeksepetiFoodPlatformClient` | Real HTTP (OAuth2 client_credentials + Partner Picking orders API) |
| GetirYemek | `MockGetirYemekFoodPlatformClient` | Still mock; real client not implemented |

**Mismatch:** `OrderSyncWorker.StartAsync` logs that Yemeksepeti still uses mock clients in Real mode. That log is wrong relative to DI. Prefer this table.

## `IFoodPlatformClient`

Source: `src/Wasla.Application/Abstractions/Platform/IFoodPlatformClient.cs`.

Capabilities on the interface:

- `FetchOrdersAsync(connection, window, ct)` — used by Worker sync; returns every page of one fetch or throws
- `MaxFetchWindow` — the longest window the client requests in one fetch (Trendyol GO: 1 hour), or null when the client ignores the window (Yemeksepeti, mock clients)
- Lifecycle: `AcceptOrderAsync`, `MarkInvoicedAsync`, `MarkShippedAsync`, `MarkDeliveredAsync`, `RejectOrderAsync`

Yemeksepeti’s real client documents that lifecycle fulfillment endpoints are **not yet implemented** for the partner API path. Do not assume Real mode means accept/reject/deliver against Yemeksepeti works end-to-end.

## Platform connections

Stored in each tenant’s TenantDb (`PlatformConnection`).

### Identity

Each tenant may have at most one PlatformConnection per platform. StoreId is provider configuration, not part of connection uniqueness. Source: `PlatformConnectionService.CreateAsync` / `UpdateAsync` and unique index `IX_PlatformConnections_Platform`.

Implications:

- A second connection for the same platform is rejected even when `StoreId` differs.
- Deactivating a connection does not free that platform for a second row. The unique index is not filtered by `IsActive`.
- `Platform` cannot be changed on an existing connection. An edit that posts a different platform is rejected and leaves the stored row unchanged.
- Different platforms may coexist on one tenant (one Trendyol connection, one Yemeksepeti connection, and one Getir connection).
- `StoreId` identifies this tenant's location in the provider. It is stored and sent to the provider. It does not model a second restaurant inside the tenant.
- A business with several restaurants uses a separate tenant per location. See [../architecture/tenancy.md](../architecture/tenancy.md).

### Credential field mapping (high level)

| Platform | Typical mapping |
|----------|-----------------|
| Trendyol GO | `SupplierId` or fallback `StoreId` → supplier id; encrypted API key/secret for Basic auth; optional `StoreId` query filter |
| Yemeksepeti | `SupplierId` → chainId (required); `StoreId` → vendorId (falls back to chainId); encrypted API key/secret → OAuth clientId/clientSecret |
| Getir | Mock credentials / connection shape only until a real client exists |

Never log decrypted secrets.

## Application-level provider options

Sections (not tenant secrets):

| Section | Purpose |
|---------|---------|
| `Platform:TrendyolGo` | `BaseUrl`, timeouts, agent name |
| `Platform:Yemeksepeti` | `BaseUrl`, `TokenPath`, `DefaultPageSize` (default 20), timeouts |

Per-tenant API keys live encrypted in TenantDb after UI setup.

## Fetch summary (Real HTTP)

Details and numbers: [../orders/synchronization.md](../orders/synchronization.md).

| Provider | Window | Statuses requested | Page size | Page cap |
|----------|--------|--------------------|-----------|----------|
| Trendyol GO | From the checkpoint (`LastSuccessfulSync`) − 5 minutes to now, in windows of at most 1 hour, at most 12 per run; 1 hour back when there is no checkpoint | All documented `packageStatuses`, including `Cancelled`, `UnSupplied` and `Delivered` | 50 | 20 pages per window (≤ 1000 packages) |
| Yemeksepeti | Last 1 hour (not checkpointed) | Provider default | default 20 | 50 pages (≤ 1000 at default size) |

Malformed pagination or “more pages at cap” fails that connection’s sync, and the checkpoint does not move past the failed window. Outage recovery, failure rules and the Trendyol GO request contract: [../orders/synchronization.md](../orders/synchronization.md#checkpoint-and-outage-recovery).

## Webhooks

For Trendyol GO the target is webhook first, with polling as the safety net (WAS-17). No webhook is implemented for any platform yet; polling is the only ingestion path. See [../orders/synchronization.md](../orders/synchronization.md#webhooks-and-polling).

## Mock order behavior (product rules)

When Mock clients generate orders:

- New mock orders should start as New/Created (“Yeni”).
- Mock updates for existing orders may move to final statuses (Delivered / Cancelled) only, unless a task explicitly expands intermediate status simulation.

## Known gaps

- Getir: Real mode still mock.
- Yemeksepeti lifecycle APIs: not implemented on the real client.
- Worker Real-mode startup log disagrees with DI for Yemeksepeti.
- Yemeksepeti polling is not checkpointed: a change older than one hour at the next successful run can be missed.
- Trendyol GO date-range limits, retention and the scope of the request limit are undocumented and not yet verified against the Stage API.

## Related docs

- [../orders/synchronization.md](../orders/synchronization.md)
- [../operations/deployment.md](../operations/deployment.md)
- [../architecture/tenancy.md](../architecture/tenancy.md)
