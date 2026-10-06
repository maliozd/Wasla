# Authentication

## Purpose and scope

This document describes Wasla cookie authentication for tenant operators and central admins: scheme/cookie contract, login/logout, password reset, API differences, and why Web and API browser sessions are not shared.

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
| `LegacyTenantCookieName` | `orderhub_auth` |
| `TenantIdClaim` | `TenantId` |

Web wrappers alias the same values:

- `src/Wasla.Web/Security/AuthSchemes.cs` → `AuthSchemes.Tenant` / `AuthSchemes.CentralAdmin`
- `src/Wasla.Web/Security/TenantAuthCookieNames.cs`
- `src/Wasla.Web/Security/CentralAdminAuthCookieNames.cs` (also lists additional legacy central-admin cookie names for cleanup)

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
- Tenant cookie: HttpOnly, Path `/`, SameSite Lax, SecurePolicy Development=`SameAsRequest` / Production=`Always`
- Central admin cookie: same cookie flags; separate login/logout/access-denied paths under `/admin`
- **No cookie `Domain` is set** (host-only cookies)
- Data Protection: `SetApplicationName("Wasla")`; optional `DataProtection:KeyPath` from configuration (no API-style hardcoded OrderHub default in Web)
- Authorization policies registered here; handler: `TenantRoleAuthorizationHandler` (`src/Wasla.Web/Security/TenantRoleRequirement.cs`)

### Tenant login / logout

**Source:** `src/Wasla.Web/Areas/Tenant/Controllers/AuthController.cs`

- Routes under `/auth`
- Validates credentials via `IAuthValidationService` against the resolved tenant
- Signs in with scheme `WaslaTenant`
- After `SignInAsync` succeeds, `ITenantLoginRecorder` (`TenantLoginRecorder`) stores the UTC time in `AppUsers.LastLoginAt` for that user in that tenant database. It uses one conditional `UPDATE` that only moves the value forward, so a slower concurrent login cannot overwrite a newer time, and it does not change `UpdatedAt`. If that database write fails, the login still succeeds and a warning with only the tenant id, user id and exception type is logged; cancellation and non-database errors are not swallowed. Failed or cancelled logins, the signup welcome link (`/auth/welcome`) and requests authenticated by an existing cookie do not record a login. `AuthValidationService` only checks credentials and reads only the columns it needs
- Claims include `TenantId` (value from `AuthSessionResult.CustomerId`), `UserId`, email, role (`ClaimTypes.Role` and `"Role"`), name
- Logout: `SignOutAsync(WaslaTenant)` and expires active + legacy tenant cookies

### Central admin login / logout

**Source:** `src/Wasla.Web/Areas/Admin/Controllers/AuthController.cs`

- Routes under `/admin`
- Claims: name identifier, email, display name, role `CentralAdmin`
- **No `TenantId` claim**
- Logout expires active + several legacy central-admin cookie names

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

- `src/Wasla.Api/Program.cs`
- `src/Wasla.Api/Security/ApiTenantAuthenticationExtensions.cs` (currently untracked working-tree)
- `src/Wasla.Api/Controllers/AuthController.cs`
- `src/Wasla.Api/Security/ExpireLegacyTenantAuthCookieMiddleware.cs` (untracked)

Current API behavior:

- Registers **tenant** cookie scheme only (`WaslaTenant` / `.Wasla.TenantAuth`) via `AddWaslaApiTenantAuthentication`
- Default authorization policy requires authenticated user **and** `TenantId` claim matching `ICurrentTenantService.CurrentTenant`
- Challenge/access-denied redirects become HTTP 401/403 (no MVC login redirect)
- `POST /api/auth/validate`: credentials check only (`AllowAnonymous`); returns `200`/`401`/`404`; **does not** `SignInAsync` and does not record `LastLoginAt`, because no session is established
- `GET /api/auth/me`: requires authorization; returns current user DTO from claims
- Middleware expires legacy `orderhub_auth` if present; that cookie is **not** an authentication scheme

API auth helpers and the validate/me behavior above include **currently uncommitted** working-tree changes. Documented behavior matches the files as read in the working tree.

## Why Web and API do not share a practical browser session

Web and API use the **same contract names** and compatible high-level cookie settings, but a browser login on Web does **not** authenticate API calls in local development:

1. Tenant Web host is typically `{slug}.wasla.local`; API is typically `localhost` — different hosts.
2. Cookies do not set a shared `Domain`, so they are host-scoped.
3. Data Protection key persistence differs:
   - API `appsettings.json` currently sets `DataProtection:KeyPath` to `C:\OrderHub-keys` (legacy naming/config debt; document the fact, do not “fix” in docs)
   - API code falls back to `C:\Wasla-keys` only if KeyPath is missing
   - Web uses optional `DataProtection:KeyPath` or default DP storage — **not** the same OrderHub path by default

Do **not** write or assume: “login once on Web and the browser is authenticated to API.”

## Legacy cookies

| Cookie / name | Status |
|---------------|--------|
| `orderhub_auth` | **Not** accepted for authentication. Expired/deleted on logout (Web) and by API `ExpireLegacyTenantAuthCookieMiddleware` when seen. Named in `WaslaAuthContracts.LegacyTenantCookieName`. |
| Central admin legacy names (`orderhub_central_admin`, `OrderHubCentralAdmin`, `.AspNetCore.OrderHubCentralAdmin`) | Cleanup on central-admin logout only; not active schemes |

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
- Completing reset replaces the tenant `AppUser` password hash
- The used token and other unused unexpired tokens for that user are marked used
- The Web action then redirects to the login page. It does not call `SignInAsync`

### CLI

**Command:** `reset-password` (`src/Wasla.Cli/Program.cs`, `src/Wasla.Cli/CliPasswordReset.cs`)

- `--scope central` or `--scope tenant`
- Tenant scope requires `--tenant` slug or id
- Interactive password entry; dry-run supported
- Central scope updates `CentralAdminUser.PasswordHash` only. Central admins have no password-reset token table in this flow
- Tenant scope updates the tenant user password hash and marks unused unexpired `PasswordResetToken` rows for that user as used
- Opens the tenant DB by decrypting the central tenant connection string

### Current limitation

There is no security stamp or session-version check on the auth cookies. Password reset, on the Web and in the CLI, changes the stored credential and does not revoke already-issued authentication cookies. A browser that already has `.Wasla.TenantAuth` or `.Wasla.CentralAdminAuth` can keep using that cookie until it expires or the user signs out.

Central Admin authorization is scheme-only (`[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]`): the account is not re-checked against CentralDb on each request, so a deactivated central admin also keeps access until the cookie expires. This is an open high-priority security follow-up; see [../operations/tenant-operations-center.md](../operations/tenant-operations-center.md#security-follow-up-high-priority-not-implemented).

### API testing

`POST /api/auth/validate` validates credentials only. Wasla.Api does not expose an endpoint that signs in a browser. Web login on `{slug}.wasla.local` does not authenticate `localhost` API requests. See [../operations/local-development.md](../operations/local-development.md).

## Authorization hook (high level)

Tenant policies are role-gated through `TenantRoleRequirement` / `TenantRoleAuthorizationHandler`:

- Requires resolved `CurrentTenant`
- Requires claim `TenantId` matching that tenant
- Parses role from `ClaimTypes.Role` or `"Role"`

Full Owner / Manager / Kitchen / Cashier / Viewer matrix: [roles-and-permissions.md](../product/roles-and-permissions.md).

## Related docs

- [tenancy.md](tenancy.md)
- [roles-and-permissions.md](../product/roles-and-permissions.md)
- [terminology.md](../product/terminology.md)
