namespace OrderHub.Web.Models.Admin;

public sealed class CentralAdminDashboardViewModel
{
    public int TotalCustomers { get; set; }
    public int ActiveCustomers { get; set; }
    public int InactiveCustomers { get; set; }
    public IReadOnlyList<CentralAdminCustomerListItemViewModel> Customers { get; set; } =
        Array.Empty<CentralAdminCustomerListItemViewModel>();
}
