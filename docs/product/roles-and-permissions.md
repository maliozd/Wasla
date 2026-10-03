# Roles and permissions

## Purpose and scope

This document records the **server-side** tenant authorization matrix currently registered in Wasla.Web. It owns role → policy mappings and last-owner protection. It does not redefine cookie schemes (see [authentication.md](../architecture/authentication.md)).

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

1. Policies are named in `src/Wasla.Web/Security/TenantPolicies.cs`
2. Web `Program.cs` maps each policy to allowed `UserRole` values via `AddTenantRolePolicy` / `TenantRoleRequirement`
3. `TenantRoleAuthorizationHandler` succeeds only when:
   - `ICurrentTenantService.CurrentTenant` is present
   - Claim `TenantId` parses and equals that tenant’s id
   - Role claim (`ClaimTypes.Role` or `"Role"`) is one of the policy’s allowed roles
4. Navigation visibility mirrors the same policies through `TenantNavigationAuthorizationService` (still via `IAuthorizationService`, not a second rule set)

## Policy matrix (current Web registration)

**Source of truth:** `src/Wasla.Web/Program.cs` authorization block.

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

## Last-owner protection

**Source:** `src/Wasla.Infrastructure/Services/TenantUserRoleService.cs`

**Outcomes:** `TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved` in `ITenantUserRoleService`

Current implementation blocks operations that would leave the tenant with no other **active** Owner when:

- Demoting the last active Owner to a non-Owner role
- Deactivating the last active Owner
- Removing the last active Owner

Controllers surface this as a localized “last owner blocked” style failure (`TenantUsersController`).

## API note

Wasla.Api’s default tenant authorization currently enforces authenticated tenant-scheme identity and matching `TenantId` claim (`ApiTenantClaimRequirement`). It does **not** re-host the full Web role policy matrix documented above. Do not assume API endpoints inherit every Web policy unless the endpoint code adds equivalent checks.

## Related docs

- [authentication.md](../architecture/authentication.md)
- [terminology.md](terminology.md)
