using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// CentralDb implementation of <see cref="IBillingPlanPriceAdminService"/> (WAS-147). Logs carry ids, plan, period,
/// effective time and the acting Central Admin id; the admin id is also stored on the row.
/// </summary>
public sealed class BillingPlanPriceAdminService : IBillingPlanPriceAdminService
{
    private static readonly string[] Periods = [BillingSubscription.MonthlyPeriod, BillingSubscription.YearlyPeriod];

    private readonly CentralDbContext _central;
    private readonly IWaslaPlanCatalog _plans;
    private readonly TimeProvider _time;
    private readonly ILogger<BillingPlanPriceAdminService> _logger;

    public BillingPlanPriceAdminService(
        CentralDbContext central,
        IWaslaPlanCatalog plans,
        TimeProvider time,
        ILogger<BillingPlanPriceAdminService> logger)
    {
        _central = central;
        _plans = plans;
        _time = time;
        _logger = logger;
    }

    public async Task<BillingPlanPriceOverview> GetOverviewAsync(CancellationToken ct)
    {
        var now = UtcNow();
        // A handful of rows per plan and period: read them all and decide each row's state in memory, with the same
        // rule checkout uses (the latest row that has started is in effect).
        var rows = await _central.BillingPlanPrices.AsNoTracking()
            .OrderBy(p => p.PlanCode)
            .ThenBy(p => p.BillingPeriod)
            .ThenByDescending(p => p.EffectiveFromUtc)
            .ToListAsync(ct);

        var inEffectIds = BillingPlanPriceQueries.InEffect(rows, now).Select(p => p.Id).ToHashSet();

        var prices = rows.Select(p => new BillingPlanPriceRow(
                p.Id,
                p.PlanCode,
                p.BillingPeriod,
                p.Price.MinorUnits,
                p.VatIncluded,
                p.EffectiveFromUtc,
                BillingPlanPriceInput.TurkeyDate(p.EffectiveFromUtc),
                p.CreatedAtUtc,
                p.EffectiveFromUtc > now ? BillingPlanPriceState.Scheduled
                : inEffectIds.Contains(p.Id) ? BillingPlanPriceState.InEffect
                : BillingPlanPriceState.Superseded))
            .ToList();

        var slots = SelfServicePlanCodes()
            .SelectMany(plan => Periods.Select(period => new BillingPlanPriceSlot(
                plan,
                period,
                prices.FirstOrDefault(p => p.PlanCode == plan && p.BillingPeriod == period && p.State == BillingPlanPriceState.InEffect),
                prices.Where(p => p.PlanCode == plan && p.BillingPeriod == period && p.State == BillingPlanPriceState.Scheduled)
                    .MinBy(p => p.EffectiveFromUtc))))
            .ToList();

        return new BillingPlanPriceOverview(slots, prices, BillingPlanPriceInput.TurkeyDate(now));
    }

    public async Task<BillingPlanPriceScheduleResult> ScheduleAsync(
        BillingPlanPriceScheduleCommand command,
        Guid centralAdminUserId,
        CancellationToken ct)
    {
        if (centralAdminUserId == Guid.Empty)
            throw new ArgumentException("The acting Central Admin is required.", nameof(centralAdminUserId));

        var now = UtcNow();
        var errors = BillingPlanPriceInput.Validate(
            command,
            SelfServicePlanCodes(),
            BillingPlanPriceInput.TurkeyDate(now),
            out var valid);
        if (valid is null)
            return new BillingPlanPriceScheduleResult(null, errors);

        if (await _central.BillingPlanPrices.AsNoTracking().AnyAsync(
                p => p.PlanCode == valid.PlanCode
                     && p.BillingPeriod == valid.BillingPeriod
                     && p.EffectiveFromUtc == valid.EffectiveFromUtc,
                ct))
            return Duplicate();

        var price = BillingPlanPrice.Create(
            valid.PlanCode,
            valid.BillingPeriod,
            valid.Price,
            valid.VatIncluded,
            valid.EffectiveFromUtc,
            centralAdminUserId,
            now);
        _central.BillingPlanPrices.Add(price);
        try
        {
            await _central.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (BillingSubscriptionStore.IsUniqueViolation(ex))
        {
            // Another admin saved the same plan, period and date in between; the unique index decided.
            _central.Entry(price).State = EntityState.Detached;
            return Duplicate();
        }

        _logger.LogInformation(
            "Plan price scheduled. PlanPriceId={PlanPriceId} PlanCode={PlanCode} BillingPeriod={BillingPeriod} EffectiveFromUtc={EffectiveFromUtc} CentralAdminUserId={CentralAdminUserId}",
            price.Id,
            price.PlanCode,
            price.BillingPeriod,
            price.EffectiveFromUtc,
            centralAdminUserId);
        return new BillingPlanPriceScheduleResult(price.Id, []);
    }

    public async Task<BillingPlanPriceWithdrawOutcome> WithdrawAsync(Guid planPriceId, Guid centralAdminUserId, CancellationToken ct)
    {
        var now = UtcNow();
        // One statement, conditional on the price still being in the future: it cannot remove a price that took
        // effect between reading and deleting. A subscription can only reference a price that took effect.
        var removed = await _central.BillingPlanPrices
            .Where(p => p.Id == planPriceId && p.EffectiveFromUtc > now)
            .ExecuteDeleteAsync(ct);
        if (removed == 1)
        {
            _logger.LogInformation(
                "Scheduled plan price withdrawn. PlanPriceId={PlanPriceId} CentralAdminUserId={CentralAdminUserId}",
                planPriceId,
                centralAdminUserId);
            return BillingPlanPriceWithdrawOutcome.Withdrawn;
        }

        return await _central.BillingPlanPrices.AsNoTracking().AnyAsync(p => p.Id == planPriceId, ct)
            ? BillingPlanPriceWithdrawOutcome.AlreadyInEffect
            : BillingPlanPriceWithdrawOutcome.NotFound;
    }

    private string[] SelfServicePlanCodes() =>
        _plans.GetPublicPlans().Where(p => !p.IsContactSales).Select(p => p.PlanCode).ToArray();

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private static BillingPlanPriceScheduleResult Duplicate() =>
        new(null, [new BillingPlanPriceValidationError(BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DuplicateEffectiveDate)]);
}
