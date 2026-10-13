namespace Wasla.Web.Realtime;

/// <summary>
/// Same-origin check for the live hub. Browsers do not apply CORS to WebSockets, and a tenant cookie is sent to its
/// host from any same-site page (for example another tenant's subdomain). So a hub request (negotiate, WebSocket, or
/// another transport) that carries an <c>Origin</c> header is refused with 403 unless that origin's host, and port when
/// the request names one, is the request's own host. The scheme is not compared: behind a TLS-terminating proxy the
/// application may see http while the page is https. A request without <c>Origin</c> is not from a browser page and
/// cannot carry a victim's cookie, so it is left to authentication. Runs before tenant resolution and authentication,
/// so a refused request opens no database.
/// </summary>
public sealed class RealtimeOriginMiddleware
{
    public const string PathPrefix = "/hubs";

    private readonly RequestDelegate _next;

    public RealtimeOriginMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase)
            && context.Request.Headers.Origin.Count > 0
            && !IsSameOrigin(context.Request.Headers.Origin.ToString(), context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }

        return _next(context);
    }

    public static bool IsSameOrigin(string origin, HostString requestHost)
    {
        if (!requestHost.HasValue
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        if (!string.Equals(uri.IdnHost, requestHost.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        return requestHost.Port is null || requestHost.Port == uri.Port;
    }
}
