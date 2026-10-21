using Wasla.Application.Onboarding;

namespace Wasla.Web.Routing;

/// <summary>
/// URLs of the public marketing site (WAS-155). The canonical host of the site is the configured apex domain: a request
/// on <c>www.</c> names the apex as canonical, and the permanent redirect from <c>www.</c> belongs to the deployment
/// (WAS-76). Application and tenant hosts (WAS-66) are built by <see cref="TenantWelcomeUrlBuilder"/>.
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
    /// The absolute canonical URL of <paramref name="path"/> on the configured marketing domain (the apex, WAS-66): the
    /// request's scheme, its port only in Development and Staging, and no query string or fragment. The host never comes
    /// from the request, except that in Development a request on <c>localhost</c> or an IP address (a local run) keeps
    /// that host.
    /// </summary>
    public static string BuildCanonicalUrl(
        HttpRequest request,
        IWebHostEnvironment environment,
        string marketingBaseDomain,
        string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(marketingBaseDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/'))
            throw new ArgumentException("The canonical path must start with '/'.", nameof(path));

        var requestHost = request.Host.Host.Trim().TrimEnd('.').ToLowerInvariant();
        var isLocalRun = environment.IsDevelopment() && WaslaHostClassifier.IsLocalHost(requestHost);

        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = isLocalRun ? requestHost : WaslaHostClassifier.NormalizeDomain(marketingBaseDomain),
            Port = TenantWelcomeUrlBuilder.ResolvePort(request, environment),
            Path = path
        };

        return builder.Uri.AbsoluteUri;
    }
}
