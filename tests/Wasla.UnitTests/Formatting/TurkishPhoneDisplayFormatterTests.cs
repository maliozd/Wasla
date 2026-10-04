using Wasla.Web.Formatting;

namespace Wasla.UnitTests.Formatting;

public sealed class TurkishPhoneDisplayFormatterTests
{
    [Fact]
    public void FormatLocalMobile_FormatsTenDigitsFromPlainOrPunctuatedInput()
    {
        var plainResult = TurkishPhoneDisplayFormatter.FormatLocalMobile("5313241245");
        var punctuatedResult = TurkishPhoneDisplayFormatter.FormatLocalMobile("(531) 324-12-45");

        Assert.Equal("531 324 12 45", plainResult);
        Assert.Equal("531 324 12 45", punctuatedResult);
    }

    [Fact]
    public void FormatWithCountryCode_ReturnsEmptyForWhitespace()
    {
        var result = TurkishPhoneDisplayFormatter.FormatWithCountryCode("   ");

        Assert.Equal(string.Empty, result);
    }
}
