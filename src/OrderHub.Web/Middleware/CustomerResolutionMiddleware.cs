using Microsoft.Extensions.Caching.Memory;
using OrderHub.Application.Abstractions.Tenant;

namespace OrderHub.Web.Middleware;

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
        ICustomerResolver resolver,
        ILogger<CustomerResolutionMiddleware> logger)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/css", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/js", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/lib", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
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
        if (cache.TryGetValue(cacheKey, out ResolvedCustomerDto? cachedCustomer) && cachedCustomer is not null)
        {
            context.Items[ItemKey] = cachedCustomer;
            await _next(context);
            return;
        }

        var customer = await resolver.ResolveByHostAsync(host, context.RequestAborted);
        if (customer is null)
        {
            logger.LogInformation("No customer for host {Host}", host);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Customer not found");
            return;
        }

        context.Items[ItemKey] = customer;
        cache.Set(cacheKey, customer, CacheTtl);

        await _next(context);
    }
}

