namespace Wasla.Web.Security;

public static class TenantPolicies
{
    public const string TenantOwner = nameof(TenantOwner);
    public const string TenantManagerOrOwner = nameof(TenantManagerOrOwner);
    public const string CanManageTenantUsers = nameof(CanManageTenantUsers);
    public const string CanManageTenantSettings = nameof(CanManageTenantSettings);
    public const string CanManagePrintBridgeDevices = nameof(CanManagePrintBridgeDevices);
    public const string CanManageDeviceSecurity = nameof(CanManageDeviceSecurity);
    public const string CanViewOrders = nameof(CanViewOrders);
    public const string CanManageOrders = nameof(CanManageOrders);
    public const string CanManualPrint = nameof(CanManualPrint);
    public const string CanViewLiveScreen = nameof(CanViewLiveScreen);
    public const string CanViewReports = nameof(CanViewReports);
    public const string CanManageOrderAutomation = nameof(CanManageOrderAutomation);
    public const string CanManageOrderNotifications = nameof(CanManageOrderNotifications);
}
