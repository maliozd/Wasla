using System.Text;
using System.Text.RegularExpressions;

namespace Wasla.Application.Onboarding;

public static class RegistrationNameNormalizer
{
    private static readonly Regex SlugPattern = new(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    public static bool IsValidSlugFormat(string slug) =>
        !string.IsNullOrWhiteSpace(slug) && SlugPattern.IsMatch(slug);

    public static string NormalizeSlug(string slug) =>
        slug.Trim().ToLowerInvariant();

    public static string GenerateSlugFromBusinessName(string businessName)
    {
        if (string.IsNullOrWhiteSpace(businessName))
            return string.Empty;

        var ascii = ToAsciiTurkish(businessName.Trim()).ToLowerInvariant();
        var builder = new StringBuilder(ascii.Length);
        var lastWasHyphen = false;

        foreach (var ch in ascii)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(ch);
                lastWasHyphen = false;
                continue;
            }

            if (char.IsWhiteSpace(ch) || ch is '-' or '_')
            {
                if (builder.Length > 0 && !lastWasHyphen)
                {
                    builder.Append('-');
                    lastWasHyphen = true;
                }
            }
        }

        return builder.ToString().Trim('-');
    }

    public static string BuildPrimaryDomain(string slug, string marketingBaseDomain)
    {
        var domain = marketingBaseDomain.Trim().TrimStart('.');
        return $"{slug}.{domain}";
    }

    /// <summary>
    /// Normalizes host input for tenant/pending-registration lookup.
    /// Accepts bare hostnames, host:port, or absolute URLs and returns lowercase host only.
    /// </summary>
    public static string? NormalizeHostForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().TrimEnd('.');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri)
            && !string.IsNullOrWhiteSpace(absoluteUri.Host))
        {
            return absoluteUri.Host.ToLowerInvariant();
        }

        if (Uri.TryCreate($"http://{trimmed}", UriKind.Absolute, out var implicitUri)
            && !string.IsNullOrWhiteSpace(implicitUri.Host))
        {
            return implicitUri.Host.ToLowerInvariant();
        }

        return trimmed.ToLowerInvariant();
    }

    public static string? ExtractSlugFromHost(string? host, string marketingBaseDomain)
    {
        var normalizedHost = NormalizeHostForComparison(host);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return null;

        var normalizedDomain = NormalizeHostForComparison(marketingBaseDomain?.Trim().TrimStart('.'));
        if (string.IsNullOrWhiteSpace(normalizedDomain))
            return null;

        var suffix = "." + normalizedDomain;
        if (!normalizedHost.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        var slug = normalizedHost[..^suffix.Length];
        if (string.Equals(slug, "www", StringComparison.Ordinal))
            return null;

        return IsValidSlugFormat(slug) ? slug : null;
    }

    private static string ToAsciiTurkish(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                'ç' or 'Ç' => 'c',
                'ğ' or 'Ğ' => 'g',
                'ı' => 'i',
                'İ' => 'i',
                'ö' or 'Ö' => 'o',
                'ş' or 'Ş' => 's',
                'ü' or 'Ü' => 'u',
                _ => ch
            });
        }

        return builder.ToString();
    }
}
