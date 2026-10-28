# Food platform integrations

## Purpose and scope

This document describes how Wasla connects to food-delivery platforms for order fetch and related configuration.

It owns:

- `Platforms:ProviderMode` (Mock / Real)
- DI registration of `IFoodPlatformClient` implementations
- Platform connection identity (one connection per platform; `StoreId` is configuration)
- High-level Trendyol GO / Yemeksepeti / Getir behavior

It does not own Worker cycle timing or upsert details (see [../orders/synchronization.md](../orders/synchronization.md)), Print Bridge, or tenancy.

This document describes committed source, not uncommitted working-tree changes.

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

### Provider HTTP clients

Source: `ProviderHttpClientRegistration.AddProviderHttpClient` (`src/Wasla.Infrastructure/Platform/`), called from `ServiceCollectionExtensions` in Real mode (WAS-95, WAS-97). Web, Api and Worker get provider clients only through `AddWaslaInfrastructure`. None of them registers, configures or builds an `HttpClient` itself.

#### One named client per provider

| Provider | Client name | How the client gets it |
|----------|-------------|------------------------|
| Yemeksepeti | `Yemeksepeti` (`YemeksepetiFoodPlatformClient.YemeksepetiHttpClientName`) | The singleton calls `IHttpClientFactory.CreateClient` with the name |
| Trendyol GO | `TrendyolGo` (`TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName`) | Typed client on `IFoodPlatformClient` (`AddTypedClient`), transient |

Until WAS-97 the Trendyol GO typed client was named after its interface, `IFoodPlatformClient`. A second `AddHttpClient<IFoodPlatformClient, X>()` would then have shared its name, configuration and handler pool, and the last base address would have won. Trendyol GO credentials would have gone to the other provider's host. Each provider now has its own:

- Base address and timeout (`Platform:<Provider>:BaseUrl` and `RequestTimeout`), set only by its own registration.
- Handler pool. The factory pools handlers per client name.
- Factory log categories: `System.Net.Http.HttpClient.<name>.LogicalHandler` and `.ClientHandler`. The Trendyol GO categories were `System.Net.Http.HttpClient.IFoodPlatformClient.*` before WAS-97. The clients' own logs keep their class categories.
- Credentials and headers, set on each request by its client (see below).
- Rate limit and retries. `TrendyolRequestRateLimiter` (a singleton) and the 429 retry belong to the Trendyol GO client only. The Worker's fetch retry runs per connection, above the clients.

`AddProviderHttpClient` enforces this at registration and startup:

- **Duplicate name.** Registering a provider client name twice throws.
- **Foreign configuration.** Another registration that configures a provider client (a second `AddHttpClient("<name>", ...)`, or `ConfigureHttpClientDefaults(b => b.ConfigureHttpClient(...))`) fails options validation. The hosts fail at startup (`ValidateOnStart`); without a host, the failure comes when the client is first created. The base address is never silently replaced.

**Adding a provider.** A future real provider (for example Getir) must register through `AddProviderHttpClient` with its own constant name:

1. Configure its base address and timeout in that one call.
2. Use `AddTypedClient<IFoodPlatformClient, TImplementation>()` or `CreateClient(name)`.
3. Do not call `AddHttpClient<IFoodPlatformClient, X>()` or `ConfigureHttpClient` for it, and do not put credentials in `DefaultRequestHeaders`.

#### Primary handler

`IHttpClientFactory` pools handlers, and every tenant shares them. The Yemeksepeti client is a singleton that keeps one `HttpClient`, and Trendyol GO typed clients resolved in different scopes reuse the same pooled handler. A pooled handler therefore holds no tenant or connection state.

**Explicit handler.** Each provider's primary handler is an explicitly constructed `SocketsHttpHandler`, so the handler type does not depend on the factory default. On .NET 8 that default is `HttpClientHandler`, which uses a `SocketsHttpHandler` internally. On .NET 9 and later the default is `SocketsHttpHandler`, and the WAS-95 helper accepted only `HttpClientHandler`. Except for the two settings below, the defaults are the same ones the .NET 8 `HttpClientHandler` uses:

- The system proxy.
- Standard certificate validation, with no revocation check.
- No decompression and no connection limit.
- Pooled connections that live as long as the handler.

The factory replaces the Trendyol GO handler every two minutes (the default handler lifetime). The Yemeksepeti singleton keeps the handler it got at startup, and with it its pooled connections, for the life of the process. That predates WAS-97.

**Settings.**

- **No cookies** (`UseCookies = false`). A `Set-Cookie` in a provider response is ignored, and no `Cookie` header is sent. Turn cookies on for a provider only if it requires them, and then only with a handler and cookie store owned by one connection.
- **No redirects** (`AllowAutoRedirect = false`). A 3xx response fails the request like any other non-2xx status (`ProviderRequestException`), with or without a `Location` header. The Worker's fetch retry treats it like any other HTTP failure and calls the original endpoint again. A redirected Yemeksepeti token response is a failed token request and never enters the token cache. Do not follow redirects manually or add an allowlist without a verified provider requirement. A followed 307 or 308 re-sends the request body, which for the Yemeksepeti token request holds the client secret.
- **Credentials per request.** Trendyol GO Basic credentials and `x-executor-user`, and the Yemeksepeti bearer token and token form, are set on each request. Do not put tenant credentials in `DefaultRequestHeaders` or in the client configuration.

**Guard.** `ProviderPrimaryHandlerGuard` is an `IHttpMessageHandlerBuilderFilter` inserted first in the filter list, so it runs after every other handler configuration and filter. It sets both settings again on whatever primary handler it finds. A later `ConfigurePrimaryHttpMessageHandler(...)` for a provider name, or a later filter that replaces the primary handler, therefore cannot turn cookies or redirects back on. A primary handler that is neither `SocketsHttpHandler` nor `HttpClientHandler` fails that client when it is created, with a message that names the client and the handler type.

#### Logging

- **Header values.** The factory's request logging redacts every header value of a provider client (`HttpClientFactoryOptions.ShouldRedactHeaderValue`, applied as a post-configuration so a later `RedactLoggedHeaders` cannot narrow it). At Trace, request and response headers appear as `Authorization: *`, `x-executor-user: *`, `Set-Cookie: *` and so on. This covers Basic credentials, bearer tokens, cookies, `x-executor-user`, `x-agentname`, the supplier id in `User-Agent`, and any header a provider adds later.
- **Redaction does not depend on log level.** Web, Api and Worker ship with `System.Net.Http.HttpClient` at Warning, and the redaction also holds when an operator lowers it to Trace.
- **Request URIs.** At Information the factory logs the method, request URI and status, and the client logs status codes and durations. Request URIs include supplier, store, chain and vendor ids and the query string. They carry no credentials; the token request's credentials are in its form body.
- **Bodies.** The factory does not log request or response bodies, and the provider clients do not log or throw response bodies or credentials (`ProviderFailureLoggingTests`). There is therefore no body redaction, because no body is logged.
- **Custom loggers.** A custom `IHttpClientLogger` added for a provider client must honour the same rule.

#### Tests that protect the contract

All of these resolve clients from the production Real-mode registration (`AddWaslaInfrastructure`) and call loopback fake providers with synthetic credentials.

- `ProviderCookieIsolationTests` (WAS-95): no cookie replay across connections, scopes or concurrent requests. Every provider primary handler is a `SocketsHttpHandler` with cookies and redirects off.
- `ProviderRedirectTests` (WAS-95, WAS-97):
  - 301, 302, 303, 307 and 308, both cross-origin and same-origin, fail every operation, and the redirect target receives nothing.
  - A missing or malformed `Location` fails the same way, with no follow-up request.
  - A redirected token response is never cached.
- `ProviderHttpClientRegistrationTests` (WAS-97):
  - Each provider has its own name, origin, handler pool and credentials, across sequential, scoped and overlapping calls.
  - Another provider on `IFoodPlatformClient`, registered either way, cannot take over Trendyol GO or Yemeksepeti configuration.
  - Duplicate names and foreign configuration fail.
  - Later handler overrides (named, `ConfigureHttpClientDefaults`, a later filter) cannot re-enable cookies or redirects, and an unsupported handler type fails.
  - The Web, Api and Worker sources do not compose HTTP clients themselves. This is a source check, because starting the real hosts in a test needs their master key, Data Protection folder and log files.
- `ProviderHttpLoggingTests` (WAS-97): with every category at Trace, no canary credential, token, cookie, provider header value or body appears in any log message, structured property, exception or scope.

#### Framework upgrades

After changing the target framework or the `Microsoft.Extensions.Http` version:

1. Run the four test classes above on the new framework.
2. Check that the factory still applies filters with the first registered filter outermost, which the guard relies on.
3. Check that `HttpClientFactoryOptions.HttpClientActions` and `ShouldRedactHeaderValue` still behave as described here.
4. Check whether the new framework logs headers, URIs (including query strings) or bodies differently, and update the Logging section to match.

Whether either real provider sets cookies or sends redirects is not confirmed (WAS-70). These rules are verified against a fake provider only.

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
- Removing a connection (Platform Connections, Remove, then the confirmation; GitHub #72) deletes its row and stored credentials, which frees the platform: the same platform and store can be connected again as a new row with a new id. Orders, `SyncLogs` and `IntegrationErrors` are kept (none has a foreign key to the row; logs keep the removed id). Removal is by id in one statement, so a repeated or stale confirmation deletes nothing and says the connection no longer exists. The Worker loads connections on every pass, so a removed one is never polled again; one removed while its pass runs ends that pass without a failure record, and the tenant's other connections continue. Order actions on existing orders of that platform then fail as they do for an inactive connection, until the platform is connected again.
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

### Credentials in the tenant Web forms

Source: `PlatformConnectionsController` (Web), `PlatformConnectionService.UpdateAsync`, the `Create` and `Edit` views and `platform-connection-form.js` (WAS-131). Every platform uses the same two credentials, API key and API secret; `SupplierId` and `ExecutorEmail` are configuration, not secrets.

- **Never sent to the browser.** `GetByIdAsync` returns only whether each credential is stored (`HasApiKey`, `HasApiSecret`), and the edit page shows only that. No input value, hidden field, attribute, script or message carries a stored credential.
- **Masked inputs.** Both fields are `type="password"` with `autocomplete="off"` (provider credentials, not a Wasla login) and `spellcheck="false"`. The reveal button next to each (`aria-pressed`, localized name) only switches the field's type, so it can show only what was typed into the current form; it fetches nothing. A revealed field is masked again on submit and on `pagehide`.
- **Create** requires both credentials (`CreatePlatformConnectionCommandValidator`, at most 500 characters each).
- **Edit.** An empty or omitted credential keeps the stored value; a non-blank value replaces only that credential, stored exactly as sent (no trimming). The service checks the effective pair (stored plus replacements: each present, a replacement at most 500 characters) before writing any field, so a refused edit, and any edit that fails later, leaves the row unchanged. Editing other fields never touches the credentials. There is no rule that both must be replaced together: no provider contract in this repository requires it, and a Yemeksepeti secret rotation keeps its client id.
- **Failed submissions.** Before either form is shown again, the controller clears the typed credentials from the view model and from ModelState (whose attempted values the input helpers would otherwise prefer), keeps the other fields and every validation error, and tells the user to enter the credentials again.

`PlatformConnectionCredentialWebTests` (real request pipeline, every failure path, roles, antiforgery, cross-tenant, logs at Trace) and `PlatformConnectionCredentialBrowserTests` (reveal control in four cultures, right to left, dark mode, phone width) cover this.

### Yemeksepeti OAuth tokens

Source: `YemeksepetiFoodPlatformClient` (WAS-88). In Real mode one client instance (a singleton) serves every tenant in a process, so its in-memory token cache is shared across tenants. A cached token is reused only when all of these match the request that obtained it:

- The `PlatformConnection` id.
- The token endpoint (`Platform:Yemeksepeti:BaseUrl` and `TokenPath`).
- The decrypted client id **and** client secret.

The cache key is an HMAC-SHA256 of those fields under a random key generated per client instance. It contains no credential text and means nothing outside the process. Consequences:

- Another connection never receives a cached token, even with the same client id or identical credentials. Each connection asks the provider for its own token.
- A changed secret (rotation or a typo) triggers a new token request. If the provider rejects the secret, that connection's fetch fails. It never falls back to an older token.
- A 401 or 403 from the orders endpoint discards that connection's cached token. The next attempt, including the sync retry, requests a new one. Other failures keep the token.
- Concurrent fetches of one connection share one token request. Different connections never wait for each other, and a failed token request affects only its own connection.
- A token is reused until 5 minutes before it expires (`expires_in`; 7200 seconds when missing).
- Token logs carry only the connection id, status code and expiry. Client ids, secrets and tokens are not logged.

Cache size (`MaxCachedTokens` = 4,096):

- A token request registers an entry before it is sent. A failed or cancelled request removes that entry when its last waiting fetch finishes, so wrong or changing credentials do not leave entries behind.
- When no token request is in progress, every entry holds a token and there are at most 4,096 entries.
- While token requests are in progress, each adds at most one entry on top of that (concurrent fetches of one connection share one). The Worker syncs at most five tenants at a time.
- Past 4,096, a new token is used for its own fetch only and a warning is logged. Further fetches of that connection then request a token each time until older entries are pruned.
- An entry whose token was discarded after a 401 or 403 is removed as soon as no fetch is using it. An entry whose token has expired is removed the next time any connection acquires a token. A rotated credential or a connection that is no longer synced therefore keeps its entry until its token has expired and another token has been acquired.

Inactive connections: the Worker does not sync an inactive connection (`IsActive` false), so its cached token is not sent. An Owner can remove a connection, and the temporary Development reset tool deletes all of a tenant's connections; a deleted connection is never loaded again, so its token is not sent either. A connection's entry ends when its token expires and is pruned.

The real provider's token scope (chain, vendor or integrator), token lifetime, behavior after a secret rotation, and token-endpoint limits are not confirmed yet (WAS-70). These rules are verified against a fake provider only.

## Application-level provider options

Sections (not tenant secrets):

| Section | Purpose |
|---------|---------|
| `Platform:TrendyolGo` | `BaseUrl`, timeouts, agent name (`AgentName`, default `Wasla`) |
| `Platform:Yemeksepeti` | `BaseUrl`, `TokenPath`, `DefaultPageSize` (default 20), timeouts |

Per-tenant API keys live encrypted in TenantDb after UI setup.

**Trendyol GO agent name.** In Real mode every Trendyol GO request identifies Wasla as `x-agentname: Wasla` and `User-Agent: <supplierId> - Wasla`, one value per header. The value is `TrendyolGoOptions.AgentName`; its default is `Wasla` and every committed appsettings file sets that same value (WAS-141, checked by `TrendyolGoAgentNameTests` and `ProviderHttpClientRegistrationTests`). Mock mode sends nothing. Trendyol's public Partner API documentation describes the `User-Agent` as the seller id followed by the integrator company name (alphanumeric, at most 30 characters). Whether Trendyol GO requires that name to match a registered integrator record has not been confirmed with Trendyol.

## Fetch summary (Real HTTP)

Details and numbers: [../orders/synchronization.md](../orders/synchronization.md).

| Provider | Window | Statuses requested | Page size | Page cap |
|----------|--------|--------------------|-----------|----------|
| Trendyol GO | From the checkpoint (`LastSuccessfulSync`) − 5 minutes to now when that fits 1 hour. After an outage: the hot window `[now − 1 hour, now]` first, then history oldest first in 1-hour windows, at most 12 per connection per Worker cycle. 1 hour back when there is no checkpoint | All documented `packageStatuses`, including `Cancelled`, `UnSupplied` and `Delivered` | 50 | 20 pages per window (≤ 1000 packages) |
| Yemeksepeti | Last 1 hour (not checkpointed) | Provider default | default 20 | 50 pages (≤ 1000 at default size) |

Malformed pagination, “more pages at cap” or a persistent HTTP 429 fails that connection’s sync, and the checkpoint does not move past the failed window. Outage recovery, failure rules and the Trendyol GO request contract: [../orders/synchronization.md](../orders/synchronization.md#checkpoint-and-outage-recovery).

Trendyol GO package requests share one process-wide limit of 40 requests per rolling 10 seconds across all tenants (`TrendyolRequestRateLimiter`, a singleton). A 429 is retried on the same page after `Retry-After` (at most 60 seconds, 10 seconds without one), at most twice. See [Trendyol GO request limiter](../orders/synchronization.md#trendyol-go-request-limiter), [HTTP 429](../orders/synchronization.md#http-429) and [Capacity model](../orders/synchronization.md#capacity-model).

## Webhooks

No webhook is implemented for any platform yet; polling is the only ingestion path. The WAS-17 decision record, [provider-webhooks.md](provider-webhooks.md), holds the evidence:

- Trendyol GO documents a Meal webhook. Wasla will use it provider-specifically, in front of the checkpointed polling, never instead of it. The endpoint and its tenant routing are blocked until Trendyol GO confirms how an event identifies its seller ([Tenant routing](provider-webhooks.md#tenant-routing)).
- Yemeksepeti's restaurant webhook contract is not currently confirmable; implementation is blocked by WAS-70.
- A pilot receives Trendyol GO Meal webhooks from the provider's stage API on the existing Wasla deployment, for exactly one configured seller and connection, without tenant routing; it is off by default, and only its connection uses the stage API while every other connection keeps the deployment's API ([trendyol-go-meal-webhook-stage-pilot.md](trendyol-go-meal-webhook-stage-pilot.md)).

See also [../orders/synchronization.md](../orders/synchronization.md#webhooks-and-polling).

## Mock order behavior (product rules)

When Mock clients generate orders:

- New mock orders should start as New/Created (“Yeni”).
- Mock updates for existing orders may move to final statuses (Delivered / Cancelled) only, unless a task explicitly expands intermediate status simulation.

## Known gaps

- Getir: Real mode still mock.
- Yemeksepeti lifecycle APIs: not implemented on the real client.
- Yemeksepeti polling is not checkpointed: a change older than one hour at the next successful run can be missed.
- Trendyol GO date-range limits, retention and the scope of the request limit are undocumented and not yet verified against the Stage API.
- The Trendyol GO request limiter is per Worker process; several Workers would need a distributed limiter.

## Related docs

- [../orders/synchronization.md](../orders/synchronization.md)
- [../operations/deployment.md](../operations/deployment.md)
- [../architecture/tenancy.md](../architecture/tenancy.md)
