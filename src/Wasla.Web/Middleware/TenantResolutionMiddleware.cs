using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.Options;

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
        IOptions<CustomerOnboardingOptions> onboardingOptions,
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
            context.Items[ItemKey] = cachedTenant;
            await _next(context);
            return;
        }

        var tenant = await resolver.ResolveByHostAsync(host, context.RequestAborted);
        if (tenant is null)
        {
            logger.LogInformation("No tenant for host {Host}", host);

            var pending = await pendingRegistrations.GetActiveByPrimaryDomainAsync(
                host,
                context.RequestAborted);
            if (pending is not null)
            {
                logger.LogInformation(
                    "Pending registration found for host {Host}. RegistrationId={RegistrationId}, Status={Status}",
                    host,
                    pending.Id,
                    pending.Status);
                context.Response.Redirect($"/signup/pending/{pending.Id}");
                return;
            }

            // Subdomain request but no matching tenant: block access with a friendly page.
            var encodedHost = Uri.EscapeDataString(host);
            context.Response.Redirect($"/tenant-not-found?host={encodedHost}");
            return;
        }

        context.Items[ItemKey] = tenant;
        cache.Set(cacheKey, tenant, CacheTtl);

        await _next(context);
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
