using System.Net;

namespace Wasla.PrintBridge.Services;

public sealed class PrintBridgeSetupHttpClientFactory
{
    private readonly HttpMessageHandler? _testHandler;

    public PrintBridgeSetupHttpClientFactory()
    {
    }

    /// <summary>Test-only: every setup request goes to <paramref name="testHandler"/> instead of the network.</summary>
    internal PrintBridgeSetupHttpClientFactory(HttpMessageHandler testHandler) => _testHandler = testHandler;

    public HttpClient Create(Uri serverUri)
    {
        if (_testHandler is not null)
            return new HttpClient(_testHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

        return new HttpClient(CreateHandler(serverUri), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public HttpClientHandler CreateHandler(Uri serverUri) =>
        new()
        {
            // Keep hostname and chain validation enabled. For local mkcert development
            // origins only, skip revocation because Windows Schannel cannot check a
            // local development CA revocation endpoint.
            CheckCertificateRevocationList = !PrintBridgeSetupCertificatePolicy.ShouldDisableRevocationCheck(serverUri)
        };
}

public static class PrintBridgeSetupCertificatePolicy
{
    public static bool ShouldDisableRevocationCheck(Uri serverUri)
    {
        if (!string.Equals(serverUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = serverUri.Host.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (string.Equals(host, "localhost", StringComparison.Ordinal))
            return true;

        if (IPAddress.TryParse(host, out var ipAddress))
            return IPAddress.IsLoopback(ipAddress);

        return string.Equals(host, "wasla.local", StringComparison.Ordinal) ||
               host.EndsWith(".wasla.local", StringComparison.Ordinal);
    }
}
