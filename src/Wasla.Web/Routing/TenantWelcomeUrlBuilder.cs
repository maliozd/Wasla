namespace Wasla.Web.Routing;

public static class TenantWelcomeUrlBuilder
{
    public static string BuildTenantAddressUrl(
        HttpRequest request,
        IWebHostEnvironment environment,
        string primaryDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryDomain);

        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = primaryDomain.Trim(),
            Port = ResolvePort(request, environment)
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    public static string BuildLoginUrl(
        HttpRequest request,
        IWebHostEnvironment environment,
        string primaryDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryDomain);

        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = primaryDomain.Trim(),
            Path = "/auth/login",
            Port = ResolvePort(request, environment)
        };

        return builder.Uri.AbsoluteUri;
    }

    public static string BuildWelcomeUrl(
        HttpRequest request,
        IWebHostEnvironment environment,
        string primaryDomain,
        string welcomeToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(welcomeToken);

        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = primaryDomain.Trim(),
            Path = "/auth/welcome",
            Port = ResolvePort(request, environment)
        };
        builder.Query = $"token={Uri.EscapeDataString(welcomeToken)}";

        return builder.Uri.AbsoluteUri;
    }

    public static string BuildPasswordResetUrl(
        HttpRequest request,
        IWebHostEnvironment environment,
        string primaryDomain,
        string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = primaryDomain.Trim(),
            Path = "/auth/reset-password",
            Port = ResolvePort(request, environment)
        };
        builder.Query = $"token={Uri.EscapeDataString(rawToken)}";

        return builder.Uri.AbsoluteUri;
    }

    public static string BuildCentralHomepageUrl(
        HttpRequest request,
        IWebHostEnvironment environment,
        string marketingBaseDomain) =>
        BuildCentralUrl(request, environment, marketingBaseDomain, "/");

    /// <summary>The signup page on the marketing host; the host comes from configuration, never from the request.</summary>
    public static string BuildCentralSignupUrl(
        HttpRequest request,
        IWebHostEnvironment environment,
        string marketingBaseDomain) =>
        BuildCentralUrl(request, environment, marketingBaseDomain, "/signup");

    private static string BuildCentralUrl(
        HttpRequest request,
        IWebHostEnvironment environment,
        string marketingBaseDomain,
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketingBaseDomain);

        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = ResolveCentralHost(request, marketingBaseDomain),
            Path = path,
            Port = ResolvePort(request, environment)
        };

        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// The application entry's sign-in (WAS-66): <c>/auth/login</c> on the configured application domain, which asks for
    /// the tenant address. The host comes from configuration, never from the request; a request on <c>localhost</c>
    /// (Development without host names) stays on <c>localhost</c>.
    /// </summary>
    public static string BuildApplicationLoginUrl(
        HttpRequest request,
        IWebHostEnvironment environment,
        string applicationBaseDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDomain);

        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = ResolveCentralHost(request, applicationBaseDomain),
            Path = "/auth/login",
            Port = ResolvePort(request, environment)
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string ResolveCentralHost(HttpRequest request, string configuredDomain)
    {
        if (string.Equals(request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return "localhost";

        return configuredDomain.Trim().TrimStart('.');
    }

    /// <summary>The request's port only in Development and Staging, so a client cannot choose it elsewhere.</summary>
    internal static int ResolvePort(HttpRequest request, IWebHostEnvironment environment)
    {
        var port = request.Host.Port;
        if (!port.HasValue)
            return -1;

        var isHttps = string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var defaultPort = isHttps ? 443 : 80;
        if (port.Value == defaultPort)
            return -1;

        if (environment.IsDevelopment() || environment.IsStaging())
            return port.Value;

        return -1;
    }
}
