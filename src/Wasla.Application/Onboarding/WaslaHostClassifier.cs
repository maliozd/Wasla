using System.Net;

namespace Wasla.Application.Onboarding;

/// <summary>The role a request host plays in Wasla Orders (WAS-66).</summary>
public enum WaslaHostKind
{
    /// <summary>Not a Wasla host: unknown, malformed, reserved, an extra label or a look-alike suffix.</summary>
    Invalid,

    /// <summary>The public marketing site: the marketing domain, <c>www.</c> in front of it, <c>localhost</c> or an IP address.</summary>
    Public,

    /// <summary>The central application entry: exactly the application domain (<c>app.wasla.tr</c>).</summary>
    Application,

    /// <summary>A tenant address: exactly one valid, unreserved slug label in front of the application domain.</summary>
    Tenant
}

/// <param name="Kind">The host's role.</param>
/// <param name="Host">The host lower-cased, without a trailing dot; the form tenant lookups and cache keys use.</param>
/// <param name="Slug">The tenant slug when <paramref name="Kind"/> is <see cref="WaslaHostKind.Tenant"/>.</param>
public readonly record struct WaslaHost(WaslaHostKind Kind, string Host, string? Slug = null);

/// <summary>
/// Classifies a request host against the configured marketing and application domains (WAS-66). Only exact host
/// boundaries count: a tenant host is <c>{slug}.{applicationBaseDomain}</c> and nothing else, so extra labels, an empty
/// label, an invalid or reserved slug and names that merely contain or end with a Wasla domain
/// (<c>tenant.app.wasla.tr.evil.example</c>) are <see cref="WaslaHostKind.Invalid"/>. Case and one trailing dot are
/// normalized; the port is never part of the host.
/// </summary>
public static class WaslaHostClassifier
{
    public static WaslaHost Classify(string? host, string marketingBaseDomain, string applicationBaseDomain)
    {
        var normalized = NormalizeHost(host);
        if (normalized is null)
            return new WaslaHost(WaslaHostKind.Invalid, string.Empty);

        if (normalized == "localhost" || IsIpAddress(normalized))
            return new WaslaHost(WaslaHostKind.Public, normalized);

        var marketing = NormalizeDomain(marketingBaseDomain);
        if (marketing.Length > 0 && (normalized == marketing || normalized == "www." + marketing))
            return new WaslaHost(WaslaHostKind.Public, normalized);

        var application = NormalizeDomain(applicationBaseDomain);
        if (application.Length > 0 && normalized == application)
            return new WaslaHost(WaslaHostKind.Application, normalized);

        var slug = TryGetTenantSlug(normalized, application);
        return slug is null
            ? new WaslaHost(WaslaHostKind.Invalid, normalized)
            : new WaslaHost(WaslaHostKind.Tenant, normalized, slug);
    }

    /// <summary>
    /// The slug of <paramref name="host"/> when it is exactly one valid, unreserved label followed by the application
    /// domain; otherwise null.
    /// </summary>
    public static string? TryGetTenantSlug(string host, string applicationBaseDomain)
    {
        var normalized = NormalizeHost(host);
        var application = NormalizeDomain(applicationBaseDomain);
        if (normalized is null || application.Length == 0)
            return null;

        var suffix = "." + application;
        if (!normalized.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        var label = normalized[..^suffix.Length];
        if (label.Contains('.')
            || !RegistrationNameNormalizer.IsValidSlugFormat(label)
            || RegistrationNameNormalizer.IsReservedSlug(label))
        {
            return null;
        }

        return label;
    }

    /// <summary>A configured domain as hosts are compared with it: trimmed, lower-cased, without outer dots.</summary>
    public static string NormalizeDomain(string? domain) =>
        (domain ?? string.Empty).Trim().Trim('.').ToLowerInvariant();

    /// <summary>
    /// The host trimmed and lower-cased with one trailing dot removed, or null when it is empty or still ends with a dot
    /// or contains an empty label.
    /// </summary>
    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var normalized = host.Trim().ToLowerInvariant();
        if (normalized.EndsWith('.'))
            normalized = normalized[..^1];

        if (normalized.Length == 0 || normalized.EndsWith('.') || normalized.StartsWith('.') || normalized.Contains(".."))
            return null;

        return normalized;
    }

    private static bool IsIpAddress(string host)
    {
        var candidate = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IPAddress.TryParse(candidate, out _);
    }
}
