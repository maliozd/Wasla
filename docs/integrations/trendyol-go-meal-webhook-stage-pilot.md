# Trendyol GO Meal webhook stage pilot

## Purpose and scope

This document describes the pilot that receives Trendyol GO **Meal** webhooks from the Trendyol GO **stage** API for exactly one explicitly configured seller, store, tenant and platform connection. It owns the pilot's configuration, receiver, durable inbox, processing rules, operator commands and test procedure.

"Stage" names the provider side only. On Wasla's side the pilot runs on the **existing Wasla deployment**, which is the test environment; there is no separate Wasla stage environment, host, database, account, service or certificate. What enabling it there requires is listed in [Existing deployment](#existing-deployment).

It is **not** tenant routing. Routing a previously unseen event to one of many tenants stays blocked on WAS-175 ([provider-webhooks.md](provider-webhooks.md#tenant-routing)). The pilot needs no routing: every accepted event belongs to the one configured connection, and anything that contradicts it is refused. There is no CentralDb index.

The deployment keeps the production Trendyol GO API for every connection. Only the pilot's one connection is routed to the stage API ([Provider API routing](#provider-api-routing)).

The Trendyol Marketplace webhook (developers.trendyol.com) and the Trendyol GO Grocery webhook are different contracts and are not used.

## Provider contract

Source: developers.tgoapps.com, English "Webhook Meal Integration". There is no Turkish Meal webhook section. Pages checked on 2026-10-26: Introduction, Event Types and Event Payloads, Create Integrator, Get Integrator, Enable Integration, Create Test Order.

| Topic | Status | What Wasla relies on |
|---|---|---|
| Event types | Confirmed | `created`, `pickupEtaCalculated`, `courierNearby`, `shipped`, `delivered`, `cancelled`, `unsupplied`. `storeChanged` and `sellerChanged`, named in older notes, are **not** listed today. An event Wasla does not know is stored as Ignored and never processed |
| Event type in the payload | Not present | The type is the path segment of the integrator's `destinationUrl`. The pilot registers `/{type}` for each type |
| Delivery | Confirmed | POST to `baseUrl + destinationUrl`. `baseUrl` must be HTTPS and shorter than 100 characters; timeout 10–5000 ms (the pilot registers 5000) |
| Authentication | Confirmed | Static `secrets.httpHeaders` and/or `httpQueryParams` set by the integrator. The pilot uses one header, never a query parameter. There is no signature, timestamp or nonce check |
| Duplicates | Confirmed | "Short-term duplicate events" are possible, so the receiver must be idempotent |
| Retries | Confirmed, wording ambiguous | Up to 3 retries, one per minute. After a long outage Trendyol may disable the integration; re-enabling recovers events up to one day old |
| Acknowledgement | Not documented | The pilot answers 200 after storing. Whether other 2xx codes or a slow 2xx count as failure is to be observed on stage |
| Ordering | Not documented | Not relied on: status-only changes go through the status merge, and anything else is read back from the provider |
| `created` payload | Confirmed | The full package, but it omits `packageModificationDate`, `lastModifiedDate` and `isCourierNearby`, carries `timestamp` instead of `packageCreationDate`, and adds `lines[].quantity`. The documentation says the two models "will never be completely identical" |
| Status events | Confirmed | `shipped`, `delivered` and `courierNearby` carry `id`, `orderNumber`, `orderCode` and `timestamp`. `cancelled` and `unsupplied` add `status` (`Cancelled` / `UnSupplied`), the cancelled items and `cancelInfo`, whose numeric `reasonCode` is stored with the cancellation (WAS-200, see [lifecycle.md](../orders/lifecycle.md#how-an-order-ended-was-200)). `pickupEtaCalculated` carries the pickup window and no `timestamp`. None carries `supplierId` or `storeId` |
| Seller identity | Partly confirmed | Only `created` carries `supplierId` and `storeId` (sample only, no field definition). One integrator, URL and secret serve every seller |
| Registration | Confirmed | Create Integrator (seller's Basic auth and `sellerId`) returns a token and creates the integrator **disabled**. Get, Enable, Disable and Create Test Order use `Authorization-int: Bearer <token>`. Enable fails without a seller |
| Test order | Confirmed | `POST .../webhook/{integratorName}/order/test` with `storeId` and `sellerId`; at most 50 per day on stage. The documentation names no other way that sends a webhook |

Stage observations are recorded in [Stage observations](#stage-observations) and are never treated as contract.

## Configuration

Section `TrendyolGoMealWebhookPilot`, read by Wasla.Web, Wasla.Api, Wasla.Worker and the CLI. Set it with environment variables (`TrendyolGoMealWebhookPilot__Enabled` and so on), never in committed files.

| Key | Meaning |
|---|---|
| `Enabled` | `false` by default. While false the receiver answers 404 and the Worker processes nothing |
| `TenantId` | The CentralDb tenant whose database receives every event |
| `PlatformConnectionId` | That tenant's Trendyol GO connection |
| `SellerId`, `StoreId` | The connection's seller (supplier) and store, as numbers |
| `HeaderName` | The header the integrator sends its secret in (default `X-Wasla-Webhook-Key`) |
| `SecretSha256` | SHA-256 of the header secret as 64 hex digits. The secret itself is never configured or stored by Wasla |
| `MaxBodyBytes` | Largest body accepted (default 262144) |
| `ReceiverBaseUrl` | The public receiver URL the integrator is registered with: exactly `https://<host>/api/webhooks/trendyol-go/meal` |
| `ProviderApiBaseUrl` | The Trendyol GO API the pilot's connection alone uses: exactly `https://stageapi.tgoapis.com`. See [Provider API routing](#provider-api-routing) |

When `Enabled` is true, Web, Api and Worker refuse to start unless every value is present and valid:

- `ReceiverBaseUrl` is `https://`, a DNS host name (not an IP address or a single label), and exactly the receiver path, with at most one trailing slash. User info, a port, a query or a fragment never match. It is shorter than 100 characters, as Trendyol GO requires.
- `ProviderApiBaseUrl` is exactly `https://stageapi.tgoapis.com` (one trailing slash allowed). Any other scheme, host, port, path, query, fragment or user info is refused, `https://api.tgoapis.com` included.

`ProviderApiBaseUrl` is checked whenever it is set, even with the pilot off: it then needs `PlatformConnectionId`, `SellerId` and `StoreId`, and it must be exactly the stage API. `Platform:TrendyolGo:BaseUrl` is not checked by the pilot: it stays the deployment's own API, which on the existing deployment `wasla-preflight` requires to be production.

### Provider API routing

Every Trendyol GO request is built in one place (`TrendyolGoFoodPlatformClient`), so one rule covers polling, the pilot's targeted reconciliation and order actions (accept, invoiced, shipped, delivered, unsupplied), in every host:

- A connection whose id is not `PlatformConnectionId` uses `Platform:TrendyolGo:BaseUrl`, whatever its seller or store. A real restaurant's connection, and a look-alike with the pilot's seller and store, stay on production.
- The connection whose id is `PlatformConnectionId` uses exactly the stage API, but only while it is still the active Trendyol GO connection of the configured `SellerId` and `StoreId`. Otherwise its request fails before credentials are read and no API is called, so the stage seller's credentials never reach production and nothing reaches stage by mistake.
- The routing follows `ProviderApiBaseUrl`, not `Enabled`. Turning the receiver off leaves the connection on the stage API. To stop calling the stage API, deactivate the connection in Wasla.
- With `ProviderApiBaseUrl` unset, no connection is routed. The pilot's connection is then an ordinary connection on the deployment's production API, and polling would send the stage seller's credentials there. Deactivate the pilot's connection before `ProviderApiBaseUrl` or `PlatformConnectionId` is removed or changed, never after.

A tenant has at most one connection per platform, so the pilot's connection is never in the same tenant as a restaurant's Trendyol GO connection.

No committed settings file enables the pilot; a test checks every `appsettings*.json`.

## Receiver

`POST /api/webhooks/trendyol-go/meal/{eventType}` in Wasla.Api (`TrendyolGoMealWebhookController`). It is anonymous for cookie authentication, and tenant resolution skips it: the host plays no part.

| Condition | Answer |
|---|---|
| Pilot off | 404 |
| Header secret missing or wrong (SHA-256, constant-time compare) | 401, nothing read or stored; logged without the header |
| `Content-Type` not `application/json` | 415 |
| Body above `MaxBodyBytes` | 413 |
| Not a JSON object with a string `id` (at most 128 characters) | 400 |
| Otherwise | The event is committed to the tenant's `ProviderWebhookEvents` table, then 200 |

The secret authenticates the integrator, not a seller.

- **Stored as Rejected, never processed:** a `created` event whose `supplierId` or `storeId` names another seller or store.
- **Stored as Ignored, never processed:** `courierNearby`, `pickupEtaCalculated` and undocumented types.

Bodies and headers are never logged; logs carry the event type, package id, status and outcome.

**Deduplication.** The unique key is SHA-256 of the platform, the event type, the package id and the provider's `timestamp` as sent. An event without an integer `timestamp` uses the body's SHA-256 instead. A repeated delivery returns 200 and raises the stored event's `DeliveryCount`.

**Event time.** The `timestamp` becomes the event's time only when it is a positive 64-bit integer that a date can hold, not before 2000, and at most 5 minutes after Wasla's clock (the provider clock tolerance of the shared ingestion path). Anything else, such as a negative, zero, fractional, string, overflowing or far-future value, never refuses the event and never causes a server error. The event is stored without an event time, a warning names its type and package, and processing uses its receipt time instead.

## Processing

`TrendyolGoMealWebhookWorker` in Wasla.Worker runs `TrendyolGoMealWebhookProcessor` every 2 seconds while the pilot is on. It runs separately from the order sync cycle, and checkpointed polling is unchanged.

1. **Claim.** One conditional update moves an event to Processing, so only one processor (or Worker process) takes it. The claim lasts 2 minutes; a stopped processor's event is taken up again after that.
2. **Connection.** It must be the configured, active Trendyol GO connection of the configured seller and store. Otherwise the event becomes Rejected (`ConnectionMismatch`) and nothing changes.
3. **Order Sync gate.** If the tenant's Order Sync setting is off, the event is Processed (`OrderSyncDisabled`) and nothing is fetched.
4. **Event:**
   - `created`, `shipped`, `delivered`:
     - A **targeted reconciliation** (`OrderSyncService.ReconcileConnectionAsync`) fetches the connection's window from the event's timestamp minus 5 minutes to now, at most one hour, with the connection's own credentials.
     - It ingests every package through the shared path ([../orders/synchronization.md](../orders/synchronization.md#shared-ingestion-was-18)).
     - It never moves the checkpoint, the last attempt or the failure counters.
     - The `created` payload is not mapped directly; its differences from the polling package are not verified on stage. No event name is mapped to a status: a `shipped` order takes its status from the package that was read back.
   - `cancelled`, `unsupplied` whose own `status` is the documented `Cancelled` / `UnSupplied`, for an order Wasla has stored:
     - That package status is applied as a **status-only observation**. It never creates an order or changes items, customer, money or time fields.
     - Otherwise the event is reconciled like the others.
5. **Result:**
   - Processed with an outcome: `Reconciled`, `StatusApplied` or `StatusUnchanged`, plus the matched order id.
   - A reconciliation that does not return the package, or any failure, is retried. An event gets 6 attempts: the first, then one after each delay of 10 s, 30 s, 1 min, 2 min and 5 min (about 8 min 40 s in all). When the sixth attempt fails the event is Failed. A Failed event is never taken up again until an operator replays it, which starts the whole schedule again. Polling still imports the order meanwhile.

Every effect stays single. One package is one order, even when the reconciliation and a polling pass insert it at once. Auto-approve and the provider-accepted receipt run once, and every change raises one live notification.

## Operator commands

Run with the deployment's own CLI and the pilot configuration (see [../operations/cli.md](../operations/cli.md#webhook-pilot)).

```text
meal-webhook-integrator --action create --name <name> --token-file <path>
meal-webhook-integrator --action show|enable|disable|test-order --name <name> --token-file <path>
meal-webhook-inbox [--limit 20] [--replay <event-id>]
```

Every integrator action requires `ProviderApiBaseUrl` to be exactly the stage API and `ReceiverBaseUrl` to be exact, as above. The integrator client talks only to the stage API.

- **`create`.**
  - Takes the seller's credentials from the pilot's encrypted platform connection.
  - Registers `ReceiverBaseUrl` (there is no command-line URL), a 5000 ms timeout and all seven documented events at `/{type}`, with a newly generated 256-bit header secret.
  - Writes the integrator token to `--token-file`: a new file, owner read/write only, never overwritten.
  - Prints only the SHA-256 to configure as `SecretSha256`.
  - Neither the secret nor the token is shown.
- **`show`, `enable` and `test-order`.** Each reads Get Integrator and fails closed unless the registration is exactly the expected one:
  - the base URL is `ReceiverBaseUrl`, with no user info, port, query or fragment;
  - the timeout is 5000 ms;
  - the integrator serves exactly the configured seller;
  - exactly the seven documented events are registered, each to its own `/{type}`, and nothing else.

  `show` prints every difference and exits 3. `enable` enables nothing while there is a difference; after enabling it reads the integrator back and disables it again unless it is then enabled with exactly that registration. `test-order` requests nothing unless the integrator is enabled with exactly that registration. The integrator's `secrets` are never read.
- **`meal-webhook-inbox`.**
  - Lists each event's delivery count, status, attempts, outcome and last error.
  - For matched events it adds the matched order's id, code and status.
  - It never shows payloads.

## Existing deployment

The pilot runs on the existing Wasla deployment, not beside it. Read-only facts of that deployment that the pilot depends on (checked 2026-10-27; see [../operations/linux-vds-runbook.md](../operations/linux-vds-runbook.md)):

- **Api exposure.** Wasla.Api listens on loopback only. Nginx publishes Wasla.Web on every Wasla host and has no location for the Api, so `/api/webhooks/trendyol-go/meal/...` reaches Wasla.Web today and answers 404. Receiving webhooks needs one Nginx location that forwards exactly that path prefix to the Api, with a body limit just above `MaxBodyBytes`.
- **Provider API.** The deployment's `Platform__TrendyolGo__BaseUrl` is the production Trendyol GO API, and `wasla-preflight` refuses any other value. That stays as it is: only the pilot's connection uses the stage API, through `TrendyolGoMealWebhookPilot__ProviderApiBaseUrl` ([Provider API routing](#provider-api-routing)).
- **Tenant.** The pilot needs its own tenant and one Trendyol GO connection for the stage seller. It cannot share a restaurant's tenant (one connection per platform), and a restaurant's tenant would show stage test orders on its Live Screen.
- **Schema.** The pilot adds the tenant migration `AddProviderWebhookEvents`. The deployment's processes refuse to start until every tenant database has it (`migration-status --require-current`).

## Test procedure

Prerequisites:
- The deployment runs a release with the pilot, its tenant migrations are applied, and the pilot is enabled for the configured tenant and connection.
- The receiver URL answers over HTTPS through Nginx.
- The integrator is created, its `SecretSha256` is configured, `show` reports exactly the expected registration, and it is enabled.

1. **Reachability.** A POST to `ReceiverBaseUrl` + `/created` without the header answers 401 from the Api (not 404 from Wasla.Web).
2. **Test order.** Request one with `meal-webhook-integrator --action test-order`. This is the documented endpoint that sends Meal webhooks. A test order placed some other way, for example from the stage partner panel, is not documented to send a webhook. Use one only to observe what it does.
3. **Delivery.** Within seconds `meal-webhook-inbox` lists a `created` event for the test package.
4. **Processing.** It reaches Processed / `Reconciled` with an order id, code and status.
5. **Independent evidence.** The inbox row is the proof of webhook delivery: its `Received` time, the integrator's own path and its `Deliveries` count. An order that only polling imported has **no** inbox row and does not count as a webhook success.
6. **Optional.** Cancel the stage order to observe `cancelled` or `unsupplied`: Processed with `StatusApplied`, and the order Cancelled.

Record what was observed in [Stage observations](#stage-observations): headers, content type, response codes accepted, retry timing, duplicates, event order, and whether `supplierId`/`storeId` matched.

## Rollback

- Set `TrendyolGoMealWebhookPilot__Enabled=false` and restart the deployment's Api and Worker: the receiver answers 404 and nothing is processed. The pilot's connection stays on the stage API; deactivate it in Wasla to stop polling it.
- Remove the rest of the `TrendyolGoMealWebhookPilot` settings only after the pilot's connection is deactivated (see [Provider API routing](#provider-api-routing)).
- Disable the integrator with `--action disable`.
- Polling is unaffected throughout, so no order is lost.
- The `ProviderWebhookEvents` table stays and can be emptied by an operator.

## Stage observations

These were observed on stage and are not contract. They may change without notice.

**2026-11-03, two test orders.**
- **Setup:** the existing deployment on release `94acc764`. One test tenant and its stage seller's connection, with an integrator created and enabled through the CLI.
- **Test orders:** two, each requested with `meal-webhook-integrator --action test-order`, at 15:29Z and 15:34Z.
- **Request proof:** the integrator's TLS ClientHello named only `stageapi.tgoapis.com`. Both API hosts resolve to the same Cloudflare addresses, so an address alone does not show which one is used.

| Topic | Observed |
|---|---|
| Events sent | Only `created`, one per test order. No other event followed within 10 minutes; both orders stayed `Created` (Wasla New) |
| Duplicates | Every `created` was delivered **twice**, the copies within 0.1 s of each other. For the first order the second copy came while the first was still being answered, so it was not a retry after a slow or failed answer. Both copies had the same package id and `timestamp`, so they deduplicated to one event with `Deliveries=2` |
| Authentication and content type | The configured header secret was accepted (no 401), and the body was accepted as `application/json` (no 415). The request's user agent was `fasthttp` |
| Acknowledgement | 200 with an empty body. No delivery was repeated after a 200 |
| Answer time | The first order's two deliveries took 2.8 s and 3.3 s to answer. These were the first stored events after the Api started, so a cold start is the likely cause. The second order's took 106 ms and 59 ms, inside the registered 5000 ms timeout |
| Package id and `timestamp` | The package `id` is a 64-character hex string, not a number. `orderCode` is numeric. `timestamp` was an integer about 1 s before receipt, and it was used as the event time |
| Seller and store | The `created` payload's `supplierId` and `storeId` matched the configured seller and store, so no event was Rejected |
| Processing | Each event reached Processed / `Reconciled` on the first attempt, 1 to 4 s after receipt. The targeted reconciliation found the test package on the stage API and created the order; the polling passes before and after it imported nothing |
| Orders | Exactly one order per package, with no duplicate idempotency key. Both orders appeared on the tenant's Live Screen |

**2026-11-03, cancelling the second test order.** The order was cancelled from the Trendyol GO side at about 15:52Z, 19 minutes after it was created. The first test order was not cancelled and received no further event.

| Topic | Observed |
|---|---|
| Event sent | `unsupplied`, not `cancelled`. Its path was `/unsupplied` and its own `status` the documented `UnSupplied`. No `cancelled` event arrived |
| Duplicates | Delivered **twice**, within 0.1 s, and deduplicated to one event with `Deliveries=2`, as with `created` |
| Answer time | 783 ms and 864 ms, both 200 |
| Processing | Processed / `StatusApplied` on the first attempt, 3 s after receipt. The order became Cancelled in Wasla, with platform status `UnSupplied` |
| Webhook or polling | The webhook made the change. `CancelledAt` was set at 15:52:47.6Z, while the event was being processed. The next polling pass that returned the package started at 15:52:49.7Z |
| Polling afterwards | That pass counted one updated order, so polling changed the order once more after the webhook had cancelled it. Its change version is 3: stored, the webhook's status change, and that pass. The fields the pass wrote are not recorded |

**2026-11-03, accepting the first test order.** The order was accepted outside Wasla at about 16:38Z; Wasla's Web and Worker logged no accept action.

| Topic | Observed |
|---|---|
| Event sent | None. No request reached the receiver, and the inbox gained no event. The documented event types have none for acceptance |
| Polling | The next pass returned the package and counted one updated order. The order became Accepted in Wasla, with platform status `Picking`. `AcceptedAt` (16:38:49.9Z) falls within that pass (16:38:49.8–50.0Z) |
| Conclusion | Polling, not the webhook, carries acceptance. Its delay is the polling interval |

**2026-11-03, provider-side status actions.** Two more test orders, requested with `meal-webhook-integrator --action test-order` at 17:53Z and 17:56Z, and the first test order. The seller-side status changes the documentation lists are:
- `picked` (Picking) and `invoiced` (Invoiced);
- `manual-shipped` and `manual-delivered`, documented as valid only for restaurants that deliver with their own couriers;
- `unsupplied`, with a restaurant reason id (621–627) and the package's item ids.

`Cancelled` is documented as a cancellation that does not come from the restaurant, so no seller call causes it. The stage API accepts calls only from authorized IP addresses. The actions were therefore made through Wasla's own order actions on the deployment, which call those endpoints with the connection's credentials. Wasla then sets its own status after the provider's success, so in these tests the webhook or polling could only confirm a status, never set it first.

| Topic | Observed |
|---|---|
| Test order creation | Each `created` event arrived 69 s after the request, not within seconds as before. Each was delivered twice in the same second and deduplicated (`Deliveries=2`). The fourth order's answers took 7 ms and 16 ms. The polling passes before the events imported nothing |
| Processing retry | The third order's event was Processed / `Reconciled` on its first attempt, about 1.5 s after receipt. For the fourth, the targeted reconciliation 0.2 s after receipt did not yet return the package (`PackageNotVisibleException`). The retry 10 s later found it and created the order, as the processing schedule documents. The next polling pass returned it and inserted nothing |
| Acceptance (`picked`) | Accepting the third order (17:55:13Z) succeeded on the stage API. No webhook arrived. Wasla's action set Accepted. The next polling pass (37 s later) returned platform status `Picking` and counted one updated order |
| Prepared (`invoiced`) | Marking the first and third orders ready (17:52:33Z, 17:59:22Z) succeeded on the stage API. No webhook arrived. Wasla's action set Ready, and the next polling pass returned platform status `Invoiced` |
| Restaurant cancellation (`unsupplied`) | Rejecting the fourth order from Wasla failed twice with HTTP 400 from the stage API. Wasla sends `reasonId` 0 and an empty `itemIdList`, while the documentation requires a listed reason id and the item ids. The order stayed New in Wasla and `Created` on stage, and no webhook arrived. The `unsupplied` webhook itself was observed earlier, after a cancellation made on the Trendyol GO side |
| Courier events | None. Both orders stayed `Invoiced`, for 38 and 31 minutes, with no `pickupEtaCalculated`, `courierNearby`, `shipped` or `delivered` event. So stage did not simulate a courier for them, and the pilot's Ignored outcome for courier events was not observed on a real delivery |
| `manual-shipped`, `manual-delivered` | Not tested. Wasla refuses Hand to courier and Delivered for platform-courier orders before any provider call. A direct call would need the seller's credentials outside Wasla. The documentation limits both endpoints to restaurants with their own couriers |
| `cancelled` | Not observed. The fourth order, left unaccepted on stage, was not cancelled within 33 minutes |

Not yet observed: Trendyol GO's retry timing after a failure or non-2xx answer, event ordering, `cancelled`, `shipped`, `delivered`, `courierNearby` and `pickupEtaCalculated`.

## Related docs

- [provider-webhooks.md](provider-webhooks.md)
- [../orders/synchronization.md](../orders/synchronization.md)
- [../operations/cli.md](../operations/cli.md)
