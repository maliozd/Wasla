using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Onboarding;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Tenant;
using Wasla.Web.Routing;

namespace Wasla.Web.Middleware;

public sealed class TenantResolutionMiddleware
{
    private const string ItemKey = "CurrentTenant";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // Paths every host may serve, an empty host included (WAS-66): static assets the static-file middleware did not
    // answer, the error page, health probes, culture switching and the unknown-tenant page itself, so failure pages
    // render on any host without a redirect loop.
    private static readonly string[] AnyHostPrefixes =
    {
        "/css",
        "/js",
        "/lib",
        "/images",
        "/img",
        "/error",
        "/health",
        "/swagger",
        "/culture",
        "/setlanguage",
        "/tenant-not-found",
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
        IWebHostEnvironment environment,
        ILogger<TenantResolutionMiddleware> logger)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var route = ClassifyPath(path);
        if (route == RouteGroup.AnyHost)
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

        // Only the Host header decides (WAS-66); X-Forwarded-Host is never applied (TrustedProxies). The host is
        // classified first and then the path, so no path makes a host serve what its role does not
        // (docs/architecture/tenancy.md, host and path policy).
        var options = onboardingOptions.Value;
        var classified = WaslaHostClassifier.Classify(host, options.MarketingBaseDomain, options.ApplicationBaseDomain);
        switch (classified.Kind)
        {
            // Unknown, malformed, reserved and look-alike hosts serve only the any-host paths. They get the
            // unknown-tenant page without any lookup, so they cannot be told apart from an unregistered tenant address;
            // Print Bridge clients get a plain 404.
            case WaslaHostKind.Invalid:
                if (route == RouteGroup.PrintBridgeApi)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                RedirectToTenantNotFound(context, host);
                return;

            // The marketing host serves the public site, signup, checkout, Central Admin and Print Bridge; sign-in
            // asks for the tenant address.
            case WaslaHostKind.Public:
                if (route == RouteGroup.Application && IsUnder(path, "/auth"))
                {
                    RedirectToTenantAddressRequired(context);
                    return;
                }

                await _next(context);
                return;

            // The application entry is neither a tenant nor the marketing site: signup and checkout move to the
            // marketing host; Central Admin, Print Bridge and the tenant address page stay; everything else asks for the
            // tenant address.
            case WaslaHostKind.Application:
                switch (route)
                {
                    case RouteGroup.PublicFlow:
                    case RouteGroup.SignupStatus:
                        RedirectToMarketingHost(context, environment, options.MarketingBaseDomain);
                        return;
                    case RouteGroup.TenantAddressRequired:
                    case RouteGroup.CentralAdmin:
                    case RouteGroup.PrintBridgeApi:
                        await _next(context);
                        return;
                    default:
                        RedirectToTenantAddressRequired(context);
                        return;
                }
        }

        // A tenant host. Central Admin, Print Bridge (authenticated by its device token) and the status page of the
        // host's own pending registration are served without tenant resolution, as before; signup and checkout belong
        // to the marketing host.
        switch (route)
        {
            case RouteGroup.PublicFlow:
                RedirectToMarketingHost(context, environment, options.MarketingBaseDomain);
                return;
            case RouteGroup.SignupStatus:
            case RouteGroup.CentralAdmin:
            case RouteGroup.PrintBridgeApi:
                await _next(context);
                return;
        }

        // Lookups and cache keys use the normalized host, so case and a trailing dot resolve the same tenant.
        var tenantHost = classified.Host;
        var cacheKey = $"tenant:{tenantHost}";
        if (cache.TryGetValue(cacheKey, out ResolvedTenantDto? cachedTenant) && cachedTenant is not null)
        {
            await ContinueWithTenantAsync(context, logger, cachedTenant);
            return;
        }

        // A host without a tenant is remembered for a short time, and concurrent requests for it share one lookup, so
        // requests for unknown hosts cannot turn into one CentralDb query each (TenantHostMissCache).
        var lookup = await misses.GetOrLookupAsync(
            TenantHostMissCache.Key(tenantHost)!,
            () => LookupAsync(tenantHost, resolver, pendingRegistrations, logger),
            context.RequestAborted);
        if (lookup.Tenant is null)
        {
            if (lookup.PendingRegistrationId is Guid pendingId)
            {
                context.Response.Redirect($"/signup/pending/{pendingId}");
                return;
            }

            // Subdomain request but no matching tenant: block access with a friendly page.
            RedirectToTenantNotFound(context, host);
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

    /// <summary>What a path is, for the host and path policy. Prefixes match whole path segments only.</summary>
    private static RouteGroup ClassifyPath(string path)
    {
        if (path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase)
            || AnyHostPrefixes.Any(prefix => IsUnder(path, prefix)))
        {
            return RouteGroup.AnyHost;
        }

        if (IsUnder(path, "/tenant-address-required") || IsUnder(path, "/customer-access-required"))
            return RouteGroup.TenantAddressRequired;
        if (IsSignupStatusPath(path))
            return RouteGroup.SignupStatus;
        if (IsUnder(path, "/signup") || IsUnder(path, "/checkout"))
            return RouteGroup.PublicFlow;
        if (IsUnder(path, "/admin"))
            return RouteGroup.CentralAdmin;
        if (IsUnder(path, "/api/print-bridge"))
            return RouteGroup.PrintBridgeApi;

        return RouteGroup.Application;
    }

    private static bool IsUnder(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');

    /// <summary><c>/signup/pending/{id}</c>: the registration status page a pending tenant host redirects to.</summary>
    private static bool IsSignupStatusPath(string path)
    {
        const string prefix = "/signup/pending/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(path[prefix.Length..].TrimEnd('/'), "D", out _);
    }

    /// <summary>The same path and query on the configured marketing host; the host never comes from the request.</summary>
    private static void RedirectToMarketingHost(HttpContext context, IWebHostEnvironment environment, string marketingBaseDomain)
    {
        var origin = TenantWelcomeUrlBuilder.BuildCentralHomepageUrl(context.Request, environment, marketingBaseDomain).TrimEnd('/');
        context.Response.Redirect(origin + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent());
    }

    private static void RedirectToTenantAddressRequired(HttpContext context)
    {
        var attemptedUrl = $"{context.Request.Path}{context.Request.QueryString}";
        if (string.IsNullOrWhiteSpace(attemptedUrl) || attemptedUrl == "/")
        {
            context.Response.Redirect("/tenant-address-required");
            return;
        }

        context.Response.Redirect($"/tenant-address-required?returnUrl={Uri.EscapeDataString(attemptedUrl)}");
    }

    private static void RedirectToTenantNotFound(HttpContext context, string host) =>
        context.Response.Redirect($"/tenant-not-found?host={Uri.EscapeDataString(host)}");

    private enum RouteGroup
    {
        /// <summary>Tenant application pages, sign-in and everything not listed below.</summary>
        Application,

        /// <summary>Served on every host: <see cref="AnyHostPrefixes"/>.</summary>
        AnyHost,

        TenantAddressRequired,

        /// <summary>Signup and checkout.</summary>
        PublicFlow,

        /// <summary><c>/signup/pending/{id}</c>.</summary>
        SignupStatus,

        CentralAdmin,

        /// <summary>The Print Bridge device API, authenticated by <c>X-PrintBridge-Token</c>.</summary>
        PrintBridgeApi
    }
}
