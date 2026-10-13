# Live tenant notifications (SignalR)

## Purpose and scope

This document describes the live notification channel of Wasla Orders: the SignalR hub in Wasla.Web, how changes in
Wasla.Web, Wasla.Worker and Wasla.Api reach it, what a browser receives, and what happens when the channel is down.

Implemented:

- **Receipt print status (WAS-23).** The shared print action (`_OrderPrintAction`) in an open Live Screen detail modal,
  the Focus detail panel and the full order details page shows Pending, Printing, and the outcome of the newest
  receipt without a reload.
- **Live Screen invalidation (WAS-24).** When an order is stored or changed (provider ingestion, an operator's lifecycle
  action, automatic approval), open Live Screens read their snapshot (`/orders/live-data`) at once instead of waiting
  for the next poll. Polling stays, at a lower rate while connected ([../orders/live-screen.md](../orders/live-screen.md)).

Not covered: practice orders of order training, the Orders management page, the Print Bridge devices page (it
refreshes on its own, [../integrations/print-bridge.md](../integrations/print-bridge.md#devices-page-presence-web-panel)),
and the print history page.

## Principles

- **The database is the source of truth.** A notification only says "this changed, at this version". The screen then
  reads the current state through its normal, authorized HTTP request. A notification is never shown as the state.
- **Published only after the commit.** Creation is announced after `CommitAsync`; a status transition after its
  conditional update changed a row; an order write after its `SaveChanges` or transaction commit. Work that did not
  happen (a duplicate print, a skipped Bridge report, a refused or failed order action, an unchanged provider order, a
  rollback) announces nothing.
- **Best effort.** A notification can be lost. One that fails is logged with its exception type only and never changes
  the result of the work it reports. What a screen missed while it was connecting or disconnected is read on reconnect
  and by the fallback. A notification lost while the screen stays connected (a failed send in Web, or the internal
  channel's 30-second pause) is **not** repaired for the print action until the next notification for that order, a
  reopen or a reload; the Live Screen repairs it at its next poll (30 seconds while connected). See
  [Limitations](#limitations).
- **The server chooses the tenant.** The group of a connection comes from the tenant resolved from the request's host
  and the revalidated tenant cookie. A client cannot name a tenant or a group.
- **Print Bridge is unchanged.** It still polls over HTTP and claims jobs with the conditional `mark-printing` update; the
  channel only reports what those requests already did.

## Processes

```text
Browser ──same origin (WebSocket, SSE or long polling)──► Wasla.Web  /hubs/tenant-events   (TenantEventsHub)
                                                            ▲
                     in process (IHubContext) ──────────────┤  manual print, reprint, operator order actions,
                                                            │  Print Bridge reports to Web
Wasla.Worker ──HTTP POST 127.0.0.1:{Realtime:Internal:Port}─┤  ingested orders, automatic approval and receipts
Wasla.Api    ──HTTP POST 127.0.0.1:{Realtime:Internal:Port}─┘  Print Bridge reports to Api (Api writes no orders)
```

`ITenantChangeNotifier` (`src/Wasla.Application/Abstractions/Realtime/ITenantChangeNotifier.cs`) is the
transport-free contract. Each process registers one implementation:

| Process | Implementation | Registration |
|---------|----------------|--------------|
| Wasla.Web | `SignalRTenantChangeNotifier`: sends to the tenant's group on the hub, waits at most 2 seconds | `AddWaslaRealtime` (`src/Wasla.Web/Realtime/RealtimeRegistration.cs`) |
| Wasla.Worker, Wasla.Api | `WebTenantChangeNotifier`: POST to Wasla.Web's loopback listener | `AddWaslaWebTenantChangeNotifier` (`src/Wasla.Infrastructure/Realtime/InternalTenantChanges.cs`) when `Realtime:Internal` is set |
| Everything else (CLI, tests), or Worker/Api without `Realtime:Internal` | `NullTenantChangeNotifier`: drops notifications | default in `AddWaslaInfrastructure` |

No Application or Infrastructure service uses SignalR types. The services call `TenantChangePublisher.PublishAsync`
(`src/Wasla.Infrastructure/Realtime/TenantChangePublisher.cs`), which uses `CancellationToken.None` (an aborted request
must not drop the notice of committed work) and catches every exception.

## Where receipt changes are published

| Change | Service | Called from |
|--------|---------|-------------|
| New receipt job (Pending) | `ReceiptPrintJobService.TryCreateReceiptJobAsync`, after its serializable transaction commits | Web manual print and Approve (in process); Worker auto-approve during sync (internal channel) |
| Reprint job (Pending) | `PrintJobHistoryService.CreateReprintAsync`, after its serializable transaction commits | Web manual print of a printed or failed receipt, the print history reprint, and the Bridge's reprint request (Web or Api) |
| Pending → Printing, Printing → Printed / Failed | `PrintBridgeJobService.TryMarkPrinting/Printed/FailedAsync`, when the conditional update changed a row | `PrintBridgeApiController` on Web (in process) or `PrintBridgeController` on Api (internal channel) |

`Cancelled` is not written by any code today, so it is never announced. The CLI's development `seed-print-job` sets a
version but announces nothing.

Print Bridge talks to the server named by its `ServerUrl`. The setup page always hands out the tenant's Web URL
(`PrintBridgeController.ResolveCustomerWebBaseUrl`), so normally its reports are recorded and announced inside Wasla.Web.
A device pointed at Wasla.Api by hand has its reports announced over the internal channel when Api has
`Realtime:Internal` configured, and not at all otherwise (its screens then rely on the fallback refresh).

## Where order changes are published (WAS-24)

| Change | Service | Called from |
|--------|---------|-------------|
| New order stored by ingestion | `OrderSyncService.UpsertOrderAsync`, after the insert's `SaveChanges` | Worker sync (internal channel) |
| Order updated by ingestion (status, items, amounts, customer fields) | `OrderSyncService.UpsertOrderAsync`, after its transaction commits; an order whose provider data did not change writes and announces nothing | Worker sync (internal channel) |
| Approve, Reject, Start preparing, Mark ready, Hand to courier, Mark delivered | `OrderActionService`, after `SaveChanges` changed the row; a wrong status, a refused user command, a missing connection or a failed provider call announces nothing | Web operator actions on Orders and the Live Screen (in process); Worker automatic approval (`OrderAutoApproveService` → `TryApproveAsync`, internal channel) |

`OrderChanged` is an invalidation, not a state: the Live Screen reads its whole snapshot, so ordering, role filters,
training isolation and the Delivered window stay on the server. Wasla.Api writes no orders. Practice orders
(`GuidedDemoSession`) are not orders and are not announced; their own countdown asks the Live Screen to refresh at each
stage ([../orders/live-screen.md](../orders/live-screen.md)). The Development-only tenant reset announces nothing.

## Message and version

A browser receives, on client method `tenantChange`:

```json
{ "type": "PrintJobChanged", "orderId": "…", "printJobId": "…", "status": "Printing", "version": 2 }
```

Nothing else: no order content, customer data, receipt payload, device, printer, token or error text.

For an order change (WAS-24), only Live Screen connections receive:

```json
{ "type": "OrderChanged", "orderId": "…", "version": 1760350000123 }
```

No status, customer, items, amounts or notes. Its `version` is the order's stored `UpdatedAt` in Unix milliseconds
(UTC): it identifies when the change was written. It is **not** a sequence: Web and Worker stamp it independently, so
the Live Screen never uses it to skip a read. Every distinct `OrderChanged` leads to (at most) one coalesced snapshot
read, and the snapshot is the state. A strictly ordered tenant-wide sequence would need a counter row written in every
order transaction (a migration and a lock on every order write); it was not built because skipping reads is not
needed for correctness.

The rest of this section is about `PrintJobChanged`.

`version` is `PrintJob.StatusVersion` (bigint, tenant migration `AddPrintJobStatusVersion`). It is **order-scoped and
only grows**:

- A new receipt job gets one more than the highest `StatusVersion` of its order, read inside the same serializable
  transaction that checks that no job of the order is Pending or Printing (`PrintJobStatusVersions.NextForNewJobAsync`).
- Every status transition adds one in the same conditional `UPDATE` that changes the status, so it is atomic with the
  transition and a skipped report (a retry, a late or duplicate report) changes neither.
- After a transition the service reads the job's status and version together and announces that pair. If another
  transition already followed, both announcements carry the newer pair; screens drop the duplicate.
- This holds because only one receipt job of an order can be active and a finished job (Printed, Failed) never changes
  again, so a newer job always starts above every older one. Reprints therefore continue the order's sequence
  (for example Pending 1, Printing 2, Failed 3, then the reprint's Pending 4).

Jobs created before the migration have `StatusVersion` 0 and keep working; their next transition takes them to 1, 2…
The read model (`IManualOrderPrintService`, `OrderReceiptPrintState.Version`) orders an order's jobs by `StatusVersion`
and then `CreatedAt`, and reports the highest version, so a rendered page and a notification use the same scale.

The Development-only CLI `seed-print-job` skips the active-job check, so it can put two active jobs on one order and
give them the same version; it is a test helper, not a production path.

## The hub

`TenantEventsHub` (`src/Wasla.Web/Realtime/TenantEventsHub.cs`), mapped at `/hubs/tenant-events` by
`MapWaslaRealtime`:

- `[Authorize(AuthenticationSchemes = WaslaTenant, Policy = CanReceiveLiveTenantUpdates)]`. The policy admits a role
  that may receive at least one stream: the roles of `CanViewLiveScreen` and of `CanManualPrint` together (today every
  assignable role). It is registered by `AddWaslaRealtime` from the shared role table.
- The negotiate and connect requests go through the normal Web pipeline: tenant resolution from the host, the tenant
  cookie with its per-request revalidation ([authentication.md](authentication.md#long-lived-live-connections)), and
  the tenant role policy.
- `OnConnectedAsync` checks each stream's role against the tenant resolved for the connection's request and joins only
  the groups that pass:

  | Group | Roles (policy) | Receives |
  |-------|----------------|----------|
  | `tenant:{tenantId:N}:print-status` | Owner, Manager, Cashier (`CanManualPrint`) | `PrintJobChanged` |
  | `tenant:{tenantId:N}:live-screen` | Owner, Manager, Kitchen, Cashier, Viewer (`CanViewLiveScreen`) | `OrderChanged` |

  A connection that passes neither is closed. Kitchen and Viewer never receive print status.
- After joining its groups the hub sends the caller `tenantEventsReady`. A client's start completes at the handshake,
  before `OnConnectedAsync` has joined the groups, so `wasla-realtime.js` reports "connected" (and screens read their
  state) only on this message; a change made between a screen's read and the join can therefore not be missed.
- The periodic session check also requires the role to still admit every stream the connection joined.
- The hub declares no method a client can call. A client invocation fails with a hub error and changes nothing.
- Requests under `/hubs` with an `Origin` header for another host are refused with 403 before tenant resolution and
  authentication (`RealtimeOriginMiddleware`), because browsers apply no CORS to WebSockets and a tenant cookie is sent
  from any same-site page. Only the host is compared (and the port when the `Host` header has one), not the scheme,
  because TLS ends at the proxy. When the `Host` header has no port (behind the proxy), any port of the same host name
  is accepted; only the proxy's port is public.
- `CloseOnAuthenticationExpiration` closes a connection when its cookie ticket expires. A session that stops being valid
  earlier is closed by the periodic check described in [authentication.md](authentication.md#long-lived-live-connections).
- Transports: WebSockets, then Server-Sent Events, then long polling. Detailed errors are off.

Connections and groups live in the Wasla.Web process. With more than one Web instance a notification would reach only
the instance it was sent on; that needs a backplane and is not built (one Web instance runs today; see
[Limitations](#limitations)).

## The internal channel (Worker and Api to Web)

Configuration section `Realtime:Internal`, the same values in Web, Worker and Api:

| Key | Meaning |
|-----|---------|
| `Port` | Wasla.Web listens on `127.0.0.1:Port`; Worker and Api post to it. Not set: the channel is off (Worker and Api drop notifications; Web opens no listener). |
| `Secret` | Shared secret, at least 32 printable ASCII characters without spaces, sent in `X-Wasla-Realtime-Secret`. A secret: set it as an environment variable, never in a committed file. |
| `TimeoutSeconds` | How long a sender waits for Web (default 3, 0.1–10). Keep it above Web's 2-second bound on the hub send, so a slow send is not taken for an unreachable Web. |

A process refuses to start when `Port` is set and the port or secret is invalid; the message names the key, never the
value. See [../operations/deployment.md](../operations/deployment.md#live-notifications).

**Receiving (Web).** `InternalTenantChangeListener` is a second Kestrel server inside the Web process, bound to the
IPv4 loopback address only, with one route (`POST /internal/tenant-changes`) and nothing else: no tenant resolution,
cookies, MVC or static files. The public listener the proxy forwards to does not serve that path. It answers 401 to a
missing or wrong secret (compared in constant time), 400 to anything that is not a complete, known message, and 202
after handing the notification to the hub. The tenant id in the message is accepted only because the caller proved the
secret; the most a message can cause is that the named tenant's open screens read their own state again.

**Sending (Worker, Api).** `WebTenantChangeNotifier` posts after the commit and waits at most `TimeoutSeconds`, with no
cookies, redirects or proxy. It never throws. After a failure (timeout, refusal, Web not running) it stops sending for
30 seconds and logs one warning, so a stopped Web cannot slow order synchronization or Print Bridge reports; the next
successful send logs that delivery resumed. Notifications in that window are dropped. A change type the channel does
not carry is dropped without pausing it.

## The browser

| File | Job |
|------|-----|
| `wwwroot/lib/microsoft-signalr/signalr.min.js` | The official browser client, `@microsoft/signalr` 8.0.29, unchanged from the npm package (integrity in its `LICENSE.txt`). Served from the app; no CDN at run time. |
| `wwwroot/js/realtime/wasla-realtime.js` | One connection per page (`window.WaslaRealtime.get`). Handlers are attached before the first start; the first start is retried (2, 5, 10, then every 30 seconds), because automatic reconnect does not cover it; automatic reconnect retries at 0, 2, 5, 10 and then every 30 seconds without giving up; stopped on `pagehide` and started again if the page returns from the back/forward cache. "Connected" means the hub's `tenantEventsReady` arrived (groups joined), not just that the transport opened. |
| `wwwroot/js/orders/orders-print-status.js` | Keeps every shown print action current (below). |
| `wwwroot/js/orders/orders-live-realtime.js` | Live Screen invalidation (WAS-24, below). |
| `Views/Orders/_LiveConnectionScripts.cshtml` | Loads the SignalR client and `wasla-realtime.js`: on the Live Screen for every user, on the order details page for `CanManualPrint` users. |
| `Views/Orders/_OrderPrintStatusScripts.cshtml` | Loads `orders-print-status.js`, only for users with `CanManualPrint`. |

Each print action carries `data-print-action-for` (the order) and `data-print-version` (the version it was rendered or
last updated at). `orders-print-status.js` reads `GET /orders/{id}/print-state` (Owner, Manager, Cashier; 404 for an
order not in this tenant; `Cache-Control: no-store`), which returns `{ orderId, version, mode, outcome }` from the same
`OrderPrintActionModel` the server-side render uses:

- **Notification:** only for an order that is on screen, and only when its version is higher than the shown one. A
  duplicate, stale or out-of-order notification fetches nothing.
- **One request per order at a time.** Notifications that arrive during a request cause exactly one more request
  afterwards. An answer older than what is shown is not applied, so a slow answer never moves the screen back.
- **While a manual print of the order is being submitted** the answer is left to that submit, which asks again when it
  finishes.
- **Connected (first time or after a reconnect):** every shown order is read at once, because notifications sent before
  or during the drop were missed. The fallback stops.
- **Disconnected or not yet connected:** every 10 seconds the shown orders are read, and only while a print action is on
  screen and the tab is visible. Nothing is polled when no detail is open. Returning to a hidden tab reads at once.
- `pagehide` stops the connection and the timers. A page restored from the back/forward cache reconnects, and that
  reconnect reads every shown order again.

What the action shows (`_OrderPrintAction.cshtml`, text from SharedResource in tr-TR, en-US, ar-SA and ru-RU):

| Newest receipt | Button | Status text (`role="status"`) |
|----------------|--------|-------------------------------|
| none | Print (`Orders.Print.Action`) | — |
| Pending | In print queue (`Orders.Print.InQueue`), disabled | — |
| Printing | Printing… (`Orders.Print.Printing`), disabled | — |
| Printed | Reprint (`PrintBridge.Reprint`) | Receipt sent to the printer (`Orders.Print.Status.Printed`) |
| Failed | Reprint | Receipt could not be printed (`Orders.Print.Status.Failed`) |

"Sent to the printer" is what Print Bridge's `mark-printed` means: the Windows spooler accepted the receipt; it is not
proof of paper output. The status text uses Bootstrap's theme-aware `text-success-emphasis` / `text-danger-emphasis`
colours, is written only when it changes (so it is announced once), and follows the page direction in Arabic.

### Live Screen invalidation (WAS-24)

`orders-live-realtime.js` subscribes to `OrderChanged` on the page's single connection and drives the existing
snapshot coordinator (`O.liveStore`); it renders nothing itself.

| Connection state | Behavior |
|------------------|----------|
| Connected | An `OrderChanged` schedules one `requestRefresh()`: notifications within 150 ms are read together and notification-triggered reads are at least 1 second apart. The coordinator still runs one request at a time with one follow-up. Polling continues every **30 seconds** as a safety net |
| Connecting, disconnected or reconnecting | Polling every **10 seconds**, as before WAS-24 |
| Each (re)connect, including after a back/forward-cache restore | One full snapshot read at once (notifications sent meanwhile were missed); a scheduled read is folded into it |

- The polling rate is switched with `setPollingIntervalMs` on the coordinator; a shorter rate reschedules the pending
  poll at once. Error backoff is unchanged.
- Older or slower responses never replace newer state: the coordinator's single-flight, generation and mutation guards
  are unchanged.
- New-order detection, sound, browser notifications and the "silent first snapshot" baseline stay in the coordinator,
  so an order is announced once however many invalidations arrive. During isolated order training the snapshot
  leaves real orders out; an invalidation only updates the real-order count faster. Training is not paused or advanced.
- `pagehide` disposes the module only when the page is really unloading; a page kept in the back/forward cache keeps
  it, and the reconnect on `pageshow` reads the snapshot.

The print-status flow does not use the snapshot coordinator, and the Live Screen flow does not use the print-state
endpoint.

## Failure behavior

| Situation | Result |
|-----------|--------|
| SignalR send fails or times out in Web | Warning with the exception type; the work's result is unchanged; print actions catch up on reconnect or reopen, the Live Screen at its next poll |
| Web not running or refusing, seen from Worker or Api | One warning, 30-second pause, the work continues; screens catch up when they reconnect to the restarted Web |
| `Realtime:Internal` not configured | Worker and Api changes are not pushed. Print actions show them within 10 seconds only while disconnected, otherwise on reopen or reload. The Live Screen shows ingested orders and automatic approvals only at its next poll: **up to 30 seconds while connected**, slower than before WAS-24. Configure the channel wherever the Worker runs |
| Browser never connects (blocked WebSockets and fallbacks, proxy without upgrade) | Print actions read every 10 seconds while shown; the Live Screen keeps polling every 10 seconds |
| Session revoked or tenant deactivated while connected | Connection closed at the next check (default 60 s; a deactivated tenant once the 5-minute host cache has expired, as for HTTP); reconnect is refused; the next page request goes to sign-in |
| Notification lost while the page stays connected | Print action: not repaired until the next notification for that order, a reopen of the panel or a reload. Live Screen: repaired at the next poll (30 seconds) |

## Limitations

- **One Web instance.** No backplane. A second Web instance would need one (and the internal channel would reach only one
  instance). WAS-22 names multi-instance support as a goal; it is not met by this phase.
- **Not durable.** Notifications are not stored or replayed. Missed ones are covered by the read on reconnect, the
  fallback while disconnected, and for the Live Screen its 30-second poll while connected. The print action does not
  read on a timer while connected (WAS-23 decision), so a lost print notification leaves it stale until the next
  notification of that order, a reopen or a reload.
- **The Live Screen polls less while connected** (30 seconds instead of 10, as WAS-24 asks for a lower-frequency
  fallback). Anything that changes the snapshot without an order write (the platform connection badge, automation
  indicators, the 2-minute Delivered window, a settings change) shows up to 30 seconds later than before while
  connected. Not measured yet; WAS-24 forbids removing polling without measurements, and the rate is one constant
  (`CONNECTED_POLLING_MS`, overridable with `connectedPollingIntervalMs`).
- **Order invalidation is not a sequence** (see [Message and version](#message-and-version)); bursts are coalesced,
  not deduplicated by version.
- **The hub send is awaited** (at most 2 seconds) inside the request that recorded the change: a manual print, an
  Approve or a Print Bridge report to Web can take that much longer when a client's connection is slow.
- **Mixed versions.** A previous Worker or Api running against the migrated schema inserts jobs at version 0 and does
  not bump versions, which breaks the ordering; stop every old process before any new one starts
  ([../operations/deployment.md](../operations/deployment.md#live-notifications)).
- **Worker and Api need `Realtime:Internal`** on the same machine as Web (loopback only).
- **Concurrency evidence.** Version and single-announcement behavior are tested with SQLite, which serializes writers.
  SQL Server locking of the serializable creation path is the existing behavior and was not re-tested.
- The Print Bridge devices page, the print history page, the Orders management page and practice orders do not use
  the channel.

## Tests

- `tests/Wasla.UnitTests/Realtime/PrintJobChangeNotificationTests.cs`: publish after commit, nothing for work that did not
  happen, Pending → Printing → Printed/Failed, reprint versions, retries, concurrent final reports, failing notifier.
- `InternalTenantChangeChannelTests.cs`: configuration validation, message validation, sender timeout, pause and payload.
- `TenantEventsHubTests.cs`: real Web pipeline with two tenants and a real SignalR client: tenant isolation, roles,
  no client-chosen group, Origin, session revocation closes the connection, Worker receipts through the loopback
  listener, the listener's refusals, the `print-state` endpoint.
- `PrintStatusBrowserTests.cs`: headless Chromium, details page (LTR and RTL) and the Live Screen modal, including the
  disconnected fallback and the read on reconnect.
- `realtime-client.test.js`, `order-print-status.test.js`: the two browser modules.
- WAS-24: `OrderChangeNotificationTests.cs` (ingestion insert, update and unchanged; every lifecycle action; refused
  and failed actions; failing notifier; tenant attribution), `TenantEventsHubTests.cs` (Kitchen and Viewer get order
  changes but never print status, a Cashier gets both, tenant isolation, Worker ingestion through the loopback listener,
  revocation of a Kitchen connection), `InternalTenantChangeChannelTests.cs` (the `OrderChanged` message),
  `live-screen-invalidation.test.js` (coalescing, polling rates, reconnect read, dispose, the coordinator's rate
  switch) and `LiveScreenRealtimeBrowserTests.cs` (headless Chromium in English and Arabic: push without polling, one
  announcement per new order through a burst, lifecycle action, disconnected fallback, reconnect, back/forward-cache
  restore with one connection, order training, a Kitchen screen).

## Source map

| Concern | Location |
|---------|----------|
| Contract | `src/Wasla.Application/Abstractions/Realtime/ITenantChangeNotifier.cs` |
| Version helper, publisher, null notifier | `src/Wasla.Infrastructure/Realtime/TenantChangePublisher.cs` |
| Internal channel (options, message, sender) | `src/Wasla.Infrastructure/Realtime/InternalTenantChanges.cs` |
| Hub, groups, notifier, origin check, revalidation, listener, registration | `src/Wasla.Web/Realtime/` |
| Print state endpoint | `OrdersController.PrintState` (`GET /orders/{id}/print-state`) |
| Print action | `Areas/Tenant/Views/Orders/_OrderPrintAction.cshtml`, `Models/Orders/OrderPrintActionModel.cs` |
| Order change publishing | `OrderSyncService.UpsertOrderAsync`, `OrderActionService` |
| Browser | `wwwroot/js/realtime/wasla-realtime.js`, `wwwroot/js/orders/orders-print-status.js`, `wwwroot/js/orders/orders-print.js`, `wwwroot/js/orders/orders-live-realtime.js`, `setIntervalMs` in `orders-live-store.js` |
