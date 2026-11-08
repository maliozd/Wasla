namespace Wasla.Application.Onboarding;

/// <summary>
/// The registration owner's mobile phone (WAS-192): Türkiye mobile numbers only, stored in E.164 (<c>+905321234567</c>).
/// Numbers outside Türkiye are an open product decision.
/// </summary>
public static class TurkishMobilePhone
{
    public const string CountryPrefix = "+90";

    /// <summary>
    /// Normalizes typed input. Spaces, hyphens, dots and parentheses are ignored; one leading <c>+90</c>, <c>0090</c>,
    /// <c>90</c> (12 digits in total) or <c>0</c> (11 digits in total) is removed; what remains must be exactly 10 digits
    /// starting with <c>5</c>. Anything else, landlines and other countries included, is not a valid owner mobile phone.
    /// </summary>
    public static bool TryNormalize(string? input, out string e164)
    {
        e164 = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var compact = new System.Text.StringBuilder(input.Length);
        foreach (var ch in input.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '.' or '(' or ')')
                continue;
            compact.Append(ch);
        }

        var value = compact.ToString();
        if (value.StartsWith('+'))
        {
            if (!value.StartsWith(CountryPrefix, StringComparison.Ordinal))
                return false;
            value = value[CountryPrefix.Length..];
        }
        else if (value.StartsWith("0090", StringComparison.Ordinal) && value.Length == 14)
        {
            value = value[4..];
        }
        else if (value.StartsWith("90", StringComparison.Ordinal) && value.Length == 12)
        {
            value = value[2..];
        }
        else if (value.StartsWith('0') && value.Length == 11)
        {
            value = value[1..];
        }

        if (value.Length != 10 || value[0] != '5' || !value.All(char.IsAsciiDigit))
            return false;

        e164 = CountryPrefix + value;
        return true;
    }

    public static bool IsValid(string? input) => TryNormalize(input, out _);

    /// <summary>The 10-digit national number of a normalized E.164 value, for example <c>5321234567</c>.</summary>
    public static string NationalNumber(string e164) =>
        e164.StartsWith(CountryPrefix, StringComparison.Ordinal) ? e164[CountryPrefix.Length..] : e164;

    /// <summary>All but the first and the last two digits hidden: <c>+90 5** *** ** 67</c>.</summary>
    public static string Mask(string e164)
    {
        var national = NationalNumber(e164);
        if (national.Length != 10)
            return CountryPrefix + " ** *** ** **";

        return $"{CountryPrefix} {national[0]}** *** ** {national[8..]}";
    }
}
