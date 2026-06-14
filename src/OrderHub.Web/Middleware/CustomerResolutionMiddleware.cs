using Microsoft.Extensions.Caching.Memory;
using OrderHub.Application.Abstractions.Tenant;

namespace OrderHub.Web.Middleware;

public sealed class CustomerResolutionMiddleware
{
    private const string ItemKey = "CurrentCustomer";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // Paths that must not require tenant resolution: admin area, static assets,
    // culture switching, swagger/health probes, and the public access-required info page.
    private static readonly string[] BypassPrefixes =
    {
        "/admin",
        "/signup",
        "/checkout",
        "/customer-access-required",
        "/culture",
        "/setlanguage",
        "/swagger",
        "/health",
        "/api/print-bridge",
        "/css",
        "/js",
        "/lib",
        "/images",
        "/img",
        "/favicon",
    };

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
        if (IsBypassPath(path))
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

        // Public marketing / root host (e.g. orderhub.local): no tenant. Block /auth (tenant login) with info page.
        if (!IsSubdomainRequest(host))
        {
            if (path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase))
            {
                var attemptedUrl = $"{context.Request.Path}{context.Request.QueryString}";
                if (string.IsNullOrWhiteSpace(attemptedUrl))
                    attemptedUrl = "/auth/login";
                var encodedReturnUrl = Uri.EscapeDataString(attemptedUrl);
                context.Response.Redirect($"/customer-access-required?returnUrl={encodedReturnUrl}");
                return;
            }

            await _next(context);
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

            // Subdomain request but no matching tenant: keep the existing behavior.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Customer not found");
            return;
        }

        context.Items[ItemKey] = customer;
        cache.Set(cacheKey, customer, CacheTtl);

        await _next(context);
    }

    private static bool IsBypassPath(string path)
    {
        var p = path;
        if (p.Length > 1 && p.EndsWith('/'))
        {
            p = p.TrimEnd('/');
        }

        if (string.Equals(p, "/", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var prefix in BypassPrefixes)
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsSubdomainRequest(string host)
    {
        // ahmet.orderhub.local => 3 parts (subdomain + base + tld)
        // orderhub.local => 2 parts (marketing host)
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 3;
    }
}

