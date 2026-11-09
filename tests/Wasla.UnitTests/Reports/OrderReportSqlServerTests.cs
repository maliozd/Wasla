using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Platform.Dtos;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Contracts.Dashboard;
using Wasla.Contracts.Enums;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Orders;
using Wasla.Web.Models.Reports;
using ApiDashboardController = Wasla.Api.Controllers.DashboardController;
using TenantReportsController = Wasla.Web.Areas.Tenant.Controllers.ReportsController;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// WAS-182: the order report's single aggregate statement on a real SQL Server (decimal sums, the local-day grouping and
/// its translation), with exact expected figures from a small known dataset. Türkiye is UTC+3, so local midnight is
/// 21:00 UTC the day before. Every test starts from an empty Orders table in the class's throwaway database.
/// </summary>
public sealed class OrderReportSqlServerTests : IClassFixture<SqlServerTenantDatabase>
{
    private static readonly TimeZoneInfo Turkey = TimeZoneHelper.ResolveTurkeyTimeZone();

    // 2026-11-07 13:00 in Türkiye.
    private static readonly DateTime Now = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerTenantDatabase _database;
    private readonly Guid _tenantId = Guid.NewGuid();

    public OrderReportSqlServerTests(SqlServerTenantDatabase database)
    {
        _database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static OrderReportFilter Filter(string? preset, string? start = null, string? end = null, string? platform = null, DateTime? now = null, TimeZoneInfo? zone = null)
    {
        var result = OrderReportFilters.Resolve(preset, start, end, platform, now ?? Now, zone ?? Turkey);
        Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(e => $"{e.Parameter}:{e.Error}")));
        return result.Filter!;
    }

    private OrderReportService Service(ITenantDbContextFactory? factory = null, DateTime? now = null) =>
        new(factory ?? _database.Instance("report"), new FixedTime(now ?? Now));

    /// <summary>Yemeksepeti's three days in the known dataset (<see cref="SeedKnownDatasetAsync"/>).</summary>
    private static readonly OrderReportFigures[] YemeksepetiDays =
    [
        new(1, 3.33m, 0, 0m, 0, 0, 1, 0),
        new(1, 30.03m, 0, 0m, 0, 0, 1, 0),
        new(2, 50.10m, 1, 0.10m, 1, 0, 0, 0)
    ];

    private static OrderReportFigures F(int count, decimal value, int completed, decimal completedValue, int cancelled = 0, int failed = 0, int inProgress = 0, int other = 0) =>
        new(count, value, completed, completedValue, cancelled, failed, inProgress, other);

    /// <summary>
    /// Every stored status, three platforms, decimal amounts, a missing provider time, two delayed imports and orders
    /// on both sides of the period's UTC bounds. Period: local 5–7 November 2026.
    /// </summary>
    private async Task SeedKnownDatasetAsync()
    {
        await ResetAsync();
        await SeedAsync(
            // 5 November (local)
            O("TY-1", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 123.45m, placed: Utc(11, 5, 9)),
            O("GY-5", FoodPlatform.GetirYemek, OrderStatus.Failed, 19.99m, placed: null, received: Utc(11, 5, 12)),
            O("TY-10", FoodPlatform.TrendyolYemek, (OrderStatus)1, 5.00m, placed: Utc(11, 5, 13)),
            // Placed 23:30 local on the 5th, imported 03:05 local on the 6th: counts on the 5th.
            O("GY-12", FoodPlatform.GetirYemek, OrderStatus.Delivered, 99.99m, placed: Utc(11, 5, 20, 30), received: Utc(11, 6, 0, 5)),
            // No provider time, imported exactly at local midnight starting the 5th: the first instant of the period.
            O("YS-15", FoodPlatform.Yemeksepeti, OrderStatus.New, 3.33m, placed: null, received: Utc(11, 4, 21)),
            // 6 November
            O("TY-2", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 76.55m, placed: Utc(11, 6, 21).AddTicks(-1)),
            O("YS-8", FoodPlatform.Yemeksepeti, OrderStatus.ReadyForPickup, 30.03m, placed: Utc(11, 6, 10)),
            O("GY-9", FoodPlatform.GetirYemek, OrderStatus.OnTheWay, 40.04m, placed: Utc(11, 6, 11)),
            // 7 November
            O("YS-3", FoodPlatform.Yemeksepeti, OrderStatus.Delivered, 0.10m, placed: Utc(11, 6, 21)),
            O("YS-4", FoodPlatform.Yemeksepeti, OrderStatus.Cancelled, 50.00m, placed: Utc(11, 7, 8)),
            O("TY-6", FoodPlatform.TrendyolYemek, OrderStatus.New, 10.01m, placed: Utc(11, 7, 9)),
            O("TY-7", FoodPlatform.TrendyolYemek, OrderStatus.Preparing, 20.02m, placed: Utc(11, 7, 9, 10)),
            // Outside: placed 23:50 local on the 4th and imported 02:19 local on the 6th; the last tick of the 4th;
            // the first instant of the 8th.
            O("TY-11", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 200.00m, placed: Utc(11, 4, 20, 50), received: Utc(11, 5, 23, 19)),
            O("YS-13", FoodPlatform.Yemeksepeti, OrderStatus.Delivered, 1000m, placed: Utc(11, 4, 21).AddTicks(-1)),
            O("YS-14", FoodPlatform.Yemeksepeti, OrderStatus.Delivered, 1000m, placed: Utc(11, 7, 21)));
    }

    [Fact]
    public async Task KnownDataset_GivesExactSummaryPlatformAndDayFigures_ThatReconcile()
    {
        await SeedKnownDatasetAsync();

        var report = await Service().GetAsync(_tenantId, Filter("custom", "2026-11-05", "2026-11-07"), Ct);

        Assert.Equal("TRY", report.CurrencyCode);
        Assert.Equal(Now, report.GeneratedAtUtc);
        Assert.Equal(F(12, 478.51m, 4, 300.09m, cancelled: 1, failed: 1, inProgress: 5, other: 1), report.Summary);
        Assert.Equal(75.02m, report.Summary.AverageCompletedOrderValue);

        Assert.Equal(
            [
                new OrderReportPlatformRow(FoodPlatform.TrendyolYemek, F(5, 235.03m, 2, 200.00m, inProgress: 2, other: 1)),
                new OrderReportPlatformRow(FoodPlatform.Yemeksepeti, F(4, 83.46m, 1, 0.10m, cancelled: 1, inProgress: 2)),
                new OrderReportPlatformRow(FoodPlatform.GetirYemek, F(3, 160.02m, 1, 99.99m, failed: 1, inProgress: 1))
            ],
            report.Platforms);
        Assert.Equal(100.00m, report.Platforms[0].Figures.AverageCompletedOrderValue);

        Assert.Equal(
            [
                new OrderReportDayRow(new DateOnly(2026, 11, 5), Utc(11, 4, 21), Utc(11, 5, 21), F(5, 251.76m, 2, 223.44m, failed: 1, inProgress: 1, other: 1)),
                new OrderReportDayRow(new DateOnly(2026, 11, 6), Utc(11, 5, 21), Utc(11, 6, 21), F(3, 146.62m, 1, 76.55m, inProgress: 2)),
                new OrderReportDayRow(new DateOnly(2026, 11, 7), Utc(11, 6, 21), Utc(11, 7, 21), F(4, 80.13m, 1, 0.10m, cancelled: 1, inProgress: 2))
            ],
            report.Days);
        Assert.Equal(111.72m, report.Days[0].Figures.AverageCompletedOrderValue);

        Assert.Equal([FoodPlatform.TrendyolYemek, FoodPlatform.Yemeksepeti, FoodPlatform.GetirYemek], report.PlatformDays.Select(p => p.Platform));
        Assert.Equal(
            [F(2, 128.45m, 1, 123.45m, other: 1), F(1, 76.55m, 1, 76.55m), F(2, 30.03m, 0, 0m, inProgress: 2)],
            report.PlatformDays[0].Days.Select(d => d.Figures));
        Assert.Equal(YemeksepetiDays, report.PlatformDays[1].Days.Select(d => d.Figures));
        Assert.Equal(
            [F(2, 119.98m, 1, 99.99m, failed: 1), F(1, 40.04m, 0, 0m, inProgress: 1), OrderReportFigures.Empty],
            report.PlatformDays[2].Days.Select(d => d.Figures));

        AssertReconciles(report);
    }

    [Fact]
    public async Task PlatformFilter_KeepsTheSameDefinitions_ForOnePlatform()
    {
        await SeedKnownDatasetAsync();

        var report = await Service().GetAsync(_tenantId, Filter("custom", "2026-11-05", "2026-11-07", "Yemeksepeti"), Ct);

        Assert.Equal(FoodPlatform.Yemeksepeti, report.Filter.Platform);
        Assert.Equal(F(4, 83.46m, 1, 0.10m, cancelled: 1, inProgress: 2), report.Summary);
        Assert.Equal([new OrderReportPlatformRow(FoodPlatform.Yemeksepeti, report.Summary)], report.Platforms);
        Assert.Equal(YemeksepetiDays, report.Days.Select(d => d.Figures));
        var only = Assert.Single(report.PlatformDays);
        Assert.Equal(FoodPlatform.Yemeksepeti, only.Platform);
        Assert.Equal(YemeksepetiDays, only.Days.Select(d => d.Figures));
        AssertReconciles(report);
    }

    [Fact]
    public async Task Presets_SelectTheirLocalDays_OnTheSameData()
    {
        await SeedKnownDatasetAsync();
        var service = Service();

        var today = await service.GetAsync(_tenantId, Filter("today"), Ct);
        Assert.Equal(F(4, 80.13m, 1, 0.10m, cancelled: 1, inProgress: 2), today.Summary);
        Assert.False(today.Filter.Period.IsComplete);

        var yesterday = await service.GetAsync(_tenantId, Filter("yesterday"), Ct);
        Assert.Equal(F(3, 146.62m, 1, 76.55m, inProgress: 2), yesterday.Summary);
        Assert.True(yesterday.Filter.Period.IsComplete);

        // 1–7 November also holds TY-11 (placed late on the 4th, imported on the 6th) and YS-13 (the last tick of the 4th).
        var week = await service.GetAsync(_tenantId, Filter("last7days"), Ct);
        Assert.Equal(7, week.Days.Count);
        Assert.Equal(new DateOnly(2026, 11, 1), week.Days[0].Date);
        var expectedWeek = F(14, 1678.51m, 6, 1500.09m, cancelled: 1, failed: 1, inProgress: 5, other: 1);
        Assert.Equal(expectedWeek, week.Summary);
        Assert.Equal(F(2, 1200.00m, 2, 1200.00m), week.Days.Single(d => d.Date == new DateOnly(2026, 11, 4)).Figures);
        Assert.All(week.Days.Where(d => d.Date < new DateOnly(2026, 11, 4)), d => Assert.Equal(OrderReportFigures.Empty, d.Figures));

        var month = await service.GetAsync(_tenantId, Filter("last30days"), Ct);
        Assert.Equal(30, month.Days.Count);
        Assert.Equal(expectedWeek, month.Summary);
        AssertReconciles(month);
    }

    [Fact]
    public async Task EmptyPeriod_IsZeroFilled_WithNoAverageAndNoPlatformRows()
    {
        await SeedKnownDatasetAsync();

        var report = await Service().GetAsync(_tenantId, Filter("custom", "2026-10-01", "2026-10-03"), Ct);

        Assert.Equal(OrderReportFigures.Empty, report.Summary);
        Assert.Null(report.Summary.AverageCompletedOrderValue);
        Assert.Empty(report.Platforms);
        Assert.Empty(report.PlatformDays);
        Assert.Equal(
            [new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 3)],
            report.Days.Select(d => d.Date));
        Assert.All(report.Days, d => Assert.Equal(OrderReportFigures.Empty, d.Figures));
    }

    [Fact]
    public async Task NoCompletedOrder_LeavesTheAverageNull_ButCountsEverythingElse()
    {
        await ResetAsync();
        await SeedAsync(
            O("A", FoodPlatform.TrendyolYemek, OrderStatus.Cancelled, 45.50m, placed: Utc(11, 7, 8)),
            O("B", FoodPlatform.TrendyolYemek, OrderStatus.New, 12.25m, placed: Utc(11, 7, 9)));

        var report = await Service().GetAsync(_tenantId, Filter("today"), Ct);

        Assert.Equal(F(2, 57.75m, 0, 0m, cancelled: 1, inProgress: 1), report.Summary);
        Assert.Null(report.Summary.AverageCompletedOrderValue);
        Assert.Null(report.Platforms.Single().Figures.AverageCompletedOrderValue);
    }

    [Fact]
    public async Task LargeDecimalAmounts_SumExactly()
    {
        await ResetAsync();
        await SeedAsync(
            O("BIG-1", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 9_999_999_999_999_999.99m, placed: Utc(11, 7, 8)),
            O("BIG-2", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 0.01m, placed: Utc(11, 7, 9)),
            O("BIG-3", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 0.02m, placed: Utc(11, 7, 9)));

        var report = await Service().GetAsync(_tenantId, Filter("today"), Ct);

        Assert.Equal(10_000_000_000_000_000.02m, report.Summary.CompletedOrderValue);
        Assert.Equal(3_333_333_333_333_333.34m, report.Summary.AverageCompletedOrderValue);
        Assert.Equal(10_000_000_000_000_000.02m, Assert.Single(report.PlatformDays).Days.Sum(d => d.Figures.CompletedOrderValue));
        AssertReconciles(report);
    }

    [Fact]
    public async Task PracticeOrders_AreNotInTheReport_AndMockProviderOrdersAre()
    {
        await ResetAsync();
        var userId = Guid.NewGuid();
        await using (var db = _database.Open())
        {
            db.AppUsers.Add(new AppUser { Id = userId, Email = $"owner-{userId:N}@report.test", FullName = "Owner", Role = UserRole.Owner, CreatedAt = Now, UpdatedAt = Now });
            db.GuidedDemoSessions.Add(new GuidedDemoSession
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ScenarioCode = "practice",
                Status = OrderStatus.Delivered,
                CustomerNameKey = "Demo.Customer",
                ItemsJson = "[]",
                ReceivedAtUtc = Utc(11, 7, 8),
                ExpiresAtUtc = Utc(11, 7, 9),
                CompletedAtUtc = Utc(11, 7, 8, 30),
                CreatedAt = Now,
                UpdatedAt = Now
            });
            await db.SaveChangesAsync(Ct);
        }

        // An order a Mock provider delivered is stored like any other order and nothing marks it as test data.
        await SeedAsync(O("MOCK-1", FoodPlatform.GetirYemek, OrderStatus.Delivered, 64.90m, placed: Utc(11, 7, 7), platformStatus: "Teslim edildi"));

        var report = await Service().GetAsync(_tenantId, Filter("today"), Ct);

        Assert.Equal(F(1, 64.90m, 1, 64.90m), report.Summary);
        await using var check = _database.Open();
        Assert.Equal(1, await check.GuidedDemoSessions.CountAsync(Ct));
    }

    [Fact]
    public async Task RepeatedPollingDeliveries_CountTheOrderOnce_WithItsLatestStatus()
    {
        await ResetAsync();
        await PrepareSyncAsync();
        var externalId = $"REPEAT-{Guid.NewGuid():N}";
        var placed = DateTime.UtcNow.AddMinutes(-3);

        var created = Payload(externalId, "Created", placed, 87.65m);
        Assert.Equal(1, (await SyncAsync(created)).InsertedCount);
        Assert.Equal(1, (await SyncAsync(created)).UnchangedCount);
        Assert.Equal(1, (await SyncAsync(Payload(externalId, "Delivered", placed, 87.65m))).UpdatedCount);
        Assert.Equal(1, (await SyncAsync(Payload(externalId, "Delivered", placed, 87.65m))).UnchangedCount);

        var localDay = OrderReportFilters.LocalToday(placed, Turkey).ToString("yyyy-MM-dd");
        var report = await Service(now: DateTime.UtcNow).GetAsync(
            _tenantId, Filter("custom", localDay, localDay, now: DateTime.UtcNow), Ct);

        Assert.Equal(F(1, 87.65m, 1, 87.65m), report.Summary);
    }

    [Fact]
    public async Task EachTenant_SeesOnlyItsOwnDatabase()
    {
        await SeedKnownDatasetAsync();
        await using var other = new SqlServerTenantDatabase();
        await other.InitializeAsync();
        var otherTenant = Guid.NewGuid();
        await using (var db = other.Open())
        {
            db.Orders.Add(O("OTHER-1", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 999.99m, placed: Utc(11, 7, 8)));
            await db.SaveChangesAsync(Ct);
        }

        var factory = new TenantFactory(new Dictionary<Guid, string>
        {
            [_tenantId] = _database.ConnectionString("tenant-a"),
            [otherTenant] = other.ConnectionString("tenant-b")
        });
        var service = Service(factory);

        var mine = await service.GetAsync(_tenantId, Filter("today"), Ct);
        var theirs = await service.GetAsync(otherTenant, Filter("today"), Ct);

        Assert.Equal(F(4, 80.13m, 1, 0.10m, cancelled: 1, inProgress: 2), mine.Summary);
        Assert.Equal(F(1, 999.99m, 1, 999.99m), theirs.Summary);
        Assert.Equal([_tenantId, otherTenant], factory.Requested);
    }

    [Fact]
    public async Task DaylightSavingPeriods_PutEveryOrderOnItsOwnLocalDay()
    {
        await ResetAsync();
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        await SeedAsync(
            // 24 October CEST (+2): 23 Oct 22:00 UTC .. 24 Oct 22:00 UTC.
            O("DST-1", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 1m, placed: Utc(10, 24, 22).AddTicks(-1)),
            // 25 October has 25 hours: 24 Oct 22:00 UTC .. 25 Oct 23:00 UTC; its last hour is after the change to CET.
            O("DST-2", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 2m, placed: Utc(10, 24, 22)),
            O("DST-3", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 4m, placed: Utc(10, 25, 22, 30)),
            // 26 October CET (+1).
            O("DST-4", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 8m, placed: Utc(10, 25, 23)),
            O("DST-5", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 16m, placed: Utc(10, 26, 23).AddTicks(-1)),
            // 29 March has 23 hours: 28 Mar 23:00 UTC .. 29 Mar 22:00 UTC.
            O("DST-6", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 32m, placed: Utc(3, 29, 21, 59)),
            O("DST-7", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 64m, placed: Utc(3, 29, 22)));

        var autumnFilter = Filter("custom", "2026-10-24", "2026-10-26", zone: berlin);
        Assert.Equal(3, OrderReportService.Stretches(autumnFilter.Period).Count);
        var autumn = await Service().GetAsync(_tenantId, autumnFilter, Ct);
        Assert.Equal([1m, 6m, 24m], autumn.Days.Select(d => d.Figures.OrderValue));

        var spring = await Service().GetAsync(_tenantId, Filter("custom", "2026-03-29", "2026-03-30", zone: berlin), Ct);
        Assert.Equal([32m, 64m], spring.Days.Select(d => d.Figures.OrderValue));

        // Türkiye has one offset all year: the whole period is one stretch at +180 minutes.
        var turkey = Filter("last30days");
        Assert.Equal([new OrderReportService.Stretch(turkey.Period.StartDate, turkey.Period.UtcEndExclusive, 180)], OrderReportService.Stretches(turkey.Period));
    }

    [Fact]
    public async Task ConcurrentStatusChangesAndInserts_NeverBreakReconciliation()
    {
        await SeedKnownDatasetAsync();
        var filter = Filter("custom", "2026-11-05", "2026-11-07");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var writer = Task.Run(async () =>
        {
            var statuses = new[] { OrderStatus.Delivered, OrderStatus.Cancelled, OrderStatus.Preparing, OrderStatus.Failed };
            var i = 0;
            await using var db = _database.Open("writer");
            while (!stop.IsCancellationRequested)
            {
                var status = statuses[i % statuses.Length];
                await db.Orders.Where(o => o.ExternalOrderId.StartsWith("TY-") || o.ExternalOrderId.StartsWith("YS-"))
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.InternalStatus, status), CancellationToken.None);
                db.Orders.Add(O($"W-{i}", FoodPlatform.GetirYemek, statuses[(i + 1) % statuses.Length], 1.01m, placed: Utc(11, 5 + i % 3, 12)));
                await db.SaveChangesAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                i++;
            }

            return i;
        }, CancellationToken.None);

        var service = Service(_database.Instance("reader"));
        var reports = new List<OrderReport>();
        for (var r = 0; r < 25; r++)
            reports.Add(await service.GetAsync(_tenantId, filter, Ct));

        await stop.CancelAsync();
        var writes = await writer;

        Assert.True(writes > 0, "The writer made no change while the reports ran.");
        Assert.All(reports, AssertReconciles);
    }

    [Fact]
    public async Task Dashboard_TodayFigures_AreTheTodayReport_WithTheirPreviousMeaning()
    {
        await SeedKnownDatasetAsync();
        var time = new FixedTime(Now);
        var factory = _database.Instance("dashboard");
        var dashboard = new DashboardService(factory, new OrderReportService(factory, time), time);

        var today = await dashboard.GetTodayAsync(_tenantId, Ct);

        // Order count and value cover every status, cancellations included; Active is New to OnTheWay.
        Assert.Equal(4, today.TodayOrderCount);
        Assert.Equal(80.13m, today.TodayOrderValue);
        Assert.Equal(2, today.ActiveOrderCount);
        Assert.Equal(1, today.CancelledOrderCount);
        Assert.Equal(
            [
                new Wasla.Application.Abstractions.Dashboard.DashboardResult.PlatformSummaryRow(FoodPlatform.Yemeksepeti, 2, 50.10m),
                new Wasla.Application.Abstractions.Dashboard.DashboardResult.PlatformSummaryRow(FoodPlatform.TrendyolYemek, 2, 30.03m)
            ],
            today.PlatformSummary);
        Assert.Equal(today.TodayOrderValue, today.Today!.Summary.OrderValue);

        // The latest orders are of any day, newest order time first, and keep their provider and import times.
        Assert.Equal(10, today.RecentOrders.Count);
        Assert.Equal("YS-14", today.RecentOrders[0].ExternalOrderId);
        Assert.Equal(today.RecentOrders.Select(o => o.OrderedAtUtc).OrderByDescending(t => t), today.RecentOrders.Select(o => o.OrderedAtUtc));
        var delayedImport = today.RecentOrders.Single(o => o.ExternalOrderId == "GY-12");
        Assert.True(delayedImport.OrderedAtIsFromPlatform);
        Assert.Equal(Utc(11, 5, 20, 30), delayedImport.OrderedAtUtc);
        Assert.Equal(Utc(11, 5, 20, 30), delayedImport.CreatedAtPlatformUtc);
        Assert.Equal(Utc(11, 6, 0, 5), delayedImport.ReceivedAtUtc);
        Assert.DoesNotContain(today.RecentOrders, o => o.ExternalOrderId == "GY-5");
    }

    [Fact]
    public async Task ApiReport_MapsTheSameFigures_WithPeriodTimeZoneAndCurrency()
    {
        await SeedKnownDatasetAsync();
        var time = new FixedTime(Now);
        var factory = _database.Instance("api");
        var reports = new OrderReportService(factory, time);
        var controller = new ApiDashboardController(new FixedTenant(_tenantId), new DashboardService(factory, reports, time), reports, time)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.QueryString = new QueryString("?preset=custom&startDate=2026-11-05&endDate=2026-11-07&platform=3");

        var result = await controller.Report("custom", "2026-11-05", "2026-11-07", "3", Ct);

        var dto = Assert.IsType<OrderReportDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(
            new OrderReportPeriodDto("custom", new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 7), Utc(11, 4, 21), Utc(11, 7, 21), "Europe/Istanbul", 3, IsComplete: false),
            dto.Period);
        Assert.Equal(FoodPlatformDto.TrendyolYemek, dto.Platform);
        Assert.Equal("TRY", dto.Currency);
        Assert.Equal(new OrderReportFiguresDto(5, 235.03m, 2, 200.00m, 100.00m, 0, 0, 2, 1), dto.Summary);
        Assert.Equal([FoodPlatformDto.TrendyolYemek], dto.Platforms.Select(p => p.Platform));
        Assert.Equal([2, 1, 2], dto.Days.Select(d => d.Figures.OrderCount));
        Assert.Equal([new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 7)], dto.Days.Select(d => d.Date));
        Assert.Equal(76.55m, dto.Days[1].Figures.AverageCompletedOrderValue);
        Assert.Null(dto.Days[2].Figures.AverageCompletedOrderValue);
    }

    /// <summary>
    /// The Platforms section is built from one read: orders stored after that read reach neither its totals nor its days,
    /// and the controller makes no further read for them.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Yemeksepeti")]
    public async Task PlatformsSection_IsOneRead_WhileOrdersChange(string? platform)
    {
        await SeedKnownDatasetAsync();
        var reads = 0;
        var reports = new ChangesAfterEachRead(Service(), async () =>
        {
            reads++;
            await SeedAsync(
                O($"LATE-T-{reads}", FoodPlatform.TrendyolYemek, OrderStatus.Delivered, 7.77m, placed: Utc(11, 6, 12)),
                O($"LATE-Y-{reads}", FoodPlatform.Yemeksepeti, OrderStatus.Delivered, 8.88m, placed: Utc(11, 7, 8)));
        });
        var controller = new TenantReportsController(new FixedTenant(_tenantId), reports, new FixedTime(Now), NullLogger<TenantReportsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var onePlatform = platform is null ? "" : $"&platform={platform}";
        controller.HttpContext.Request.QueryString = new QueryString($"?preset=custom&startDate=2026-11-05&endDate=2026-11-07{onePlatform}&section=platforms");

        var view = Assert.IsType<ViewResult>(await controller.Index(Ct));
        var report = Assert.IsType<ReportPageViewModel>(view.Model).Report!;

        Assert.Equal(1, reads);
        AssertReconciles(report);
        var yemeksepeti = Assert.Single(report.PlatformDays, p => p.Platform == FoodPlatform.Yemeksepeti);
        Assert.Equal(YemeksepetiDays, yemeksepeti.Days.Select(d => d.Figures));
        Assert.Equal(
            platform is null ? F(12, 478.51m, 4, 300.09m, cancelled: 1, failed: 1, inProgress: 5, other: 1) : F(4, 83.46m, 1, 0.10m, cancelled: 1, inProgress: 2),
            report.Summary);

        var later = await Service().GetAsync(_tenantId, report.Filter, Ct);
        Assert.NotEqual(report.Summary, later.Summary);
        AssertReconciles(later);
    }

    private static void AssertReconciles(OrderReport report)
    {
        static void Consistent(OrderReportFigures f) => Assert.Equal(
            f.OrderCount,
            f.CompletedOrderCount + f.CancelledOrderCount + f.FailedOrderCount + f.InProgressOrderCount + f.OtherStatusOrderCount);

        Consistent(report.Summary);
        Assert.All(report.Platforms, p => Consistent(p.Figures));
        Assert.All(report.Days, d => Consistent(d.Figures));
        Assert.Equal(report.Summary, report.Platforms.Aggregate(OrderReportFigures.Empty, (sum, p) => sum + p.Figures));
        Assert.Equal(report.Summary, report.Days.Aggregate(OrderReportFigures.Empty, (sum, d) => sum + d.Figures));
        Assert.Equal(report.Filter.Period.DayCount, report.Days.Count);

        Assert.Equal(report.Platforms.Select(p => p.Platform), report.PlatformDays.Select(p => p.Platform));
        foreach (var (row, platform) in report.Platforms.Zip(report.PlatformDays))
        {
            Assert.Equal(report.Days.Select(d => d.Date), platform.Days.Select(d => d.Date));
            Assert.All(platform.Days, d => Consistent(d.Figures));
            Assert.Equal(row.Figures, platform.Days.Aggregate(OrderReportFigures.Empty, (sum, d) => sum + d.Figures));
        }

        for (var day = 0; day < report.Days.Count; day++)
        {
            var index = day;
            Assert.Equal(report.Days[index].Figures, report.PlatformDays.Aggregate(OrderReportFigures.Empty, (sum, p) => sum + p.Days[index].Figures));
        }
    }

    private async Task ResetAsync()
    {
        await using var db = _database.Open();
        await db.OrderItemOptions.ExecuteDeleteAsync(Ct);
        await db.OrderItems.ExecuteDeleteAsync(Ct);
        await db.Orders.ExecuteDeleteAsync(Ct);
        await db.GuidedDemoSessions.ExecuteDeleteAsync(Ct);
    }

    private async Task SeedAsync(params Order[] orders)
    {
        await using var db = _database.Open();
        db.Orders.AddRange(orders);
        await db.SaveChangesAsync(Ct);
    }

    private static Order O(string code, FoodPlatform platform, OrderStatus status, decimal total, DateTime? placed, DateTime? received = null, string platformStatus = "status") =>
        new()
        {
            Id = Guid.NewGuid(),
            Platform = platform,
            ExternalOrderId = code,
            ExternalOrderCode = code,
            IdempotencyKey = $"{platform}:{code}",
            InternalStatus = status,
            PlatformStatus = platformStatus,
            CustomerName = "Rapor Testi",
            CustomerPhone = "5313241245",
            CustomerAddress = "Test",
            TotalAmount = total,
            CreatedAtPlatform = placed,
            ReceivedAt = received ?? placed ?? throw new ArgumentException("An order needs a time."),
            CreatedAt = Now,
            UpdatedAt = Now,
            RawPayloadJson = "{}",
            ChangeVersion = 1
        };

    private async Task PrepareSyncAsync()
    {
        await using var db = _database.Open();
        if (!await db.TenantOperationalSettings.AnyAsync(Ct))
        {
            db.TenantOperationalSettings.Add(new TenantOperationalSettings
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                OrderSyncEnabled = true,
                AutoApproveNewOrders = false,
                AutoPrintReceiptOnAutoApprove = false,
                ReceiptPrintCopyCount = 1,
                OperationalMode = TenantOperationalMode.Live,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        if (!await db.PlatformConnections.AnyAsync(Ct))
        {
            db.PlatformConnections.Add(new PlatformConnection
            {
                Id = Guid.NewGuid(),
                Platform = FoodPlatform.TrendyolYemek,
                StoreId = "store-report",
                EncryptedApiKey = "fake-key",
                EncryptedApiSecret = "fake-secret",
                IsActive = true,
                SyncIntervalSeconds = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<OrderSyncTenantResult> SyncAsync(ExternalOrderDto payload) =>
        await new OrderSyncService(
            _database.Instance("sync"),
            [new FakeProvider(payload)],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            new NoNotifications(),
            NullLogger<OrderSyncService>.Instance).SyncTenantWithResultAsync(_tenantId, Ct);

    private static ExternalOrderDto Payload(string externalOrderId, string providerStatus, DateTime placedUtc, decimal total) =>
        new(
            FoodPlatform.TrendyolYemek,
            externalOrderId,
            "TY-" + externalOrderId[^12..],
            placedUtc,
            "Report Customer",
            "5313241245",
            "Test Address",
            total,
            0m,
            0m,
            total,
            PaymentMethod.CreditCard,
            PaymentStatus.Paid,
            providerStatus,
            "{}",
            [new ExternalOrderItemDto(externalOrderId + "-item", "Lahmacun", 1, total, total, null, [])],
            null);

    /// <summary>The real report, with orders changing right after every read.</summary>
    private sealed class ChangesAfterEachRead(IOrderReportService inner, Func<Task> change) : IOrderReportService
    {
        public async Task<OrderReport> GetAsync(Guid tenantId, OrderReportFilter filter, CancellationToken ct)
        {
            var report = await inner.GetAsync(tenantId, filter, ct);
            await change();
            return report;
        }
    }

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FixedTenant(Guid tenantId) : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant { get; } = new(tenantId, "Report", "report", "report.localhost");
    }

    private sealed class TenantFactory(IReadOnlyDictionary<Guid, string> connections) : ITenantDbContextFactory
    {
        public List<Guid> Requested { get; } = [];

        public Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct)
        {
            Requested.Add(tenantId);
            return Task.FromResult(new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(connections[tenantId]).Options));
        }
    }

    private sealed class FakeProvider(ExternalOrderDto payload) : IFoodPlatformClient
    {
        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<ExternalOrderDto>>([payload]);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task RejectOrderAsync(PlatformConnection connection, Order order, int? reasonId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoNotifications : ITenantChangeNotifier
    {
        public Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoReceipts : IOrderReceiptCreationService
    {
        public Task TryCreateOnOrderAcceptedAsync(Guid tenantId, Guid orderId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoAutoApprove : IOrderAutoApproveService
    {
        public Task ProcessNewlyInsertedOrderAsync(Guid tenantId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) => Task.CompletedTask;
    }
}
