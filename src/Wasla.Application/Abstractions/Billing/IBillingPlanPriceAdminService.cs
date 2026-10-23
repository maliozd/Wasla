namespace Wasla.Application.Abstractions.Billing;

/// <summary>
/// Central Admin management of Wasla's plan prices (WAS-147). Prices are never edited: an admin schedules a new price
/// for a plan and billing period from a Türkiye date, or withdraws one that has not taken effect yet. Checkout charges
/// only the price these rows put in effect; nothing seeds them.
/// </summary>
public interface IBillingPlanPriceAdminService
{
    Task<BillingPlanPriceOverview> GetOverviewAsync(CancellationToken ct);

    Task<BillingPlanPriceScheduleResult> ScheduleAsync(
        BillingPlanPriceScheduleCommand command,
        Guid centralAdminUserId,
        CancellationToken ct);

    /// <summary>Removes a price that has not taken effect yet; a price in effect or replaced is never removed.</summary>
    Task<BillingPlanPriceWithdrawOutcome> WithdrawAsync(Guid planPriceId, Guid centralAdminUserId, CancellationToken ct);
}

/// <summary>
/// The form values as submitted: the amount as a whole number of kuruş and the date as <c>yyyy-MM-dd</c>, unparsed,
/// so every invalid input gets its own error. <see cref="VatIncluded"/> is null when no choice was made.
/// </summary>
public sealed record BillingPlanPriceScheduleCommand(
    string? PlanCode,
    string? BillingPeriod,
    string? AmountKurus,
    bool? VatIncluded,
    string? EffectiveDate);

public sealed record BillingPlanPriceScheduleResult(
    Guid? PlanPriceId,
    IReadOnlyList<BillingPlanPriceValidationError> Errors)
{
    public bool Succeeded => PlanPriceId is not null;
}

public sealed record BillingPlanPriceValidationError(BillingPlanPriceField Field, BillingPlanPriceError Error);

public enum BillingPlanPriceField
{
    PlanCode = 0,
    BillingPeriod = 1,
    AmountKurus = 2,
    VatIncluded = 3,
    EffectiveDate = 4
}

public enum BillingPlanPriceError
{
    /// <summary>Missing, unknown, or a contact-sales plan that cannot be bought online.</summary>
    PlanInvalid = 0,
    PeriodInvalid = 1,
    AmountRequired = 2,

    /// <summary>Not a whole number of kuruş (dots, commas, signs, letters or non-ASCII digits).</summary>
    AmountInvalid = 3,
    AmountOutOfRange = 4,
    VatRequired = 5,
    DateRequired = 6,
    DateInvalid = 7,
    DateInPast = 8,

    /// <summary>A price for the same plan and period already takes effect on that date.</summary>
    DuplicateEffectiveDate = 9
}

public enum BillingPlanPriceWithdrawOutcome
{
    Withdrawn = 0,
    NotFound = 1,
    AlreadyInEffect = 2
}

/// <summary>Every self-service plan and period with the price in effect and the next scheduled one, and every price.</summary>
public sealed record BillingPlanPriceOverview(
    IReadOnlyList<BillingPlanPriceSlot> Slots,
    IReadOnlyList<BillingPlanPriceRow> Prices,
    DateOnly TodayInTurkey);

/// <summary>One plan and billing period. Without <see cref="InEffect"/> no checkout can start for it.</summary>
public sealed record BillingPlanPriceSlot(
    string PlanCode,
    string BillingPeriod,
    BillingPlanPriceRow? InEffect,
    BillingPlanPriceRow? NextScheduled);

public sealed record BillingPlanPriceRow(
    Guid Id,
    string PlanCode,
    string BillingPeriod,
    long AmountKurus,
    bool VatIncluded,
    DateTime EffectiveFromUtc,
    DateOnly EffectiveDateInTurkey,
    DateTime CreatedAtUtc,
    BillingPlanPriceState State);

public enum BillingPlanPriceState
{
    InEffect = 0,
    Scheduled = 1,
    Superseded = 2
}
