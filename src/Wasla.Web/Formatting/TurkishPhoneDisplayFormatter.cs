namespace Wasla.Web.Formatting;

public static class TurkishPhoneDisplayFormatter
{
    private const string CountryPrefix = "+90";

    /// <summary>
    /// <c>532 123 45 67</c> for a 10-digit Turkish number, also when it is stored in E.164 (<c>+905321234567</c>, as a
    /// verified registration owner phone is since WAS-192). Any other value is returned trimmed, unchanged.
    /// </summary>
    public static string FormatLocalMobile(string? localNumber)
    {
        if (string.IsNullOrWhiteSpace(localNumber))
            return string.Empty;

        var digits = new string(localNumber.Where(char.IsDigit).ToArray());
        if (digits.Length == 10)
            return Grouped(digits);

        if (IsTurkishInternational(localNumber, digits))
            return Grouped(digits[2..]);

        return localNumber.Trim();
    }

    /// <summary><c>+90 532 123 45 67</c> for a 10-digit or E.164 Turkish number; other values keep the earlier behavior.</summary>
    public static string FormatWithCountryCode(string? localNumber)
    {
        var formatted = FormatLocalMobile(localNumber);
        return string.IsNullOrWhiteSpace(formatted) ? string.Empty : $"{CountryPrefix} {formatted}";
    }

    /// <summary>
    /// A stored phone value for display: <c>+90 532 123 45 67</c> for an E.164 Turkish number, anything else exactly as it
    /// was stored (trimmed), so values typed before WAS-192 are shown as entered.
    /// </summary>
    public static string FormatStored(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return IsTurkishInternational(value, digits) ? $"{CountryPrefix} {Grouped(digits[2..])}" : value.Trim();
    }

    private static bool IsTurkishInternational(string value, string digits) =>
        value.TrimStart().StartsWith(CountryPrefix, StringComparison.Ordinal) && digits.Length == 12;

    private static string Grouped(string digits) => $"{digits[..3]} {digits[3..6]} {digits[6..8]} {digits[8..10]}";
}
