namespace Wasla.Web.Routing;

/// <summary>
/// URLs of the public marketing site (WAS-155). The canonical host of the site is the apex domain: a request on
/// <c>www.</c> names the apex as canonical, and the permanent redirect from <c>www.</c> belongs to the deployment
/// (WAS-76). Application and tenant hosts (WAS-66) are not built here.
/// </summary>
public static class PublicSiteUrls
{
    /// <summary>The confirmed Wasla Instagram account, the only social destination published so far.</summary>
    public const string InstagramUrl = "https://www.instagram.com/app.wasla.tr/";

    /// <summary>The Instagram handle, shown as the link text.</summary>
    public const string InstagramHandle = "app.wasla.tr";

    /// <summary>The marketing page of Wasla POS.</summary>
    public const string PosPath = "/pos";

    /// <summary>
    /// The absolute canonical URL of <paramref name="path"/> on the current request's public origin: same scheme and
    /// port, the host without a leading <c>www.</c>, and no query string or fragment.
    /// </summary>
    public static string BuildCanonicalUrl(HttpRequest request, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/'))
            throw new ArgumentException("The canonical path must start with '/'.", nameof(path));

        var host = request.Host.Host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host["www.".Length..];

        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = host,
            Port = request.Host.Port ?? -1,
            Path = path
        };

        return builder.Uri.AbsoluteUri;
    }
}
