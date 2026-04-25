using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Dashboard;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class DashboardService : IDashboardService
{
    private readonly ICustomerDbContextFactory _dbFactory;

    public DashboardService(ICustomerDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<DashboardResult> GetTodayAsync(Guid customerId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var todayStartLocal = DateTime.SpecifyKind(nowLocal.Date, DateTimeKind.Unspecified);
        var todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(todayStartLocal, tz);
        var todayEndUtc = todayStartUtc.AddDays(1);

        var todayOrders = db.Orders.AsNoTracking()
            .Where(o => o.ReceivedAt >= todayStartUtc && o.ReceivedAt < todayEndUtc);

        var todayOrderCount = await todayOrders.CountAsync(ct);
        var todayRevenue = await todayOrders.SumAsync(o => o.TotalAmount, ct);

        var activeCount = await todayOrders.CountAsync(o =>
            o.InternalStatus == OrderStatus.New ||
            o.InternalStatus == OrderStatus.Accepted ||
            o.InternalStatus == OrderStatus.Preparing ||
            o.InternalStatus == OrderStatus.ReadyForPickup ||
            o.InternalStatus == OrderStatus.OnTheWay, ct);

        var cancelledCount = await todayOrders.CountAsync(o => o.InternalStatus == OrderStatus.Cancelled, ct);

        var platform = await todayOrders
            .GroupBy(o => o.Platform)
            .Select(g => new DashboardResult.PlatformSummaryRow(
                g.Key,
                g.Count(),
                g.Sum(x => x.TotalAmount)))
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        var recent = await db.Orders.AsNoTracking()
            .OrderByDescending(o => o.ReceivedAt)
            .Take(10)
            .Select(o => new DashboardResult.RecentOrderRow(
                o.Id,
                o.Platform,
                o.ExternalOrderCode,
                o.InternalStatus,
                o.CustomerName,
                o.TotalAmount,
                o.ReceivedAt))
            .ToListAsync(ct);

        return new DashboardResult
        {
            TodayOrderCount = todayOrderCount,
            TodayRevenue = todayRevenue,
            ActiveOrderCount = activeCount,
            CancelledOrderCount = cancelledCount,
            PlatformSummary = platform,
            RecentOrders = recent
        };
    }
}

