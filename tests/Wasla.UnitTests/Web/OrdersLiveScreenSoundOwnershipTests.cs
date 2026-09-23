namespace Wasla.UnitTests.Web;

/// <summary>
/// Phase 2B1: Live Screen owns automatic new-order sound; Orders management does not.
/// </summary>
public sealed class OrdersLiveScreenSoundOwnershipTests
{
    [Fact]
    public void OrdersPage_DoesNotOwnAutomaticNewOrderSoundTrigger()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");
        var ordersPage = Read("Areas", "Tenant", "Views", "Orders", "Index.cshtml");

        Assert.DoesNotContain("playSoundNow", tableJs, StringComparison.Ordinal);
        Assert.Contains("settings.newOrderSoundEnabled && O.audio", storeJs, StringComparison.Ordinal);

        // Phase 2B4: Orders no longer loads the operational audio/notification stack at all.
        Assert.DoesNotContain("orders-audio.js", ordersPage, StringComparison.Ordinal);
        Assert.DoesNotContain("orders-notification-settings.js", ordersPage, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_InitializesNotificationDetectionBaseline()
    {
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("O.notificationSettings.load", liveJs, StringComparison.Ordinal);
        Assert.Contains("O.liveStore.start()", liveJs, StringComparison.Ordinal);
        Assert.True(
            liveJs.IndexOf("O.notificationSettings.load", StringComparison.Ordinal)
            < liveJs.IndexOf("O.liveStore.start()", StringComparison.Ordinal));
        Assert.Contains("orders-audio.js", liveView, StringComparison.Ordinal);
        Assert.Contains("orders-notification-settings.js", liveView, StringComparison.Ordinal);
        Assert.Contains("notificationSettingsJsonUrl: \"/notification-settings/current\"", liveView, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_BaselineCaptureHappensBeforePolling_SoInitialIdsDoNotNotify()
    {
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        Assert.Contains("The first complete JSON snapshot is the notification baseline.", liveJs, StringComparison.Ordinal);
        Assert.DoesNotContain("captureKnownOrderIdsFromContainer", liveJs, StringComparison.Ordinal);
        Assert.DoesNotContain("playSoundNow", liveJs, StringComparison.Ordinal);
        Assert.Contains("if (!baselineReady)", storeJs, StringComparison.Ordinal);
        Assert.Contains("isBaseline || !meta.newIds", storeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_NewIdsAfterBaseline_TriggerOnePlaySoundNowCall()
    {
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        Assert.Contains("await O.audio.playSoundNow({", storeJs, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(storeJs, "await O.audio.playSoundNow({"));

        var liveSoundBlockStart = storeJs.IndexOf("settings.newOrderSoundEnabled", StringComparison.Ordinal);
        Assert.True(liveSoundBlockStart >= 0);
        var liveSoundBlock = storeJs.Substring(liveSoundBlockStart, Math.Min(900, storeJs.Length - liveSoundBlockStart));
        Assert.Contains("playSoundNow", liveSoundBlock, StringComparison.Ordinal);
        Assert.Contains("newOrderSoundName", liveSoundBlock, StringComparison.Ordinal);
        Assert.Contains("newOrderSoundRepeatCount", liveSoundBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("for (let i = 0; i < newIds.length", liveSoundBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("meta.newIds.forEach", liveSoundBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_ReusesExistingNotificationSettingsWithoutBrowserNotificationMove()
    {
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("settings.newOrderSoundEnabled", storeJs, StringComparison.Ordinal);
        Assert.Contains("settings.newOrderSoundName", storeJs, StringComparison.Ordinal);
        Assert.Contains("settings.newOrderSoundRepeatCount", storeJs, StringComparison.Ordinal);
        Assert.Contains("notificationSettingsJsonUrl", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain("BroadcastChannel", storeJs, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", storeJs, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
