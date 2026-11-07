using Wasla.Application.Onboarding;

namespace Wasla.PrintBridge.Setup;

/// <summary>
/// Which servers a setup link may send Print Bridge to, and which setup values may be sent where (WAS-91).
/// <para>
/// Production accepts only HTTPS on a Wasla Orders host: <c>wasla.tr</c> (and <c>www.</c>), <c>app.wasla.tr</c> or one
/// tenant label in front of it (<c>{slug}.app.wasla.tr</c>), classified exactly as the server classifies hosts
/// (<see cref="WaslaHostClassifier"/>): no look-alike suffix, extra label or reserved name. The development hosts
/// (<c>localhost</c>, loopback addresses, <c>wasla.local</c> and its subdomains, plain HTTP included) are accepted only by
/// Debug builds, an explicit development scenario; Release builds never accept them.
/// </para>
/// </summary>
public sealed class PrintBridgeServerTrustPolicy
{
    public const string ProductionMarketingDomain = "wasla.tr";
    public const string ProductionApplicationDomain = "app.wasla.tr";

    private readonly bool _allowDevelopmentServers;
    private readonly IReadOnlyCollection<string> _testHosts;

    private PrintBridgeServerTrustPolicy(bool allowDevelopmentServers, IReadOnlyCollection<string>? testHosts = null)
    {
        _allowDevelopmentServers = allowDevelopmentServers;
        _testHosts = testHosts ?? [];
    }

    /// <summary>Wasla production hosts over HTTPS only.</summary>
    public static PrintBridgeServerTrustPolicy Production { get; } = new(allowDevelopmentServers: false);

    /// <summary>Production hosts and, additionally, the local development hosts over HTTP or HTTPS.</summary>
    public static PrintBridgeServerTrustPolicy Development { get; } = new(allowDevelopmentServers: true);

    /// <summary>The policy of this build: <see cref="Development"/> in Debug builds, <see cref="Production"/> otherwise.</summary>
    public static PrintBridgeServerTrustPolicy ForCurrentBuild() =>
#if DEBUG
        Development;
#else
        Production;
#endif

    public bool AllowsDevelopmentServers => _allowDevelopmentServers;

    /// <summary>Test-only: production rules plus these exact host names over HTTP or HTTPS (fake servers).</summary>
    internal static PrintBridgeServerTrustPolicy ForTests(params string[] hosts) =>
        new(allowDevelopmentServers: false, hosts.Select(h => h.ToLowerInvariant()).ToArray());

    public bool IsTrusted(string? serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && IsTrusted(uri);

    public bool IsTrusted(Uri serverUri)
    {
        if (!serverUri.IsAbsoluteUri || !string.IsNullOrEmpty(serverUri.UserInfo))
            return false;

        var isHttps = string.Equals(serverUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(serverUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isHttp)
            return false;

        var host = serverUri.IdnHost.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
            return false;

        if (_testHosts.Contains(host))
            return true;

        if (_allowDevelopmentServers && IsDevelopmentHost(host))
            return true;

        if (!isHttps)
            return false;

        return WaslaHostClassifier.Classify(
                host,
                ProductionMarketingDomain,
                ProductionApplicationDomain,
                allowLocalHosts: false).Kind != WaslaHostKind.Invalid;
    }

    /// <summary>Scheme, host and port are equal; the path never matters.</summary>
    public static bool IsSameOrigin(string? first, string? second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var a)
        && Uri.TryCreate(second, UriKind.Absolute, out var b)
        && IsSameOrigin(a, b);

    public static bool IsSameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.IdnHost.TrimEnd('.'), second.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port;

    /// <summary>The host and, when it is not the scheme's default, the port: what the confirmation shows as the server.</summary>
    public static string DisplayHost(string? serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            return string.Empty;

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6)
            host = $"[{host}]";
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }

    private static bool IsDevelopmentHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "wasla.local"
        || host.EndsWith(".wasla.local", StringComparison.Ordinal);
}
