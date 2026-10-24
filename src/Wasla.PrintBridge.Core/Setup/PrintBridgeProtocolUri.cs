using System.Globalization;

namespace Wasla.PrintBridge.Setup;

/// <summary>Parsed, validated automatic-setup request from a <c>wasla-printbridge://setup</c> URI.</summary>
public sealed record PrintBridgeProtocolSetupRequest(string ServerUrl, string Code);

/// <summary>
/// Strict parser/validator for the custom <c>wasla-printbridge://setup?server=...&amp;code=...</c> protocol URI.
///
/// Pure logic with no WinForms/Windows dependency so it can be unit tested directly.
/// The setup code is treated as a secret: it is validated but never logged by this type.
/// </summary>
public static class PrintBridgeProtocolUri
{
    public const string Scheme = "wasla-printbridge";
    public const string SetupAction = "setup";

    public const int MaxUriLength = 4096;
    public const int MaxServerUrlLength = 2048;
    public const int MinCodeLength = 16;
    public const int MaxCodeLength = 512;

    private static readonly string[] AllowedProductionHostSuffixes =
    [
        "wasla.local",
        "wasla.com",
        "wasla.app"
    ];

    public static bool TryParseSetup(
        string? uri,
        out PrintBridgeProtocolSetupRequest? request,
        out string? errorCode)
    {
        request = null;
        errorCode = null;

        if (string.IsNullOrWhiteSpace(uri))
        {
            errorCode = "empty";
            return false;
        }

        var trimmed = uri.Trim();
        if (trimmed.Length > MaxUriLength)
        {
            errorCode = "too_long";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            errorCode = "malformed";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "scheme";
            return false;
        }

        // wasla-printbridge://setup?... — the host segment carries the action.
        if (!string.Equals(parsed.Host, SetupAction, StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "action";
            return false;
        }

        string? server = null;
        string? code = null;

        foreach (var (key, value) in EnumerateQuery(parsed.Query))
        {
            switch (key.ToLowerInvariant())
            {
                case "server":
                    if (server is not null) { errorCode = "duplicate_param"; return false; }
                    server = value;
                    break;
                case "code":
                    if (code is not null) { errorCode = "duplicate_param"; return false; }
                    code = value;
                    break;
                default:
                    errorCode = "unexpected_param";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(server))
        {
            errorCode = "missing_server";
            return false;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            errorCode = "missing_code";
            return false;
        }

        server = server.Trim();
        code = code.Trim();

        if (server.Length > MaxServerUrlLength)
        {
            errorCode = "server_too_long";
            return false;
        }

        if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri) ||
            (!string.Equals(serverUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(serverUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            errorCode = "invalid_server_url";
            return false;
        }

        if (!IsAllowedSetupServer(serverUri))
        {
            errorCode = "untrusted_server_url";
            return false;
        }

        if (code.Length < MinCodeLength || code.Length > MaxCodeLength || !IsBase64Url(code))
        {
            errorCode = "invalid_code";
            return false;
        }

        request = new PrintBridgeProtocolSetupRequest(server, code);
        return true;
    }

    private static IEnumerable<(string Key, string Value)> EnumerateQuery(string query)
    {
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
            yield break;

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                yield return (Uri.UnescapeDataString(pair), string.Empty);
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
            yield return (key, value);
        }
    }

    private static bool IsBase64Url(string value)
    {
        foreach (var c in value)
        {
            var ok = c is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_';
            if (!ok)
                return false;
        }

        return true;
    }

    private static bool IsAllowedSetupServer(Uri serverUri)
    {
        var host = serverUri.Host.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (IsLocalDevelopmentHost(host))
            return true;

        if (!string.Equals(serverUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return AllowedProductionHostSuffixes.Any(suffix =>
            string.Equals(host, suffix, StringComparison.Ordinal) ||
            host.EndsWith("." + suffix, StringComparison.Ordinal));
    }

    private static bool IsLocalDevelopmentHost(string host) =>
        string.Equals(host, "localhost", StringComparison.Ordinal) ||
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(host, "::1", StringComparison.Ordinal) ||
        string.Equals(host, "wasla.local", StringComparison.Ordinal) ||
        host.EndsWith(".wasla.local", StringComparison.Ordinal);

    /// <summary>Builds the canonical protocol URI. Used for diagnostics/round-trip tests only.</summary>
    public static string BuildSetupUri(string serverUrl, string code) =>
        string.Create(CultureInfo.InvariantCulture, $"{Scheme}://{SetupAction}?server={Uri.EscapeDataString(serverUrl)}&code={Uri.EscapeDataString(code)}");
}
