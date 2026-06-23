using Wasla.Application.Abstractions.Admin;

namespace Wasla.Web.Areas.Admin.Models;

public sealed class CentralAdminDashboardViewModel
{
    public int TotalCustomers { get; set; }
    public int ActiveCustomers { get; set; }
    public int InactiveCustomers { get; set; }
    public IReadOnlyList<CentralAdminTenantListItemViewModel> Customers { get; set; } =
        Array.Empty<CentralAdminTenantListItemViewModel>();
    public IReadOnlyList<CentralAdminTenantListItemViewModel> RecentCustomers { get; set; } =
        Array.Empty<CentralAdminTenantListItemViewModel>();

    // Pending registration counts
    public int RegPendingPayment { get; set; }
    public int RegPaymentReceivedSetupPending { get; set; }
    public int RegProvisioned { get; set; }

    // Registrations needing attention (PaymentSucceeded, not yet provisioned)
    public IReadOnlyList<AdminPendingRegistrationListItemDto> AttentionRegistrations { get; set; } =
        Array.Empty<AdminPendingRegistrationListItemDto>();
}

