using System.Globalization;
using Microsoft.Extensions.Options;
using Wasla.Application.Onboarding;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Sms;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The fixed rules around owner phone verification (WAS-192): Türkiye mobile normalization, the SMS templates and their
/// fallback, and the bounds of <c>Wasla:Signup</c>. No host, database or SMS is involved.
/// </summary>
public sealed class RegistrationPhoneRulesTests
{
    [Theory]
    [InlineData("5321234567", "+905321234567")]
    [InlineData("0532 123 45 67", "+905321234567")]
    [InlineData("+90 532 123 45 67", "+905321234567")]
    [InlineData("+90 (532) 123-45-67", "+905321234567")]
    [InlineData("90 532 123 45 67", "+905321234567")]
    [InlineData("0090 532 123 45 67", "+905321234567")]
    [InlineData(" 532.123.45.67 ", "+905321234567")]
    public void TurkishMobileNumbers_AreNormalizedToE164(string input, string expected)
    {
        Assert.True(TurkishMobilePhone.TryNormalize(input, out var e164));
        Assert.Equal(expected, e164);
        Assert.Equal("5321234567", TurkishMobilePhone.NationalNumber(e164));
        Assert.Equal("+90 5** *** ** 67", TurkishMobilePhone.Mask(e164));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0212 555 12 34")]
    [InlineData("+44 7700 900123")]
    [InlineData("+90 212 555 12 34")]
    [InlineData("532 123 45 6")]
    [InlineData("532 123 45 678")]
    [InlineData("532-123-45-6a")]
    [InlineData("+9O 532 123 45 67")]
    public void OtherNumbers_AreNotOwnerMobiles(string? input)
    {
        Assert.False(TurkishMobilePhone.TryNormalize(input, out _));
    }

    [Theory]
    [InlineData("tr-TR", "Wasla doğrulama kodunuz: 042917. Kod 5 dakika geçerlidir.")]
    [InlineData("en-US", "Your Wasla verification code is 042917. It is valid for 5 minutes.")]
    [InlineData("ar-SA", "Wasla هو 042917")]
    [InlineData("ru-RU", "Wasla: 042917")]
    // A culture without its own template uses Turkish, the default culture.
    [InlineData("de-DE", "Wasla doğrulama kodunuz: 042917.")]
    public void SmsTemplates_CarryTheCodeAndItsValidity_WithoutTemplateComments(string culture, string expected)
    {
        var text = new SmsMessageTemplates().RegistrationVerificationText(CultureInfo.GetCultureInfo(culture), "042917", 5);

        Assert.NotNull(text);
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.Contains("5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("#", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutATurkishTemplate_ThereIsNoText_SoTheSendFailsClosed()
    {
        var templates = new SmsMessageTemplates(typeof(object).Assembly);

        Assert.Null(templates.RegistrationVerificationText(CultureInfo.GetCultureInfo("en-US"), "042917", 5));
    }

    [Fact]
    public void SignupDefaults_AreTheApprovedValues_AndValid()
    {
        var options = new SignupOptions();

        Assert.Equal(TimeSpan.FromMinutes(5), options.PhoneVerification.CodeLifetime);
        Assert.Equal(TimeSpan.FromSeconds(60), options.PhoneVerification.ResendWait);
        Assert.Equal(5, options.PhoneVerification.MaxFailedAttempts);
        Assert.Equal("destek@wasla.tr", options.SupportEmail);
        Assert.True(new SignupOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("CodeLifetime")]
    [InlineData("ResendWait")]
    [InlineData("MaxFailedAttempts")]
    [InlineData("MaxSendsPerHour")]
    [InlineData("IdleLifetime")]
    [InlineData("SupportEmail")]
    public void SignupValuesOutsideTheirBounds_StopStartup(string setting)
    {
        var options = new SignupOptions();
        switch (setting)
        {
            case "CodeLifetime": options.PhoneVerification.CodeLifetime = TimeSpan.FromHours(2); break;
            case "ResendWait": options.PhoneVerification.ResendWait = TimeSpan.FromMinutes(6); break;
            case "MaxFailedAttempts": options.PhoneVerification.MaxFailedAttempts = 0; break;
            case "MaxSendsPerHour": options.PhoneVerification.MaxSendsPerHour = 0; break;
            case "IdleLifetime": options.Draft.IdleLifetime = TimeSpan.FromMinutes(5); break;
            case "SupportEmail": options.SupportEmail = "not-an-address"; break;
        }

        var result = new SignupOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(setting, result.FailureMessage, StringComparison.Ordinal);
    }
}
