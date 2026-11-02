using System.Text.Json;
using Xunit.Sdk;

namespace Wasla.UnitTests.Web;

public sealed class LiveScreenNotificationSettingsClickTests
{
    [Theory]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"ok\":true,\"at\":\"button\"}")]
    public void SubmittedForm_BeforeOrAfterDocumentReplacement_DoesNotRequireClickDetails(string response)
    {
        using var document = JsonDocument.Parse(response);

        LiveScreenNotificationSettingsBrowserTests.AssertFormSubmitted(document.RootElement, "button[type=submit]", 120);
    }

    [Fact]
    public void MissedSubmit_StillFailsWithTheClickTargetAndPosition()
    {
        using var document = JsonDocument.Parse("{\"ok\":false,\"at\":\"div#ordersLiveScreenHost\"}");

        var error = Assert.Throws<FailException>(() =>
            LiveScreenNotificationSettingsBrowserTests.AssertFormSubmitted(document.RootElement, "button[type=submit]", 120));

        Assert.Equal("The click aimed at button[type=submit] (y 120) submitted no form; it landed on div#ordersLiveScreenHost.", error.Message);
    }
}
