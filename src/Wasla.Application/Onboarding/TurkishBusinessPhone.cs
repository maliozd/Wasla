namespace Wasla.Application.Onboarding;

/// <summary>
/// The restaurant's business phone in a signup (mobile or landline). The signup page sends the national digits after
/// its fixed <c>+90</c>; this class reads them the same way the page does (<c>signup-phone-format.js</c>), so both refuse
/// the same input. It checks only that the number is not longer than a Türkiye number: a shorter one (a 444 line) is
/// accepted as before, and there is no extension field, so digits typed after the number make it too long.
/// </summary>
public static class TurkishBusinessPhone
{
    public const int NationalLength = 10;

    /// <summary>
    /// Every digit of the input, less one leading <c>0090</c>, <c>90</c> or trunk <c>0</c> when there are more than
    /// 10 digits (a national number never starts with 0 or 9). Other characters are ignored.
    /// </summary>
    public static string NationalDigits(string? input)
    {
        var digits = new string((input ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length <= NationalLength)
            return digits;

        if (digits.StartsWith("0090", StringComparison.Ordinal))
            return digits[4..];
        if (digits.StartsWith("90", StringComparison.Ordinal))
            return digits[2..];
        return digits.StartsWith('0') ? digits[1..] : digits;
    }

    /// <summary>More national digits than a Türkiye number has.</summary>
    public static bool IsTooLong(string? input) => NationalDigits(input).Length > NationalLength;
}
