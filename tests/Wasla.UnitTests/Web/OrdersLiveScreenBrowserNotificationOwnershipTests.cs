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

        Assert.DoesNotContain("!isLiveDisplayPage() && live && newIds.length > 0", tableJs, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(tableJs, "showBrowserNotificationIfAllowed()"));
        Assert.Contains("isLiveDisplayPage() && newIds.length > 0 && O.audio", tableJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_OwnsBrowserNotificationTriggerForNewIds()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");

        Assert.Contains("Phase 2B3:", tableJs, StringComparison.Ordinal);
        Assert.Contains("O.audio.showBrowserNotificationIfAllowed()", tableJs, StringComparison.Ordinal);
        Assert.Contains("new Notification(O.getMessage(\"newOrderArrived\")", audioJs, StringComparison.Ordinal);
        Assert.Contains("newOrderArrived", liveView, StringComparison.Ordinal);
        Assert.Contains("checkOrdersPage", liveView, StringComparison.Ordinal);
        Assert.Contains("orders-audio.js", liveView, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_BaselineCannotNotify_OnlyNewIdsAfterPoll()
    {
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        Assert.Contains("O.table.captureKnownOrderIdsFromContainer()", liveJs, StringComparison.Ordinal);
        Assert.DoesNotContain("showBrowserNotificationIfAllowed", liveJs, StringComparison.Ordinal);
        Assert.True(
            liveJs.IndexOf("captureKnownOrderIdsFromContainer", StringComparison.Ordinal)
            < liveJs.IndexOf("initPolling", StringComparison.Ordinal));

        var notifBlock = tableJs[
            tableJs.IndexOf("Phase 2B3:", StringComparison.Ordinal)
            ..];
        Assert.Contains("newIds.length > 0", notifBlock, StringComparison.Ordinal);
        Assert.Contains("isLiveDisplayPage()", notifBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_RespectsExistingBrowserNotificationSettingAndPermission()
    {
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");

        Assert.Contains("O.state.notificationSettings.showBrowserNotification", audioJs, StringComparison.Ordinal);
        Assert.Contains("Notification.permission !== \"granted\"", audioJs, StringComparison.Ordinal);
        Assert.Contains("function showBrowserNotificationIfAllowed()", audioJs, StringComparison.Ordinal);
        // Permission is requested from settings UI, not automatically on Live poll.
        Assert.DoesNotContain("requestPermission", ReadWwwroot("js", "orders", "orders-table.js"), StringComparison.Ordinal);
        Assert.Contains("maybeRequestBrowserNotificationPermission", audioJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_OneBrowserNotificationCallPerPollingBatch()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");
        var audioJs = ReadWwwroot("js", "orders", "orders-audio.js");

        Assert.Equal(1, CountOccurrences(tableJs, "showBrowserNotificationIfAllowed()"));
        Assert.Equal(1, CountOccurrences(audioJs, "new Notification("));
        Assert.DoesNotContain("newIds.forEach", tableJs.Substring(tableJs.IndexOf("Phase 2B3:", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void Phase2B1And2B2OwnershipMarkersRemainIntact()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        Assert.Contains("Phase 2B1:", tableJs, StringComparison.Ordinal);
        Assert.Contains("Phase 2B2:", tableJs, StringComparison.Ordinal);
        Assert.Contains("await O.audio.playSoundNow({", tableJs, StringComparison.Ordinal);
        Assert.Contains("markOrdersAsRecentlyNew(newIds)", tableJs, StringComparison.Ordinal);
        Assert.DoesNotContain("BroadcastChannel", tableJs, StringComparison.Ordinal);
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
