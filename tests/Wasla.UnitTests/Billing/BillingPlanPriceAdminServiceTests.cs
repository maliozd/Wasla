using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// Plan prices set by Central Admins are the only prices checkout uses (WAS-147): input rules, effective dates at
/// 00:00 Türkiye time, a future price that is not charged before its date, checkout blocked without a price, and
/// withdrawal of prices that have not taken effect. Starter is used here; the shared test database seeds Pro, Monthly.
/// </summary>
public sealed class BillingPlanPriceAdminServiceTests : IDisposable
{
    private static readonly string[] OnlinePlans = ["Starter", "Pro", "ProPlus"];
    private static readonly DateOnly Today = BillingPlanPriceInput.TurkeyDate(BillingTestData.Now);

    private readonly SqliteCentralDatabase _db = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    public static TheoryData<string?, string?, string?, bool?, string?, BillingPlanPriceField, BillingPlanPriceError> Invalid() => new()
    {
        { null, "Monthly", "100", true, "2026-11-01", BillingPlanPriceField.PlanCode, BillingPlanPriceError.PlanInvalid },
        { "Enterprise", "Monthly", "100", true, "2026-11-01", BillingPlanPriceField.PlanCode, BillingPlanPriceError.PlanInvalid },
        { "Starter", "monthly", "100", true, "2026-11-01", BillingPlanPriceField.BillingPeriod, BillingPlanPriceError.PeriodInvalid },
        { "Starter", "Monthly", " ", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountRequired },
        { "Starter", "Monthly", "+100", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountInvalid },
        { "Starter", "Monthly", "10 0", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountInvalid },
        { "Starter", "Monthly", "１００", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountInvalid },
        { "Starter", "Monthly", "000", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountOutOfRange },
        { "Starter", "Monthly", "1000000001", true, "2026-11-01", BillingPlanPriceField.AmountKurus, BillingPlanPriceError.AmountOutOfRange },
        { "Starter", "Monthly", "100", null, "2026-11-01", BillingPlanPriceField.VatIncluded, BillingPlanPriceError.VatRequired },
        { "Starter", "Monthly", "100", true, "2026-02-30", BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DateInvalid },
        { "Starter", "Monthly", "100", true, "2026-10-21", BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DateInPast }
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnInvalidInput_GivesExactlyItsError(
        string? plan, string? period, string? kurus, bool? vat, string? date,
        BillingPlanPriceField field, BillingPlanPriceError error)
    {
        var errors = BillingPlanPriceInput.Validate(new(plan, period, kurus, vat, date), OnlinePlans, Today, out var valid);

        Assert.Null(valid);
        Assert.Equal(new BillingPlanPriceValidationError(field, error), Assert.Single(errors));
    }

    [Theory]
    [InlineData("1", 0.01)]
    [InlineData("29990", 299.90)]
    [InlineData("0029990", 299.90)]
    [InlineData("1000000000", 10000000.00)]
    public void AValidInput_ConvertsKurusExactly(string kurus, double tl)
    {
        var errors = BillingPlanPriceInput.Validate(new("Starter", "Yearly", kurus, false, "2026-10-22"), OnlinePlans, Today, out var valid);

        Assert.Empty(errors);
        Assert.Equal(new BillingPrice((decimal)tl, "TRY"), valid!.Price);
        Assert.Equal(long.Parse(kurus), valid.Price.MinorUnits);
        Assert.False(valid.VatIncluded);
    }

    [Fact]
    public void AnEffectiveDate_StartsAtMidnightTurkeyTime()
    {
        var date = new DateOnly(2026, 11, 1);

        Assert.Equal(new DateTime(2026, 10, 31, 21, 0, 0), BillingPlanPriceInput.EffectiveFromUtc(date));
        Assert.Equal(date, BillingPlanPriceInput.TurkeyDate(new DateTime(2026, 10, 31, 21, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(date.AddDays(-1), BillingPlanPriceInput.TurkeyDate(new DateTime(2026, 10, 31, 20, 59, 59, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task TodaysDate_TakesEffectImmediately_AndCheckoutChargesExactlyThatPrice()
    {
        var scheduled = await ScheduleAsync("Starter", "12345", vat: false, Today);
        var registration = await _db.SeedRegistrationAsync(planCode: "Starter");

        var started = await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, Ct);

        Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);
        var view = (await _db.Store().GetForRegistrationAsync(registration.Id, Ct))!;
        Assert.Equal((scheduled, new BillingPrice(123.45m, "TRY")), (view.PlanPriceId, view.Price));
        var overview = await Service().GetOverviewAsync(Ct);
        var slot = overview.Slots.Single(s => s.PlanCode == "Starter" && s.BillingPeriod == "Monthly");
        Assert.Equal(scheduled, slot.InEffect!.Id);
        Assert.False(slot.InEffect.VatIncluded);
        Assert.Equal(12345, slot.InEffect.AmountKurus);
        await using var db = _db.Open();
        Assert.Equal(BillingTestData.AdminId, (await db.BillingPlanPrices.SingleAsync(p => p.Id == scheduled, Ct)).CreatedByCentralAdminUserId);
    }

    [Fact]
    public async Task AFuturePrice_IsNotChargedBeforeItsDate_AndReplacesTheCurrentOneFromThen()
    {
        var current = await ScheduleAsync("Starter", "10000", vat: true, Today);
        var next = await ScheduleAsync("Starter", "20000", vat: true, Today.AddDays(5));
        var early = await StartAsync();

        _db.Time.UtcNow = BillingPlanPriceInput.EffectiveFromUtc(Today.AddDays(5)).AddTicks(-1);
        var justBefore = await StartAsync();
        var overviewBefore = await Service().GetOverviewAsync(Ct);

        _db.Time.UtcNow = BillingPlanPriceInput.EffectiveFromUtc(Today.AddDays(5));
        var onTheDate = await StartAsync();
        var overviewAfter = await Service().GetOverviewAsync(Ct);

        Assert.Equal(100.00m, early.Price.Amount);
        Assert.Equal(100.00m, justBefore.Price.Amount);
        Assert.Equal((next, 200.00m), (onTheDate.PlanPriceId, onTheDate.Price.Amount));
        Assert.Equal(BillingPlanPriceState.Scheduled, overviewBefore.Prices.Single(p => p.Id == next).State);
        Assert.Equal(next, overviewBefore.Slots.Single(s => s.PlanCode == "Starter" && s.BillingPeriod == "Monthly").NextScheduled!.Id);
        Assert.Equal(BillingPlanPriceState.InEffect, overviewAfter.Prices.Single(p => p.Id == next).State);
        Assert.Equal(BillingPlanPriceState.Superseded, overviewAfter.Prices.Single(p => p.Id == current).State);
        // The early subscription keeps the price it started with.
        Assert.Equal(100.00m, (await _db.Store().GetForRegistrationAsync(early.PendingRegistrationId, Ct))!.Price.Amount);
    }

    [Fact]
    public async Task WithoutAPriceForThePlanAndPeriod_CheckoutStaysBlocked_AndTheSummaryShowsNoPrice()
    {
        // A yearly Starter price and a future monthly one do not price a monthly Starter checkout today.
        await ScheduleAsync("Starter", "50000", vat: true, Today, period: "Yearly");
        await ScheduleAsync("Starter", "5000", vat: true, Today.AddDays(1));
        var registration = await _db.SeedRegistrationAsync(planCode: "Starter");

        var started = await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, Ct);

        Assert.Equal(new BillingStartResult(BillingStoreOutcome.PriceNotConfigured, null), started);
        var overview = await Service().GetOverviewAsync(Ct);
        Assert.Null(overview.Slots.Single(s => s.PlanCode == "Starter" && s.BillingPeriod == "Monthly").InEffect);
        Assert.Equal(6, overview.Slots.Count);
        Assert.DoesNotContain(overview.Slots, s => s.PlanCode == "Enterprise");
    }

    [Fact]
    public async Task TheCheckoutSummary_ShowsThePriceInEffect_FromTheSameSource()
    {
        // The summary reads the real clock, so these prices are placed around the real now.
        var registration = await _db.SeedRegistrationAsync(planCode: "Starter");
        var none = await PendingRegistrations().GetCheckoutDetailsAsync(registration.Id, Ct);

        await using (var db = _db.Open())
        {
            db.BillingPlanPrices.Add(BillingTestData.PlanPrice("Starter", price: new BillingPrice(77.70m, "TRY"), effectiveFromUtc: DateTime.UtcNow.AddDays(-1)));
            db.BillingPlanPrices.Add(BillingTestData.PlanPrice("Starter", price: new BillingPrice(88.80m, "TRY"), effectiveFromUtc: DateTime.UtcNow.AddDays(1)));
            await db.SaveChangesAsync(Ct);
        }

        var priced = await PendingRegistrations().GetCheckoutDetailsAsync(registration.Id, Ct);

        Assert.Null(none!.Price);
        Assert.Equal(("Starter", "Monthly", new BillingPrice(77.70m, "TRY"), true), (priced!.Price!.PlanCode, priced.Price.BillingPeriod, priced.Price.Price, priced.Price.VatIncluded));
        Assert.False(priced.IsYearlyBilling);
    }

    [Fact]
    public async Task ThePublicReader_ReturnsTheSamePricesCheckoutCharges_MonthlyAndYearlySeparately()
    {
        var monthly = await ScheduleAsync("Starter", "30000", vat: true, Today);
        var yearly = await ScheduleAsync("Starter", "275000", vat: false, Today, period: "Yearly");
        var next = await ScheduleAsync("Starter", "40000", vat: true, Today.AddDays(2));

        var now = await Reader().GetPricesInEffectAsync(Ct);
        _db.Time.UtcNow = BillingPlanPriceInput.EffectiveFromUtc(Today.AddDays(2));
        var later = await Reader().GetPricesInEffectAsync(Ct);
        var charged = await StartAsync();

        Assert.Equal((monthly, 300m, true), Starter(now, "Monthly"));
        Assert.Equal((yearly, 2750m, false), Starter(now, "Yearly"));
        Assert.DoesNotContain(now, p => p.PlanPriceId == next);
        // The seeded Pro, Monthly price is there too; nothing is invented for plans and periods without a price.
        Assert.Contains(now, p => p.PlanCode == "Pro" && p.BillingPeriod == "Monthly");
        Assert.DoesNotContain(now, p => p.PlanCode == "ProPlus" || (p.PlanCode == "Pro" && p.BillingPeriod == "Yearly"));
        Assert.Equal((next, 400m, true), Starter(later, "Monthly"));
        Assert.Equal((yearly, 2750m, false), Starter(later, "Yearly"));
        Assert.Equal(next, charged.PlanPriceId);

        static (Guid, decimal, bool) Starter(IReadOnlyList<BillingPriceQuote> prices, string period)
        {
            var quote = prices.Single(p => p.PlanCode == "Starter" && p.BillingPeriod == period);
            return (quote.PlanPriceId, quote.Price.Amount, quote.VatIncluded);
        }
    }

    [Fact]
    public async Task OnlyAPriceThatHasNotTakenEffect_CanBeWithdrawn()
    {
        var inEffect = await ScheduleAsync("Starter", "100", vat: true, Today);
        var scheduled = await ScheduleAsync("Starter", "200", vat: true, Today.AddDays(2));
        var later = await ScheduleAsync("Starter", "300", vat: true, Today.AddDays(3));

        Assert.Equal(BillingPlanPriceWithdrawOutcome.AlreadyInEffect, await Service().WithdrawAsync(inEffect, BillingTestData.AdminId, Ct));
        Assert.Equal(BillingPlanPriceWithdrawOutcome.Withdrawn, await Service().WithdrawAsync(scheduled, BillingTestData.AdminId, Ct));
        Assert.Equal(BillingPlanPriceWithdrawOutcome.NotFound, await Service().WithdrawAsync(scheduled, BillingTestData.AdminId, Ct));

        // At the exact instant it takes effect a price is no longer withdrawable.
        _db.Time.UtcNow = BillingPlanPriceInput.EffectiveFromUtc(Today.AddDays(3));
        Assert.Equal(BillingPlanPriceWithdrawOutcome.AlreadyInEffect, await Service().WithdrawAsync(later, BillingTestData.AdminId, Ct));

        await using var db = _db.Open();
        Assert.Equal(
            new[] { inEffect, later }.Order(),
            await db.BillingPlanPrices.Where(p => p.PlanCode == "Starter").Select(p => p.Id).OrderBy(id => id).ToListAsync(Ct));
    }

    [Fact]
    public async Task TheSameDateSavedConcurrently_IsADuplicate_DecidedByTheUniqueIndex()
    {
        var date = Today.AddDays(4);
        var service = new BillingPlanPriceAdminService(
            _db.Open(new InsertAfterFirstPriceRead(() =>
            {
                using var other = _db.Open();
                other.BillingPlanPrices.Add(BillingPlanPrice.Create(
                    "Starter", "Monthly", new BillingPrice(1m, "TRY"), true, BillingPlanPriceInput.EffectiveFromUtc(date), Guid.NewGuid(), BillingTestData.Now));
                other.SaveChanges();
            })),
            new WaslaPlanCatalog(),
            _db.Time,
            NullLogger<BillingPlanPriceAdminService>.Instance);

        var result = await service.ScheduleAsync(Command("Starter", "500", true, date), BillingTestData.AdminId, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(new BillingPlanPriceValidationError(BillingPlanPriceField.EffectiveDate, BillingPlanPriceError.DuplicateEffectiveDate), Assert.Single(result.Errors));
        await using var db = _db.Open();
        Assert.Equal(1m, (await db.BillingPlanPrices.SingleAsync(p => p.PlanCode == "Starter", Ct)).Amount);
    }

    private BillingPlanPriceAdminService Service() =>
        new(_db.Open(), new WaslaPlanCatalog(), _db.Time, NullLogger<BillingPlanPriceAdminService>.Instance);

    private BillingPlanPriceReader Reader() => new(_db.Open(), _db.Time);

    private PendingRegistrationService PendingRegistrations()
    {
        var db = _db.Open();
        return new PendingRegistrationService(
            db,
            new WaslaPlanCatalog(),
            new SignupReferenceDataService(db),
            Options.Create(new TenantOnboardingOptions()),
            NullLogger<PendingRegistrationService>.Instance);
    }

    private static BillingPlanPriceScheduleCommand Command(string plan, string kurus, bool vat, DateOnly date, string period = "Monthly") =>
        new(plan, period, kurus, vat, date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    private async Task<Guid> ScheduleAsync(string plan, string kurus, bool vat, DateOnly date, string period = "Monthly")
    {
        var result = await Service().ScheduleAsync(Command(plan, kurus, vat, date, period), BillingTestData.AdminId, Ct);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        return result.PlanPriceId!.Value;
    }

    private async Task<BillingSubscriptionView> StartAsync()
    {
        var registration = await _db.SeedRegistrationAsync(planCode: "Starter");
        var started = await _db.Store().StartForRegistrationAsync(registration.Id, BillingTestData.Sandbox, Ct);
        Assert.Equal(BillingStoreOutcome.Applied, started.Outcome);
        return (await _db.Store().GetForRegistrationAsync(registration.Id, Ct))!;
    }

    /// <summary>Runs <paramref name="insert"/> once, after the service's duplicate check read the prices and before it saves.</summary>
    private sealed class InsertAfterFirstPriceRead(Action insert) : DbCommandInterceptor
    {
        private bool _done;

        public override InterceptionResult DataReaderDisposing(
            DbCommand command,
            DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            if (!_done && command.CommandText.Contains("\"BillingPlanPrices\"", StringComparison.Ordinal))
            {
                _done = true;
                insert();
            }

            return result;
        }
    }
}
