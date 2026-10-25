using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Wasla.Web.Security;

/// <summary>
/// Where a tenant user goes after signing in (WAS-113): a safe local return URL the user may open, otherwise the
/// user's landing page. Both are decided by the destination endpoint's own <c>[Authorize]</c> metadata and the
/// registered tenant policies, so the result is always a page the given principal is authorized to open on this host.
/// </summary>
public interface ITenantLandingPageResolver
{
    /// <summary>The first of <see cref="TenantLandingPageResolver.LandingPages"/> the user may open, or null.</summary>
    Task<string?> GetLandingPageAsync(ClaimsPrincipal user, HttpContext httpContext);

    /// <summary>
    /// <paramref name="returnUrl"/> when it is a safe local page the user may open, otherwise the user's landing page.
    /// Null when the user may open neither.
    /// </summary>
    Task<string?> ResolveAsync(ClaimsPrincipal user, string? returnUrl, HttpContext httpContext);
}

public sealed class TenantLandingPageResolver : ITenantLandingPageResolver
{
    /// <summary>
    /// Landing pages in order of preference. With the current policies: Owner, Manager and Viewer get the Dashboard
    /// (<c>CanViewReports</c>); Kitchen and Cashier get the Live Screen (<c>CanViewLiveScreen</c>). Orders
    /// (<c>CanViewOrders</c>) is the last resort.
    /// </summary>
    public static readonly IReadOnlyList<string> LandingPages = ["/dashboard", "/orders/live-display", "/orders"];

    private readonly TenantPageEndpointMatcher _endpoints;
    private readonly IAuthorizationPolicyProvider _policies;
    private readonly IAuthorizationService _authorization;

    public TenantLandingPageResolver(
        TenantPageEndpointMatcher endpoints,
        IAuthorizationPolicyProvider policies,
        IAuthorizationService authorization)
    {
        _endpoints = endpoints;
        _policies = policies;
        _authorization = authorization;
    }

    public async Task<string?> GetLandingPageAsync(ClaimsPrincipal user, HttpContext httpContext)
    {
        foreach (var page in LandingPages)
        {
            if (await MayOpenAsync(user, page, httpContext).ConfigureAwait(false))
                return page;
        }

        return null;
    }

    public async Task<string?> ResolveAsync(ClaimsPrincipal user, string? returnUrl, HttpContext httpContext)
    {
        if (TenantReturnUrl.TryGetLocalPath(returnUrl, out var path)
            && await MayOpenAsync(user, path, httpContext).ConfigureAwait(false))
        {
            return returnUrl;
        }

        return await GetLandingPageAsync(user, httpContext).ConfigureAwait(false);
    }

    /// <summary>
    /// True when a GET request for <paramref name="path"/> reaches a controller action that requires a tenant session
    /// and whose combined authorization policy admits <paramref name="user"/> for the tenant resolved for this request.
    /// Anonymous pages, other schemes (Central Admin), non-MVC endpoints and unmatched or ambiguous paths are refused.
    /// </summary>
    private async Task<bool> MayOpenAsync(ClaimsPrincipal user, PathString path, HttpContext httpContext)
    {
        var endpoint = await _endpoints.MatchGetAsync(path, httpContext).ConfigureAwait(false);
        if (endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>() is null
            || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return false;
        }

        var policy = await AuthorizationPolicy.CombineAsync(
            _policies,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>()).ConfigureAwait(false);
        if (policy is null || policy.AuthenticationSchemes.Any(scheme => scheme != AuthSchemes.Tenant))
            return false;

        var result = await _authorization.AuthorizeAsync(user, resource: null, policy).ConfigureAwait(false);
        return result.Succeeded;
    }
}

/// <summary>
/// Finds the endpoint a GET request for a path on this host would reach, with the application's own route matcher
/// (the same endpoints, constraints and precedence as a real request). Nothing is executed and no tenant or session
/// work is done; the caller authorizes the endpoint it gets back.
/// </summary>
public sealed class TenantPageEndpointMatcher
{
    private readonly ILogger<TenantPageEndpointMatcher> _logger;
    private RequestDelegate? _match;

    public TenantPageEndpointMatcher(ILogger<TenantPageEndpointMatcher> logger)
    {
        _logger = logger;
    }

    internal void Initialize(RequestDelegate match) => _match = match;

    public async Task<Endpoint?> MatchGetAsync(PathString path, HttpContext httpContext)
    {
        var match = _match ?? throw new InvalidOperationException(
            "The tenant page matcher is not initialized. Call EnableWaslaTenantLandingPages() on the application.");

        var probe = new DefaultHttpContext { RequestServices = httpContext.RequestServices };
        probe.Request.Method = HttpMethods.Get;
        probe.Request.Scheme = httpContext.Request.Scheme;
        probe.Request.Host = httpContext.Request.Host;
        probe.Request.Path = path;
        try
        {
            await match(probe).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An ambiguous match: a real request would fail too, so the path is not a destination.
            _logger.LogDebug("No single endpoint matched a return path ({ExceptionType}).", ex.GetType().Name);
            return null;
        }

        return probe.GetEndpoint();
    }
}

/// <summary>Return URL rules: only a plain path on this host, never one that could mean another host once decoded.</summary>
public static class TenantReturnUrl
{
    private const int MaxLength = 2048;

    /// <summary>
    /// Accepts <c>/path</c> or <c>/path?query</c> on this host and gives the decoded path to match. Refuses other
    /// hosts and schemes (<c>//host</c>, <c>/\host</c>, <c>https:</c>, <c>~/</c>), backslashes, control and white-space
    /// characters, fragments, non-ASCII text, encoded slashes, backslashes and percent signs (double encoding),
    /// malformed escapes, dot segments, and the sign-in pages under <c>/auth</c> (login, logout, Access denied), which
    /// would loop.
    /// </summary>
    public static bool TryGetLocalPath(string? returnUrl, out PathString path)
    {
        path = default;
        if (string.IsNullOrEmpty(returnUrl) || returnUrl.Length > MaxLength)
            return false;

        if (returnUrl[0] != '/' || (returnUrl.Length > 1 && returnUrl[1] is '/' or '\\'))
            return false;

        foreach (var c in returnUrl)
        {
            if (c is '\\' or '#' || c <= ' ' || c >= '\u007f')
                return false;
        }

        var queryStart = returnUrl.IndexOf('?');
        var rawPath = queryStart < 0 ? returnUrl : returnUrl[..queryStart];
        if (rawPath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || rawPath.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || rawPath.Contains("%25", StringComparison.Ordinal))
        {
            return false;
        }

        // A percent sign left after decoding is a malformed escape.
        var decoded = Uri.UnescapeDataString(rawPath);
        if (decoded.Contains('%') || decoded.Contains('\\') || decoded.Contains("//", StringComparison.Ordinal)
            || decoded.Any(char.IsControl))
        {
            return false;
        }

        if (decoded.Split('/').Any(segment => segment is "." or ".."))
            return false;

        if (decoded.Equals("/auth", StringComparison.OrdinalIgnoreCase)
            || decoded.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = new PathString(decoded);
        return true;
    }
}

public static class TenantLandingPageRegistration
{
    public static IServiceCollection AddWaslaTenantLandingPages(this IServiceCollection services)
    {
        services.AddSingleton<TenantPageEndpointMatcher>();
        services.AddScoped<ITenantLandingPageResolver, TenantLandingPageResolver>();
        return services;
    }

    /// <summary>
    /// Gives <see cref="TenantPageEndpointMatcher"/> a routing-only branch of <paramref name="app"/> that matches against
    /// the application's own endpoint sources. Call it after every endpoint is mapped. The branch stops after matching,
    /// so no endpoint ever runs in it, and nothing is added to the application's own pipeline.
    /// </summary>
    public static WebApplication EnableWaslaTenantLandingPages(this WebApplication app)
    {
        var sources = ((IEndpointRouteBuilder)app).DataSources.ToList();
        var branch = ((IApplicationBuilder)app).New();
        branch.UseRouting();
        branch.Use(static (HttpContext _, RequestDelegate _) => Task.CompletedTask);
        branch.UseEndpoints(endpoints =>
        {
            foreach (var source in sources)
                endpoints.DataSources.Add(source);
        });
        app.Services.GetRequiredService<TenantPageEndpointMatcher>().Initialize(branch.Build());
        return app;
    }
}
