using Microsoft.Extensions.Caching.Memory;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.Diagnostics;

namespace Wasla.Api.Middleware;

public sealed class TenantResolutionMiddleware
{
    private const string ItemKey = "CurrentTenant";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IMemoryCache cache,
        ITenantResolver resolver,
        ILogger<TenantResolutionMiddleware> logger)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/print-bridge", StringComparison.OrdinalIgnoreCase))
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

        var cacheKey = $"tenant:{host.ToLowerInvariant()}";

        if (cache.TryGetValue(cacheKey, out ResolvedTenantDto? cachedTenant) && cachedTenant is not null)
        {
            await ContinueWithTenantAsync(context, logger, cachedTenant);
            return;
        }

        var tenant = await resolver.ResolveByHostAsync(host, context.RequestAborted);
        if (tenant is null)
        {
            logger.LogInformation("No tenant for host {Host}", host);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Tenant not found");
            return;
        }

        cache.Set(cacheKey, tenant, CacheTtl);
        await ContinueWithTenantAsync(context, logger, tenant);
    }

    private async Task ContinueWithTenantAsync(HttpContext context, ILogger logger, ResolvedTenantDto tenant)
    {
        context.Items[ItemKey] = tenant;
        TenantDiagnosticContext.Apply(context, tenant.Id);
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["TenantId"] = tenant.Id.ToString("D")
        }))
        {
            await _next(context);
        }
    }
}
