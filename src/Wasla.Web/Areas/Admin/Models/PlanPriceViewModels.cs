using Wasla.Application.Abstractions.Billing;
using Wasla.Web.Areas.Admin.Models.TenantOperations;

namespace Wasla.Web.Areas.Admin.Models;

public sealed class AdminPlanPricesViewModel
{
    public required BillingPlanPriceOverview Overview { get; init; }

    /// <summary>The plans that can be bought online, with their localized names, in catalog order.</summary>
    public required IReadOnlyList<AdminPlanOption> Plans { get; init; }

    public AdminPlanPriceForm Form { get; init; } = new();

    /// <summary>Localized messages for the fields the last submission got wrong.</summary>
    public IReadOnlyDictionary<BillingPlanPriceField, string> Errors { get; init; } =
        new Dictionary<BillingPlanPriceField, string>();

    public string? SuccessMessage { get; init; }

    public string? ErrorMessage { get; init; }

    public string PlanName(string planCode) =>
        Plans.FirstOrDefault(p => p.Code == planCode)?.DisplayName ?? planCode;
}

public sealed record AdminPlanOption(string Code, string DisplayName);

/// <summary>The schedule form exactly as posted; the Application service validates and converts it.</summary>
public sealed class AdminPlanPriceForm
{
    public string? PlanCode { get; set; }

    public string? BillingPeriod { get; set; }

    public string? AmountKurus { get; set; }

    /// <summary><c>true</c>, <c>false</c>, or nothing when no choice was made.</summary>
    public string? VatIncluded { get; set; }

    /// <summary><c>yyyy-MM-dd</c> from the date input, in every UI culture.</summary>
    public string? EffectiveDate { get; set; }
}

/// <summary>Maps plan price states and errors to resource keys and badges. Presentation only; no rule lives here.</summary>
public static class AdminPlanPricePresenter
{
    public static AdminStatus State(BillingPlanPriceState state) => state switch
    {
        BillingPlanPriceState.InEffect => new(AdminStatusTone.Success, "Admin.PlanPrices.Status.InEffect", "bi-check-circle"),
        BillingPlanPriceState.Scheduled => new(AdminStatusTone.Info, "Admin.PlanPrices.Status.Scheduled", "bi-calendar-event"),
        _ => new(AdminStatusTone.Neutral, "Admin.PlanPrices.Status.Superseded", "bi-dash-circle")
    };

    public static AdminStatus NoPrice { get; } =
        new(AdminStatusTone.Danger, "Admin.PlanPrices.NoPrice", "bi-x-octagon");

    public static string ErrorKey(BillingPlanPriceError error) => error switch
    {
        BillingPlanPriceError.PlanInvalid => "Admin.PlanPrices.Error.Plan",
        BillingPlanPriceError.PeriodInvalid => "Admin.PlanPrices.Error.Period",
        BillingPlanPriceError.AmountRequired => "Admin.PlanPrices.Error.AmountRequired",
        BillingPlanPriceError.AmountInvalid => "Admin.PlanPrices.Error.AmountInvalid",
        BillingPlanPriceError.AmountOutOfRange => "Admin.PlanPrices.Error.AmountRange",
        BillingPlanPriceError.VatRequired => "Admin.PlanPrices.Error.VatRequired",
        BillingPlanPriceError.DateRequired => "Admin.PlanPrices.Error.DateRequired",
        BillingPlanPriceError.DateInvalid => "Admin.PlanPrices.Error.DateInvalid",
        BillingPlanPriceError.DateInPast => "Admin.PlanPrices.Error.DatePast",
        BillingPlanPriceError.DuplicateEffectiveDate => "Admin.PlanPrices.Error.Duplicate",
        _ => "Admin.PlanPrices.Error.Summary"
    };

    public static string PeriodKey(string billingPeriod) =>
        billingPeriod == "Yearly" ? "Signup.BillingYearly" : "Signup.BillingMonthly";
}
