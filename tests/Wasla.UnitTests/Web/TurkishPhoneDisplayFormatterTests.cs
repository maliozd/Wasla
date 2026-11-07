using Wasla.Web.Formatting;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The display form of Turkish phone numbers: ten digits as before, and from WAS-192 also the E.164 form in which a
/// verified registration owner phone is stored, without a doubled country code.
/// </summary>
public sealed class TurkishPhoneDisplayFormatterTests
{
    [Theory]
    [InlineData("5321234567", "+90 532 123 45 67")]
    [InlineData("532 123 45 67", "+90 532 123 45 67")]
    [InlineData("+905321234567", "+90 532 123 45 67")]
    [InlineData(" +90 532 123 45 67 ", "+90 532 123 45 67")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void WithCountryCode_FormatsTenDigitAndE164TurkishNumbers(string? input, string expected)
    {
        Assert.Equal(expected, TurkishPhoneDisplayFormatter.FormatWithCountryCode(input));
    }

    [Theory]
    [InlineData("5321234567", "532 123 45 67")]
    [InlineData("+905321234567", "532 123 45 67")]
    // Other values are returned as they were (unchanged behavior).
    [InlineData("0532 123 45 67", "0532 123 45 67")]
    [InlineData("12345", "12345")]
    public void LocalMobile_FormatsTenDigitAndE164TurkishNumbers(string input, string expected)
    {
        Assert.Equal(expected, TurkishPhoneDisplayFormatter.FormatLocalMobile(input));
    }

    [Theory]
    [InlineData("+905321234567", "+90 532 123 45 67")]
    [InlineData("0532 765 43 21", "0532 765 43 21")]
    [InlineData("5321234567", "5321234567")]
    [InlineData("+44 7700 900123", "+44 7700 900123")]
    [InlineData("  ", "")]
    public void Stored_FormatsOnlyE164TurkishNumbers_AndShowsOtherValuesAsStored(string input, string expected)
    {
        Assert.Equal(expected, TurkishPhoneDisplayFormatter.FormatStored(input));
    }
}
