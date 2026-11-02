using Microsoft.Extensions.Options;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The contact message itself: what <see cref="ContactMessage.Create"/> accepts and stores, how the form's phone number
/// is normalized, the recipient setting, and the notification email the Wasla team receives. No database or network.
/// </summary>
public sealed class ContactMessageModelTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_TrimsTheValues_AndStartsWithAPendingNotification()
    {
        var message = Create(fullName: "  Ayşe Yılmaz ", restaurant: " Lezzet Ocakbaşı ", email: " ayse@lezzet.test ", phone: "5321234567", culture: " tr-TR ");

        Assert.NotEqual(Guid.Empty, message.Id);
        Assert.Equal("Ayşe Yılmaz", message.FullName);
        Assert.Equal("Lezzet Ocakbaşı", message.RestaurantName);
        Assert.Equal("ayse@lezzet.test", message.Email);
        Assert.Equal("5321234567", message.Phone);
        Assert.Equal("tr-TR", message.Culture);
        Assert.True(message.PrivacyNoticeAcknowledged);
        Assert.Equal(Now, message.CreatedAtUtc);
        Assert.Equal(ContactMessageNotificationStatus.Pending, message.NotificationStatus);
        Assert.Null(message.NotificationAttemptedAtUtc);
    }

    [Fact]
    public void Create_StoresAnEmptyPhoneAndCultureAsNull()
    {
        var message = Create(phone: "  ", culture: "");

        Assert.Null(message.Phone);
        Assert.Null(message.Culture);
    }

    [Fact]
    public void Create_RequiresThePrivacyNoticeAcknowledgement()
    {
        var error = Assert.Throws<ArgumentException>(() => Create(acknowledged: false));
        Assert.Equal("privacyNoticeAcknowledged", error.ParamName);
    }

    [Theory]
    [InlineData("", "Restaurant", "a@b.test", "Message", "fullName")]
    [InlineData("Name", " ", "a@b.test", "Message", "restaurantName")]
    [InlineData("Name", "Restaurant", "", "Message", "email")]
    [InlineData("Name", "Restaurant", "a@b.test", "\n", "message")]
    [InlineData("Name", "Restaurant", "no-at-sign", "Message", "email")]
    [InlineData("Name", "Restaurant", "a@b@c.test", "Message", "email")]
    [InlineData("Name", "Restaurant", "a b@c.test", "Message", "email")]
    [InlineData("Name", "Restaurant", "@c.test", "Message", "email")]
    public void Create_RefusesMissingOrMalformedValues(string fullName, string restaurant, string email, string text, string parameter)
    {
        var error = Assert.Throws<ArgumentException>(() => Create(fullName: fullName, restaurant: restaurant, email: email, text: text));
        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public void Create_RefusesValuesLongerThanTheirColumns()
    {
        Assert.Equal("fullName", Assert.Throws<ArgumentException>(() => Create(fullName: new string('a', ContactMessageLimits.FullNameMaxLength + 1))).ParamName);
        Assert.Equal("restaurantName", Assert.Throws<ArgumentException>(() => Create(restaurant: new string('a', ContactMessageLimits.RestaurantNameMaxLength + 1))).ParamName);
        Assert.Equal("message", Assert.Throws<ArgumentException>(() => Create(text: new string('a', ContactMessageLimits.MessageMaxLength + 1))).ParamName);
        Assert.Equal("culture", Assert.Throws<ArgumentException>(() => Create(culture: "tr-TR-extended")).ParamName);
        Assert.Equal("email", Assert.Throws<ArgumentException>(() => Create(email: new string('a', ContactMessageLimits.EmailMaxLength) + "@b.test")).ParamName);

        // At the limit is accepted.
        Assert.Equal(ContactMessageLimits.MessageMaxLength, Create(text: new string('a', ContactMessageLimits.MessageMaxLength)).Message.Length);
    }

    [Theory]
    [InlineData("532123456")]
    [InlineData("53212345678")]
    [InlineData("53212345a7")]
    public void Create_StoresAPhoneOnlyAsTenDigits(string phone)
    {
        Assert.Equal("phoneDigits", Assert.Throws<ArgumentException>(() => Create(phone: phone)).ParamName);
    }

    [Fact]
    public void Create_RefusesAnUnknownTopic()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContactMessage.Create("Name", "Restaurant", "a@b.test", null, (ContactTopic)99, "Message", null, true, Now));
    }

    [Fact]
    public void NotificationOutcome_IsRecordedWithItsTime()
    {
        var message = Create();
        var later = Now.AddSeconds(3);

        message.MarkNotificationSent(later);
        Assert.Equal((ContactMessageNotificationStatus.Sent, later), (message.NotificationStatus, message.NotificationAttemptedAtUtc!.Value));

        message.MarkNotificationFailed(later);
        Assert.Equal(ContactMessageNotificationStatus.Failed, message.NotificationStatus);

        message.MarkNotificationNotConfigured(later);
        Assert.Equal(ContactMessageNotificationStatus.NotConfigured, message.NotificationStatus);
    }

    // --- Phone number ------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("5446695691", "5446695691")]
    [InlineData("544 669 56 91", "5446695691")]
    [InlineData("+90 544 669 56 91", "5446695691")]
    [InlineData("+905446695691", "5446695691")]
    [InlineData("90 544 669 56 91", "5446695691")]
    [InlineData("0544 669 56 91", "5446695691")]
    [InlineData("0 (544) 669-56-91", "5446695691")]
    [InlineData("(212) 555.12.34", "2125551234")]
    [InlineData("0312 555 12 34", "3125551234")]
    public void Phone_IsNormalizedToTenDigits(string? input, string? expected)
    {
        Assert.True(ContactPhoneNumber.TryNormalize(input, out var digits));
        Assert.Equal(expected, digits);
    }

    [Theory]
    [InlineData("544 669 56")]
    [InlineData("544 669 56 911")]
    [InlineData("+1 544 669 56 91")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("6446695691")]
    [InlineData("1446695691")]
    [InlineData("0844 669 56 91")]
    [InlineData("544 669 56 9a")]
    [InlineData("544+669 56 91")]
    [InlineData("++90 544 669 56 91")]
    [InlineData("544/669/56/91")]
    public void Phone_ThatIsNotATurkishNumber_IsRefused(string input)
    {
        Assert.False(ContactPhoneNumber.TryNormalize(input, out var digits));
        Assert.Null(digits);
    }

    // --- Recipient and notification email ----------------------------------------------------------

    [Theory]
    [InlineData("mehmetaliozdemir@wasla.tr", true)]
    [InlineData(" team@wasla.tr ", true)]
    [InlineData("", false)]
    [InlineData("not-an-address", false)]
    [InlineData("Team <team@wasla.tr>", false)]
    [InlineData("a@wasla.tr, b@wasla.tr", false)]
    public void RecipientEmail_MustBeOneAddress(string value, bool valid)
    {
        var result = new ContactOptionsValidator().Validate(null, new ContactOptions { RecipientEmail = value });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
            Assert.Contains("Wasla:Contact:RecipientEmail", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationEmail_GoesToTheRecipient_IsSensitive_AndEncodesWhatTheSenderTyped()
    {
        var factory = new ContactMessageEmailFactory(
            new EmailTemplateRenderer(),
            Options.Create(new EmailOptions { FromName = "Wasla" }),
            Options.Create(new ContactOptions { RecipientEmail = "team@wasla.test" }));
        var message = Create(fullName: "<b>Ayşe</b>", restaurant: "Lezzet & Ocak", email: "ayse@lezzet.test", phone: "5446695691",
            text: "Merhaba,\n<script>alert(1)</script>", topic: ContactTopic.PrintBridgeSetup);

        var email = factory.BuildNotification(message);

        Assert.Equal("team@wasla.test", email.ToEmail);
        Assert.True(email.IsSensitive);
        Assert.Equal("Wasla iletişim formu: yeni mesaj (Print Bridge kurulumu)", email.Subject);
        Assert.DoesNotContain("Ayşe", email.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("lezzet", email.Subject, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("&lt;b&gt;Ayşe&lt;/b&gt;", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Lezzet &amp; Ocak", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("+90 544 669 56 91", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("2026-10-27 12:30 UTC", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(message.Id.ToString(), email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", email.HtmlBody, StringComparison.Ordinal);

        Assert.Contains("<script>alert(1)</script>", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("ayse@lezzet.test", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationEmail_WithoutAPhone_SaysSo()
    {
        var factory = new ContactMessageEmailFactory(
            new EmailTemplateRenderer(),
            Options.Create(new EmailOptions()),
            Options.Create(new ContactOptions { RecipientEmail = "team@wasla.test" }));

        var email = factory.BuildNotification(Create(phone: null));

        Assert.Contains("Telefon: -", email.TextBody, StringComparison.Ordinal);
        Assert.Equal("Wasla iletişim formu: yeni mesaj (Wasla Orders)", email.Subject);
    }

    private static ContactMessage Create(
        string fullName = "Ayşe Yılmaz",
        string restaurant = "Lezzet Ocakbaşı",
        string email = "ayse@lezzet.test",
        string? phone = "5446695691",
        string text = "Trendyol GO siparişlerimizi tek ekranda toplamak istiyoruz.",
        string? culture = "tr-TR",
        bool acknowledged = true,
        ContactTopic topic = ContactTopic.WaslaOrders) =>
        ContactMessage.Create(fullName, restaurant, email, phone, topic, text, culture, acknowledged, Now);
}
