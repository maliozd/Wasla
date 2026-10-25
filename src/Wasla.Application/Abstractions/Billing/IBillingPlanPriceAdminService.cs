namespace Wasla.Application.Abstractions.Billing;

/// <summary>
/// Central Admin management of Wasla's plan prices (WAS-147). An admin schedules a new price for a plan and billing
/// period from a Türkiye date, withdraws one that has not taken effect yet, or corrects one that nothing references yet
/// (GitHub #64). A referenced price never changes; it is superseded by scheduling a new one. Checkout charges only the
/// price these rows put in effect; nothing seeds them.
/// </summary>
public interface IBillingPlanPriceAdminService
{
    Task<BillingPlanPriceOverview> GetOverviewAsync(CancellationToken ct);

    /// <summary>One price with its references and correction history; null when it does not exist.</summary>
    Task<BillingPlanPriceDetail?> GetAsync(Guid planPriceId, CancellationToken ct);

    /// <summary>
    /// Corrects an unreferenced price in place with the validation of <see cref="ScheduleAsync"/>, and records the
    /// previous and new values with the acting admin. Refused without any change when something references the price,
    /// when it changed since <see cref="BillingPlanPriceCorrectionCommand.ExpectedRevision"/>, or when it is gone; the
    /// check and the write are one locked transaction, so a reference that appears in between makes it refuse.
    /// </summary>
    Task<BillingPlanPriceCorrectionResult> CorrectAsync(
        BillingPlanPriceCorrectionCommand command,
        Guid centralAdminUserId,
        CancellationToken ct);

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

/// <summary>
/// The correction form as submitted: the price row and the revision the form was loaded with, and the new amount (whole
/// kuruş), VAT choice and Türkiye date exactly as typed. Plan, period and currency stay the row's.
/// </summary>
public sealed record BillingPlanPriceCorrectionCommand(
    Guid PlanPriceId,
    int ExpectedRevision,
    string? AmountKurus,
    bool? VatIncluded,
    string? EffectiveDate);

public sealed record BillingPlanPriceCorrectionResult(
    BillingPlanPriceCorrectionOutcome Outcome,
    IReadOnlyList<BillingPlanPriceValidationError> Errors,
    int ReferenceCount)
{
    public static BillingPlanPriceCorrectionResult Of(BillingPlanPriceCorrectionOutcome outcome, int referenceCount = 0) =>
        new(outcome, [], referenceCount);
}

public enum BillingPlanPriceCorrectionOutcome
{
    Corrected = 0,

    /// <summary>The submitted values are the stored ones; nothing was written or recorded.</summary>
    Unchanged = 1,

    /// <summary>See <see cref="BillingPlanPriceCorrectionResult.Errors"/>; nothing was written.</summary>
    Invalid = 2,
    NotFound = 3,

    /// <summary>Something references the price (<see cref="BillingPlanPriceCorrectionResult.ReferenceCount"/>): supersede it.</summary>
    Referenced = 4,

    /// <summary>Another admin corrected the price after the form was loaded.</summary>
    Stale = 5
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

/// <summary>
/// One price. <see cref="ReferenceCount"/> is how many records refer to it (today: subscriptions started from it); a
/// referenced price cannot be corrected, only superseded.
/// </summary>
public sealed record BillingPlanPriceRow(
    Guid Id,
    string PlanCode,
    string BillingPeriod,
    long AmountKurus,
    bool VatIncluded,
    DateTime EffectiveFromUtc,
    DateOnly EffectiveDateInTurkey,
    DateTime CreatedAtUtc,
    BillingPlanPriceState State,
    int Revision,
    int ReferenceCount)
{
    public bool IsReferenced => ReferenceCount > 0;
}

/// <summary>
/// One price for the correction page. <see cref="SupersedeEffectiveDate"/> is the earliest Türkiye date from today on
/// with no price yet for the same plan and period: the date a superseding price is prefilled with.
/// </summary>
public sealed record BillingPlanPriceDetail(
    BillingPlanPriceRow Price,
    string Currency,
    IReadOnlyList<BillingPlanPriceCorrectionEntry> Corrections,
    DateOnly TodayInTurkey,
    DateOnly SupersedeEffectiveDate);

/// <summary>One recorded correction, newest first. The admin's display name is null when that admin no longer exists.</summary>
public sealed record BillingPlanPriceCorrectionEntry(
    DateTime CorrectedAtUtc,
    Guid CorrectedByCentralAdminUserId,
    string? CorrectedByDisplayName,
    long PreviousAmountKurus,
    string PreviousCurrency,
    bool PreviousVatIncluded,
    DateOnly PreviousEffectiveDateInTurkey,
    long NewAmountKurus,
    string NewCurrency,
    bool NewVatIncluded,
    DateOnly NewEffectiveDateInTurkey);

public enum BillingPlanPriceState
{
    InEffect = 0,
    Scheduled = 1,
    Superseded = 2
}
