namespace Wasla.UnitTests.Web;

/// <summary>
/// Phase 2B3: Live Screen owns automatic browser desktop notifications; Orders does not.
/// </summary>
public sealed class OrdersLiveScreenBrowserNotificationOwnershipTests
{
    [Fact]
    public void Orders_DoesNotOwnAutomaticBrowserNotificationTrigger()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        Assert.DoesNotContain("showBrowserNotificationIfAllowed", tableJs, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(storeJs, "showBrowserNotificationIfAllowed()"));
        Assert.Contains("meta.isBaseline || !meta.newIds", storeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_OwnsBrowserNotificationTriggerForNewIds()
    {
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");

        Assert.Contains("O.audio.showBrowserNotificationIfAllowed()", storeJs, StringComparison.Ordinal);
        Assert.Contains("new Notification(O.getMessage(\"newOrderArrived\")", audioJs, StringComparison.Ordinal);
        Assert.Contains("newOrderArrived", liveView, StringComparison.Ordinal);
        Assert.Contains("checkOrdersPage", liveView, StringComparison.Ordinal);
        Assert.Contains("orders-audio.js", liveView, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_BaselineCannotNotify_OnlyNewIdsAfterPoll()
    {
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        Assert.Contains("O.liveStore.start()", liveJs, StringComparison.Ordinal);
        Assert.DoesNotContain("showBrowserNotificationIfAllowed", liveJs, StringComparison.Ordinal);
        Assert.Contains("if (!baselineReady)", storeJs, StringComparison.Ordinal);
        Assert.Contains("isBaseline || !meta.newIds || !meta.newIds.length", storeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_RespectsExistingBrowserNotificationSettingAndPermission()
    {
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");

        Assert.Contains("O.state.notificationSettings.showBrowserNotification", audioJs, StringComparison.Ordinal);
        Assert.Contains("Notification.permission !== \"granted\"", audioJs, StringComparison.Ordinal);
        Assert.Contains("function showBrowserNotificationIfAllowed()", audioJs, StringComparison.Ordinal);
        // Permission is requested from settings UI, not automatically on Live poll.
        Assert.DoesNotContain("requestPermission", ReadWwwroot("js", "orders", "orders-live-store.js"), StringComparison.Ordinal);
        Assert.Contains("maybeRequestBrowserNotificationPermission", audioJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_OneBrowserNotificationCallPerPollingBatch()
    {
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");

        Assert.Equal(1, CountOccurrences(storeJs, "showBrowserNotificationIfAllowed()"));
        Assert.Equal(1, CountOccurrences(audioJs, "new Notification("));
        Assert.DoesNotContain("meta.newIds.forEach", storeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase2B1And2B2OwnershipMarkersRemainIntact()
    {
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        Assert.Contains("await O.audio.playSoundNow({", storeJs, StringComparison.Ordinal);
        Assert.Contains("O.table.markOrdersAsRecentlyNew(meta.newIds)", storeJs, StringComparison.Ordinal);
        Assert.Contains("function markOrdersAsRecentlyNew(orderIds)", tableJs, StringComparison.Ordinal);
        Assert.DoesNotContain("BroadcastChannel", storeJs, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", storeJs, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", tableJs, StringComparison.Ordinal);
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
