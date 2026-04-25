using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OrderHub.Domain.Entities.Central;
using OrderHub.Infrastructure.Persistence.Central;

namespace OrderHub.Api.Middleware;

public sealed class CustomerResolutionMiddleware
{
    private const string ItemKey = "CurrentCustomer";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly RequestDelegate _next;

    public CustomerResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IMemoryCache cache,
        CentralDbContext centralDb,
        ILogger<CustomerResolutionMiddleware> logger)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var host = context.Request.Host.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid host");
            return;
        }

        var cacheKey = $"customer:{host.ToLowerInvariant()}";

        if (cache.TryGetValue(cacheKey, out Customer? cachedCustomer) && cachedCustomer is not null)
        {
            context.Items[ItemKey] = cachedCustomer;
            await _next(context);
            return;
        }

        // Lookup must support inactive detection for proper status code.
        var customer = await centralDb.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.PrimaryDomain == host, context.RequestAborted);

        if (customer is null)
        {
            logger.LogInformation("No customer for host {Host}", host);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Customer not found");
            return;
        }

        if (!customer.IsActive)
        {
            logger.LogInformation("Inactive customer for host {Host}", host);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Service unavailable");
            return;
        }

        context.Items[ItemKey] = customer;
        cache.Set(cacheKey, customer, CacheTtl);

        await _next(context);
    }
}

