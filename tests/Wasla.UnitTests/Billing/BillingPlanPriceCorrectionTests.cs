using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// Correcting and superseding plan prices (GitHub #64) over a SQLite CentralDb: an unreferenced price is corrected in
/// place with the schedule rules and an audit record of the admin and the old and new values; a referenced price is
/// refused and superseded by a new row instead; stale forms, duplicates and unchanged submissions write nothing; and the
/// two interleavings of a correction with a checkout start resolve to a snapshot that matches its price row. SQLite runs
/// these one after another; the locking itself is proven on SQL Server (BillingPlanPriceCorrectionSqlServerTests).
/// </summary>
public sealed class BillingPlanPriceCorrectionTests : IDisposable
{
    private static readonly DateOnly Today = BillingPlanPriceInput.TurkeyDate(BillingTestData.Now);
    private static readonly Guid OtherAdminId = new("b0000000-0000-0000-0000-00000000b064");

    private readonly SqliteCentralDatabase _db = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AnUnreferencedPrice_IsCorrectedInPlace_AndTheAuditRecordsTheAdminAndTheOldAndNewValues()
    {
        // The production mistake: a yearly price entered below the monthly one.
        var yearly = await ScheduleAsync("Starter", "35900", vat: true, Today, period: "Yearly");
        var correctedAt = BillingTestData.Now.AddMinutes(5);
        _db.Time.UtcNow = correctedAt;

        var result = await Service().CorrectAsync(new(yearly, 1, "499900", false, Iso(Today.AddDays(1))), BillingTestData.AdminId, Ct);

        Assert.Equal(BillingPlanPriceCorrectionOutcome.Corrected, result.Outcome);
        Assert.Empty(result.Errors);
        await using var db = _db.Open();
        var stored = await db.BillingPlanPrices.AsNoTracking().SingleAsync(p => p.Id == yearly, Ct);
        Assert.Equal(new BillingPrice(4999.00m, "TRY"), stored.Price);
        Assert.False(stored.VatIncluded);
        Assert.Equal(BillingPlanPriceInput.EffectiveFromUtc(Today.AddDays(1)), stored.EffectiveFromUtc);
        Assert.Equal(("Starter", "Yearly", 2), (stored.PlanCode, stored.BillingPeriod, stored.Revision));
        // The author of the price stays who created it; the correction is recorded separately.
        Assert.Equal(BillingTestData.AdminId, stored.CreatedByCentralAdminUserId);

        var audit = Assert.Single(await db.BillingPlanPriceCorrections.AsNoTracking().ToListAsync(Ct));
        Assert.Equal((yearly, 2, BillingTestData.AdminId, correctedAt), (audit.PlanPriceId, audit.Revision, audit.CorrectedByCentralAdminUserId, audit.CorrectedAtUtc));
        Assert.Equal((new BillingPrice(359.00m, "TRY"), true, BillingPlanPriceInput.EffectiveFromUtc(Today)), (audit.PreviousPrice, audit.PreviousVatIncluded, audit.PreviousEffectiveFromUtc));
        Assert.Equal((new BillingPrice(4999.00m, "TRY"), false, BillingPlanPriceInput.EffectiveFromUtc(Today.AddDays(1))), (audit.NewPrice, audit.NewVatIncluded, audit.NewEffectiveFromUtc));

        var detail = (await Service().GetAsync(yearly, Ct))!;
        var entry = Assert.Single(detail.Corrections);
        Assert.Equal((35900L, true, Today, 499900L, false, Today.AddDays(1)), (entry.PreviousAmountKurus, entry.PreviousVatIncluded, entry.PreviousEffectiveDateInTurkey, entry.NewAmountKurus, entry.NewVatIncluded, entry.NewEffectiveDateInTurkey));
        Assert.Equal(BillingPlanPriceState.Scheduled, detail.Price.State);
    }

    [Fact]
    public async Task ACorrection_MayKeepThePricesPastDate_ButCannotMoveItToAnotherPastDate()
    {
        // The seeded Pro, Monthly price took effect 30 days ago and nothing references it yet.
        var seeded = _db.PlanPrice;
        var keptDate = BillingPlanPriceInput.TurkeyDate(seeded.EffectiveFromUtc);

        var moved = await Service().CorrectAsync(new(seeded.Id, 1, "15000", true, Iso(keptDate.AddDays(1))), BillingTestData.AdminId, Ct);
        var kept = await Service().CorrectAsync(new(seeded.Id, 1, "15000", true, Iso(keptDate)), BillingTestData.AdminId, Ct);

        Assert.Equal(BillingPlanPriceCorrectionOutcome.Invalid, moved.Outcome);
        Assert.Equal(new BillingPlanPriceValidationError(BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DateInPast), Assert.Single(moved.Errors));
        Assert.Equal(BillingPlanPriceCorrectionOutcome.Corrected, kept.Outcome);
        await using var db = _db.Open();
        var stored = await db.BillingPlanPrices.AsNoTracking().SingleAsync(p => p.Id == seeded.Id, Ct);
        // Keeping the date keeps the exact instant the price started at, which is not midnight for this seeded row.
        Assert.Equal((150.00m, seeded.EffectiveFromUtc), (stored.Amount, stored.EffectiveFromUtc));
        Assert.Equal(seeded.EffectiveFromUtc, Assert.Single(await db.BillingPlanPriceCorrections.ToListAsync(Ct)).NewEffectiveFromUtc);
    }

    public static TheoryData<string?, bool?, string?, BillingPlanPriceField, BillingPlanPriceError> InvalidCorrections() => new()
    {
        { "", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountRequired },
        { "12,5", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountInvalid },
        { "-500", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountInvalid },
        { "0", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountOutOfRange },
        { "1000000001", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountOutOfRange },
        { "100", null, "2026-11-01", BillingPlanPriceField.VatIncluded, BillingPlanPriceError.VatRequired },
        { "100", true, "", BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DateRequired },
        { "100", true, "01.11.2026", BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DateInvalid },
        { "100", true, "2026-10-21", BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DateInPast }
    };

    [Theory]
    [MemberData(nameof(InvalidCorrections))]
    public async Task AnInvalidCorrection_GetsTheScheduleRulesError_AndChangesNothing(
        string? kurus, bool? vat, string? date, BillingPlanPriceField field, BillingPlanPriceError error)
    {
        var price = await ScheduleAsync("Starter", "10000", vat: true, Today.AddDays(3));

        var result = await Service().CorrectAsync(new(price, 1, kurus, vat, date), BillingTestData.AdminId, Ct);

        Assert.Equal(BillingPlanPriceCorrectionOutcome.Invalid, result.Outcome);
        Assert.Equal(new BillingPlanPriceValidationError(field, error), Assert.Single(result.Errors));
        await AssertUnchangedAsync(price, 100m, Today.AddDays(3));
    }

    [Fact]
    public async Task AReferencedPrice_IsRefusedWithItsReferenceCount_AndNeitherChangesNorIsAudited()
    {
        await BillingTestData.StartAsync(_db, BillingTestData.Sandbox);
        await BillingTestData.StartAsync(_db, BillingTestData.Production);

        var result = await Service().CorrectAsync(new(_db.PlanPrice.Id, 1, "99999", false, Iso(Today.AddDays(1))), BillingTestData.AdminId, Ct);
        var invalidButReferenced = await Service().CorrectAsync(new(_db.PlanPrice.Id, 1, "not a number", null, null), BillingTestData.AdminId, Ct);

        Assert.Equal((BillingPlanPriceCorrectionOutcome.Referenced, 2), (result.Outcome, result.ReferenceCount));
        Assert.Empty(result.Errors);
        Assert.Equal(BillingPlanPriceCorrectionOutcome.Referenced, invalidButReferenced.Outcome);
        await AssertUnchangedAsync(_db.PlanPrice.Id, BillingTestData.MonthlyPrice.Amount, BillingPlanPriceInput.TurkeyDate(_db.PlanPrice.EffectiveFromUtc));

        var overview = await Service().GetOverviewAsync(Ct);
        var row = overview.Prices.Single(p => p.Id == _db.PlanPrice.Id);
        Assert.Equal((2, true, BillingPlanPriceState.InEffect), (row.ReferenceCount, row.IsReferenced, row.State));
    }

    [Fact]
    public async Task AFormLoadedBeforeAnotherAdminsCorrection_IsStale_AndKeepsTheOtherAdminsValues()
    {
        var price = await ScheduleAsync("Starter", "10000", vat: true, Today.AddDays(3));
        Assert.Equal(BillingPlanPriceCorrectionOutcome.Corrected,
            (await Service().CorrectAsync(new(price, 1, "20000", true, Iso(Today.AddDays(3))), OtherAdminId, Ct)).Outcome);

        var stale = await Service().CorrectAsync(new(price, 1, "30000", true, Iso(Today.AddDays(3))), BillingTestData.AdminId, Ct);
        var current = await Service().CorrectAsync(new(price, 2, "30000", true, Iso(Today.AddDays(3))), BillingTestData.AdminId, Ct);

        Assert.Equal(BillingPlanPriceCorrectionOutcome.Stale, stale.Outcome);
        Assert.Equal(BillingPlanPriceCorrectionOutcome.Corrected, current.Outcome);
        await using var db = _db.Open();
        var audits = await db.BillingPlanPriceCorrections.AsNoTracking().OrderBy(c => c.Revision).ToListAsync(Ct);
        Assert.Equal(
            [(2, OtherAdminId, 100m, 200m), (3, BillingTestData.AdminId, 200m, 300m)],
            audits.Select(a => (a.Revision, a.CorrectedByCentralAdminUserId, a.PreviousAmount, a.NewAmount)));
    }

    [Fact]
    public async Task ACorrectionToADateTheSamePlanAndPeriodAlreadyUse_IsADuplicate()
    {
        await ScheduleAsync("Starter", "10000", vat: true, Today.AddDays(2));
        var price = await ScheduleAsync("Starter", "20000", vat: true, Today.AddDays(3));
        await ScheduleAsync("Starter", "30000", vat: true, Today.AddDays(2), period: "Yearly");

        var duplicate = await Service().CorrectAsync(new(price, 1, "20000", true, Iso(Today.AddDays(2))), BillingTestData.AdminId, Ct);
        var otherPeriodsDate = await Service().CorrectAsync(new(price, 1, "20000", true, Iso(Today.AddDays(4))), BillingTestData.AdminId, Ct);

        Assert.Equal(new BillingPlanPriceValidationError(BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DuplicateEffectiveDate), Assert.Single(duplicate.Errors));
        Assert.Equal(BillingPlanPriceCorrectionOutcome.Corrected, otherPeriodsDate.Outcome);
    }

    [Fact]
    public async Task SubmittingTheStoredValues_IsUnchanged_AndAnUnknownPriceIsNotFound()
    {
        var price = await ScheduleAsync("Starter", "10000", vat: true, Today.AddDays(3));

        var unchanged = await Service().CorrectAsync(new(price, 1, "0010000", true, Iso(Today.AddDays(3))), BillingTestData.AdminId, Ct);
        var unknown = await Service().CorrectAsync(new(Guid.NewGuid(), 1, "100", true, Iso(Today)), BillingTestData.AdminId, Ct);

        Assert.Equal(BillingPlanPriceCorrectionOutcome.Unchanged, unchanged.Outcome);
        Assert.Equal(BillingPlanPriceCorrectionOutcome.NotFound, unknown.Outcome);
        Assert.Null(await Service().GetAsync(Guid.NewGuid(), Ct));
        await AssertUnchangedAsync(price, 100m, Today.AddDays(3));
    }

    [Fact]
    public async Task Supersede_AddsANewRow_LeavesTheReferencedOneUnchanged_AndCheckoutFollowsTheNewOne()
    {
        var (_, earlyRegistration) = await BillingTestData.StartAsync(_db, BillingTestData.Sandbox);
        var referenced = (await Service().GetAsync(_db.PlanPrice.Id, Ct))!;
        Assert.True(referenced.Price.IsReferenced);
        Assert.Equal(Today, referenced.SupersedeEffectiveDate);

        // The page prefills the schedule form from the referenced price; the admin changes the amount.
        var replacement = await Service().ScheduleAsync(
            new(referenced.Price.PlanCode, referenced.Price.BillingPeriod, "15000", referenced.Price.VatIncluded, Iso(referenced.SupersedeEffectiveDate)),
            BillingTestData.AdminId,
            Ct);

        Assert.True(replacement.Succeeded);
        await AssertUnchangedAsync(_db.PlanPrice.Id, BillingTestData.MonthlyPrice.Amount, BillingPlanPriceInput.TurkeyDate(_db.PlanPrice.EffectiveFromUtc));
        var overview = await Service().GetOverviewAsync(Ct);
        Assert.Equal(replacement.PlanPriceId, overview.Slots.Single(s => s.PlanCode == "Pro" && s.BillingPeriod == "Monthly").InEffect!.Id);
        Assert.Equal(BillingPlanPriceState.Superseded, overview.Prices.Single(p => p.Id == _db.PlanPrice.Id).State);

        var (_, lateRegistration) = await BillingTestData.StartAsync(_db, BillingTestData.Sandbox);
        var early = (await _db.Store().GetForRegistrationAsync(earlyRegistration, Ct))!;
        var late = (await _db.Store().GetForRegistrationAsync(lateRegistration, Ct))!;
        Assert.Equal((_db.PlanPrice.Id, BillingTestData.MonthlyPrice), (early.PlanPriceId, early.Price));
        Assert.Equal((replacement.PlanPriceId!.Value, new BillingPrice(150.00m, "TRY")), (late.PlanPriceId, late.Price));

        // Today is taken now, so a further replacement is prefilled with tomorrow.
        Assert.Equal(Today.AddDays(1), (await Service().GetAsync(replacement.PlanPriceId.Value, Ct))!.SupersedeEffectiveDate);
    }

    [Fact]
    public async Task ACheckoutThatReadThePriceBeforeACorrection_IsPricedFromTheCorrectedRow()
    {
        // The checkout start reads the seeded price; before it locks the row, a Central Admin corrects that price.
        BillingPlanPriceCorrectionResult? correction = null;
        var store = _db.Store(new AfterFirstRead("\"BillingPlanPrices\"", () =>
            correction = Service()
                .CorrectAsync(new(_db.PlanPrice.Id, 1, "20000", false, Iso(BillingPlanPriceInput.TurkeyDate(_db.PlanPrice.EffectiveFromUtc))), BillingTestData.AdminId, Ct)
                .GetAwaiter().GetResult()));
        var registration = await _db.SeedRegistrationAsync();

        var started = await store.StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, Ct);

        Assert.Equal(BillingPlanPriceCorrectionOutcome.Corrected, correction?.Outcome);
        Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);
        var view = (await _db.Store().GetForRegistrationAsync(registration.Id, Ct))!;
        // The lock found a newer revision, so the start read the price again: the snapshot is the corrected row.
        Assert.Equal((_db.PlanPrice.Id, new BillingPrice(200.00m, "TRY")), (view.PlanPriceId, view.Price));
        var quote = await Reader().GetPricesInEffectAsync(Ct);
        Assert.False(quote.Single(q => q.PlanPriceId == _db.PlanPrice.Id).VatIncluded);
    }

    [Fact]
    public async Task AReferenceAddedAfterTheCorrectionsFirstCheck_MakesTheLockedCheckRefuse()
    {
        // The correction counts no reference, then a checkout starts from the price before the correction locks it.
        var service = new BillingPlanPriceAdminService(
            _db.Open(new AfterFirstRead("\"BillingSubscriptions\"", () =>
                BillingTestData.StartAsync(_db, BillingTestData.Sandbox).GetAwaiter().GetResult())),
            new WaslaPlanCatalog(),
            _db.Time,
            NullLogger<BillingPlanPriceAdminService>.Instance);

        var result = await service.CorrectAsync(new(_db.PlanPrice.Id, 1, "20000", true, Iso(Today)), BillingTestData.AdminId, Ct);

        Assert.Equal((BillingPlanPriceCorrectionOutcome.Referenced, 1), (result.Outcome, result.ReferenceCount));
        await AssertUnchangedAsync(_db.PlanPrice.Id, BillingTestData.MonthlyPrice.Amount, BillingPlanPriceInput.TurkeyDate(_db.PlanPrice.EffectiveFromUtc));
    }

    private BillingPlanPriceAdminService Service() =>
        new(_db.Open(), new WaslaPlanCatalog(), _db.Time, NullLogger<BillingPlanPriceAdminService>.Instance);

    private BillingPlanPriceReader Reader() => new(_db.Open(), _db.Time);

    private async Task<Guid> ScheduleAsync(string plan, string kurus, bool vat, DateOnly date, string period = "Monthly")
    {
        var result = await Service().ScheduleAsync(new(plan, period, kurus, vat, Iso(date)), BillingTestData.AdminId, Ct);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        return result.PlanPriceId!.Value;
    }

    private async Task AssertUnchangedAsync(Guid planPriceId, decimal amount, DateOnly date)
    {
        await using var db = _db.Open();
        var stored = await db.BillingPlanPrices.AsNoTracking().SingleAsync(p => p.Id == planPriceId, Ct);
        Assert.Equal((amount, 1, date), (stored.Amount, stored.Revision, BillingPlanPriceInput.TurkeyDate(stored.EffectiveFromUtc)));
        Assert.False(await db.BillingPlanPriceCorrections.AnyAsync(c => c.PlanPriceId == planPriceId, Ct));
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Runs <paramref name="action"/> once, after the first read outside a transaction of <paramref name="table"/>.</summary>
    private sealed class AfterFirstRead(string table, Action action) : DbCommandInterceptor
    {
        private bool _done;

        public override InterceptionResult DataReaderDisposing(
            DbCommand command,
            DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            if (!_done && command.Transaction is null && command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                _done = true;
                action();
            }

            return result;
        }
    }
}
