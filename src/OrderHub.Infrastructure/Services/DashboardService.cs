using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Dashboard;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class DashboardService : IDashboardService
{
    private readonly ITenantDbContextFactory _dbFactory;

    public DashboardService(ITenantDbContextFactory dbFactory)
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

        // EF Core translation can be finicky with record constructors inside GroupBy projections.
        // Keep the DB query to anonymous types, then map in-memory.
        var platformRaw = await todayOrders
            .GroupBy(o => o.Platform)
            .Select(g => new
            {
                Platform = g.Key,
                Count = g.Count(),
                Revenue = (decimal?)g.Sum(x => x.TotalAmount)
            })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        var platform = platformRaw
            .Select(x => new DashboardResult.PlatformSummaryRow(
                x.Platform,
                x.Count,
                x.Revenue ?? 0m))
            .ToList();

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

