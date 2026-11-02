# Authentication

## Purpose and scope

This document describes Wasla cookie authentication for tenant operators and central admins: scheme/cookie contract, login/logout, password reset, API differences, and when a Web session reaches the API.

Role and policy matrices are documented in [roles-and-permissions.md](../product/roles-and-permissions.md) — this file only summarizes how schemes feed authorization.

Wasla does **not** use JWT for this auth contract.

## Canonical contract names

**Source (currently uncommitted):** `src/Wasla.Application/Security/WaslaAuthContracts.cs`

| Constant | Value |
|----------|--------|
| `TenantScheme` | `WaslaTenant` |
| `CentralAdminScheme` | `WaslaCentralAdmin` |
| `TenantCookieName` | `.Wasla.TenantAuth` |
| `CentralAdminCookieName` | `.Wasla.CentralAdminAuth` |
| `TenantIdClaim` | `TenantId` |
| `CentralAdminSecurityStampClaim` | `Wasla.CentralAdminSecurityStamp` |

Web wrappers alias the same values:

- `src/Wasla.Web/Security/AuthSchemes.cs` → `AuthSchemes.Tenant` / `AuthSchemes.CentralAdmin`
- `src/Wasla.Web/Security/TenantAuthCookieNames.cs`
- `src/Wasla.Web/Security/CentralAdminAuthCookieNames.cs`

Each of these exposes only the active Wasla names; see [Cookie inventory](#cookie-inventory).

**Working-tree note:** `WaslaAuthContracts.cs` is untracked; Web cookie-name/scheme files and API auth setup currently reference it. Treat the contract above as the intended source of truth once committed; verify against the working tree when auditing.

## Scheme isolation

Current implementation keeps two independent cookie schemes:

| Audience | Scheme | Cookie | `TenantId` claim |
|----------|--------|--------|------------------|
| Tenant panel users | `WaslaTenant` | `.Wasla.TenantAuth` | Required; must match resolved tenant |
| Central admin | `WaslaCentralAdmin` | `.Wasla.CentralAdminAuth` | Not issued |

Controllers select schemes explicitly (for example `[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, ...)]` vs `AuthSchemes.CentralAdmin`). A central-admin cookie must not authorize tenant endpoints; a tenant cookie must not authorize central-admin endpoints.

## Web setup

**Source:** `src/Wasla.Web/Program.cs`

- Default authenticate/challenge scheme: tenant (`WaslaTenant`)
- Tenant cookie: HttpOnly, Path `/`, SameSite Lax, SecurePolicy Development=`SameAsRequest` / Production=`Always`. Registered by `AddWaslaTenantCookie` (`src/Wasla.Web/Security/TenantCookieAuthentication.cs`), which also attaches the per-request session check (see [Tenant session revalidation](#tenant-session-revalidation))
- Central admin cookie: same cookie flags; separate login/logout/access-denied paths under `/admin`. Registered by `AddWaslaCentralAdminCookie` (`src/Wasla.Web/Security/CentralAdminCookieAuthentication.cs`), which also attaches the per-request account check (see [Central admin session revalidation](#central-admin-session-revalidation))
- **No cookie `Domain` is set** (host-only cookies)
- Data Protection: `SetApplicationName("Wasla")`; optional `DataProtection:KeyPath` from configuration (Web has no default key folder; the API's default is described under [When a Web session reaches the API](#when-a-web-session-reaches-the-api))
- Authorization policies registered here; handler: `TenantRoleAuthorizationHandler` (`src/Wasla.Web/Security/TenantRoleRequirement.cs`)

### Tenant login / logout

**Source:** `src/Wasla.Web/Areas/Tenant/Controllers/AuthController.cs`

- Routes under `/auth`
- Validates credentials via `IAuthValidationService` against the resolved tenant
- Signs in with scheme `WaslaTenant`
- After `SignInAsync` succeeds, `ITenantLoginRecorder` (`TenantLoginRecorder`) stores the UTC time in `AppUsers.LastLoginAt` for that user in that tenant database. It uses one conditional `UPDATE` that only moves the value forward, so a slower concurrent login cannot overwrite a newer time, and it does not change `UpdatedAt`. If that database write fails, the login still succeeds and a warning with only the tenant id, user id and exception type is logged; cancellation and non-database errors are not swallowed. Failed or cancelled logins, the signup welcome link (`/auth/welcome`) and requests authenticated by an existing cookie do not record a login. `AuthValidationService` only checks credentials and reads only the columns it needs
- Claims include `TenantId` (value from `AuthSessionResult.CustomerId`), `UserId` (also `ClaimTypes.NameIdentifier`), email, role (`ClaimTypes.Role` and `"Role"`), name, and the user's security stamp (`Wasla.TenantSecurityStamp`, `WaslaAuthContracts.TenantSecurityStampClaim`)
- The signup welcome link (`/auth/welcome`) builds the session from the user's current row (`IAuthValidationService.GetActiveSessionAsync`): current role and stamp, and no session for a user who is inactive or gone
- Logout: `SignOutAsync(WaslaTenant)` and expires `.Wasla.TenantAuth` only

### Where a tenant user goes after signing in

**Source:** `src/Wasla.Web/Security/TenantLandingPages.cs` (`ITenantLandingPageResolver`, `TenantReturnUrl`), used by the tenant `AuthController`. Added in WAS-113.

The login POST, the login page opened with a session that is already valid on this host, and the signup welcome link all ask one resolver where to go. The answer is always a page the signed-in principal may open on this host:

1. **A return URL**, only when all of the following hold:
   - It is a plain path on this host (`/path` or `/path?query`). Everything else is refused: another host or scheme (`https://…`, `//host`, `/\host`, `javascript:`), backslashes, control and white-space characters, fragments, non-ASCII text, encoded slashes, backslashes and percent signs (`%2F`, `%5C`, `%25`, so double encoding), malformed escapes, dot segments, and anything under `/auth` (login, logout, Access denied), which would loop.
   - A GET for that path reaches a controller action through the application's own route matcher (the same endpoints, constraints and precedence as a real request). Unknown and POST-only paths, anonymous pages, Central Admin pages and non-MVC endpoints (hubs, health) are refused.
   - The action's own `[Authorize]` metadata, combined as the authorization middleware combines it, admits the principal for the tenant resolved for this request.

   The query string of an accepted return URL is kept unchanged.
2. **Otherwise the user's landing page:** the first of `/dashboard`, `/orders/live-display` (Live Screen) and `/orders` that the same check admits. With the current policies this is the Dashboard for Owner, Manager and Viewer (`CanViewReports`), and the Live Screen for Kitchen and Cashier. The full list is in [roles-and-permissions.md](../product/roles-and-permissions.md#landing-page-after-sign-in).
3. **A session that may open none of them** goes to `/auth/login`. Only obsolete `Staff` can reach this, and its session is rejected there, so the login form renders. The login page redirects a signed-in user only when there is a destination, so it never redirects to itself.

The resolver reads no data. It does not open another tenant's resources or disclose whether they exist. A return URL naming another tenant's order is decided by route and role alone, and that page then answers as it does for any unknown id. Endpoint authorization is unchanged: the destination still runs its own policy check when the browser opens it.

### Access denied

`GET /auth/access-denied` is where the tenant cookie scheme sends a signed-in user who lacks a page's policy. It requires a tenant session. An anonymous request goes to the login page, and signing in from there does not return to Access denied, because `/auth` is never a destination.

The page tells a signed-in user that they are signed in but lack permission. It offers one recovery link, `Auth.AccessDenied.GoToStartPage`, to their landing page, and no login link. The layout keeps its logout control. The denied path (`ReturnUrl`) is neither shown on the card nor followed. Copy is localized in TR, EN, AR and RU, and Arabic renders right to left through the tenant layout.

### Tenant session revalidation

**Source:** `src/Wasla.Infrastructure/Services/TenantSessionValidator.cs` (the rules, behind `ITenantSessionValidator`), `src/Wasla.Infrastructure/Security/TenantSessionCookieEvents.cs` (the cookie events), `src/Wasla.Application/Security/TenantSessionClaims.cs` (the claims). Web's `TenantCookieEvents` (`src/Wasla.Web/Security/TenantCookieEvents.cs`) and the API's `ApiTenantCookieEvents` (`src/Wasla.Api/Security/ApiTenantAuthenticationExtensions.cs`) derive from the shared events and differ only in how they answer a challenge or a forbid.

Every request that presents `.Wasla.TenantAuth` to Wasla.Web or Wasla.Api is checked against the user's row in the tenant database before any role claim is used. Both use the same validator, so the rules below are identical in both. Tenant resolution runs before authentication, and the tenant database is chosen from the resolved host, never from the cookie.

| Situation | Result |
|-----------|--------|
| Cookie `TenantId` differs from the resolved tenant | Rejected and signed out on that host. No database is opened |
| `TenantId`, user id, role or stamp claim missing, malformed or empty (including cookies issued before stamps existed) | Rejected and signed out. No database is opened |
| A security claim repeated (even with an identical value), the two role claims or the two user id claims disagreeing, a role that is not exactly the name the login writes for an assignable role (for example lower case, padded, numeric, comma-combined, or obsolete `Staff`), or an id or stamp not in the issued lowercase GUID form | Rejected and signed out. No database is opened |
| User no longer exists | Rejected and signed out |
| User inactive | Rejected and signed out |
| Stored security stamp differs from the cookie's | Rejected and signed out |
| Stored role differs from the cookie's role (for example a role edited directly in the database) | Rejected and signed out |
| No tenant resolved for the request (a central host, or a path that skips tenant resolution such as `/culture` or `/error`) | The session does not authenticate that request. The cookie is not deleted and not renewed |
| The tenant database cannot be read | The request fails closed before any controller runs: Wasla.Web answers HTTP 500, Wasla.Api answers HTTP 503 with `{"error":"tenant_session_unavailable"}` (`TenantSessionUnavailableMiddleware`). Only the exception type is logged; the session is neither accepted, renewed nor deleted, and is checked again on the next request |

A rejected session is anonymous for that request. On Web, protected pages redirect to `/auth/login` on the same host (no `ReturnUrl` from another host is followed, see [Where a tenant user goes after signing in](#where-a-tenant-user-goes-after-signing-in)), and the login page itself renders, so there is no redirect loop. On the API, a protected endpoint answers 401 without a redirect; the rejected cookie is deleted on that host as well.

Which changes end existing sessions: `AppUsers.SecurityStamp` is replaced whenever a user's role, active state or password changes (user management, the Web password reset and `reset-password --scope tenant`). Deleting a user or deactivating them also fails the check directly. A name-only edit keeps the stamp. A role change is not refreshed into the cookie: the old session is rejected and the user signs in again with the new role. A reactivated user's old cookie stays rejected, because deactivation already replaced the stamp; they sign in again with their password. An Owner who changes their own password on the Users page is signed out on the next request.

Cost and timing: one primary-key read of `AppUsers` (active flag, role, stamp) per authenticated tenant request, through the existing per-tenant `ITenantDbContextFactory`. Nothing is cached, so a change applies from the next request. Sliding renewal is decided by the cookie handler before validation; it is deferred and only a session that has just passed validation is re-issued.

Deployment: the `AddAppUserSecurityStamp` tenant migration gives every existing user a new stamp. Cookies issued before it carry no stamp, so every tenant user signs in again once after the release. A tenant database without the column fails closed (see [../operations/deployment.md](../operations/deployment.md#release-order-migrate-every-database-first)).

Wasla.Api runs the same check on the same cookie (WAS-94). It has no endpoint that issues a tenant cookie, so the only tenant sessions it can see are Web sessions; see [When a Web session reaches the API](#when-a-web-session-reaches-the-api).

### Central admin login / logout

**Source:** `src/Wasla.Web/Areas/Admin/Controllers/AuthController.cs`

- Routes under `/admin`
- Claims: name identifier (the admin id), email, display name, role `CentralAdmin`, and the admin's security stamp (`Wasla.CentralAdminSecurityStamp`). They are built by `CentralAdminSessionClaims.Create` (`src/Wasla.Application/Security/CentralAdminSessionClaims.cs`), and the stamp is read in the same query as the verified password hash
- **No `TenantId` claim**
- Logout expires `.Wasla.CentralAdminAuth` only; the tenant cookie is untouched
- An unknown email, an inactive account and a wrong password get the same result and the same page: `Admin.InvalidCredentials`, status 200, nothing else different (WAS-90; there is no separate "inactive" message any more). Every attempt verifies one bcrypt hash: the account's own (also for an inactive account), or for an unknown email the static dummy hash `AuthValidationService.DummyHash` (default work factor), so an unknown email does not answer measurably faster. A stored hash that cannot be read fails the same way. `CentralAdminAuthService`
- Signing in with an inactive admin is refused as above, and the attempt is not recorded as a login. There is no supported command that deactivates or reactivates a central admin; see [Central admin session revalidation](#central-admin-session-revalidation) for the CentralDb statement

Tenant and Central Admin login POSTs are rate limited; see [Credential rate limiting](#credential-rate-limiting).

### Central admin session revalidation

**Source:** `src/Wasla.Infrastructure/Services/CentralAdminSessionValidator.cs` (the rules, behind `ICentralAdminSessionValidator`), `src/Wasla.Web/Security/CentralAdminCookieEvents.cs` (the cookie events), `src/Wasla.Application/Security/CentralAdminSessionClaims.cs` (the claims). Added in WAS-37.

Every request that authenticates `.Wasla.CentralAdminAuth` is checked against the admin's row in CentralDb before it is accepted: every Admin page and action, the authenticated logout, and the "already signed in" check of the Admin login page. No tenant database is opened.

| Situation | Result |
|-----------|--------|
| Admin id, role or stamp claim missing, repeated, malformed or empty, a role other than exactly `CentralAdmin`, or an id or stamp not in the issued lowercase GUID form. This includes every cookie issued before WAS-37, which has no stamp | Rejected and signed out. CentralDb is not read |
| Admin no longer exists | Rejected and signed out |
| Admin inactive | Rejected and signed out |
| Stored security stamp differs from the cookie's | Rejected and signed out |
| CentralDb cannot be read | The request fails closed before any controller runs: the global exception handler answers with the HTTP 500 error page, as for a tenant session on Web. Only the exception type is logged; the session is neither accepted, renewed nor deleted, and is checked again on the next request |

"Signed out" applies to the Central Admin scheme only: `.Wasla.CentralAdminAuth` is deleted, and a tenant cookie in the same browser is left alone. A rejected session is anonymous for that request, so Admin pages redirect to `/admin/login` and the login page renders.

Which changes end existing sessions: `CentralAdminUsers.SecurityStamp` is replaced by both central password commands, `reset-password --scope central` and `reset-central-admin-password`. Deactivating (`IsActive = 0`) or deleting the admin fails the check directly. Signing in again does not change the stamp, so it does not end the admin's other sessions.

There is no supported command to deactivate or reactivate a central admin. Until there is, deactivate in CentralDb and replace the stamp in the same statement:

```sql
UPDATE [CentralAdminUsers]
SET [IsActive] = 0, [SecurityStamp] = NEWID(), [UpdatedAt] = SYSUTCDATETIME()
WHERE [NormalizedEmail] = N'ADMIN@EXAMPLE.COM';
```

Reactivating then only sets `[IsActive] = 1`; cookies issued before the deactivation stay rejected because of the new stamp, and the admin signs in again. A deactivation that does not replace the stamp still rejects every request while the account is inactive, but a copy of an old cookie that was not presented during that time becomes valid again when the account is reactivated, until it expires. Any other direct write of `PasswordHash` must also set a new `SecurityStamp`.

Remember-me and sliding expiration do not bypass the check. A remember-me (persistent) cookie is validated like any other. Sliding renewal is decided by the cookie handler before validation; it is deferred, and only a session that has just passed validation is re-issued, so a rejected session, a pre-WAS-37 cookie or a request whose validation could not complete never gets a renewed cookie.

Cost and timing: one primary-key read of `CentralAdminUsers` (active flag and stamp only) per authenticated Central Admin request. Nothing is cached, so a change applies from the next request.

Logs: `Central admin session rejected: {Reason}.` (Information; the reason is the state name, for example `AccountInactive` or `StampChanged`) and `Central admin session could not be validated against CentralDb ({ExceptionType}).` (Error). Neither contains the admin id, email, stamp or cookie.

Deployment: the `AddCentralAdminSecurityStamp` CentralDb migration gives every existing admin a new stamp. Cookies issued before it carry no stamp, so every central admin signs in again once after the release. Admin login reads the column, so run `migrate-central` before the new Web starts (see [../operations/deployment.md](../operations/deployment.md#release-order-migrate-every-database-first)). An admin row inserted directly with the column's all-zero default gets a new stamp on its first login; a session is never issued or accepted with the empty stamp.

Wasla.Api does not register the Central Admin scheme, so it neither accepts nor validates this cookie.

## Credential rate limiting

**Sources:** `src/Wasla.Infrastructure/Security/RateLimiting/` (options, validation, partitions, `Retry-After`, rejection log), `src/Wasla.Web/Security/AuthenticationRateLimit.cs` (Web filter), `src/Wasla.Api/Security/ApiAuthenticationRateLimiting.cs` (Api policy), `src/Wasla.Infrastructure/Security/TrustedProxies.cs` (client address). Added in WAS-90.

Every endpoint that accepts a password is limited:

| Endpoint | Policy | Partition | Default limit |
|----------|--------|-----------|---------------|
| Web `POST /auth/login` | `auth-tenant-login` | Resolved tenant + client network | 10 attempts per 5 minutes |
| Web `POST /admin/login` | `auth-central-admin-login` | Client network (on any host) | 5 attempts per 5 minutes |
| Api `POST /api/auth/validate` | `auth-api-credential-validation` | Resolved tenant + client network | 10 attempts per 5 minutes |

The same mechanism and configuration section also limit anonymous signup: `signup-submission` (`POST /signup`, 10 per hour per client network) and `signup-checkout` (the checkout POSTs that change a registration, 20 per 10 minutes). See [tenancy.md](tenancy.md#signup-abuse-limits). The public contact form uses it too: `contact-submission` (`POST /contact`, 5 per hour per client network, `AuthenticationRateLimiting:ContactSubmission`); over the limit the form is shown again with status 429 and `Contact.TooManyAttempts`, and nothing is stored or sent. Outside these policies: the forgot-password POST keeps its own earlier policy (`tenant-forgot-password`, 5 per 10 minutes per host and address); the reset-password POST (which needs a one-time emailed token) and Print Bridge device-token requests have no limit from this section.

**How it counts.** Each policy is a fixed window per partition with no queue. Every attempt counts: a success, a wrong password, an unknown email and an invalid form alike. When the window ends the partition starts again from zero; there is no account lockout and nothing is stored.

**Partitions.** Built only from server-side values:

- *Tenant:* the tenant `TenantResolutionMiddleware` resolved from the `Host` header, never a value from the body or query. Tenant A's attempts never use tenant B's allowance. A Web request that reaches `/auth/login` without a tenant shares one "no tenant" bucket per client network. On the Api an unknown host is answered `404` by tenant resolution before the limiter, so it never counts and never sees `429`.
- *Client network:* `HttpContext.Connection.RemoteIpAddress` after the trusted-proxy step ([deployment.md](../operations/deployment.md#reverse-proxy-and-client-address)). An `X-Forwarded-For` value is used only when the connecting peer is a configured trusted proxy. IPv4 and IPv4-mapped IPv6 (`::ffff:a.b.c.d`) count as the same client; IPv6 is grouped per `/64`; a request without an address shares one bucket.
- *Policy:* each policy has its own limiter, so an exhausted tenant login does not limit the Central Admin login or the Api, and the reverse.

Partition keys are never sent to the client or logged.

**Where it runs.**

- *Api:* the ASP.NET Core rate-limiting middleware, placed after tenant resolution and before Print Bridge device authentication, session revalidation and authorization. A rejected request reaches no controller, its JSON body is not read, and no tenant database is opened, even when it carries a session cookie.
- *Web:* an MVC action filter (`[AuthenticationRateLimit]`) on the two login POST actions. It runs after antiforgery validation and model binding and before the action, so a rejected attempt performs no database read and no password verification. A POST with a missing or invalid antiforgery token is still answered `400` first and does not use a permit.

**Responses over the limit.**

- *Api:* `429 Too Many Requests` with `Retry-After`, `Cache-Control: no-store` and `{"error":"too_many_requests"}` as `application/json`. No redirect, no `Location`, no cookie, no HTML. The API configures no CORS, and the `429` adds none.
- *Web:* `429` with `Retry-After` and `Cache-Control: no-store`, showing the same login page again with the localized `Auth.Login.TooManyAttempts` message (TR, EN, AR, RU) in a `role="alert"` box, so screen readers announce it. The page keeps the submitted email and the return URL, never echoes the password, and is the same for every email, so it reveals nothing about whether an account exists. The return URL is only followed after a successful sign-in, and only when it is a local page the new session may open ([Where a tenant user goes after signing in](#where-a-tenant-user-goes-after-signing-in)).
- *`Retry-After`:* whole seconds, rounded up, from the limiter's report of the time until the window resets. For a fixed window, ASP.NET Core reports the full window length, so the value is an upper bound.

Below the limit nothing changes: Web redirects after a successful login and shows the same page for invalid credentials; the Api answers `200`, `401` or `404`. The Api's `401`, `403` and `503` for other endpoints, session revalidation, Print Bridge device tokens, the login GET pages, `/` and the health endpoints are not rate limited.

`AuthValidationService` verifies an unknown email against a dummy hash computed once per process, not a new bcrypt hash per attempt, so each tenant attempt costs one verification.

**Configuration.** Section `AuthenticationRateLimiting`, also settable through environment variables such as `AuthenticationRateLimiting__TenantLogin__PermitLimit`:

```json
"AuthenticationRateLimiting": {
  "TenantLogin": { "PermitLimit": 10, "Window": "00:05:00" },
  "CentralAdminLogin": { "PermitLimit": 5, "Window": "00:05:00" },
  "ApiCredentialValidation": { "PermitLimit": 10, "Window": "00:05:00" }
}
```

These are the defaults in code; the committed appsettings do not repeat them. `PermitLimit` must be 1–10000 and `Window` between `00:00:01` and `1.00:00:00`. Web and Api refuse to start with any other value, so a typo cannot silently disable the limit. A change takes effect after a restart.

Why these defaults (no threshold was set in WAS-90): 10 attempts per 5 minutes allows at most 120 password guesses per hour per tenant and network. That still leaves room for several staff behind one restaurant router to mistype a password. Central Admin has few accounts and more power, so it gets 5.

**Limitations.**

- *Process-local.* Counters are in each process's memory. Web and Api count separately, a restart clears the counters, and with several instances of one process behind a load balancer each instance has its own allowance. Up to that many times the limit can then be used. Today Wasla runs one instance of each on one server; nothing here is a distributed limit.
- *Per network, not per account.* An attacker spread over many networks is not stopped by these limits, and there is no per-account backoff or lockout. A persistent per-account defence needs failure counters stored with each tenant user and Central Admin (a schema change and migration of every database) and a decision on the lockout trade-off (an attacker can lock out a known account); neither has been made, so it is not implemented.
- *Fixed window.* Up to twice the limit can pass in a short burst around a window boundary.
- *Reverse proxy.* Behind a proxy that is not listed in `TrustedProxies`, every client has the proxy's address, so all users of a tenant share one allowance. See [deployment.md](../operations/deployment.md#reverse-proxy-and-client-address).

**Monitoring:** see [observability.md](../operations/observability.md#authentication-rate-limit-rejections). **Rollback:** raise `PermitLimit` or shorten `Window` and restart, or deploy the previous release. A limit cannot be set to 0. Nothing is persisted, and no schema or data change is involved.

## Remember-me / lifetimes (confirmed in source)

Scheme defaults in Web `Program.cs`:

| Scheme | `ExpireTimeSpan` | Sliding |
|--------|------------------|---------|
| Tenant | 7 days | yes |
| Central admin | 8 hours | yes |

Remember-me persistence (`src/Wasla.Web/Security/AuthCookiePersistence.cs`):

| Audience | Remember-me off | Remember-me on |
|----------|-----------------|----------------|
| Tenant | session cookie (`IsPersistent = false`) | persistent **14 days** |
| Central admin | session cookie | persistent **1 day** |

API tenant cookie registration also sets `ExpireTimeSpan = 7 days` with sliding expiration (`ApiTenantAuthenticationExtensions`), but API `POST /api/auth/validate` does not sign in (see below).

## API differences

**Sources:**

- `src/Wasla.Api/Program.cs`, `src/Wasla.Api/WaslaApiPipeline.cs` (pipeline order and endpoint mapping)
- `src/Wasla.Api/Security/ApiTenantAuthenticationExtensions.cs` (cookie scheme, `ApiTenantCookieEvents`, policies)
- `src/Wasla.Api/Middleware/TenantSessionUnavailableMiddleware.cs`
- `src/Wasla.Api/Controllers/AuthController.cs`
- `src/Wasla.Api/Security/ApiDataProtection.cs` (Data Protection key ring)

Current API behavior:

- Registers the **tenant** cookie scheme only (`WaslaTenant` / `.Wasla.TenantAuth`), with the same cookie contract as Web, via `AddWaslaApiTenantAuthentication`
- Pipeline: tenant resolution from the host → rate limiter ([Credential rate limiting](#credential-rate-limiting); only `POST /api/auth/validate` has a policy) → Print Bridge device-token middleware → `TenantSessionUnavailableMiddleware` → authentication (session revalidation) → authorization
- Every request that presents the tenant cookie is revalidated exactly as on Web ([Tenant session revalidation](#tenant-session-revalidation))
- Authorization uses the shared tenant role policies; every tenant endpoint names one. The default and fallback policies admit only a current Owner, so an endpoint without a named policy, or with a bare `[Authorize]`, is Owner-only. The endpoint matrix is in [roles-and-permissions.md](../product/roles-and-permissions.md#api-endpoints)
- Status codes, never redirects: no session, or a rejected session → `401`; a current session without the required role → `403` (the session is kept); tenant database unreadable during validation → `503` with `{"error":"tenant_session_unavailable"}`. Responses for 401 and 403 have no body and no `Location`. A request to a route the API does not map is also answered by the Owner-only fallback (401 or 403) rather than 404
- `POST /api/auth/validate`: credentials check only (`AllowAnonymous`); returns `200`/`401`/`404`, and `429` over its rate limit ([Credential rate limiting](#credential-rate-limiting)); **does not** `SignInAsync` and does not record `LastLoginAt`, because no session is established
- `GET /api/auth/me`: any current tenant user with an assignable role (`AuthenticatedTenantUser`); returns the current user DTO from the validated claims
- `/api/print-bridge/*` authenticates with the `X-PrintBridge-Token` device token only (`PrintBridgeAuthMiddleware`); those routes skip tenant resolution, so a tenant cookie on them is neither used nor deleted

## When a Web session reaches the API

Web and API use the **same scheme, cookie name and cookie settings** (`WaslaTenant`, `.Wasla.TenantAuth`, host-only, path `/`, SameSite Lax, HttpOnly, 7-day sliding expiration) and the same Data Protection application name (`Wasla`). The cookie's ticket is protected for that scheme name, so **any** API process that has Web's Data Protection keys can read a Web session cookie. Whether it gets one depends on deployment:

- **Keys.** By default they are not shared: the API persists keys to `DataProtection:KeyPath`; when that is unset it uses `C:\Wasla-keys` on Windows and the framework's default key store on other operating systems (`ApiDataProtection`; no committed configuration sets the path). Web uses `DataProtection:KeyPath` only when it is set, otherwise the default store. An operator who points both at one folder shares the key ring. `docs/deployment-config.md` (non-canonical) recommends exactly that when Web and API must share protected payloads.
- **Host.** The cookie is host-only. A browser sends it to the API only when the API is served on the tenant's own host (for example behind a reverse proxy under the tenant domain). Anyone who has the cookie value can still present it with the tenant's `Host` header.

Since WAS-94 a shared key ring is safe for tenant sessions in the sense that matters: a Web session presented to the API gets exactly the Web checks (current user, active, stamp, role, tenant from the host) and the same role policies, so a deactivated, deleted, demoted or password-reset user's old cookie is rejected by the API as it is by Web, and a lower role cannot use an API write that its Web policy forbids. Sharing is still a deployment choice, not a requirement; in local development Web (`{slug}.app.wasla.local`) and API (`localhost`) are different hosts with different keys, so a Web login does **not** authenticate API calls there.

What sharing does not change: the Central Admin cookie is not registered by the API at all; Web revalidates it against CentralDb (WAS-37, [Central admin session revalidation](#central-admin-session-revalidation)). Provider HTTP clients and their cookie containers are unrelated to these sessions (WAS-95).

### Cross-site requests to the API

The API accepts a browser cookie and has state-changing endpoints, so it was checked for cross-site request forgery (WAS-94). With the current code, a cross-site or sibling-subdomain page cannot make a browser perform an authenticated API write:

- **SameSite Lax.** A different site's page cannot get the browser to attach the cookie to a cross-site `POST` or `PATCH`; Lax only sends it on top-level `GET` navigations, and no API `GET` changes state.
- **Same-site but cross-origin requests.** These come from another tenant's subdomain, or from a compromised sibling host, and SameSite does not stop them. They still cannot write:
  - The API configures no CORS, so it answers no preflight. A `PATCH` and a `POST` with `Content-Type: application/json` both need a preflight, so the browser never sends them.
  - The only requests a browser sends without a preflight are "simple" ones: a form-encoded, multipart or `text/plain` body. Every API write binds a JSON body (`[FromBody]`), so the API answers those with `415` before the action runs.
  - Responses are not readable cross-origin either, since there is no `Access-Control-Allow-Origin`.
- **Tests.** `ApiTenantSessionTests.OwnerSession_SimpleCrossSiteRequestBodies_CannotMutate` sends all five simple-body shapes with a valid Owner session and gets `415` with no change, and checks that a `PATCH` preflight gets no CORS headers.

This protection holds only while the API keeps these properties. Adding CORS with credentials, a write that accepts form or text bodies, or a `GET` that mutates would reopen the question. An antiforgery or custom-header requirement on cookie-authenticated writes would be defence in depth; it is not implemented.

## Long-lived live connections

**Source:** `src/Wasla.Web/Realtime/` (`TenantEventsHub`, `RealtimeSessionRevalidator`, `RealtimeOriginMiddleware`). Added in WAS-23; the channel itself is described in [realtime.md](realtime.md).

The live hub (`/hubs/tenant-events`) is one long request per browser tab, so the per-request check above would run only when it opens. Wasla.Web therefore checks it at connect and again while it is open:

- **Connect.** The negotiate and connect requests are normal Web requests: tenant resolution from the host, the tenant cookie (`WaslaTenant`) with its revalidation, and policy `CanReceiveLiveTenantUpdates` (the roles of `CanViewLiveScreen` and `CanManualPrint` together). A rejected or missing session is not connected; a central-admin cookie is not accepted. The groups joined come from the resolved tenant and the role, never from the client: `live-screen` for `CanViewLiveScreen`, `print-status` for `CanManualPrint`.
- **While open.** Every `Realtime:SessionRevalidationSeconds` (default 60, 1–600) each connection's principal is checked again with the same `ITenantSessionValidator` the cookie scheme uses, for the tenant the connection was resolved for, its role again against every stream the connection joined, and the connection's host is resolved again (the same `ITenantResolver` and 5-minute host cache HTTP requests use). The connection is closed when the tenant is no longer the active tenant of that host, the user was deleted or deactivated, their role, password or security stamp changed, or the role no longer admits it. When the tenant database cannot be read the connection is closed too (fail closed). One primary-key read of `AppUsers` per open connection per interval.
- **Expiry.** The hub closes a connection when its cookie's authentication ticket expires (`CloseOnAuthenticationExpiration`).
- **After a close** the browser reconnects through negotiate, where the cookie scheme decides again, so a revoked session gets no new connection; its next page request goes to sign-in.

So a revoked session can keep an already open connection for at most one interval. In that time it can receive only its own tenant's notifications of the streams it joined (order and job ids, a print status and versions); every state it then reads goes through `GET /orders/{id}/print-state` or `GET /orders/live-data`, which are revalidated per request like any page.

**Cross-site WebSockets.** Browsers do not apply CORS to WebSockets, and the tenant cookie is sent from any same-site page (for example another tenant's subdomain). Every request under `/hubs` that carries an `Origin` header for another host is refused with 403 before tenant resolution and authentication. Only the host (and the port when the `Host` header names one) is compared, because TLS ends at the proxy. The hub has no client-callable methods, so even an admitted connection cannot write anything.

**Internal channel.** Wasla.Worker and Wasla.Api reach the hub through a loopback-only listener authenticated by a shared secret (`Realtime:Internal`), not through any tenant or user session. See [realtime.md](realtime.md#the-internal-channel-worker-and-api-to-web).

## Cookie inventory

Wasla's own cookies (WAS-141). Nothing else is registered, read or deleted as an authentication cookie; `WaslaAuthCookieInventoryTests` checks the exact set.

| Cookie | Set by | Attributes |
|--------|--------|------------|
| `.Wasla.TenantAuth` | Web and API, scheme `WaslaTenant` | HttpOnly, SameSite Lax, Path `/`, host-only, 7-day sliding; Secure: API always, Web outside Development |
| `.Wasla.CentralAdminAuth` | Web, scheme `WaslaCentralAdmin` | HttpOnly, SameSite Lax, Path `/`, host-only, 8-hour sliding; Secure outside Development |
| `.Wasla.SignupRegistration` | Web, `SignupRegistrationOwnership` | HttpOnly, SameSite Strict, Path `/`, host-only, expires with the registration; Secure outside Development |

Sign-out deletes only that audience's active cookie: tenant logout deletes `.Wasla.TenantAuth`, Central Admin logout deletes `.Wasla.CentralAdminAuth`. The antiforgery, request-culture and TempData cookies keep their ASP.NET Core default names.

## Legacy property: `AuthSessionResult.CustomerId`

**Source:** `src/Wasla.Application/Abstractions/Auth/AuthSessionResult.cs`

The property is still named `CustomerId`. Sign-in stores that value in the **`TenantId` claim**. Do not treat a global rename of this property as a required follow-up for feature work.

## Password reset

### Web token flow

**Service:** `src/Wasla.Infrastructure/Services/TenantPasswordResetService.cs`

**UI:** tenant `AuthController` forgot/reset actions

Current implementation:

- Raw token length 32 bytes; SHA-256 hash stored; expiry **60 minutes**
- Emails reset link via `IEmailSender`
- Only an active user of the resolved tenant gets a token and an email. The page is the same confirmation (`Auth.ForgotPassword.ConfirmationDescription`, "if an active account exists for this email address…") for an active, inactive or unknown email, a user of another tenant, and a failed delivery: same status (200), body, headers and cookies (WAS-90). The service still distinguishes these outcomes for its logs. What it cannot hide is time: an eligible request does a token write and the email send before answering, which can take noticeably longer with SMTP
- Completing reset replaces the tenant `AppUser` password hash and security stamp, which ends that user's existing sessions
- The used token and other unused unexpired tokens for that user are marked used
- The Web action then redirects to the login page. It does not call `SignInAsync`

### CLI

**Command:** `reset-password` (`src/Wasla.Cli/Program.cs`, `src/Wasla.Cli/CliPasswordReset.cs`)

- `--scope central` or `--scope tenant`
- Tenant scope requires `--tenant` slug or id
- Interactive password entry; dry-run supported
- Central scope updates `CentralAdminUser.PasswordHash` and `SecurityStamp`, which ends that admin's existing Central Admin sessions. Central admins have no password-reset token table in this flow. The older `reset-central-admin-password` command replaces the stamp too
- Tenant scope updates the tenant user password hash and security stamp (ending that user's Web and API sessions) and marks unused unexpired `PasswordResetToken` rows for that user as used
- Opens the tenant DB by decrypting the central tenant connection string

### Sessions after a reset

Tenant sessions ([Tenant session revalidation](#tenant-session-revalidation)) and Central Admin sessions ([Central admin session revalidation](#central-admin-session-revalidation)) are both revalidated on every request, so a password reset of either kind ends the account's existing sessions on their next request.

### API testing

`POST /api/auth/validate` validates credentials only. Wasla.Api does not expose an endpoint that signs in a browser. Web login on `{slug}.app.wasla.local` does not authenticate `localhost` API requests. See [../operations/local-development.md](../operations/local-development.md).

## Authorization hook (high level)

Tenant policies are role-gated through `TenantRoleRequirement` / `TenantRoleAuthorizationHandler` (`src/Wasla.Infrastructure/Security/TenantRoleRequirement.cs`), used by Web and API alike. The handler sees only a session that has passed [revalidation](#tenant-session-revalidation) on this request, so the role it reads is the user's current role:

- Requires resolved `CurrentTenant`
- Requires claim `TenantId` matching that tenant
- Parses role from `ClaimTypes.Role` or `"Role"` (`TenantSessionClaims`, the same strict reader the validator uses): each at most once, equal when both are present, and exactly the issued name of an assignable role

Full Owner / Manager / Kitchen / Cashier / Viewer matrix: [roles-and-permissions.md](../product/roles-and-permissions.md).

## Related docs

- [tenancy.md](tenancy.md)
- [roles-and-permissions.md](../product/roles-and-permissions.md)
- [terminology.md](../product/terminology.md)
