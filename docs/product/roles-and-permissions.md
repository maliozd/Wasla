# Roles and permissions

## Purpose and scope

This document records the **server-side** tenant authorization matrix currently registered in Wasla.Web. It owns role → policy mappings and the user-management rules (acting Owner, own account, last Owner). It does not redefine cookie schemes (see [authentication.md](../architecture/authentication.md)).

UI may hide buttons; **hiding UI is not authorization**. Controllers and services enforce policies/handlers.

## Roles

**Source:** `src/Wasla.Domain/Enums/UserRole.cs`

| Role | Value | Status |
|------|-------|--------|
| Owner | 1 | Active |
| Manager | 2 | Active |
| Kitchen | 3 | Active |
| Cashier | 4 | Active |
| Viewer | 5 | Active |
| Staff | 100 | `[Obsolete]` — not assignable in current tenant user APIs; not included in any policy below |

Assignable roles for create/update (`TenantUserRoleService` / `TenantUsersController`): Owner, Manager, Kitchen, Cashier, Viewer only.

## How enforcement works

1. Policies and the roles each admits are defined once, for Web and API, in `WaslaTenantPolicies` (`src/Wasla.Application/Security/WaslaTenantPolicies.cs`). Web's `TenantPolicies` (`src/Wasla.Web/Security/TenantPolicies.cs`) only aliases those names
2. Web `Program.cs` and the API's `AddWaslaApiTenantAuthentication` both register every policy from that table with `AddWaslaTenantRolePolicies` (`src/Wasla.Infrastructure/Security/TenantRoleRequirement.cs`) as a `TenantRoleRequirement`
3. Before any policy runs, the tenant session is revalidated against the user's row in the tenant database, by Web and API alike: it must belong to this tenant, and the user must exist, be active, and still have the cookie's role and security stamp. Otherwise the request is anonymous. See [authentication.md](../architecture/authentication.md#tenant-session-revalidation)
4. `TenantRoleAuthorizationHandler` succeeds only when:
   - `ICurrentTenantService.CurrentTenant` is present
   - Claim `TenantId` parses and equals that tenant’s id
   - Role claim (`ClaimTypes.Role` or `"Role"`) is one of the policy’s allowed roles
5. Navigation visibility mirrors the same policies through `TenantNavigationAuthorizationService` (still via `IAuthorizationService`, not a second rule set)

## Policy matrix (Web and API registration)

**Source of truth:** `WaslaTenantPolicies.AllowedRoles` (`src/Wasla.Application/Security/WaslaTenantPolicies.cs`), registered by Web `Program.cs` and by Wasla.Api.

| Policy | Owner | Manager | Kitchen | Cashier | Viewer |
|--------|:-----:|:-------:|:-------:|:-------:|:------:|
| `TenantOwner` | ✓ | | | | |
| `TenantManagerOrOwner` | ✓ | ✓ | | | |
| `CanManageTenantUsers` | ✓ | | | | |
| `CanManageTenantSettings` | ✓ | | | | |
| `CanManagePrintBridgeDevices` | ✓ | | | | |
| `CanManageDeviceSecurity` | ✓ | | | | |
| `CanViewOrders` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `CanManageOrders` | ✓ | ✓ | ✓ | ✓ | |
| `CanManualPrint` | ✓ | ✓ | | ✓ | |
| `CanViewLiveScreen` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `CanViewReports` | ✓ | ✓ | | | ✓ |
| `ManagePlatformConnections` (named policy) | ✓ | | | | |
| `AuthenticatedTenantUser` (any assignable role; used by the API's `/api/auth/me`) | ✓ | ✓ | ✓ | ✓ | ✓ |

Obsolete `Staff` is not listed in any of these policies.

## Surface mapping (server-side)

These are the primary Web surfaces that apply the policies (not an exhaustive action list):

| Capability | Policy | Typical controllers / routes |
|------------|--------|------------------------------|
| View orders / history | `CanViewOrders` | `OrdersController` class-level |
| Mutate order lifecycle | `CanManageOrders` | Order action endpoints on `OrdersController` |
| Live Screen view / poll, including the read-only effective automation states | `CanViewLiveScreen` | `/orders/live-display`, `/orders/live-screen`, `/orders/live-data` |
| Manual print / reprint | `CanManualPrint` | Orders print actions; some Print Bridge job actions |
| Order sync / order automation settings | `TenantManagerOrOwner` | Orders sync/order-settings endpoints; `OrderSettingsController`; nav `CanManageOrderSettings` |
| Platform, receipt printer, account, and branch settings pages | `CanManageTenantSettings` | `PlatformConnectionsController`, `ReceiptPrinterSettingsController`, `AccountSettingsController`, `BranchesController` |
| Team / users | `CanManageTenantUsers` | `TenantUsersController` |
| Print Bridge devices | `CanManagePrintBridgeDevices` | Device management actions on `PrintBridgeController` |
| Print Bridge device security (tokens, etc.) | `CanManageDeviceSecurity` | Security-sensitive Print Bridge actions |
| Dashboard / reports | `CanViewReports` | `DashboardController` |
| Guided setup and order training (card, section panels, commands, practice order) | `TenantOwner` | `GuidedSetupController` and `GuidedDemoController` class-level; nav `CanUseGuidedSetup`. See [onboarding.md](onboarding.md) |

`PrintBridgeController` is authenticated under the tenant scheme; individual actions apply device vs security vs manual-print policies as above.

`BranchesController` is a settings page inside the current tenant. It is not a multi-location model. Extra restaurant locations are separate tenants. See [tenancy.md](../architecture/tenancy.md).

Central admin uses scheme `WaslaCentralAdmin` and is outside this tenant role matrix.

## User management rules: acting Owner, own account, last Owner

**Source:** `src/Wasla.Infrastructure/Services/TenantUserRoleService.cs`

**Outcomes:** `TenantUserRoleUpdateOutcome` in `ITenantUserRoleService`

Every create, edit, activate, deactivate, role change and removal is made on behalf of the signed-in user (`TenantUserActor`: user id and security stamp from the validated session) and runs in one serializable transaction that, before writing:

1. Re-reads the acting user: they must still exist, be active, be an Owner (the `CanManageTenantUsers` role) and have the session's security stamp. Otherwise nothing is written (`ActorNotAuthorized`; `TenantUsersController` answers with Forbid). This closes the gap between the request's session check and the write, for example an Owner demoted by another Owner while their own request is in flight.
2. Refuses a change to the acting user's own role or active state, and removing themselves (`SelfChangeNotAllowed`, localized as `TenantUsers.SelfChangeBlocked`). Another Owner must make that change. An Owner may still change their own name and password; a new password ends their current session.
3. Keeps the last-Owner checks: demoting, deactivating or removing the last active Owner is blocked (`LastOwnerWouldBeRemoved`, localized “last owner blocked”). Because every change is made by an active Owner who cannot change themselves, that Owner always remains; the explicit checks stay as a second guard.

A change to a user's role, active state or password replaces that user's security stamp, which ends their existing sessions on their next request.

Concurrency: on SQL Server, the serializable transaction's read locks keep two Owners who change each other at the same time from both succeeding. One waits for the other and then fails step 1, or is chosen as the deadlock victim and fails without writing (the generic error page). The automated tests run on SQLite, which serializes the two transactions; they show the re-check, not SQL Server locking.

## API endpoints

Wasla.Api uses the same policies as Web, registered from the same table, after the same session revalidation (WAS-94). Each endpoint applies the policy of the Web screen for the same operation:

| Endpoint | Access | Owner | Manager | Kitchen | Cashier | Viewer | Web equivalent |
|----------|--------|:-----:|:-------:|:-------:|:-------:|:------:|----------------|
| `GET /api/auth/me` | `AuthenticatedTenantUser` | ✓ | ✓ | ✓ | ✓ | ✓ | (own identity) |
| `GET /api/orders`, `GET /api/orders/{id}` | `CanViewOrders` | ✓ | ✓ | ✓ | ✓ | ✓ | `OrdersController` |
| `GET /api/dashboard/summary`, `GET /api/dashboard/today` | `CanViewReports` | ✓ | ✓ | | | ✓ | `DashboardController` |
| `GET /api/branches`, `POST /api/branches` | `CanManageTenantSettings` | ✓ | | | | | `BranchesController` |
| `GET /api/platform-connections`, `POST /api/platform-connections`, `PATCH /api/platform-connections/{id}/active` | `CanManageTenantSettings` | ✓ | | | | | `PlatformConnectionsController` |
| `POST /api/auth/validate` | Anonymous: credential check for API clients, issues no session | | | | | | |
| `/api/print-bridge/*` | Anonymous to the cookie scheme: authenticated by the `X-PrintBridge-Token` device token (`PrintBridgeAuthMiddleware`) | | | | | | Print Bridge device API |
| `GET /`, `/health/live`, `/health/ready` | Anonymous: service banner and probes | | | | | | |

- Obsolete `Staff` gets `403` everywhere.
- A request with no session, or a rejected one, gets `401`. A current session without the role gets `403`.
- `POST /api/branches` uses `IBranchService` and `CreateBranchCommandValidator` like Web, so an address is required.
- The API's default and fallback policies admit only a current Owner, so an endpoint added without a named policy is Owner-only.
- `ApiAuthorizationContractTests` reads every endpoint the API maps and fails when:
  - an endpoint has no decision above;
  - an `[Authorize]` names no policy;
  - an endpoint admits other roles than its Web policy.

The API has no endpoints for notification settings, product tours, operational mode, order lifecycle actions, receipt printers, users or account settings; those exist only on Web.

## Related docs

- [authentication.md](../architecture/authentication.md)
- [terminology.md](terminology.md)
