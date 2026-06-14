using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Checkout;

public sealed class CheckoutReviewViewModel
{
    public Guid RegistrationId { get; set; }

    public string PlanCode { get; set; } = string.Empty;

    public string PlanDisplayName { get; set; } = string.Empty;

    public string BillingPeriod { get; set; } = string.Empty;

    public string BusinessName { get; set; } = string.Empty;

    public string BusinessTypesDisplay { get; set; } = string.Empty;

    public string PrimaryDomain { get; set; } = string.Empty;

    public string BusinessPhone { get; set; } = string.Empty;

    public string OwnerFullName { get; set; } = string.Empty;

    public string OwnerEmail { get; set; } = string.Empty;

    public string? OwnerPhone { get; set; }

    public string AddressSummary { get; set; } = string.Empty;

    public decimal MonthlyPriceTry { get; set; }

    public decimal TotalPriceTry { get; set; }

    public bool IsYearlyBilling { get; set; }

    public PendingRegistrationStatus Status { get; set; }

    public bool CanSimulatePayment { get; set; }

    public bool CanCancel { get; set; }

    public string? StatusNoticeKey { get; set; }
}
