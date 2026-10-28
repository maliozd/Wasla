using Wasla.Domain.Entities.Central;

namespace Wasla.Application.Contact;

/// <summary>
/// The optional phone number of the public contact form: a Turkish number, stored as ten digits without the country
/// code. Spaces, dashes, dots, brackets, a leading <c>+90</c>, <c>90</c> or <c>0</c> are accepted while typing.
/// </summary>
public static class ContactPhoneNumber
{
    /// <summary>
    /// The ten stored digits of <paramref name="input"/>, or null when it is empty. False when it is not a Turkish
    /// landline or mobile number (ten digits starting with 2, 3, 4 or 5 after the prefix).
    /// </summary>
    public static bool TryNormalize(string? input, out string? digits)
    {
        digits = null;
        if (string.IsNullOrWhiteSpace(input))
            return true;

        var trimmed = input.Trim();
        if (trimmed.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-' or '.' or '(' or ')' or '+')))
            return false;
        if (trimmed.IndexOf('+') > 0 || trimmed.Count(c => c == '+') > 1)
            return false;

        var value = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        if (trimmed.StartsWith('+'))
        {
            if (!value.StartsWith("90", StringComparison.Ordinal))
                return false;
            value = value[2..];
        }
        else if (value.Length == ContactMessageLimits.PhoneDigits + 2 && value.StartsWith("90", StringComparison.Ordinal))
        {
            value = value[2..];
        }
        else if (value.Length == ContactMessageLimits.PhoneDigits + 1 && value.StartsWith('0'))
        {
            value = value[1..];
        }

        if (value.Length != ContactMessageLimits.PhoneDigits || value[0] is < '2' or > '5')
            return false;

        digits = value;
        return true;
    }
}
