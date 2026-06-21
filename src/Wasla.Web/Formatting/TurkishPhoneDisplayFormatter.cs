namespace Wasla.Web.Formatting;

public static class TurkishPhoneDisplayFormatter
{
    public static string FormatLocalMobile(string? localNumber)
    {
        if (string.IsNullOrWhiteSpace(localNumber))
            return string.Empty;

        var digits = new string(localNumber.Where(char.IsDigit).ToArray());
        if (digits.Length == 10)
            return $"{digits[..3]} {digits[3..6]} {digits[6..8]} {digits[8..10]}";

        return localNumber.Trim();
    }

    public static string FormatWithCountryCode(string? localNumber)
    {
        var formatted = FormatLocalMobile(localNumber);
        return string.IsNullOrWhiteSpace(formatted) ? string.Empty : $"+90 {formatted}";
    }
}
