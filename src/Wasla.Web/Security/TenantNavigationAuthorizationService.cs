using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Wasla.Web.Security;

public interface ITenantNavigationAuthorizationService
{
    Task<TenantNavigationPermissions> GetPermissionsAsync(ClaimsPrincipal user);
}

public sealed record TenantNavigationPermissions(
    bool CanViewReports,
    bool CanViewOrders,
    bool CanManageOrders,
    bool CanViewLiveScreen,
    bool CanManualPrint,
    bool CanManageTenantUsers,
    bool CanManageTenantSettings,
    bool CanManagePrintBridgeDevices,
    bool CanManageDeviceSecurity,
    bool CanManageOrderSettings,
    bool CanManageOrderNotifications)
{
    /// <summary>
    /// Order Settings page access (notifications and/or automation sections).
    /// </summary>
    public bool CanViewOrderSettingsPage => CanManageOrderSettings || CanManageOrderNotifications;

    public bool CanViewAnyPrintBridgeLink => CanManagePrintBridgeDevices || CanManageDeviceSecurity;

    public bool CanViewAnySettingsLink =>
        CanViewOrderSettingsPage ||
        CanManageTenantSettings ||
        CanManageTenantUsers;
}

public sealed class TenantNavigationAuthorizationService : ITenantNavigationAuthorizationService
{
    private readonly IAuthorizationService _authorization;

    public TenantNavigationAuthorizationService(IAuthorizationService authorization)
    {
        _authorization = authorization;
    }

    public async Task<TenantNavigationPermissions> GetPermissionsAsync(ClaimsPrincipal user)
    {
        var canViewReports = await IsAuthorizedAsync(user, TenantPolicies.CanViewReports).ConfigureAwait(false);
        var canViewOrders = await IsAuthorizedAsync(user, TenantPolicies.CanViewOrders).ConfigureAwait(false);
        var canManageOrders = await IsAuthorizedAsync(user, TenantPolicies.CanManageOrders).ConfigureAwait(false);
        var canViewLiveScreen = await IsAuthorizedAsync(user, TenantPolicies.CanViewLiveScreen).ConfigureAwait(false);
        var canManualPrint = await IsAuthorizedAsync(user, TenantPolicies.CanManualPrint).ConfigureAwait(false);
        var canManageTenantUsers = await IsAuthorizedAsync(user, TenantPolicies.CanManageTenantUsers).ConfigureAwait(false);
        var canManageTenantSettings = await IsAuthorizedAsync(user, TenantPolicies.CanManageTenantSettings).ConfigureAwait(false);
        var canManagePrintBridgeDevices = await IsAuthorizedAsync(user, TenantPolicies.CanManagePrintBridgeDevices).ConfigureAwait(false);
        var canManageDeviceSecurity = await IsAuthorizedAsync(user, TenantPolicies.CanManageDeviceSecurity).ConfigureAwait(false);
        var canManageOrderSettings = await IsAuthorizedAsync(user, TenantPolicies.CanManageOrderAutomation).ConfigureAwait(false);
        var canManageOrderNotifications = await IsAuthorizedAsync(user, TenantPolicies.CanManageOrderNotifications).ConfigureAwait(false);

        return new TenantNavigationPermissions(
            canViewReports,
            canViewOrders,
            canManageOrders,
            canViewLiveScreen,
            canManualPrint,
            canManageTenantUsers,
            canManageTenantSettings,
            canManagePrintBridgeDevices,
            canManageDeviceSecurity,
            canManageOrderSettings,
            canManageOrderNotifications);
    }

    private async Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string policy) =>
        (await _authorization.AuthorizeAsync(user, resource: null, policyName: policy).ConfigureAwait(false)).Succeeded;
}
