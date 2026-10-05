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
| `CentralAdminSecurityStampClaim` | `Wasla.CentralAdminSecurityStamp` |

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
- Central admin cookie: same cookie flags; separate login/logout/access-denied paths under `/admin`; `EventsType = CentralAdminCookieEvents` (see [Central Admin session revalidation](#central-admin-session-revalidation)). The tenant cookie has no events.
- **No cookie `Domain` is set** (host-only cookies)
- Data Protection: `SetApplicationName("Wasla")`; optional `DataProtection:KeyPath` from configuration (no API-style hardcoded OrderHub default in Web)
- Authorization policies registered here; handler: `TenantRoleAuthorizationHandler` (`src/Wasla.Web/Security/TenantRoleRequirement.cs`)

### Tenant login / logout

**Source:** `src/Wasla.Web/Areas/Tenant/Controllers/AuthController.cs`

- Routes under `/auth`
- Validates credentials via `IAuthValidationService` against the resolved tenant
- Signs in with scheme `WaslaTenant`
- Claims include `TenantId` (value from `AuthSessionResult.CustomerId`), `UserId`, email, role (`ClaimTypes.Role` and `"Role"`), name
- Logout: `SignOutAsync(WaslaTenant)` and expires active + legacy tenant cookies

### Central admin login / logout

**Source:** `src/Wasla.Web/Areas/Admin/Controllers/AuthController.cs`

- Routes under `/admin`
- Claims: name identifier (the `CentralAdminUser` id), email, display name, role `CentralAdmin`, and the account's security stamp (`Wasla.CentralAdminSecurityStamp`)
- **No `TenantId` claim**
- Logout expires active + several legacy central-admin cookie names

### Central admin session revalidation

**Sources:** `src/Wasla.Web/Security/CentralAdminCookieEvents.cs`, `src/Wasla.Infrastructure/Services/CentralAdminSessionValidator.cs`, `src/Wasla.Infrastructure/Persistence/Central/CentralDbContext.cs`

Every time the Central Admin cookie is authenticated (every request to an Admin page, and `/admin/login`), the session is checked against CentralDb. It is accepted only while all of these hold:

- The cookie has a parseable, non-empty account id and security stamp claim.
- The `CentralAdminUsers` row with that id exists.
- The row is active (`IsActive`).
- The row's `SecurityStamp` equals the stamp in the cookie.

Otherwise the principal is rejected and signed out of `WaslaCentralAdmin`. The response deletes the cookie and the request is challenged to `/admin/login`. The rejection is server-side, so a copy of the cookie kept elsewhere is rejected too. Logs record only the reason (`MissingOrMalformedClaims`, `AccountNotFound`, `AccountInactive`, `StampChanged`), never the cookie, stamp, email or password hash.

**What rotates the stamp.** `CentralDbContext.SaveChanges` / `SaveChangesAsync` gives an admin a new `SecurityStamp` whenever `PasswordHash` or `IsActive` changes on a tracked `CentralAdminUser`, in the same UPDATE statement as that change. Nothing else rotates it (see [Writes that bypass the rotation](#writes-that-bypass-the-rotation)). Signing in does not rotate it, so an admin may hold several sessions. One rotation revokes all of them and no other admin's.

**Managing accounts.** Change a central admin's password or status only through the CLI. All of these save through `CentralDbContext`:

| Change | Command | Effect on sessions |
|--------|---------|--------------------|
| Disable | `disable-central-admin --email <email> [--dry-run]` | All existing sessions are rejected on their next request |
| Enable (disabled account) | `enable-central-admin --email <email> [--dry-run]` | New stamp; no session issued before the disable comes back |
| Password | `reset-password --scope central` or `reset-central-admin-password` | All sessions issued before the reset are rejected |

The stamp rotates only when a command actually changes the status or the password. A disable or enable on an account already in that state writes nothing and does not rotate the stamp. In particular, `enable-central-admin` on an account that is already enabled revokes no session. See [cli.md](../operations/cli.md#central-admins).

#### Writes that bypass the rotation

The rotation runs only when `CentralDbContext.SaveChanges` / `SaveChangesAsync` saves a tracked `CentralAdminUser`. These writes never reach it:

- SQL run directly against CentralDb (scripts, SSMS, `sqlcmd`).
- EF bulk updates and raw SQL through EF: `ExecuteUpdate` / `ExecuteUpdateAsync`, `ExecuteSqlRaw` / `ExecuteSqlInterpolated`. No application code updates `CentralAdminUsers` this way today. Code that does in future must set `SecurityStamp` itself in the same call.

Without a new stamp, only the `IsActive` check still protects sessions. `IsActive = 0` blocks every session at once, but setting it back to `1` makes every cookie issued before the disable valid again. A `PasswordHash` change revokes nothing. **Any such write that changes `IsActive` or `PasswordHash` must set `SecurityStamp = NEWID()` in the same `UPDATE` statement**, for example:

```sql
UPDATE [CentralAdminUsers]
SET [IsActive] = 0, [SecurityStamp] = NEWID(), [UpdatedAt] = SYSUTCDATETIME()
WHERE [NormalizedEmail] = N'ADMIN@EXAMPLE.COM';
```

If such a write was made without a new stamp:

- **The account is still disabled.** Re-enable it with `enable-central-admin`, not with SQL. The disabled-to-enabled transition rotates the stamp, so cookies from before the disable stay rejected.
- **The account is already enabled again, or its password was changed by SQL.** Cookies issued before that write are valid again, and `enable-central-admin` does nothing for an enabled account. Rotate the stamp explicitly with `UPDATE [CentralAdminUsers] SET [SecurityStamp] = NEWID() WHERE [NormalizedEmail] = N'...'`. Alternatively, reset the password with the CLI, or run `disable-central-admin` and then `enable-central-admin`; the account is briefly disabled.

**Sliding expiration and remember-me.** Remember-me only makes the cookie persistent; it is validated like any other. The cookie handler decides on sliding renewal before validation, so `CentralAdminCookieEvents` defers it and renews only a session that has just been validated. A renewed cookie keeps the same stamp, so it is revoked by the same change as the original.

**Cookies issued before this check.** They carry no stamp claim and are rejected. Every central admin signs in once after the release that adds the `SecurityStamp` column.

**Deployment.** The `AddCentralAdminSecurityStamp` CentralDb migration adds `CentralAdminUsers.SecurityStamp` and gives every existing admin a distinct stamp. Run `migrate-central` before the new Web starts, as the [release order](../operations/deployment.md#release-order-migrate-every-database-first) requires for every CentralDb migration. A new Web against an unmigrated CentralDb cannot read the column. Every request that carries a Central Admin cookie fails closed with 503, and the sign-in form fails with the generic error page.

**Cost and failure.** One CentralDb query per authenticated Admin request, by primary key, selecting only `IsActive` and `SecurityStamp`. No tenant database is opened and nothing is cached, so a change takes effect on the session's next request.

**CentralDb unavailable.** If the account cannot be read, validation fails closed: `CentralAdminCookieEvents` logs the exception type only and throws `CentralAdminSessionUnavailableException`, which carries no SQL detail. In Production the global error page (`/error`) answers **503** for that exception, with the generic localized message and trace id and no Admin content; every other unhandled error stays 500. The cookie is neither renewed nor deleted, so the same session works again once CentralDb answers. This applies to every Admin page and to `/admin/login`. In Development the developer exception page is shown instead.

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
- `POST /api/auth/validate`: credentials check only (`AllowAnonymous`); returns `200`/`401`/`404`; **does not** `SignInAsync`
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
- Central scope updates `CentralAdminUser.PasswordHash`, which also rotates the admin's `SecurityStamp` and revokes their existing sessions (see [Central admin session revalidation](#central-admin-session-revalidation)). Central admins have no password-reset token table in this flow
- Tenant scope updates the tenant user password hash and marks unused unexpired `PasswordResetToken` rows for that user as used
- Opens the tenant DB by decrypting the central tenant connection string

### Current limitation

This applies to **tenant** sessions only. The tenant cookie has no security stamp or session-version check. A tenant password reset, on the Web or in the CLI, changes the stored credential and does not revoke already-issued `.Wasla.TenantAuth` cookies. A browser that already has one can keep using it until it expires or the user signs out.

Central Admin sessions are revalidated on every request and revoked by a password change or deactivation; see [Central admin session revalidation](#central-admin-session-revalidation).

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
