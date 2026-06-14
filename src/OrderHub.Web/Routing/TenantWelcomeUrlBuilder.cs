namespace OrderHub.Web.Routing;

public static class TenantWelcomeUrlBuilder
{
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

    private static int ResolvePort(HttpRequest request, IWebHostEnvironment environment)
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
