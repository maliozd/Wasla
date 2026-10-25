using System.Globalization;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Time;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;

namespace Wasla.Application.Billing;

/// <summary>
/// Validates a scheduled plan price from the Central Admin form (WAS-147) and converts it: a whole number of kuruş to a
/// TRY amount, and a Türkiye calendar date to the UTC instant of 00:00 Türkiye time on that date. A correction of an
/// unreferenced price (GitHub #64) uses the same rules; it may keep the date the price already has even when that date
/// is past, but it cannot move a price to another past date.
/// </summary>
public static class BillingPlanPriceInput
{
    public const string Currency = "TRY";

    /// <summary>An input guard against typing errors (10,000,000 TRY per period), not a commercial limit.</summary>
    public const long MaxAmountKurus = 1_000_000_000;

    private const int MaxAmountDigits = 10;

    public static IReadOnlyList<BillingPlanPriceValidationError> Validate(
        BillingPlanPriceScheduleCommand command,
        IReadOnlyCollection<string> selfServicePlanCodes,
        DateOnly todayInTurkey,
        out ValidBillingPlanPrice? valid,
        DateOnly? keptEffectiveDate = null)
    {
        var errors = new List<BillingPlanPriceValidationError>();
        valid = null;

        var planCode = command.PlanCode?.Trim();
        if (string.IsNullOrEmpty(planCode) || !selfServicePlanCodes.Contains(planCode, StringComparer.Ordinal))
            errors.Add(new(BillingPlanPriceField.PlanCode, BillingPlanPriceError.PlanInvalid));

        var period = command.BillingPeriod?.Trim();
        if (period is not (BillingSubscription.MonthlyPeriod or BillingSubscription.YearlyPeriod))
            errors.Add(new(BillingPlanPriceField.BillingPeriod, BillingPlanPriceError.PeriodInvalid));

        long amountKurus = 0;
        var amountText = command.AmountKurus?.Trim();
        if (string.IsNullOrEmpty(amountText))
            errors.Add(new(BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountRequired));
        else if (amountText.Any(c => c is < '0' or > '9'))
            errors.Add(new(BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountInvalid));
        else if (amountText.TrimStart('0').Length > MaxAmountDigits
                 || (amountKurus = long.Parse(amountText, NumberStyles.None, CultureInfo.InvariantCulture)) is < 1 or > MaxAmountKurus)
            errors.Add(new(BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountOutOfRange));

        if (command.VatIncluded is null)
            errors.Add(new(BillingPlanPriceField.VatIncluded, BillingPlanPriceError.VatRequired));

        DateOnly effectiveDate = default;
        var dateText = command.EffectiveDate?.Trim();
        if (string.IsNullOrEmpty(dateText))
            errors.Add(new(BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DateRequired));
        else if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out effectiveDate))
            errors.Add(new(BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DateInvalid));
        else if (effectiveDate < todayInTurkey && effectiveDate != keptEffectiveDate)
            errors.Add(new(BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DateInPast));

        if (errors.Count == 0)
        {
            valid = new ValidBillingPlanPrice(
                planCode!,
                period!,
                BillingPrice.FromMinorUnits(amountKurus, Currency),
                command.VatIncluded!.Value,
                effectiveDate,
                EffectiveFromUtc(effectiveDate));
        }

        return errors;
    }

    /// <summary>00:00 Türkiye time on <paramref name="date"/>, as UTC.</summary>
    public static DateTime EffectiveFromUtc(DateOnly date) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), TimeZoneHelper.ResolveTurkeyTimeZone());

    public static DateOnly TurkeyDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneHelper.ResolveTurkeyTimeZone()));
}

public sealed record ValidBillingPlanPrice(
    string PlanCode,
    string BillingPeriod,
    BillingPrice Price,
    bool VatIncluded,
    DateOnly EffectiveDate,
    DateTime EffectiveFromUtc);
