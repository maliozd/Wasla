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

    /// <summary>The CentralDb plan price checkout would charge; null when none applies yet.</summary>
    public decimal? PriceTry { get; set; }

    public bool PriceVatIncluded { get; set; }

    public bool IsYearlyBilling { get; set; }

    public PendingRegistrationStatus Status { get; set; }

    /// <summary>
    /// True only where the payment simulator exists (<see cref="Security.CheckoutPaymentSimulator"/>). It selects
    /// the copy; <see cref="CanSimulatePayment"/> also depends on the registration's status.
    /// </summary>
    public bool IsPaymentSimulatorAvailable { get; set; }

    public bool CanSimulatePayment { get; set; }

    public bool CanCancel { get; set; }

    public string? StatusNoticeKey { get; set; }
}
