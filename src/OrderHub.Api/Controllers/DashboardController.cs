using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly ICustomerDbContextFactory _customerDbFactory;

    public DashboardController(ICurrentCustomerService currentCustomer, ICustomerDbContextFactory customerDbFactory)
    {
        _currentCustomer = currentCustomer;
        _customerDbFactory = customerDbFactory;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        await using var db = await _customerDbFactory.CreateAsync(customer.Id, ct);

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var todayStartLocal = DateTime.SpecifyKind(nowLocal.Date, DateTimeKind.Unspecified);
        var todayStart = TimeZoneInfo.ConvertTimeToUtc(todayStartLocal, tz);
        var todayEnd = todayStart.AddDays(1);

        var todayQuery = db.Orders.AsNoTracking()
            .Where(o => o.CreatedAtPlatform >= todayStart && o.CreatedAtPlatform < todayEnd);

        var todayOrderCount = await todayQuery.CountAsync(ct);
        var revenue = await todayQuery.SumAsync(o => o.TotalAmount, ct);

        var pendingCount = await todayQuery
            .CountAsync(o =>
                o.InternalStatus == OrderStatus.New ||
                o.InternalStatus == OrderStatus.Accepted ||
                o.InternalStatus == OrderStatus.Preparing ||
                o.InternalStatus == OrderStatus.ReadyForPickup ||
                o.InternalStatus == OrderStatus.OnTheWay, ct);

        var breakdown = await todayQuery
            .GroupBy(o => o.Platform)
            .Select(g => new
            {
                platform = g.Key,
                count = g.Count(),
                revenue = g.Sum(x => x.TotalAmount)
            })
            .ToListAsync(ct);

        var recent = await db.Orders.AsNoTracking()
            .OrderByDescending(o => o.CreatedAtPlatform)
            .Take(10)
            .Select(o => new
            {
                o.Id,
                o.Platform,
                o.ExternalOrderCode,
                o.InternalStatus,
                o.CustomerName,
                o.TotalAmount,
                o.CreatedAtPlatform
            })
            .ToListAsync(ct);

        return Ok(new
        {
            todayOrderCount,
            revenue,
            pendingCount,
            platformBreakdown = breakdown,
            recentOrders = recent
        });
    }
}

