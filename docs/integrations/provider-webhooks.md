# Provider webhooks: decision record (WAS-17)

## Purpose and scope

This record says what the official documentation of Trendyol GO and Yemeksepeti confirms about inbound webhooks for restaurant orders, what it leaves open, and how Wasla should build on it. It is the evidence base for WAS-16 and its subtasks WAS-18 to WAS-21, and for the Yemeksepeti confirmation task WAS-70.

It owns:

- The per-provider webhook verdicts and the evidence behind them.
- The architectural decision on webhooks versus reconciliation polling.
- The open questions that must be answered by the providers.

It does not own the current polling implementation ([../orders/synchronization.md](../orders/synchronization.md)), provider configuration and HTTP clients ([food-platforms.md](food-platforms.md)) or order lifecycle rules ([../orders/lifecycle.md](../orders/lifecycle.md)).

No webhook is implemented. Nothing in this record describes existing Wasla behavior unless it says so and names the source file.

Research date: 2026-10-19. It re-verifies and supersedes the WAS-17 research comment of 2026-10-06 (see [Changes since the 2026-10-06 research](#changes-since-the-2026-10-06-research)).

## Evidence standard

- Provider behavior is taken only from the official sources in the [source register](#source-register). Source ids such as [T5] or [Y7] point there.
- Third-party pages were used only to find official pages. They are not evidence.
- No provider was contacted, no credential was used and no stage or production call was made.
- No official provider correspondence about webhooks exists in this repository or on the Linear issues. The only provider-related file in `docs/` is an internal note about reaching the Trendyol GO stage panel through a VDS IP; it says nothing about webhooks.

Each finding has one classification:

| Classification | Meaning |
|---|---|
| Confirmed | Stated in a current official source |
| Not supported | The official source lists what exists, and this is not in the list |
| Not documented | No official source answers it |
| Partner confirmation required | Stated only in an archived, generic or ambiguous official source, or the source contradicts itself; the provider must confirm before Wasla relies on it |
| Not applicable | The question does not apply to this provider's model |

## Decision summary

### Trendyol GO

**Webhook delivery for restaurant (Meal) orders is confirmed.** The English documentation has a "Webhook Meal Integration" section [T1–T10]. The integrator registers itself through an API, subscribes to event types, and Trendyol GO POSTs each event to an HTTPS URL the integrator chooses [T1, T5].

What the contract gives:

- Seven event types, from `created` to `delivered`, `cancelled` and `unsupplied` [T2].
- A full order in `created` only; every other event carries identifiers and a timestamp [T2].
- Authentication by static custom headers or query parameters that the integrator defines [T5].
- A receiver timeout chosen by the integrator, 10–5000 ms [T5].
- Up to 3 retries [T1, T3], and a possible duplicate delivery [T5].

What it does not give:

- A signature, timestamp or nonce check.
- Sender IP ranges.
- Ordering and event ids.
- A defined acknowledgement response.
- Events for acceptance (`Picking`) or preparation (`Invoiced`) [T2, T14].
- A documented way to tell which seller an event belongs to. One integrator with one URL set and one secret set serves every seller [T1, T5]. `supplierId` and `storeId` appear only in the `created` sample, with no field definition, and the status events have neither [T2].

Confidence: high that the webhook exists and what its events and payloads are. Medium on delivery semantics, because the retry wording is ambiguous [T1] and acknowledgement, ordering and the disable threshold are undocumented.

Recommendation:

- Use Trendyol GO webhooks provider-specifically, as a faster path in front of the existing checkpointed polling, never as a replacement for it.
- Enabling the external endpoint is **blocked**, and so is the tenant-routing design behind it. Wasla cannot yet safely tell which tenant a previously unseen order belongs to. Trendyol GO must first confirm a tenant discriminator, by documentation or by a stage test that it then confirms (see [Tenant routing](#tenant-routing)).
- Internal, provider-agnostic ingestion work can go ahead now (see [Implications](#implications-for-was-18-to-was-21)).

### Yemeksepeti

**Webhook delivery for restaurant orders is not confirmed from a current official source.**

- The current public developer site, developer.yemeksepeti.com, is for Local Shops (quick commerce). It sends restaurant partners to integration.yemeksepeti.com [Y1].
- integration.yemeksepeti.com returned HTTP 522 on 2026-10-19 [Y4], as it did on 2026-10-06.
- An Internet Archive copy of that portal from 2025-07-06 [Y5] describes a restaurant model built on Delivery Hero's Integration Middleware. The integrator builds a "plugin" that receives new orders by webhook (Order Dispatch) and cancellations by webhook (Order Status Update). It calls the middleware to accept, reject and mark orders prepared. A report service lists accepted or cancelled order ids for the last 24 hours. The archived page links to Delivery Hero's current POS Plugin and Middleware API specifications [Y7, Y8] as its API reference.
- Delivery Hero's own platform list for that middleware does not include Yemeksepeti or Türkiye [Y6].

Confidence: low. The archived source is 15 months old, and the live portal is unavailable. Everything about the Yemeksepeti restaurant contract is therefore **partner confirmation required**.

The archived model matters even before it is confirmed. If it is current:

- New restaurant orders reach an integrator only by webhook. The report service returns only accepted and cancelled orders [Y8], so polling cannot take in new orders.
- Wasla's current Yemeksepeti client follows the Local Shops Partner API (OAuth client credentials and a vendor orders GET; see [Comparison with Wasla today](#comparison-with-wasla-today)). It would then be the wrong product for restaurants.

Conclusion: the Yemeksepeti restaurant webhook contract cannot be confirmed at present. Implementation is blocked by WAS-70, the separate provider-confirmation task. Build nothing for Yemeksepeti webhooks until WAS-70 obtains the current restaurant contract.

### Webhooks and reconciliation polling

Webhooks can cut the normal order-arrival latency. They do not by themselves remove the need for reconciliation polling. Polling may be reduced or removed only when official delivery, retry, ordering and completeness guarantees justify it.

Neither provider gives those guarantees:

- **Trendyol GO** retries an event only 3 times [T1, T3]. It may disable an integration after downtime and offers events from the disabled period for at most one day [T1, T6]. It documents no ordering, and it has no events for `Picking` or `Invoiced`. Uber Eats can move an order to `Invoiced` by itself [T16], and no webhook reports that.
- **Yemeksepeti** has no current confirmed contract.

**Decision:**

1. One idempotent shared ingestion path for webhooks and polling (WAS-18). Its first internal part is in place, with polling as its only caller ([../orders/synchronization.md](../orders/synchronization.md#shared-ingestion-was-18)).
2. A durable webhook inbox (WAS-19).
3. Polling stays as reconciliation (WAS-21).
4. No external webhook endpoint and no central routing schema until a tenant discriminator is confirmed ([Tenant routing](#tenant-routing)).

For Trendyol GO the checkpointed polling stays enabled for every connection. Its frequency may be lowered only after Wasla's own metrics show webhook coverage in production, and it is not removed while the contract stays as documented today.

Webhook support is confirmed for one provider only, so enablement is provider-specific. Wasla must not define a false common webhook contract. The shared part is the ingestion pipeline behind provider-specific adapters, not the HTTP contract.

## Trendyol GO findings

Product: "Uber Eats Trendyol Go – Meal", Webhook Meal Integration (English documentation). The Turkish "Yemek Entegrasyonu" section has no webhook page [T17]. The Grocery webhook [T18] is a different product with a different contract; it is cited below only where it shows that a Meal behavior is undocumented, never as evidence for Meal.

| Question | Classification | Finding | Source |
|---|---|---|---|
| Inbound webhook for restaurant orders | Confirmed | Near-real-time package status events for Meal orders, delivered to integrator endpoints | [T1] |
| Registration | Confirmed | Self-service through API. Create an integrator with the base URL, event types and secrets, using a seller's token and seller id. The integrator starts disabled. Add sellers, enable, send a test order, then repeat on production. Create only one integrator and add every seller to it | [T1, T5, T6, T8] |
| Production creation path | Partner confirmation required | Create Integrator lists only a stage URL, and states that a successful stage test leads to a transfer to production. The other management endpoints list both stage and production URLs | [T5, T6, T9] |
| Certification | Not documented | No certification step beyond the stage test | [T1, T5] |
| Stage access | Confirmed | Stage API access needs Wasla's outbound IPs authorized through a seller request. Production needs none, but an IP can be blocked | [T12] |
| Method | Confirmed | POST to `baseUrl + destinationUrl` of the event type | [T1, T5] |
| Content type | Not documented | Payload examples are JSON; the `Content-Type` header is not stated | [T2] |
| Endpoint URL rules | Confirmed | `baseUrl` must be HTTPS, a fully qualified domain name and under 100 characters. Each event type has its own `destinationUrl` subpath | [T5] |
| Event types | Confirmed | `created`, `pickupEtaCalculated`, `courierNearby`, `shipped`, `delivered`, `cancelled` (not caused by the seller), `unsupplied` (caused by the seller). The integrator subscribes per type | [T2, T5] |
| Event type in the payload | Not documented | No payload example has an event-type field, so the receiver must tell types apart by `destinationUrl` | [T2, T5] |
| New order contents | Confirmed | `created` carries the full package: customer, address, lines, payment and totals. It omits some GET fields (for example `packageModificationDate`) and replaces `packageCreationDate` with `timestamp`. The documentation warns that the two models will never be identical | [T2] |
| Other events | Confirmed | `courierNearby`, `shipped` and `delivered` carry `id`, `orderNumber`, `orderCode` and `timestamp`. `cancelled` and `unsupplied` add `status`, cancelled line items and `cancelInfo` (reason code and text). `pickupEtaCalculated` carries `id`, `orderNumber`, `pickupEtaState` and the estimated pickup window, and no `timestamp`. None of them carries `supplierId` or `storeId` | [T2] |
| Seller identity in `created` | Partner confirmation required | The sample includes `supplierId` and `storeId`. No field definition says they are always present, or that `supplierId` equals the seller id registered with Add Seller | [T2, T8] |
| Seller-specific URL, integrator or secret | Not documented | The documentation advises one integrator for all sellers. `baseUrl`, `destinationUrl` and `secrets` are set per integrator, not per seller | [T1, T5, T9] |
| Package lookup that identifies the seller | Not documented | Both documented package reads, the list and the single package by `packageId`, sit under `/suppliers/{supplierid}/` and use that supplier's own credentials. They cannot find the seller of an unknown package | [T11, T15] |
| Meaning of `timestamp` | Not documented | For `created` it stands in for the creation date. For the other events it is not defined (event time or send time) | [T2] |
| Accepted | Not supported | No event. `Picking` is a status the seller sets | [T2, T14] |
| Rejected | Confirmed | `unsupplied` covers cancellation by the seller | [T2, T14] |
| Preparation | Not supported | No event for `Invoiced`, which the seller sets. In stores moved to Uber Eats, Uber can set `Invoiced` itself from the preparation time (currently Bilecik only) | [T2, T14, T16] |
| Courier assignment | Not documented | No assignment event. `pickupEtaCalculated` reports a changed estimated pickup time | [T2] |
| Courier pickup | Confirmed | `courierNearby` (courier near the store), then `shipped` (handed to the courier) | [T2] |
| On the way | Confirmed | No separate event; `shipped` is the handover | [T2] |
| Delivered | Confirmed | `delivered` | [T2] |
| Cancellation | Confirmed | `cancelled` and `unsupplied`, with line items and reason code | [T2, T14] |
| Signature | Not documented | — | [T1, T5] |
| Shared secret | Confirmed | Static values in `secrets.httpHeaders` and/or `secrets.httpQueryParams`, defined by the integrator and sent with every event. One set per integrator | [T5, T9] |
| Bearer token | Not applicable | `Authorization-int` is the integrator's token for calls *to* Trendyol GO's management endpoints, not for events sent to Wasla | [T6, T9] |
| Timestamp, nonce, replay protection | Not documented | No signed timestamp, nonce or replay window | [T1, T5] |
| IP allowlist of the sender | Not documented | No sender ranges are published | [T1, T12] |
| TLS and certificates | Confirmed (HTTPS only) | HTTPS is required. Protocol version and certificate rules for the receiver are not stated | [T5] |
| Retries | Partner confirmation required | Up to 3 retries before an event is marked failed. The same page also says failed events are retried every minute for at most 3 attempts, and the FAQ says 3 retries every minute. Count and spacing are not unambiguous | [T1, T3] |
| Timeout | Confirmed | Set by the integrator, 10–5000 ms | [T5] |
| Backoff, total delivery duration | Not documented | — | [T1] |
| Integration disabled after downtime | Confirmed, threshold not documented | Trendyol may disable an integrator whose system is down for some time; the integrator re-enables it. After re-enabling, events from the disabled period can be retrieved for at most one day. How (automatic redelivery or something else) is not stated | [T1, T6] |
| Ordering | Not documented | — | [T1, T2] |
| Duplicates | Confirmed | Short-term duplicate events are possible; endpoints must be idempotent | [T1, T5] |
| Event identifier | Not documented | Payloads carry the package `id` (the 64-character package id of the orders API) but no event id or sequence | [T2, T15] |
| Required response | Not documented | No status code or body is defined for Meal. The Grocery product expects only 2xx, which does not apply here | [T1, T18] |
| Sandbox and test delivery | Confirmed | Stage environment and a Create Test Order endpoint: at most 50 test orders a day on stage, 20 on production | [T1, T10] |
| Rate limit for follow-up calls | Confirmed (scope not documented) | At most 50 requests to the same endpoint in 10 seconds (HTTP 429 above). Per supplier, integrator or IP is not stated. The Service Limitations table does not list the order packages endpoint | [T11, T13] |
| Polling as reconciliation | Not documented | The provider does not say. Wasla's decision is in [Webhooks and reconciliation polling](#webhooks-and-reconciliation-polling) | — |

Multi-seller use:

- One integrator serves all sellers. Each Add Seller call needs that seller's Basic authorization [T1, T8].
- Removing the last seller disables the integrator [T8].
- The secrets are per integrator [T5], so every tenant's events arrive with the same credentials.

Whether a multi-tenant SaaS may register sellers of independent restaurants under one integrator is not stated (partner confirmation required).

## Yemeksepeti findings

### Restaurant integration (archived model)

All rows are **partner confirmation required**. The sources are the archived Yemeksepeti portal [Y5] and the Delivery Hero specifications it links to [Y7, Y8]. Delivery Hero does not list Yemeksepeti among that middleware's platforms [Y6], and the live portal is unavailable [Y4].

| Question | What the archived or linked source says | Source |
|---|---|---|
| Inbound webhook | Order Dispatch (new order) and Order Status Update (cancellation by Yemeksepeti) webhooks to the integrator's plugin | [Y5] |
| Registration | Sign an NDA, get approval from the Yemeksepeti technical team, and request credentials with a PGP key (they arrive encrypted by e-mail). Then a test integration, an end-to-end test with Yemeksepeti, production, pilot restaurants, and rollout. The integrator supplies an integration name, code, base URL and plugin user name; each chain has a chain code; each restaurant has a vendor code and a remote id | [Y5] |
| Method and content type | New order: `POST /order/{remoteId}`. Status update: `PUT /remoteId/{remoteId}/remoteOrder/{remoteOrderId}/posOrderStatus`. Both JSON | [Y7] |
| Event types | Status values `ORDER_CANCELLED`, `ORDER_PICKED_UP`, `PRODUCT_ORDER_MODIFICATION_SUCCESSFUL`, `PRODUCT_ORDER_MODIFICATION_FAILED`, `COURIER_ARRIVED_AT_VENDOR`, `SHOW_RIDER_WAITING_WARNING`, `HIDE_RIDER_WAITING_WARNING`. The list can grow. The archived Yemeksepeti page names only cancellations | [Y5, Y7] |
| New order contents | Full order on dispatch | [Y7] |
| Accept, reject | Outbound calls by the integrator (`order_accepted`, `order_rejected` to callback URLs in the order). An order not accepted or rejected in time is cancelled automatically | [Y7, Y8] |
| Preparation | Outbound "mark order as prepared" call | [Y5, Y8] |
| Courier assignment | Not documented | [Y7] |
| Courier at store, pickup | `COURIER_ARRIVED_AT_VENDOR`, `ORDER_PICKED_UP` | [Y7] |
| On the way, delivered | No separate status; no delivered status | [Y7] |
| Cancellation | `ORDER_CANCELLED` | [Y5, Y7] |
| Authentication | Every request carries a JWT (HS512) signed with a shared secret issued with the credentials, different for staging and production. The receiver also checks the `service: middleware` claim | [Y7] |
| Timestamp, nonce, replay | Not documented | [Y7] |
| IP allowlist | The archived page lists three middleware IP addresses to allow | [Y5] |
| TLS | A valid certificate is required; expired or self-signed certificates fail | [Y5, Y7] |
| Retries | On 429 or 5xx, up to 10 retries with exponential backoff and jitter, 30 s minimum, about 35 s rising to 240 s. During a POS outage orders keep being resent, and unanswered orders may be cancelled | [Y5, Y7] |
| Acknowledgement | Acknowledge within a few seconds, then process asynchronously. 200 or 202 with `remoteResponse.remoteOrderId` (mandatory, needed for later status updates). Allowed codes: 200, 201, 202, 400, 401, 429, 450, 500, 502 | [Y7] |
| Ordering | Not documented for orders | [Y7] |
| Duplicates | A slow acknowledgement can trigger a retry and a duplicate order | [Y7] |
| Event identifier | Not documented | [Y7] |
| Sandbox | A test integration and test restaurant on request; an end-to-end test with Yemeksepeti | [Y5] |
| Reconciliation endpoint | `GET /v2/chains/{chainCode}/orders/ids` (status `accepted` or `cancelled` only, 1–24 hours back) and `GET /v2/chains/{chainCode}/orders/{orderId}` | [Y5, Y8] |
| Rate limits | Not documented | [Y8] |
| Polling | Cannot take in new orders: the report service lists only accepted and cancelled orders | [Y8] |

### Local Shops Partner API

developer.yemeksepeti.com documents the Local Shops Partner API. Its webhook is mandatory and secured by a secret sent in `Authorization`. It is retried 5 times, 10 seconds apart, after 10 seconds without a response, and duplicates must be handled. Its GET and PUT calls are limited to 60 requests a minute [Y2, Y3].

The site itself says it is not for restaurants [Y1]. For restaurant orders it is **not applicable**, and it must not be used as the Yemeksepeti restaurant contract.

## Provider comparison matrix

| Topic | Trendyol GO Meal webhook | Yemeksepeti restaurant (archived model) |
|---|---|---|
| Webhook exists | Confirmed [T1] | Partner confirmation required [Y4, Y5] |
| Enablement | Self-service API, stage then production [T1, T5] | Approval, NDA and PGP credentials through Yemeksepeti [Y5] |
| New order payload | Full (`created`) [T2] | Full (dispatch) [Y7] |
| Status events | Courier near store, handover, delivered, cancellations, pickup ETA [T2] | Cancelled, picked up, courier arrived, modification results [Y7] |
| Accept / preparation events | None [T2] | Outbound calls by the integrator [Y8] |
| Delivered event | Yes [T2] | No [Y7] |
| Authentication | Static custom header or query parameter [T5] | HS512 JWT with shared secret [Y7] |
| Signature, timestamp, nonce | Not documented | JWT signature only; no timestamp or nonce documented [Y7] |
| Sender IP allowlist | Not documented | Three IPs in the archived page [Y5] |
| Receiver timeout | 10–5000 ms, chosen by the integrator [T5] | "A few seconds" [Y7] |
| Retries | Up to 3, about a minute apart (ambiguous) [T1, T3] | Up to 10, 30–240 s backoff, on 429/5xx [Y7] |
| After long downtime | Integration may be disabled; up to 1 day of events afterwards [T1, T6] | Orders may be cancelled [Y5] |
| Ordering | Not documented | Not documented |
| Duplicates | Possible [T5] | Possible [Y7] |
| Event id | None | None documented |
| Acknowledgement | Not documented | 200/202 with `remoteOrderId` [Y7] |
| Tenant discriminator | Not confirmed: one integrator, URL and secret for all sellers; `supplierId` only in the `created` sample [T1, T2, T5] | `remoteId` in the dispatch URL, chosen by the integrator [Y7] (partner confirmation required) |
| Reconciliation read | Order packages by status and modification window, or one package by id, per supplier [T15] | Accepted/cancelled order ids, ≤ 24 h [Y8] |
| Polling can take in new orders | Yes | No [Y8] |

## Comparison with Wasla today

Verified against `dev` at b27505e. None of the cited files changed up to 198b49b. Static review only; nothing was run.

### Ingestion and recovery

- **Polling only.** Neither real client has a webhook. `Wasla.Api` has controllers for auth, branches, dashboard, orders, platform connections and Print Bridge only, and no webhook, callback, inbox or signature code exists in `src`.
- **Trendyol GO polling** meets the reconciliation prerequisites since WAS-69. It requests every documented status, including the terminal ones, and resumes from the `LastSuccessfulSync` checkpoint with a 5-minute overlap and bounded history windows. See [Checkpoint and outage recovery](../orders/synchronization.md#checkpoint-and-outage-recovery). It is therefore able to cover what the webhook misses: `Picking`, `Invoiced` (including Uber's automatic `Invoiced` [T16]), events lost after 3 retries, and the period of a disabled integration beyond one day [T1, T6].
- **Yemeksepeti polling** calls `GET /v2/chains/{chainId}/vendors/{vendorId}/orders` on `yemeksepeti.partner.deliveryhero.io` with OAuth client credentials (`YemeksepetiFoodPlatformClient`, `appsettings.json` of Api and Worker). That matches the Local Shops Partner API [Y2], not the archived restaurant model [Y5, Y8]. The code marks the endpoint as unconfirmed (TODO), uses a fixed one-hour lookback, and throws `NotSupportedException` for every lifecycle action. If the archived model is current, this client cannot receive restaurant orders at all. This is a finding for WAS-70, not a change made here.

### Status and time mapping

- `DefaultOrderStatusMapper` maps Trendyol GO `Created` → New, `Shipped` → OnTheWay, `Delivered` → Delivered, and `Cancelled`/`UnSupplied` → Cancelled. That covers the webhook events `created`, `shipped`, `delivered`, `cancelled` and `unsupplied` without a new status. `courierNearby` and `pickupEtaCalculated` have no Wasla status; they are information, not lifecycle steps.
- The Yemeksepeti mapping (`RECEIVED`, `READY_FOR_PICKUP`, `DISPATCHED`, `DELIVERED`, `CANCELLED`) is Local Shops vocabulary [Y3]. The archived restaurant model uses `ORDER_CANCELLED`, `ORDER_PICKED_UP` and so on, and has no delivered status [Y7].
- `docs/orders/lifecycle.md` and `OrderDeliveryPolicy` give Yemeksepeti `DISPATCHED` as the pickup example and assume Delivered arrives through synchronization. Both are unconfirmed for Yemeksepeti restaurants.
- Order times: the `created` event replaces `packageCreationDate` with `timestamp` and omits `packageModificationDate` [T2]. A webhook insert would therefore have no `PlatformModifiedAt` until the next poll, which the existing "keep stored values" rule tolerates ([Order times](../orders/lifecycle.md#order-times-was-125)). Whether the `timestamp` of a status event may date `DeliveredAt` or `CancelledAt` depends on its undocumented meaning. Until Trendyol GO defines it, the existing [milestone rule](../orders/lifecycle.md#milestone-times) (observation time, live windows only) is the safe default.

### Idempotency and tenant boundaries

- **Idempotency key.** Orders are upserted by `Platform` + `ExternalOrderId`, and the Trendyol GO `ExternalOrderId` is the package `id` ([Fetch-level duplicate handling](../orders/synchronization.md#fetch-level-duplicate-handling)). Webhook payloads carry that same `id` [T2, T15], so polling and webhooks share one key.
- **Thin events.** The upsert treats every payload as a complete order and replaces items and options on a real change ([Order upsert](../orders/synchronization.md#order-upsert)). A Trendyol GO status event has no lines [T2], so it must not go through that path: it would look like an order with no items. Status-only events need their own apply path, and an event for an order Wasla does not have must not create one.
- **Tenant routing.** Unresolved; see [Tenant routing](#tenant-routing) below.
- **Shared side effects.** Setup-mode rules already sit in the shared side-effect services, so a future webhook path inherits them ([Operational mode: Setup](../orders/synchronization.md#operational-mode-setup)). So does `MergeInternalStatusForSync`, which keeps stale or out-of-order status changes from moving an order backwards.

### Tenant routing

**Facts**

- **Wasla today.** A webhook request arrives on a central host with no tenant in the URL. Tenant resolution today is host- and claim-based. Platform connections, their supplier ids and their credentials live in each TenantDb. `CentralDbContext` has no platform connection, supplier or package index.
- **Trendyol GO identifiers.** The documentation gives no reliable seller discriminator for an event:
  - One integrator, with one `baseUrl`, one set of `destinationUrl`s and one set of secrets, serves every seller [T1, T5, T9]. Neither the URL nor the secret tells sellers apart.
  - `supplierId` and `storeId` appear in the `created` sample only. Nothing defines them as always present, or ties `supplierId` to the seller id registered with Add Seller [T2, T8].
  - Status events carry no seller field at all [T2].
- **Package reads.** Both documented package reads are scoped to one supplier and use that supplier's credentials [T11, T15].

**Consequences**

- **A central routing index helps only in two cases:** the event carries a routing identifier Wasla can rely on, or the package is already known from an earlier `created` event or from polling.
- **It cannot by itself route a previously unseen event.**
  - A new `created` event could be routed only through its `supplierId`, whose presence and meaning are not contractual.
  - A status event for an unknown package has no identifier at all.
- **"Unknown order → targeted reconciliation fetch" has the same problem.**
  - A fetch needs a specific tenant's supplier id and credentials, which is exactly what is unknown.
  - Trying the credentials of several tenants is not acceptable. It would cross tenant boundaries and spend the shared request limit.
  - Until a discriminator exists, an unroutable event must not reach any tenant database. That tenant's own checkpointed polling picks the change up. Whether such an event is dropped or held without tenant data is a WAS-19/WAS-20 design point.
- **Usable discriminators.** Any one of these would resolve it, once confirmed by Trendyol GO:
  - A seller-specific destination URL.
  - A seller-specific integrator or secret.
  - Seller identity in a documented payload field or header on every event.
  - An authoritative package lookup that returns the seller before any tenant database is accessed.

  A stage test can show which candidate exists. Production routing may rely on it only after Trendyol GO confirms it is part of the contract.
- **What is blocked.**
  - No central schema change should be approved on the current documentation alone.
  - Enabling the Trendyol GO endpoint and the final routing design wait for a confirmed discriminator.
- **Yemeksepeti, for contrast.** In the archived model the dispatch URL carries the integrator's own restaurant identifier: `POST /order/{remoteId}`, where `remoteId` is the integrator's id for the restaurant [Y7]. That would give a per-restaurant discriminator if the model is confirmed (WAS-70).

### Missing components (gaps, not changed here)

| Component | Needed for | Issue |
|---|---|---|
| Normalized ingestion contracts and a status-only apply path that never creates orders or replaces items, used first by polling | Thin events, one pipeline | WAS-18 (can proceed) |
| Webhook endpoint per provider adapter | Receiving events | WAS-20; **blocked** on a confirmed tenant discriminator |
| Central routing index (supplier → tenant, package → tenant) | Routing events without a tenant host | **Blocked**: no schema approval until a discriminator is confirmed; it cannot route unseen events by itself ([Tenant routing](#tenant-routing)) |
| Durable inbox with a composite dedupe key, replay and dead letters | Acknowledge fast, retry safely | WAS-19 (design can proceed; placement depends on routing) |
| Secret validation in constant time, payload and content limits, log redaction | Authenticity | WAS-20 |
| Webhook metrics (last event, lag, duplicates, retries, dead letters) and a connection health signal | Deciding any polling reduction | WAS-21 |
| Integrator management (create, add seller, enable, rotate secret) | Trendyol GO registration | Follow-up, after stage validation |

## Stage pilot

A pilot receives Trendyol GO Meal webhooks from the provider's stage API, on the existing Wasla deployment, for exactly one explicitly configured seller, store, tenant and connection: [trendyol-go-meal-webhook-stage-pilot.md](trendyol-go-meal-webhook-stage-pilot.md). It does not resolve [Tenant routing](#tenant-routing): it needs none, refuses anything that contradicts its configuration, adds no central index and refuses the production API. It is the way to collect the stage evidence WAS-175 asks for.

## Implications for WAS-18 to WAS-21

| Issue | Implication |
|---|---|
| WAS-18 shared ingestion | **Internal, provider-agnostic work can proceed:** <br>• normalized ingestion contracts; <br>• full-order versus status-only handling (full: polling results, and later a Trendyol GO `created` or a Yemeksepeti dispatch); <br>• idempotency boundaries (`Platform` + `ExternalOrderId`); <br>• status-only events never create an order and never replace items; <br>• status changes through `MergeInternalStatusForSync`; <br>• the existing polling routed through the shared path. <br>**Not unblocked:** the external Trendyol GO webhook endpoint and any tenant-routing schema. Both wait for a confirmed tenant discriminator ([Tenant routing](#tenant-routing)). |
| WAS-19 inbox | **Design can proceed.** No provider documents an event id, so the dedupe key is a composite fingerprint: platform, package or order id, event type and provider `timestamp` where present. Persist first, then acknowledge inside the configured Trendyol GO timeout (at most 5000 ms [T5]). Process in the background. Replaying an applied event is a no-op. Where the inbox lives (central or per tenant) depends on routing and is blocked with it. |
| WAS-20 security | **Provider-specific, and the Trendyol GO endpoint is blocked on routing.** Trendyol GO: a high-entropy secret in a custom header (not a query parameter, which ends up in URLs and logs), compared in constant time, and rotated through Update Integrator [T9]. HTTPS only. The issue's criteria for signatures, a timestamp/nonce replay window and an IP allowlist cannot be met from the official contract [T1, T5]. Replay protection must come from inbox dedupe and the status merge, and the criteria should say so. The shared secret authenticates Trendyol GO, not a seller, so it does not route. Yemeksepeti: blocked; if [Y7] is confirmed, JWT HS512 validation, the `service` claim, IP allowlist and a valid certificate. |
| WAS-21 reconciliation | **Required for Trendyol GO.** Its polling side is already unblocked by WAS-69. Keep the checkpointed polling for every connection. Webhook metrics wait for the endpoint. Lower the polling frequency only on evidence, and keep a floor. Yemeksepeti reconciliation is blocked; note that the archived report service covers only accepted and cancelled orders within 24 hours [Y8]. |

## Recommended implementation sequence

This sequence invents no missing contract. Each step names what it waits for.

1. **Provider questions (owner action).** Send the [questions below](#known-unknowns-and-provider-questions) to Trendyol GO and Yemeksepeti. This record does not contact either provider.
2. **WAS-18 internal work, with polling as the only caller.** Build the normalized ingestion contracts, the shared normalize → validate → idempotency → upsert/apply → lifecycle path and the status-only apply path, and route the existing polling through them. No endpoint and no central schema.
3. **Stage validation (needs owner authorization and stage IP authorization [T12]).**
   - **Setup:** create one stage integrator pointed at a request-recording HTTPS receiver the owner controls, not a product endpoint. Add the stage seller (more than one if stage allows), enable it, and send test orders [T10].
   - **Record:** the documentation gaps — whether `supplierId` and `storeId` arrive in every `created` and match the registered seller, `Content-Type`, the headers actually sent, the response codes Trendyol GO accepts, retry timing, duplicate frequency, event order, and the meaning of `timestamp`.
   - **Write-up:** update this record with the results, labelled as stage observations rather than contract.
4. **Tenant discriminator decision.** Only after Trendyol GO confirms a discriminator ([Tenant routing](#tenant-routing)): design the routing and ask for approval of any central schema change.
5. **WAS-19 and WAS-20 for Trendyol GO.** A Trendyol GO adapter and endpoint behind a per-environment switch that is off by default, with no real provider call in Mock mode. Use the inbox and header-secret validation, and a distinct `destinationUrl` per event type, because payloads do not name their type [T2, T5].
6. **WAS-21.** Webhook metrics, resilience tests (duplicates, replay, out-of-order events, concurrent webhook and poll, delivery/cancellation conflicts) and accurate connection health. Polling cadence stays unchanged until production data exists.
7. **Trendyol GO production**, once Trendyol GO confirms the production creation path and multi-seller use. Enable per connection, and keep polling.
8. **Yemeksepeti, only after WAS-70.** If the restaurant middleware model is confirmed, it is a new integration (a plugin endpoint plus middleware calls) that replaces the Local Shops client. That needs its own decision. If WAS-70 confirms another model, revisit this record.

## Known unknowns and provider questions

### Trendyol GO

Questions 1 to 3 decide tenant routing and block the endpoint ([Tenant routing](#tenant-routing)).

1. Are `supplierId` and `storeId` guaranteed in every `created` event, and is `supplierId` always the seller id registered with Add Seller [T2, T8]? Can the status events carry the seller id too?
2. Can `destinationUrl` or the secrets differ per seller, or may a SaaS create one integrator per seller, although the documentation advises one integrator [T1, T5]?
3. Is there a package lookup by package id that identifies the seller, using the integrator token instead of a supplier's credentials [T15]?
4. Is Webhook Meal Integration generally available to every Meal seller, including stores moved to Uber Eats, and why is it missing from the Turkish documentation [T17]?
5. How is the production integrator created? Create Integrator lists only a stage URL [T5]. Is there any certification or approval?
6. May one integrator serve independent restaurants of a multi-tenant SaaS?
7. Is there a payload signature or a published sender IP range?
8. What exactly are the retry count and spacing [T1, T3], which HTTP status codes count as an acknowledgement, and what happens after the last attempt?
9. Is any ordering guaranteed? Will an event id or sequence number be added?
10. What does `timestamp` mean in status events: event time or send time?
11. What `Content-Type` and headers are sent?
12. After how much downtime is an integrator disabled, is the integrator notified, and how are events from the disabled period retrieved [T6]?
13. Will events be added for `Picking`, `Invoiced` (including Uber's automatic `Invoiced` [T16]), item changes or partial cancellation?
14. Does the 50-requests-per-10-seconds limit [T11] apply per supplier, integrator or IP?

### Yemeksepeti (Delivery Hero Türkiye)

The WAS-70 questions stand. Add these, from the archived model:

1. Is the Integration Middleware POS-plugin model in [Y5] still the official restaurant integration in Türkiye? Where is its current documentation, given that integration.yemeksepeti.com returns HTTP 522 [Y4] and Delivery Hero's platform list omits Yemeksepeti [Y6]?
2. Are third-party multi-tenant products such as Wasla eligible? What do the NDA, approval and PGP credential steps require today?
3. Are the JWT HS512 details [Y7] and the three middleware IP addresses [Y5] current for Yemeksepeti?
4. Which status updates does Yemeksepeti actually send? Is there a delivered status [Y7]?
5. What is the auto-cancel time for an order that is not accepted [Y7]?
6. Is the order report service [Y8] the reconciliation endpoint for Yemeksepeti? What are its rate limits, and can it list orders that are not yet accepted?
7. Is the Partner API orders GET that Wasla calls today valid for restaurants at all?

## Changes since the 2026-10-06 research

- **Trendyol GO event list.** The 2026-10-06 comment also named `storeChanged` and `sellerChanged`. The Event Types page lists only the seven types above on 2026-10-19 [T2]. Whether they were removed cannot be verified, so they are not part of this record.
- **Yemeksepeti restaurant portal.** The 2026-10-06 comment found no Internet Archive snapshot of integration.yemeksepeti.com. On 2026-10-19 the archive returned a capture from 2025-07-06 [Y5]. It is the basis of the archived model above.
- **Trendyol GO polling.** The poller gaps the comment reported (terminal statuses missing, fixed one-hour lookback) were fixed by WAS-69. The current behavior is in [../orders/synchronization.md](../orders/synchronization.md#checkpoint-and-outage-recovery).
- **TLS claim.** The comment's TLS 1.2 statement came from the Trendyol GO overview page, which was not re-checked here. This record makes no TLS version claim.

## Source register

All sources accessed 2026-10-19.

- **Trendyol GO pages** (developers.tgoapps.com) are public, rendered in the browser (Docusaurus 2.0.1), and show no page version or last-update date unless stated.
- **"Archived"** means an Internet Archive copy of an official page.

### Trendyol GO

| Id | Title | URL | Version / date | Access | Supports |
|---|---|---|---|---|---|
| T1 | Webhook Meal Integration – Introduction | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/Introduction | — | Public | Existence, prerequisites, steps, retries, disabling |
| T2 | Event Types and Event Payloads | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/Event-Types-Payloads | — | Public | Event list, payloads |
| T3 | FAQ (Webhook Meal Integration) | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/FAQ | — | Public | Retries, test, disable |
| T4 | Error Response (Webhook Meal Integration) | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/Error-Response | — | Public | Errors of the management API (not of Wasla's receiver) |
| T5 | Create Integrator | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/endpoints/Create-Integrator | — | Public | POST, URL rules, timeout, secrets, duplicates, stage-only URL |
| T6 | Enable Integration | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/endpoints/Enable-Integration | — | Public | One-day recovery, needs a seller |
| T7 | Disable Integration | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/endpoints/Disable-Integration | — | Public | Integrator-initiated disable |
| T8 | Add Seller; Remove Seller | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/endpoints/Add-Seller and https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/endpoints/Remove-Seller | — | Public | Seller Basic auth required; last removal disables |
| T9 | Update Integrator; Get Integrator; Token Refresh | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/endpoints/Update-Integrator, https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/endpoints/Get-Integrator and https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/endpoints/Token-Refresh | — | Public | Secret rotation, `Authorization-int`, production URLs |
| T10 | Create Test Order (webhook) | https://developers.tgoapps.com/en/docs/trendyol-go-meal/webhook-order-integration/endpoints/Create-Test-Order | — | Public | Test order limits |
| T11 | 2. Authorization | https://developers.tgoapps.com/en/docs/authorization | — | Public | 50 requests / 10 s |
| T12 | 3. Canlı-Test Ortam Bilgileri | https://developers.tgoapps.com/docs/canli-test-ortam-bilgileri | — | Public | Stage IP authorization |
| T13 | 1. Service Limitations | https://developers.tgoapps.com/en/docs/service-limitations | — | Public | Limit table without order packages |
| T14 | Package Models | https://developers.tgoapps.com/en/docs/trendyol-go-meal/order-integration/package-models | — | Public | Status meanings, reason codes |
| T15 | Get Order Packages | https://developers.tgoapps.com/en/docs/trendyol-go-meal/order-integration/get-order-packages | — | Public | Package id, pickup ETA fields |
| T16 | Uber Eats Transition Integration Changes (Meal) | https://developers.tgoapps.com/en/docs/uber-eats-migration/meal-uber-eats-migration-changes | Changelog, latest entry 2026-09-14 | Public | Automatic `Invoiced`, Bilecik scope |
| T17 | 8. Uber Eats Trendyol Go – Yemek Entegrasyonu (category) | https://developers.tgoapps.com/docs/category/8-uber-eats-trendyol-go---yemek-entegrasyonu | — | Public | No webhook section in Turkish |
| T18 | Webhook Integration (Grocery) | https://developers.tgoapps.com/en/docs/trendyol-go-grocery/order-integration/webhook-integration | — | Public | Different product; contrast only |

### Yemeksepeti and Delivery Hero

| Id | Title | URL | Version / date | Access | Supports |
|---|---|---|---|---|---|
| Y1 | Introduction (Local Shops developer site) | https://developer.yemeksepeti.com/en/documentation/introduction | Sitemap lastmod 2026-10-16 | Public | Local Shops only; restaurants sent to integration.yemeksepeti.com |
| Y2 | POS Partner Picking – How to Integrate | https://developer.yemeksepeti.com/en/documentation/pos-partner-picking-how-to-integrate | Sitemap lastmod 2026-10-16 | Public | Shops webhook, OAuth, GET 60 days |
| Y3 | POS Partner Picking – FAQ | https://developer.yemeksepeti.com/en/documentation/pos-partner-picking-faq | Sitemap lastmod 2026-10-16 | Public | Shops statuses, secret, retries, 60 requests/min |
| Y4 | Yemeksepeti restaurant integration portal (live) | https://integration.yemeksepeti.com/en/ | — | **Unavailable** (HTTP 522, 2026-10-19) | Restaurant docs not readable |
| Y5 | Yemeksepeti Entegrasyon Dokümanı (archived) | https://web.archive.org/web/20250706200524/https://integration.yemeksepeti.com/ | Captured 2025-07-06 | Public, archived | Restaurant middleware model, onboarding, IPs, report service |
| Y6 | Restaurant Integration API (POS) – Supported Platforms | https://developers.deliveryhero.com/documentation/pos.html | Last-Modified 2026-02-25 | Public | Yemeksepeti not listed |
| Y7 | Delivery Hero POS Plugin API (OpenAPI) | https://integration-middleware.stg.restaurant-partners.com/apidocs/pluginApi.yaml (rendered at https://integration-middleware.stg.restaurant-partners.com/apidocs/pos-plugin-api) | info.version 1.0.0; Last-Modified 2026-08-05 | Public | Dispatch, statuses, JWT, retries, acknowledgement |
| Y8 | Delivery Hero POS Middleware API (OpenAPI) | https://integration-middleware.stg.restaurant-partners.com/apidocs/middlewareExternalApi.yaml (rendered at https://integration-middleware.stg.restaurant-partners.com/apidocs/pos-middleware-api) | info.version 1.0.0; Last-Modified 2026-08-05 | Public | Login, status callbacks, order report service |

## Related docs

- [food-platforms.md](food-platforms.md)
- [../orders/synchronization.md](../orders/synchronization.md)
- [../orders/lifecycle.md](../orders/lifecycle.md)
