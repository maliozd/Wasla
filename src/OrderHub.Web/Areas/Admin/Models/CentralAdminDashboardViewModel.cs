namespace OrderHub.Web.Areas.Admin.Models;

public sealed class CentralAdminDashboardViewModel
{
    public int TotalCustomers { get; set; }
    public int ActiveCustomers { get; set; }
    public int InactiveCustomers { get; set; }
    public IReadOnlyList<CentralAdminTenantListItemViewModel> Customers { get; set; } =
        Array.Empty<CentralAdminTenantListItemViewModel>();
}

