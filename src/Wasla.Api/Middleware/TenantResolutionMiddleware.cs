using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Onboarding;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Tenant;

namespace Wasla.Api.Middleware;

/// <summary>
/// Resolves the tenant of an Api request from its host under the same host contract as Wasla.Web (WAS-66,
/// <see cref="WaslaHostClassifier"/>): only exactly <c>{slug}.{ApplicationBaseDomain}</c> is looked up. Health probes and
/// Swagger (mapped only in Development) answer on any host; the Print Bridge API, authenticated by its device token,
/// answers on every valid Wasla host and is a plain 404 on any other. Every other path on a host that is not a tenant
/// host gets the unknown-tenant 404 without a CentralDb lookup.
/// </summary>
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
        TenantHostMissCache misses,
        IOptions<TenantOnboardingOptions> onboardingOptions,
        IHostEnvironment environment,
        ILogger<TenantResolutionMiddleware> logger)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        // The Trendyol GO Meal webhook stage pilot never uses the host: its one tenant comes from configuration, and the
        // receiver answers 404 while the pilot is off.
        if (IsUnder(path, "/swagger") || IsUnder(path, "/health") || IsUnder(path, "/api/webhooks/trendyol-go/meal"))
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

        // Only the Host header decides; localhost and IP addresses are valid only in Development.
        var options = onboardingOptions.Value;
        var classified = WaslaHostClassifier.Classify(
            host, options.MarketingBaseDomain, options.ApplicationBaseDomain, allowLocalHosts: environment.IsDevelopment());

        if (IsUnder(path, "/api/print-bridge"))
        {
            if (classified.Kind == WaslaHostKind.Invalid)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await _next(context);
            return;
        }

        // The marketing host, the application entry and invalid hosts are never a tenant: the unknown-tenant answer,
        // without a lookup, so a value stored in CentralDb under any other shape cannot resolve.
        if (classified.Kind != WaslaHostKind.Tenant)
        {
            await WriteTenantNotFoundAsync(context);
            return;
        }

        // Lookups and cache keys use the normalized host, as in Wasla.Web.
        var tenantHost = classified.Host;
        var cacheKey = $"tenant:{tenantHost}";

        if (cache.TryGetValue(cacheKey, out ResolvedTenantDto? cachedTenant) && cachedTenant is not null)
        {
            await ContinueWithTenantAsync(context, logger, cachedTenant);
            return;
        }

        // A host without a tenant is remembered for a short time, and concurrent requests for it share one lookup
        // (TenantHostMissCache). The lookup is not cancelled with the request that started it: others may be waiting.
        var lookup = await misses.GetOrLookupAsync(
            TenantHostMissCache.Key(tenantHost)!,
            async () =>
            {
                var resolved = await resolver.ResolveByHostAsync(tenantHost, CancellationToken.None);
                if (resolved is null)
                    logger.LogInformation("No tenant for host {Host}", tenantHost);
                return resolved is null ? TenantHostLookup.NotFound : new TenantHostLookup(resolved);
            },
            context.RequestAborted);
        if (lookup.Tenant is not { } tenant)
        {
            await WriteTenantNotFoundAsync(context);
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

    private static async Task WriteTenantNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Tenant not found");
    }

    /// <summary>The path is <paramref name="prefix"/> or below it; whole path segments only.</summary>
    private static bool IsUnder(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');
}
