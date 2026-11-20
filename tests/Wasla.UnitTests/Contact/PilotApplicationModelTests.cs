using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The pilot application rules that need no host (WAS-217): what a stored application must hold, when Central Admin may
/// send its notification again, which pilot address is valid, and when the form is published.
/// </summary>
public sealed class PilotApplicationModelTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Id = Guid.Parse("6f0e1c7a-6d1a-4c55-9a47-1c7b3a0e2d11");

    [Fact]
    public void APilotApplication_KeepsItsSubmissionId_TheCity_AndNoConsentFlag()
    {
        var application = ContactMessage.CreatePilotApplication(Id, " Lezzet Ocakbaşı ", " Ayşe Kaya ", "ayse@lezzet.test", "5324182790", " 34 ", " ", "tr-TR", Now);

        Assert.Equal(Id, application.Id);
        Assert.Equal(ContactTopic.PilotApplication, application.Topic);
        Assert.Equal(("Lezzet Ocakbaşı", "Ayşe Kaya", "34"), (application.RestaurantName, application.FullName, application.CityPlateCode));
        Assert.Equal(string.Empty, application.Message);
        Assert.False(application.PrivacyNoticeAcknowledged);
        Assert.Equal(ContactMessageNotificationStatus.Pending, application.NotificationStatus);
    }

    [Theory]
    [InlineData("", "34")]          // the phone is required
    [InlineData("53241827", "34")]  // not ten digits
    [InlineData("5324182790", "")]  // the city is required
    [InlineData("5324182790", "00")]
    [InlineData("5324182790", "82")]
    [InlineData("5324182790", "3")]
    [InlineData("5324182790", "İstanbul")]
    public void APilotApplication_WithoutAValidPhoneOrCity_IsNotCreated(string phone, string city)
    {
        Assert.Throws<ArgumentException>(() =>
            ContactMessage.CreatePilotApplication(Id, "Lezzet Ocakbaşı", "Ayşe Kaya", "ayse@lezzet.test", phone, city, null, "tr-TR", Now));
    }

    [Fact]
    public void APilotApplication_NeedsItsSubmissionId_AndANoteWithinTheLimit()
    {
        Assert.Throws<ArgumentException>(() =>
            ContactMessage.CreatePilotApplication(Guid.Empty, "Lezzet", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", null, "tr-TR", Now));
        Assert.Throws<ArgumentException>(() =>
            ContactMessage.CreatePilotApplication(Id, "Lezzet", "Ayşe Kaya", "ayse@lezzet.test", "5324182790", "34", new string('a', ContactMessageLimits.MessageMaxLength + 1), "tr-TR", Now));
    }

    [Fact]
    public void TheContactFormFactory_DoesNotCreatePilotApplications()
    {
        Assert.Throws<ArgumentException>(() =>
            ContactMessage.Create("Ayşe Kaya", "Lezzet", "ayse@lezzet.test", null, ContactTopic.PilotApplication, "Merhaba", "tr-TR", true, Now));
    }

    [Theory]
    [InlineData(ContactMessageNotificationStatus.Failed, 0, true)]
    [InlineData(ContactMessageNotificationStatus.NotConfigured, 0, true)]
    [InlineData(ContactMessageNotificationStatus.Sent, 60, false)]
    [InlineData(ContactMessageNotificationStatus.Pending, 5, false)]
    [InlineData(ContactMessageNotificationStatus.Pending, 15, true)]
    public void AResend_IsAllowedAfterAFailureOrAStuckAttemptOnly(ContactMessageNotificationStatus status, int minutesAgo, bool allowed)
    {
        Assert.Equal(allowed, ContactNotificationRetry.CanResend(status, Now.AddMinutes(-minutesAgo), Now.AddMinutes(-minutesAgo), Now));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("pilot@wasla.test", true)]
    [InlineData("Pilot <pilot@wasla.test>", false)]
    [InlineData("not-an-address", false)]
    public void ThePilotAddress_IsOptional_ButMustBeOnePlainAddress(string? address, bool valid)
    {
        var result = new ContactOptionsValidator().Validate(null, new ContactOptions { RecipientEmail = "team@wasla.test", PilotRecipientEmail = address });

        Assert.Equal(valid, result.Succeeded);
    }

    [Theory]
    [InlineData(false, false, "Production", false)]
    [InlineData(true, false, "Production", false)]  // enabled, but the notice lacks its controller and legal facts
    [InlineData(true, true, "Production", true)]
    [InlineData(true, false, "Development", true)]  // a draft notice outside Production
    [InlineData(true, false, "Staging", true)]
    [InlineData(false, true, "Development", false)]
    public void TheForm_IsOpenOnlyWhenEnabled_AndProductionHasEveryNoticeFact(bool enabled, bool completeNotice, string environment, bool open)
    {
        var gate = new PilotApplicationGate(Options.Create(new PilotApplicationOptions
        {
            Enabled = enabled,
            PrivacyNotice = completeNotice ? CompleteNotice() : new PilotPrivacyNoticeOptions { ControllerName = "Örnek Test Ltd." }
        }), new Environment(environment));

        Assert.Equal(open, gate.IsOpen);
        Assert.Equal(!completeNotice, gate.NoticeIsDraft);
        Assert.Equal(enabled && !completeNotice && environment == "Production", gate.ClosedForMissingNotice);
        if (!completeNotice)
            Assert.Contains("Wasla:PilotApplication:PrivacyNotice:ControllerAddress", gate.MissingNoticeFields);
    }

    internal static PilotPrivacyNoticeOptions CompleteNotice() => new()
    {
        ControllerName = "Örnek Test Yazılım Ltd. Şti.",
        ControllerAddress = "Test Mahallesi, Örnek Sokak No. 1, İstanbul",
        ControllerContact = "kvkk@example.test",
        LegalBasis = "Test hukuki sebep metni.",
        Transfers = "Test aktarım metni.",
        Retention = "Test saklama süresi metni.",
        RequestChannel = "basvuru@example.test"
    };

    private sealed class Environment(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Wasla.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
