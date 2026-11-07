using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Orders;
using Wasla.Application.Reports;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The order report (WAS-182, docs/orders/reporting.md). One SQL statement groups the cohort by platform, stored
/// status and local day and returns at most one row per group; summary, platform rows and day rows are summed from
/// those rows, so they reconcile and come from one consistent read. No order, item or payload is loaded.
/// </summary>
public sealed class OrderReportService : IOrderReportService
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public OrderReportService(ITenantDbContextFactory dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task<OrderReport> GetAsync(Guid tenantId, OrderReportFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var db = await _dbFactory.CreateAsync(tenantId, ct);
        var groups = await QueryGroupsAsync(db, filter, ct);
        return Build(filter, groups, _time.GetUtcNow().UtcDateTime);
    }

    internal sealed record Group(FoodPlatform Platform, OrderStatus Status, DateOnly Day, int Count, decimal Value);

    internal static async Task<IReadOnlyList<Group>> QueryGroupsAsync(TenantDbContext db, OrderReportFilter filter, CancellationToken ct)
    {
        var period = filter.Period;
        var start = period.UtcStartInclusive;
        var end = period.UtcEndExclusive;

        var orders = db.Orders.AsNoTracking().Where(o => o.OrderedAt >= start && o.OrderedAt < end);
        if (filter.Platform is { } platform)
            orders = orders.Where(o => o.Platform == platform);

        var key = WithLocalDay((o, day) => new { o.Platform, o.InternalStatus, Day = day }, LocalDayExpression(period));
        var rows = await orders
            .GroupBy(key)
            .Select(g => new { g.Key.Platform, g.Key.InternalStatus, g.Key.Day, Count = g.Count(), Value = g.Sum(o => o.TotalAmount) })
            .ToListAsync(ct);

        return rows.Select(r => new Group(r.Platform, r.InternalStatus, DateOnly.FromDateTime(r.Day), r.Count, r.Value)).ToList();
    }

    internal static OrderReport Build(OrderReportFilter filter, IReadOnlyList<Group> groups, DateTime generatedAtUtc)
    {
        var period = filter.Period;
        var days = new OrderReportFigures[period.DayCount];
        Array.Fill(days, OrderReportFigures.Empty);
        var platforms = new Dictionary<FoodPlatform, OrderReportFigures>();
        var summary = OrderReportFigures.Empty;

        foreach (var group in groups)
        {
            var index = group.Day.DayNumber - period.StartDate.DayNumber;
            if (index < 0 || index >= days.Length)
                throw new InvalidOperationException(
                    $"An order group was dated {group.Day:yyyy-MM-dd}, outside the report period {period.StartDate:yyyy-MM-dd}..{period.EndDate:yyyy-MM-dd}.");

            var figures = OrderReportFigures.ForStatus(group.Status, group.Count, group.Value);
            summary += figures;
            days[index] += figures;
            platforms[group.Platform] = platforms.TryGetValue(group.Platform, out var sum) ? sum + figures : figures;
        }

        var platformRows = platforms
            .OrderByDescending(p => p.Value.OrderCount)
            .ThenBy(p => (int)p.Key)
            .Select(p => new OrderReportPlatformRow(p.Key, p.Value))
            .ToList();
        var dayRows = days
            .Select((figures, i) => new OrderReportDayRow(
                period.StartDate.AddDays(i), period.DayStartsUtc[i], period.DayStartsUtc[i + 1], figures))
            .ToList();

        return new OrderReport(filter, OrderCurrency.Code, generatedAtUtc, summary, platformRows, dayRows);
    }

    /// <summary>
    /// The local day of <c>Order.OrderedAt</c> as SQL, for orders inside the period. A stretch of 24-hour days with one
    /// UTC offset is <c>CONVERT(date, DATEADD(minute, offset, OrderedAt))</c>; a day that is not 24 hours long (a
    /// daylight-saving change) is matched by its own UTC bounds. Stretches are chained by their UTC end, so every order
    /// lands on the day whose bounds hold it. A Türkiye period after 2016 is one stretch at +180 minutes.
    /// </summary>
    internal static Expression<Func<Order, DateTime>> LocalDayExpression(OrderReportPeriod period)
    {
        var order = Expression.Parameter(typeof(Order), "o");
        var orderedAt = Expression.Property(order, nameof(Order.OrderedAt));
        var stretches = Stretches(period);

        Expression DayOf(Stretch s) => s.OffsetMinutes is { } minutes
            ? Expression.Property(
                Expression.Call(orderedAt, typeof(DateTime).GetMethod(nameof(DateTime.AddMinutes))!, Expression.Constant((double)minutes)),
                nameof(DateTime.Date))
            : Expression.Constant(s.FirstDay.ToDateTime(TimeOnly.MinValue));

        Expression body = DayOf(stretches[^1]);
        for (var i = stretches.Count - 2; i >= 0; i--)
            body = Expression.Condition(Expression.LessThan(orderedAt, Expression.Constant(stretches[i].UtcEndExclusive)), DayOf(stretches[i]), body);

        return Expression.Lambda<Func<Order, DateTime>>(body, order);
    }

    /// <param name="OffsetMinutes">The stretch's UTC offset, or null for a single day that is not 24 hours long.</param>
    internal sealed record Stretch(DateOnly FirstDay, DateTime UtcEndExclusive, int? OffsetMinutes);

    internal static IReadOnlyList<Stretch> Stretches(OrderReportPeriod period)
    {
        var stretches = new List<Stretch>();
        for (var i = 0; i < period.DayCount; i++)
        {
            var date = period.StartDate.AddDays(i);
            var startUtc = period.DayStartsUtc[i];
            var endUtc = period.DayStartsUtc[i + 1];
            var offset = date.ToDateTime(TimeOnly.MinValue) - startUtc;
            int? minutes = endUtc - startUtc == Day && offset.Ticks % TimeSpan.TicksPerMinute == 0
                ? (int)offset.TotalMinutes
                : null;

            if (minutes is not null && stretches.Count > 0 && stretches[^1].OffsetMinutes == minutes)
                stretches[^1] = stretches[^1] with { UtcEndExclusive = endUtc };
            else
                stretches.Add(new Stretch(date, endUtc, minutes));
        }

        return stretches;
    }

    /// <summary>Puts <paramref name="localDay"/> in place of the second parameter of <paramref name="key"/>.</summary>
    private static Expression<Func<Order, TKey>> WithLocalDay<TKey>(
        Expression<Func<Order, DateTime, TKey>> key,
        Expression<Func<Order, DateTime>> localDay)
    {
        var order = key.Parameters[0];
        var day = new ReplaceParameter(localDay.Parameters[0], order).Visit(localDay.Body);
        var body = new ReplaceParameter(key.Parameters[1], day).Visit(key.Body);
        return Expression.Lambda<Func<Order, TKey>>(body, order);
    }

    private sealed class ReplaceParameter(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
