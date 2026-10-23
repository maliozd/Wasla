using System.Text;
using System.Text.RegularExpressions;

namespace Wasla.Application.Onboarding;

public static class RegistrationNameNormalizer
{
    private static readonly Regex SlugPattern = new(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>A slug is one DNS label of the tenant host, so it is never longer than a label may be.</summary>
    public const int MaxSlugLength = 63;

    /// <summary>
    /// Names that are never a tenant address (WAS-66): they belong to Wasla's own hosts and products, as
    /// <c>{name}.app.wasla.tr</c> or as root-level names such as <c>admin.wasla.tr</c>. Signup refuses them and host
    /// resolution never looks them up.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>(StringComparer.Ordinal)
    {
        "admin", "api", "app", "docs", "help", "partners", "pos", "status", "www"
    };

    public static bool IsValidSlugFormat(string slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug.Length <= MaxSlugLength && SlugPattern.IsMatch(slug);

    public static bool IsReservedSlug(string slug) => ReservedSlugs.Contains(slug);

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

    /// <summary>The tenant host <c>{slug}.{applicationBaseDomain}</c>, stored as the tenant's primary domain.</summary>
    public static string BuildPrimaryDomain(string slug, string applicationBaseDomain)
    {
        var domain = applicationBaseDomain.Trim().TrimStart('.');
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

    /// <summary>
    /// The tenant slug of a host, URL or <c>host:port</c> value when it is exactly <c>{slug}.{applicationBaseDomain}</c>
    /// with a valid, unreserved slug (<see cref="WaslaHostClassifier"/>); otherwise null.
    /// </summary>
    public static string? ExtractSlugFromHost(string? host, string applicationBaseDomain)
    {
        var normalizedHost = NormalizeHostForComparison(host);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return null;

        return WaslaHostClassifier.TryGetTenantSlug(normalizedHost, applicationBaseDomain);
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
