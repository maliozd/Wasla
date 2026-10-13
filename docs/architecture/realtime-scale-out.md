# Decision: live notifications on one Wasla.Web instance (WAS-25)

## Status and scope

Accepted for the first production stage (2026-10, WAS-25). It applies to the live channel of [realtime.md](realtime.md):
the SignalR hub in Wasla.Web, the loopback internal channel from Wasla.Worker and Wasla.Api, and the browser clients.
It records why one Web instance is enough now, where that stops being true, and what the multi-instance setup has to
be when it is needed. Nothing here is built yet beyond what [realtime.md](realtime.md) describes: there is no
backplane, no sticky-session configuration and no second instance.

## Context: the hosting the code is written for

```text
restaurant browsers ──HTTPS──► nginx (VDS, TLS) ──► Wasla.Web 127.0.0.1:5200   one process: pages, APIs, /hubs/tenant-events
Print Bridge devices ─HTTPS──┘                       ▲  loopback 127.0.0.1:{Realtime:Internal:Port}
                                                      ├── Wasla.Worker (same machine)
                                                      └── Wasla.Api    (same machine)
SQL Server: CentralDb + one database per tenant
```

Facts from the code that decide the topology:

- **Connections and groups live in Web's memory** (`DefaultHubLifetimeManager`; no `AddStackExchangeRedis`).
  `SignalRTenantChangeNotifier` sends through `IHubContext`, which reaches only connections of its own process.
- **The internal channel is loopback only.** `InternalTenantChangeListener` binds `127.0.0.1:Port` with one route;
  Worker and Api post to that address. It reaches exactly one Web process: the one on the same machine that owns the
  port.
- **Negotiation is on.** `MapWaslaRealtime` allows WebSockets, Server-Sent Events and long polling, and the browser
  client negotiates first (`withUrl` without `skipNegotiation`). The connection token from `/negotiate` exists only in
  the process that issued it.
- **Correctness never depends on delivery.** Screens render only their canonical HTTP reads; a lost notification is
  repaired by the Live Screen's poll (30 s connected, 10 s otherwise), by the read on every reconnect, or for a print
  action by the next event, reopen or reload ([realtime.md](realtime.md#limitations)).
- **Releases stop the old version first.** Several migrations forbid running the previous Web next to the new one
  ([../operations/deployment.md](../operations/deployment.md#release-order-migrate-every-database-first)), so a
  release is a stop, migrate, start, not a rolling update.

## Decision

1. **Run one Wasla.Web instance.** No backplane, no sticky sessions, no Azure SignalR, no Kubernetes.
2. **Keep the transports and negotiation as they are** (WebSockets, then SSE, then long polling). Do not switch to
   WebSockets-only with `skipNegotiation`.
3. **Make the single instance observable** (the `Wasla.Realtime` metrics,
   [../operations/observability.md](../operations/observability.md#live-notification-metrics)) and spread reconnects
   after a restart (jitter in `wasla-realtime.js`), so the thresholds below can be watched.
4. **When the thresholds are reached, go to several instances with both a Redis backplane and sticky routing** (below),
   in its own issue. Never add a second instance without them.

## 1. Why one instance is acceptable now

- The load is small and bounded per tenant: a restaurant has a few Live Screens and a few staff with print actions
  open, each one connection. A pilot of tens of tenants means hundreds of connections. The bounded load tests on a
  developer machine (Evidence below) handle that many connections, reconnect storms and bursts with sub-millisecond
  median hub sends, and correctness never depends on the channel.
- A second instance does not buy availability today: releases already stop Web (see Context), and the VDS is one
  machine, so a machine or nginx failure takes every instance down together.
- Every multi-instance option costs operational parts the VDS does not have yet (Redis, sticky routing, health-based
  draining) and changes the internal channel.

## 2. Current failure and capacity boundaries

| Boundary | What happens | Measured or bounded by |
|----------|--------------|-------------------------|
| Web restart or release | Every connection drops. Screens report disconnected, the Live Screen polls every 10 s, print actions read every 10 s while shown; clients reconnect at 0 s and then 2, 5, 10, 30 s plus 0–50 % random jitter, each reading its state once connected | `ReconnectStorms_*` (all tabs reconnect at once); jitter spread in `realtime-resilience.test.js` |
| Web down, seen from Worker or Api | First post fails within `TimeoutSeconds` (3 s), then 30 s pause per failure, one warning; sync and Bridge reports continue | `wasla.realtime.internal.pauses`, `result=paused` |
| A client that stops reading (frozen tablet, dead network) | Its outgoing buffer fills; after that every send to its tenant's group waits the full 2 s bound (operator request, Bridge report or internal request is that much slower) until the server's client timeout (30 s by default) closes it. Other tabs keep receiving everything; memory stays bounded by the buffer | `AClientThatStopsReading_*`: about 2,360 small messages buffered, then 5 sends at 2 s, closed by the client timeout, healthy tab received all |
| Session checks | One `AppUsers` primary-key read (and a cached host lookup) per open connection every 60 s: about connections ÷ 60 reads per second | `wasla.realtime.session_checks`, `wasla.realtime.session_check.duration` |
| Snapshot reads triggered by notifications | Each connected Live Screen reads `/orders/live-data` at most once per second while changes keep arriving (150 ms settle, 1 s gap, one read at a time with one follow-up), plus its 30 s poll | `realtime-resilience.test.js` (1,000 events in 10 s → at most 12 reads); `http.server.request.duration` for `/orders/live-data` |
| Process memory and threads | Each connection holds a WebSocket, its pipes and one session-check loop | Resource lines of the load tests (Evidence) |

The one real ceiling is the single process: CPU and memory of one Web, and one point of failure.

## 3. What breaks if a second Web instance is added without coordination

- **Notifications reach only one instance.** A change made on instance A (an operator's Approve, a Print Bridge
  report) reaches screens connected to A only; screens on B see it at their next poll (30 s for a connected Live
  Screen) or never, for an open print action, until reopen or reload.
- **Negotiation breaks.** The token from `/negotiate` on A is unknown on B: a WebSocket or SSE connect routed to B is
  refused (404) and the client retries or falls back unpredictably; long polling needs every poll on the same instance.
- **The internal channel reaches one instance at most.** Two instances on the VDS cannot both bind
  `127.0.0.1:{Realtime:Internal:Port}`: the second fails to start its listener. With different ports, Worker and Api
  post to only one of them. On separate machines, loopback reaches only the local one.
- **Per-process state differs:** login rate limits, the unknown-host cache and the 5-minute tenant host cache are per
  process (documented in [authentication.md](authentication.md) and [tenancy.md](tenancy.md)); Data Protection keys must
  be shared through `DataProtection__KeyPath` or cookies issued by one instance fail on the other.
- **Nothing is lost:** the database and the polling fallback keep every screen correct, only slower. That is why a
  second instance is a performance and availability change, not a correctness emergency.

## 4. The internal channel with several Web instances

With a backplane the internal channel only has to reach **one** healthy Web instance: that instance's hub context
publishes to the backplane and every instance delivers to its own connections. So:

- Same machine: give each instance its own `Realtime:Internal:Port`, and let Worker and Api try a list of ports in
  order (one failure pauses only that target). This is a small change to `InternalRealtimeOptions` and
  `WebTenantChangeNotifier`.
- Separate machines: the loopback design no longer fits. Either keep a Web instance next to every Worker and Api, or
  replace the listener with a private-network endpoint (TLS and the shared secret), or let Worker and Api publish to the
  backplane directly. Decide in the multi-instance issue, together with WAS-103 (which makes a missing channel visible).

## 5. Sticky sessions

**Required** as long as negotiation and the SSE and long-polling fallbacks are used: the negotiate request and the
transport requests of one connection must reach the same instance. A backplane does not remove this (it shares
messages, not connections). With nginx (open source), route by tenant host, `hash $host consistent;`, in the upstream
of `/hubs/`: every request of a tenant then lands on one instance, which also keeps a tenant's operator actions and its
screens together. `ip_hash` is a fallback, but many devices of one restaurant share one public address anyway.

## 6. WebSockets-only with `skipNegotiation`

Supported by SignalR, and it would remove the need for sticky routing of the connect request (a WebSocket is one
long request). **Not desirable for Wasla now:** restaurant networks, captive portals and some proxies block or break
WebSockets, and today those screens still get pushes over SSE or long polling. With WebSockets only they would fall
back to polling. It also removes neither the backplane requirement nor the internal-channel question. Revisit only if
measurements show negligible SSE and long-polling use (`signalr.server.connection.duration` by
`signalr.transport`) and sticky routing becomes a burden.

## 7. When a backplane or a managed service becomes necessary

- **Redis backplane** (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`): necessary as soon as more than one Web
  instance serves the hub. Self-hosted Redis on the VDS fits the current hosting; its outage then degrades to the
  polling fallback (sends fail, the notifier logs and counts `failed`, nothing else fails).
- **Azure SignalR Service or another managed service:** not applicable while Wasla is self-hosted on a VDS. Reconsider
  only when hosting moves to Azure or connection counts exceed what a few Web instances hold.

## 8. Recommended topology

| Stage | Topology |
|-------|----------|
| Now (pilot, first production) | One Web, nginx, loopback internal channel, no backplane (this document) |
| More capacity or zero-downtime releases on the same VDS | Two Web processes on different local ports behind nginx with `hash $host consistent` for `/hubs/` (and the same upstream for pages, so a tenant's actions and screens share an instance), a local Redis as SignalR backplane, distinct internal ports with Worker and Api trying each, shared Data Protection key folder |
| Several machines | The same, with Redis reachable from every Web, the internal channel replaced (section 4), nginx or a load balancer with host-hash affinity and health checks |

## 9. Health checks, load balancing, release order and recovery (multi-instance)

- **Routing:** only to instances whose `/health/ready` is healthy (CentralDb reachable). `/health/live` only says the
  process runs. Add a Redis check to readiness when the backplane is introduced.
- **Proxy:** WebSocket upgrade, `proxy_read_timeout` above the 15 s keep-alive (120 s today), `proxy_buffering off`
  for SSE, as in [../operations/deployment.md](../operations/deployment.md#live-notifications), on every upstream.
- **Draining:** take an instance out of the upstream first, then stop it. Its connections close; clients reconnect with
  jitter to the remaining instance and read their state.
- **Order:** Redis before Web; migrations before any new process (and never next to an old Web where the release says
  so); Web instances before Worker and Api, so the internal channel has a target; Worker and Api last.
- **Recovery:** Redis down → pushes stop, polling continues, alert on `wasla.realtime.notifications{result=failed}`;
  one Web down → its tenants move to the other instance on reconnect; Web listener down → Worker and Api pause 30 s per
  failure and log once.

## 10. Thresholds that trigger the scale-out work

Open the multi-instance issue when any of these holds over a normal service peak (see
[../operations/observability.md](../operations/observability.md#live-notification-metrics) for how to read them):

- Zero-downtime releases or surviving a Web crash become a requirement (availability, not load).
- `wasla.realtime.connections.active` above about 2,000 on the VDS, or above half of what a heavier load run on the
  production machine sustained with hub send p95 under 50 ms.
- `wasla.realtime.notification.duration` (channel `hub`) p95 above 250 ms, or `result=timeout` above 0.1 % of sends,
  sustained (not single stuck clients, which the client timeout removes).
- Web process CPU above 60 % or a growing thread-pool queue (`System.Runtime` counter `threadpool-queue-length`) at peak.
- `/orders/live-data` p95 above 500 ms at peak (`http.server.request.duration`), since every notification multiplies
  into reads by every open Live Screen of the tenant.
- `wasla.realtime.session_check.duration` p95 above 1 s, or session-check reads becoming a visible share of tenant
  database load.

## Evidence

Bounded load tests (`tests/Wasla.UnitTests/Realtime/RealtimeLoadTests.cs`), real Web pipeline and real .NET SignalR
clients over WebSockets in one process on a developer machine (Windows 11, .NET 8); figures are for this machine and
are not a production capacity claim:

| Scenario | Default run (every pull request) |
|----------|-----------------------------------|
| 3 tenants × 4 roles, 120 tabs, 600 notifications (half over the internal channel) | connect 0.7 s; burst 0.28 s; every tab received exactly its tenant's streams; hub send p50 0.06 ms, p95 1.3 ms; internal send p50 3.7 ms, p95 48 ms |
| 3 reconnect storms of all 120 tabs | 0.7–0.8 s each; active connections back to 120 (Wasla and ASP.NET Core counts), each tab in its groups once |
| Tabs closed during a burst | no failed sends; active count and transport count back to the open tabs |
| A client that stops reading | ~2,360 messages buffered, then 5 sends waited the 2 s bound, closed by the client timeout (8 s in the test), healthy tab received all |

The heavier run (`WASLA_REALTIME_LOAD_*`, [../operations/testing.md](../operations/testing.md#realtime-load-tests)) is
recorded in the WAS-25 pull request.
