using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Tenant;

namespace Wasla.Web.Middleware;

public sealed class TenantResolutionMiddleware
{
    private const string ItemKey = "CurrentTenant";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // Paths that must not require tenant resolution: admin area, static assets,
    // culture switching, swagger/health probes, and the public access-required info page.
    private static readonly string[] BypassPrefixes =
    {
        "/admin",
        "/signup",
        "/checkout",
        "/tenant-address-required",
        "/customer-access-required",
        "/tenant-not-found",
        "/culture",
        "/setlanguage",
        "/swagger",
        "/health",
        "/error",
        "/api/print-bridge",
        "/css",
        "/js",
        "/lib",
        "/images",
        "/img",
        "/favicon",
    };

    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IMemoryCache cache,
        ITenantResolver resolver,
        IPendingRegistrationService pendingRegistrations,
        TenantHostMissCache misses,
        IOptions<TenantOnboardingOptions> onboardingOptions,
        ILogger<TenantResolutionMiddleware> logger)
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

        var marketingBaseDomain = onboardingOptions.Value.MarketingBaseDomain;

        // Central/public hosts serve landing, signup, and checkout without tenant resolution.
        if (IsCentralPublicHost(host, marketingBaseDomain))
        {
            if (path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase))
            {
                var attemptedUrl = $"{context.Request.Path}{context.Request.QueryString}";
                if (string.IsNullOrWhiteSpace(attemptedUrl))
                    attemptedUrl = "/auth/login";
                var encodedReturnUrl = Uri.EscapeDataString(attemptedUrl);
                context.Response.Redirect($"/tenant-address-required?returnUrl={encodedReturnUrl}");
                return;
            }

            await _next(context);
            return;
        }

        // Non-tenant hosts that are not under the marketing domain fall through to normal routing.
        if (!IsTenantHost(host, marketingBaseDomain))
        {
            await _next(context);
            return;
        }

        var cacheKey = $"tenant:{host.ToLowerInvariant()}";
        if (cache.TryGetValue(cacheKey, out ResolvedTenantDto? cachedTenant) && cachedTenant is not null)
        {
            await ContinueWithTenantAsync(context, logger, cachedTenant);
            return;
        }

        // A host without a tenant is remembered for a short time, and concurrent requests for it share one lookup, so
        // requests for unknown hosts cannot turn into one CentralDb query each (TenantHostMissCache).
        var lookup = await misses.GetOrLookupAsync(
            TenantHostMissCache.Key(host)!,
            () => LookupAsync(host, resolver, pendingRegistrations, logger),
            context.RequestAborted);
        if (lookup.Tenant is null)
        {
            if (lookup.PendingRegistrationId is Guid pendingId)
            {
                context.Response.Redirect($"/signup/pending/{pendingId}");
                return;
            }

            // Subdomain request but no matching tenant: block access with a friendly page.
            var encodedHost = Uri.EscapeDataString(host);
            context.Response.Redirect($"/tenant-not-found?host={encodedHost}");
            return;
        }

        cache.Set(cacheKey, lookup.Tenant, CacheTtl);
        await ContinueWithTenantAsync(context, logger, lookup.Tenant);
    }

    // Runs once per host at a time, for whichever request arrived first; it is not cancelled when that request ends,
    // because other requests may be waiting for it.
    private static async Task<TenantHostLookup> LookupAsync(
        string host,
        ITenantResolver resolver,
        IPendingRegistrationService pendingRegistrations,
        ILogger logger)
    {
        var tenant = await resolver.ResolveByHostAsync(host, CancellationToken.None);
        if (tenant is not null)
            return new TenantHostLookup(tenant);

        logger.LogInformation("No tenant for host {Host}", host);

        var pending = await pendingRegistrations.GetActiveByPrimaryDomainAsync(host, CancellationToken.None);
        if (pending is null)
            return TenantHostLookup.NotFound;

        logger.LogInformation(
            "Pending registration found for host {Host}. RegistrationId={RegistrationId}, Status={Status}",
            host,
            pending.Id,
            pending.Status);
        return new TenantHostLookup(null, pending.Id, pending.Status);
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

    private static bool IsBypassPath(string path)
    {
        var p = path;
        if (p.Length > 1 && p.EndsWith('/'))
        {
            p = p.TrimEnd('/');
        }

        foreach (var prefix in BypassPrefixes)
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsCentralPublicHost(string host, string marketingBaseDomain)
    {
        var normalizedHost = NormalizeHost(host);
        var normalizedDomain = NormalizeDomain(marketingBaseDomain);

        if (normalizedHost == "localhost") return true;
        if (normalizedHost == normalizedDomain) return true;
        if (normalizedHost == $"www.{normalizedDomain}") return true;

        var hostForIpCheck = normalizedHost;
        if (hostForIpCheck.StartsWith('[') && hostForIpCheck.EndsWith(']'))
            hostForIpCheck = hostForIpCheck[1..^1];

        if (IPAddress.TryParse(hostForIpCheck, out _))
            return true;

        return false;
    }

    private static bool IsTenantHost(string host, string marketingBaseDomain)
    {
        if (IsCentralPublicHost(host, marketingBaseDomain)) return false;

        var normalizedHost = NormalizeHost(host);
        var normalizedDomain = NormalizeDomain(marketingBaseDomain);

        return normalizedHost.EndsWith("." + normalizedDomain, StringComparison.Ordinal);
    }

    private static string NormalizeHost(string host) =>
        host.Trim().TrimEnd('.').ToLowerInvariant();

    private static string NormalizeDomain(string marketingBaseDomain) =>
        marketingBaseDomain.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
}
