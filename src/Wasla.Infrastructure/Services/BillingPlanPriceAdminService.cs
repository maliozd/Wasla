using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// CentralDb implementation of <see cref="IBillingPlanPriceAdminService"/> (WAS-147). Logs carry ids, plan, period,
/// effective time, revisions and the acting Central Admin id, never amounts; the admin id is also stored on the row, and
/// a correction's previous and new values are stored in <c>BillingPlanPriceCorrections</c> (GitHub #64).
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

        var references = await BillingPlanPriceReferences.CountAsync(_central, ct);
        var prices = ToRows(rows, references, now);

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

    public async Task<BillingPlanPriceDetail?> GetAsync(Guid planPriceId, CancellationToken ct)
    {
        var price = await _central.BillingPlanPrices.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planPriceId, ct);
        if (price is null)
            return null;

        var now = UtcNow();
        var today = BillingPlanPriceInput.TurkeyDate(now);
        var samePlanAndPeriod = await _central.BillingPlanPrices.AsNoTracking()
            .Where(p => p.PlanCode == price.PlanCode && p.BillingPeriod == price.BillingPeriod)
            .ToListAsync(ct);
        var references = new Dictionary<Guid, int>
        {
            [price.Id] = await BillingPlanPriceReferences.CountAsync(_central, price.Id, ct)
        };
        var row = ToRows(samePlanAndPeriod, references, now).Single(p => p.Id == price.Id);

        var corrections = await _central.BillingPlanPriceCorrections.AsNoTracking()
            .Where(c => c.PlanPriceId == planPriceId)
            .OrderByDescending(c => c.CorrectedAtUtc)
            .ThenByDescending(c => c.Revision)
            .Select(c => new
            {
                Correction = c,
                AdminName = _central.CentralAdminUsers
                    .Where(a => a.Id == c.CorrectedByCentralAdminUserId)
                    .Select(a => a.DisplayName)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        // A superseding price is prefilled with the first date from today on that its plan and period do not use yet.
        var taken = samePlanAndPeriod.Select(p => p.EffectiveFromUtc).ToHashSet();
        var supersedeDate = today;
        while (taken.Contains(BillingPlanPriceInput.EffectiveFromUtc(supersedeDate)))
            supersedeDate = supersedeDate.AddDays(1);

        return new BillingPlanPriceDetail(
            row,
            price.Currency,
            corrections.Select(x => new BillingPlanPriceCorrectionEntry(
                    x.Correction.CorrectedAtUtc,
                    x.Correction.CorrectedByCentralAdminUserId,
                    x.AdminName,
                    x.Correction.PreviousPrice.MinorUnits,
                    x.Correction.PreviousCurrency,
                    x.Correction.PreviousVatIncluded,
                    BillingPlanPriceInput.TurkeyDate(x.Correction.PreviousEffectiveFromUtc),
                    x.Correction.NewPrice.MinorUnits,
                    x.Correction.NewCurrency,
                    x.Correction.NewVatIncluded,
                    BillingPlanPriceInput.TurkeyDate(x.Correction.NewEffectiveFromUtc)))
                .ToList(),
            today,
            supersedeDate);
    }

    public async Task<BillingPlanPriceCorrectionResult> CorrectAsync(
        BillingPlanPriceCorrectionCommand command,
        Guid centralAdminUserId,
        CancellationToken ct)
    {
        if (centralAdminUserId == Guid.Empty)
            throw new ArgumentException("The acting Central Admin is required.", nameof(centralAdminUserId));

        var before = await _central.BillingPlanPrices.AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.PlanPriceId, ct);
        if (before is null)
            return BillingPlanPriceCorrectionResult.Of(BillingPlanPriceCorrectionOutcome.NotFound);
        if (before.Revision != command.ExpectedRevision)
            return BillingPlanPriceCorrectionResult.Of(BillingPlanPriceCorrectionOutcome.Stale);

        // A referenced price is refused whatever was typed: the page offers to supersede it instead. This early answer
        // only spares the validation; the decision is made again under the row lock below.
        var referenceCount = await BillingPlanPriceReferences.CountAsync(_central, before.Id, ct);
        if (referenceCount > 0)
            return BillingPlanPriceCorrectionResult.Of(BillingPlanPriceCorrectionOutcome.Referenced, referenceCount);

        var now = UtcNow();
        var keptDate = BillingPlanPriceInput.TurkeyDate(before.EffectiveFromUtc);
        var errors = BillingPlanPriceInput.Validate(
            new BillingPlanPriceScheduleCommand(before.PlanCode, before.BillingPeriod, command.AmountKurus, command.VatIncluded, command.EffectiveDate),
            SelfServicePlanCodes(),
            BillingPlanPriceInput.TurkeyDate(now),
            out var valid,
            keptDate);
        if (valid is null)
            return new BillingPlanPriceCorrectionResult(BillingPlanPriceCorrectionOutcome.Invalid, errors, 0);

        // Keeping the date keeps the exact instant the price already starts at.
        var effectiveFromUtc = valid.EffectiveDate == keptDate ? before.EffectiveFromUtc : valid.EffectiveFromUtc;
        if (valid.Price == before.Price && valid.VatIncluded == before.VatIncluded && effectiveFromUtc == before.EffectiveFromUtc)
            return BillingPlanPriceCorrectionResult.Of(BillingPlanPriceCorrectionOutcome.Unchanged);

        await using var transaction = await _central.Database.BeginTransactionAsync(ct);

        // Lock the row first, at the revision the form was loaded with, exactly as a writer that starts referencing it
        // does (BillingPlanPriceReferences). From here until commit no reference can be added and no other correction
        // can land; a reference committed before the lock is seen below.
        if (!await BillingPlanPriceReferences.TryLockUnchangedAsync(_central, before.Id, command.ExpectedRevision, ct))
        {
            await transaction.RollbackAsync(ct);
            var exists = await _central.BillingPlanPrices.AsNoTracking().AnyAsync(p => p.Id == before.Id, ct);
            return BillingPlanPriceCorrectionResult.Of(exists ? BillingPlanPriceCorrectionOutcome.Stale : BillingPlanPriceCorrectionOutcome.NotFound);
        }

        referenceCount = await BillingPlanPriceReferences.CountAsync(_central, before.Id, ct);
        if (referenceCount > 0)
            return await RefuseAsync(transaction, BillingPlanPriceCorrectionResult.Of(BillingPlanPriceCorrectionOutcome.Referenced, referenceCount));

        if (effectiveFromUtc != before.EffectiveFromUtc
            && await _central.BillingPlanPrices.AsNoTracking().AnyAsync(
                p => p.Id != before.Id
                     && p.PlanCode == before.PlanCode
                     && p.BillingPeriod == before.BillingPeriod
                     && p.EffectiveFromUtc == effectiveFromUtc,
                ct))
            return await RefuseAsync(transaction, DuplicateCorrection());

        int updated;
        try
        {
            // One conditional statement: still unreferenced and still at the expected revision, or nothing changes.
            updated = await _central.BillingPlanPrices
                .Where(p => p.Id == before.Id && p.Revision == command.ExpectedRevision)
                .Unreferenced(_central)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Amount, valid.Price.Amount)
                    .SetProperty(p => p.Currency, valid.Price.Currency)
                    .SetProperty(p => p.VatIncluded, valid.VatIncluded)
                    .SetProperty(p => p.EffectiveFromUtc, effectiveFromUtc)
                    .SetProperty(p => p.Revision, p => p.Revision + 1), ct);
        }
        catch (Exception ex) when (BillingSubscriptionStore.IsUniqueViolation(ex))
        {
            // A price for the same plan, period and time was saved in between; the unique index decided.
            return await RefuseAsync(transaction, DuplicateCorrection());
        }

        if (updated != 1)
        {
            // Not reachable while the row lock holds; refuse rather than guess.
            return await RefuseAsync(transaction, BillingPlanPriceCorrectionResult.Of(BillingPlanPriceCorrectionOutcome.Stale));
        }

        var correction = BillingPlanPriceCorrection.Record(before, valid.Price, valid.VatIncluded, effectiveFromUtc, centralAdminUserId, now);
        _central.BillingPlanPriceCorrections.Add(correction);
        await _central.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Plan price corrected. PlanPriceId={PlanPriceId} PlanCode={PlanCode} BillingPeriod={BillingPeriod} Revision={Revision} EffectiveFromUtc={EffectiveFromUtc} CorrectionId={CorrectionId} CentralAdminUserId={CentralAdminUserId}",
            before.Id,
            before.PlanCode,
            before.BillingPeriod,
            correction.Revision,
            effectiveFromUtc,
            correction.Id,
            centralAdminUserId);
        return BillingPlanPriceCorrectionResult.Of(BillingPlanPriceCorrectionOutcome.Corrected);
    }

    private static async Task<BillingPlanPriceCorrectionResult> RefuseAsync(
        IDbContextTransaction transaction,
        BillingPlanPriceCorrectionResult result)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        return result;
    }

    private static List<BillingPlanPriceRow> ToRows(
        IReadOnlyCollection<BillingPlanPrice> rows,
        IReadOnlyDictionary<Guid, int> references,
        DateTime now)
    {
        var inEffectIds = BillingPlanPriceQueries.InEffect(rows, now).Select(p => p.Id).ToHashSet();
        return rows.Select(p => new BillingPlanPriceRow(
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
                : BillingPlanPriceState.Superseded,
                p.Revision,
                references.GetValueOrDefault(p.Id)))
            .ToList();
    }

    private static BillingPlanPriceCorrectionResult DuplicateCorrection() =>
        new(
            BillingPlanPriceCorrectionOutcome.Invalid,
            [new BillingPlanPriceValidationError(BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DuplicateEffectiveDate)],
            0);

    private string[] SelfServicePlanCodes() =>
        _plans.GetPublicPlans().Where(p => !p.IsContactSales).Select(p => p.PlanCode).ToArray();

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private static BillingPlanPriceScheduleResult Duplicate() =>
        new(null, [new BillingPlanPriceValidationError(BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DuplicateEffectiveDate)]);
}
