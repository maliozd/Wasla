using Microsoft.AspNetCore.Http;

namespace OrderHub.Web.Services;

/// <summary>
/// Forwards the incoming browser request's Cookie and Host headers
/// to the outbound API request. This lets the API see the same authenticated
/// user and the same tenant subdomain as the browser sees.
/// </summary>
public sealed class CookieForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CookieForwardingHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is not null)
        {
            // Forward Cookie header
            if (ctx.Request.Headers.TryGetValue("Cookie", out var cookies))
            {
                request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookies.ToString());
            }

            // Forward Host header so CustomerResolutionMiddleware can resolve the tenant
            // The API is on a different port but the same hostname (e.g. ahmet.orderhub.local)
            var browserHost = ctx.Request.Host.Host;
            if (!string.IsNullOrEmpty(browserHost))
            {
                request.Headers.Host = browserHost;
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}

