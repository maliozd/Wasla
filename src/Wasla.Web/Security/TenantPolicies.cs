using Wasla.Application.Security;

namespace Wasla.Web.Security;

/// <summary>
/// Web names for the shared tenant policies. The roles each admits are defined once, in
/// <see cref="WaslaTenantPolicies.AllowedRoles"/>, for Web and Api.
/// </summary>
public static class TenantPolicies
{
    public const string TenantOwner = WaslaTenantPolicies.TenantOwner;
    public const string TenantManagerOrOwner = WaslaTenantPolicies.TenantManagerOrOwner;
    public const string CanManageTenantUsers = WaslaTenantPolicies.CanManageTenantUsers;
    public const string CanManageTenantSettings = WaslaTenantPolicies.CanManageTenantSettings;
    public const string CanManagePrintBridgeDevices = WaslaTenantPolicies.CanManagePrintBridgeDevices;
    public const string CanManageDeviceSecurity = WaslaTenantPolicies.CanManageDeviceSecurity;
    public const string CanViewOrders = WaslaTenantPolicies.CanViewOrders;
    public const string CanManageOrders = WaslaTenantPolicies.CanManageOrders;
    public const string CanManualPrint = WaslaTenantPolicies.CanManualPrint;
    public const string CanViewLiveScreen = WaslaTenantPolicies.CanViewLiveScreen;
    public const string CanViewReports = WaslaTenantPolicies.CanViewReports;
}
