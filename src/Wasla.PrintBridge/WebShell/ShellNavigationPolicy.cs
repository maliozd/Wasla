namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// The WebView2 shell loads only packaged files, served from a synthetic HTTPS origin that is mapped to
/// the <c>shell-ui</c> folder next to the executable. The <c>.invalid</c> top-level domain is reserved
/// (RFC 2606), so the origin can never resolve to a real site.
/// </summary>
public static class ShellNavigationPolicy
{
    public const string HostName = "shell.printbridge.invalid";
    public const string DocumentPath = "/index.html";
    public const string AssetFolderName = "shell-ui";

    public static readonly Uri StartUri = new($"https://{HostName}{DocumentPath}");

    /// <summary>Top-level navigation: only the shell document itself, never a remote or local file URL.</summary>
    public static bool IsAllowedNavigation(string? uri) =>
        TryParseShellUri(uri, out var parsed)
        && string.Equals(parsed.AbsolutePath, DocumentPath, StringComparison.Ordinal)
        && string.IsNullOrEmpty(parsed.Query);

    /// <summary>Sub-resource requests: only files on the shell origin; everything else is answered with 403.</summary>
    public static bool IsAllowedResourceRequest(string? uri) => TryParseShellUri(uri, out _);

    /// <summary>Web messages are accepted only from the shell document.</summary>
    public static bool IsTrustedMessageSource(string? source) => IsAllowedNavigation(source);

    private static bool TryParseShellUri(string? uri, out Uri parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var candidate))
            return false;

        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(candidate.Host, HostName, StringComparison.OrdinalIgnoreCase)
            || !candidate.IsDefaultPort
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        parsed = candidate;
        return true;
    }
}

/// <summary>Decisions for browser features the shell never needs. Everything is denied.</summary>
public static class ShellRequestPolicy
{
    public static bool AllowNewWindow(string? uri) => false;

    public static bool AllowPermission(string? permissionKind) => false;

    public static bool AllowDownload(string? uri) => false;

    public static bool AllowExternalUriScheme(string? uri) => false;

    public static bool AllowFrameNavigation(string? uri) => false;
}
