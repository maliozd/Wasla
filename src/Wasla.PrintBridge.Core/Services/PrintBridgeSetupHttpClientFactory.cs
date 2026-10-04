using System.Net;

namespace Wasla.PrintBridge.Services;

public sealed class PrintBridgeSetupHttpClientFactory
{
    public HttpClient Create(Uri serverUri)
    {
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
