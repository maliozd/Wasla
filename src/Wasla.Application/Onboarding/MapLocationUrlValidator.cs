namespace Wasla.Application.Onboarding;

public static class MapLocationUrlValidator
{
    public static bool IsValidOptionalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }
}
