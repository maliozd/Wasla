using Wasla.Domain.Enums;

namespace Wasla.Application.Security;

/// <summary>
/// Tenant authorization policies and the roles each admits, shared by Wasla.Web and Wasla.Api. Both register their
/// policies from <see cref="AllowedRoles"/>, so the same operation admits the same roles in both. Obsolete
/// <see cref="UserRole.Staff"/> is in no policy.
/// </summary>
public static class WaslaTenantPolicies
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
    public const string ManagePlatformConnections = nameof(ManagePlatformConnections);

    /// <summary>Any current tenant user with an assignable role, whatever that role is.</summary>
    public const string AuthenticatedTenantUser = nameof(AuthenticatedTenantUser);

    public static IReadOnlyDictionary<string, IReadOnlyList<UserRole>> AllowedRoles { get; } =
        new Dictionary<string, IReadOnlyList<UserRole>>(StringComparer.Ordinal)
        {
            [TenantOwner] = [UserRole.Owner],
            [TenantManagerOrOwner] = [UserRole.Owner, UserRole.Manager],
            [CanManageTenantUsers] = [UserRole.Owner],
            [CanManageTenantSettings] = [UserRole.Owner],
            [CanManagePrintBridgeDevices] = [UserRole.Owner],
            [CanManageDeviceSecurity] = [UserRole.Owner],
            [CanViewOrders] = [UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer],
            [CanManageOrders] = [UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier],
            [CanManualPrint] = [UserRole.Owner, UserRole.Manager, UserRole.Cashier],
            [CanViewLiveScreen] = [UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer],
            [CanViewReports] = [UserRole.Owner, UserRole.Manager, UserRole.Viewer],
            [ManagePlatformConnections] = [UserRole.Owner],
            [AuthenticatedTenantUser] = [UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer]
        };
}
