using System.Xml.Linq;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Live Screen owns the explicit enable-sound / stop-sound unlock flow; Orders/history does not.
/// </summary>
public sealed class OrdersLiveScreenSoundEnableTests
{
    private static readonly string[] LocalizedCultures = ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    [Fact]
    public void LiveScreen_HasEnableSoundControlAndStopSoundAction()
    {
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("id=\"ordersLiveDisplaySoundBanner\"", liveView, StringComparison.Ordinal);
        Assert.Contains("id=\"ordersLiveDisplayEnableNotificationSound\"", liveView, StringComparison.Ordinal);
        Assert.Contains("id=\"ordersLiveDisplayStopSound\"", liveView, StringComparison.Ordinal);
        Assert.Contains("id=\"ordersLiveDisplaySoundActive\"", liveView, StringComparison.Ordinal);
        Assert.Contains("@L[\"Notification.EnableSoundButton\"]", liveView, StringComparison.Ordinal);
        Assert.Contains("@L[\"Notification.StopSoundButton\"]", liveView, StringComparison.Ordinal);
        Assert.Contains("wasla-orders-sound-enable-btn", liveView, StringComparison.Ordinal);
        Assert.Contains("[\"enableNotificationSound\"]", liveView, StringComparison.Ordinal);
        Assert.Contains("[\"stopSound\"]", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain("ordersEnableNotificationSound", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain("ordersLiveDisplaySoundSelect", liveView, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersAndHistory_DoNotExposeSoundEnableOrStopControls()
    {
        var ordersPage = Read("Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var table = Read("Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");

        Assert.DoesNotContain("orders-audio.js", ordersPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ordersLiveDisplayEnableNotificationSound", ordersPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ordersEnableNotificationSound", ordersPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ordersLiveDisplayStopSound", ordersPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Notification.EnableSoundButton", ordersPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Notification.StopSoundButton", ordersPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ordersLiveDisplayEnableNotificationSound", table, StringComparison.Ordinal);
        Assert.DoesNotContain("ordersLiveDisplayStopSound", table, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableSoundHandler_IsWiredOnceAndUnlocksOnlyAfterSuccessfulPlay()
    {
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");

        Assert.Contains("function initAudioUnlock()", audioJs, StringComparison.Ordinal);
        Assert.Contains("O.audio.initAudioUnlock()", liveJs, StringComparison.Ordinal);
        Assert.Contains("dataset.soundEnableWired !== \"1\"", audioJs, StringComparison.Ordinal);
        Assert.Contains("dataset.soundEnableWired = \"1\"", audioJs, StringComparison.Ordinal);
        Assert.Contains("enableNotificationSoundFromControl", audioJs, StringComparison.Ordinal);
        Assert.Contains("await audio.play();", audioJs, StringComparison.Ordinal);

        var enableFnStart = audioJs.IndexOf("async function enableNotificationSoundFromControl()", StringComparison.Ordinal);
        Assert.True(enableFnStart >= 0);
        var enableFn = audioJs.Substring(enableFnStart, audioJs.IndexOf("function wireSoundEnableControls()", StringComparison.Ordinal) - enableFnStart);
        Assert.True(
            enableFn.IndexOf("await audio.play();", StringComparison.Ordinal)
            < enableFn.IndexOf("markSoundUnlocked();", StringComparison.Ordinal));
        Assert.Contains("handlePlayRejection(error);", enableFn, StringComparison.Ordinal);
        Assert.DoesNotContain("markSoundUnlocked();", enableFn.Substring(0, enableFn.IndexOf("await audio.play();", StringComparison.Ordinal)));
    }

    [Fact]
    public void SessionUnlock_DoesNotTrustPersistedFlagWithoutUsablePlayback()
    {
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");
        var settingsJs = ReadWwwroot("js", "orders", "orders-notification-settings.js");

        Assert.Contains("return sessionUnlocked === true;", audioJs, StringComparison.Ordinal);
        Assert.Contains("sessionUnlocked = true;", audioJs, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem(SOUND_UNLOCK_KEY, \"true\")", audioJs, StringComparison.Ordinal);
        Assert.DoesNotContain("return localStorage.getItem(\"Wasla.soundUnlocked\") === \"true\";", audioJs, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage.setItem(\"Wasla.soundUnlocked\", \"true\")", settingsJs, StringComparison.Ordinal);
    }

    [Fact]
    public void StopSound_IsASeparateLiveScreenActionAndDoesNotClearUnlock()
    {
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("id=\"ordersLiveDisplayStopSound\"", liveView, StringComparison.Ordinal);
        Assert.Contains("function stopCurrentSound()", audioJs, StringComparison.Ordinal);
        Assert.Contains("dataset.soundStopWired !== \"1\"", audioJs, StringComparison.Ordinal);
        Assert.Contains("setStopSoundVisible(true)", audioJs, StringComparison.Ordinal);

        var stopFnStart = audioJs.IndexOf("function stopCurrentSound()", StringComparison.Ordinal);
        var stopFn = audioJs.Substring(stopFnStart, audioJs.IndexOf("function syncSoundEnableUi()", StringComparison.Ordinal) - stopFnStart);
        Assert.Contains("stopCurrentAlertSound();", stopFn, StringComparison.Ordinal);
        Assert.Contains("setStopSoundVisible(false);", stopFn, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionUnlocked = false", stopFn, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage.removeItem", stopFn, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserNotificationsAndSoundOwnership_RemainOnLiveScreenWithoutSignalR()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var ordersPage = Read("Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");

        Assert.Contains("isLiveDisplayPage() && newIds.length > 0 && O.state.notificationSettings && O.audio", tableJs, StringComparison.Ordinal);
        Assert.Contains("isLiveDisplayPage() && newIds.length > 0 && O.audio", tableJs, StringComparison.Ordinal);
        Assert.Contains("showBrowserNotificationIfAllowed()", tableJs, StringComparison.Ordinal);
        Assert.Contains("orders-audio.js", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain("orders-audio.js", ordersPage, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", tableJs, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", audioJs, StringComparison.Ordinal);
        Assert.DoesNotContain("BroadcastChannel", tableJs, StringComparison.Ordinal);
        Assert.DoesNotContain("new HubConnectionBuilder", audioJs, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableAndStopSoundText_IsLocalizedInEverySupportedCulture()
    {
        foreach (var culture in LocalizedCultures)
        {
            var resources = ReadResourceValues(culture);
            foreach (var key in new[]
            {
                "Notification.EnableSoundButton",
                "Notification.SoundMayBeOffTitle",
                "Notification.SoundMayBeOffDescription",
                "Notification.SoundActive",
                "Notification.StopSoundButton"
            })
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{key} missing for {culture}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} empty for {culture}.");
            }
        }

        Assert.Equal("Bildirim sesini etkinleştir", ReadResourceValues("tr-TR")["Notification.EnableSoundButton"]);
        Assert.Equal("Enable notification sound", ReadResourceValues("en-US")["Notification.EnableSoundButton"]);
        Assert.Equal("Sesi durdur", ReadResourceValues("tr-TR")["Notification.StopSoundButton"]);
        Assert.Equal("Stop sound", ReadResourceValues("en-US")["Notification.StopSoundButton"]);
    }

    private static Dictionary<string, string> ReadResourceValues(string culture)
    {
        var path = Path.Combine(
            GetRepositoryRoot(), "src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx");

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .ToDictionary(
                d => d.Attribute("name")!.Value,
                d => d.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot(), "src", "Wasla.Web" }.Concat(segments).ToArray()));

    private static string ReadWwwroot(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot(), "src", "Wasla.Web", "wwwroot" }.Concat(segments).ToArray()));

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
