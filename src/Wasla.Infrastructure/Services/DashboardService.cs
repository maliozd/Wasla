using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Dashboard;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The Web and API Dashboards. Today's figures are the shared order report for today (WAS-182); this service adds the
/// latest orders and the API's synchronization statistics, which are not report figures.
/// </summary>
public sealed class DashboardService : IDashboardService
{
    private const int RecentOrderCount = 10;

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IOrderReportService _reports;
    private readonly TimeProvider _time;

    public DashboardService(ITenantDbContextFactory dbFactory, IOrderReportService reports, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _reports = reports;
        _time = time;
    }

    public async Task<DashboardResult> GetTodayAsync(Guid tenantId, CancellationToken ct)
    {
        var period = TodayPeriod();
        var report = await _reports.GetAsync(tenantId, new OrderReportFilter(period, Platform: null), ct);

        await using var db = await _dbFactory.CreateAsync(tenantId, ct);

        // The latest orders of any day by the operational order time (WAS-125), like Orders and Live Screen.
        var recent = await db.Orders.AsNoTracking()
            .OrderByDescending(o => o.OrderedAt)
            .ThenBy(o => o.Id)
            .Take(RecentOrderCount)
            .Select(o => new
            {
                o.Id,
                o.Platform,
                o.ExternalOrderId,
                o.ExternalOrderCode,
                o.InternalStatus,
                o.PlatformStatus,
                o.CustomerName,
                o.TotalAmount,
                o.CreatedAtPlatform,
                o.ReceivedAt,
                o.OrderedAt
            })
            .ToListAsync(ct);

        var summary = report.Summary;
        return new DashboardResult
        {
            Today = report,
            TodayOrderCount = summary.OrderCount,
            TodayOrderValue = summary.OrderValue,
            ActiveOrderCount = summary.InProgressOrderCount,
            CancelledOrderCount = summary.CancelledOrderCount,
            PlatformSummary = report.Platforms
                .Select(p => new DashboardResult.PlatformSummaryRow(p.Platform, p.Figures.OrderCount, p.Figures.OrderValue))
                .ToList(),
            RecentOrders = recent
                .Select(o => new DashboardResult.RecentOrderRow(
                    o.Id,
                    o.Platform,
                    o.ExternalOrderCode,
                    o.InternalStatus,
                    o.CustomerName,
                    o.TotalAmount,
                    o.OrderedAt,
                    o.CreatedAtPlatform != null)
                {
                    ExternalOrderId = o.ExternalOrderId,
                    PlatformStatus = o.PlatformStatus,
                    CreatedAtPlatformUtc = o.CreatedAtPlatform,
                    ReceivedAtUtc = o.ReceivedAt
                })
                .ToList()
        };
    }

    public async Task<DashboardSyncStatistics> GetTodaySyncStatisticsAsync(Guid tenantId, CancellationToken ct)
    {
        var period = TodayPeriod();
        var start = period.UtcStartInclusive;
        var end = period.UtcEndExclusive;

        await using var db = await _dbFactory.CreateAsync(tenantId, ct);
        var runs = db.SyncLogs.AsNoTracking().Where(s => s.StartedAt >= start && s.StartedAt < end);
        var total = await runs.CountAsync(ct);
        var succeeded = await runs.CountAsync(s => s.Status == SyncStatus.Success, ct);
        return new DashboardSyncStatistics(succeeded, total);
    }

    private OrderReportPeriod TodayPeriod() =>
        OrderReportFilters.ForPreset(OrderReportPreset.Today, _time.GetUtcNow().UtcDateTime, TimeZoneHelper.ResolveTurkeyTimeZone());
}
