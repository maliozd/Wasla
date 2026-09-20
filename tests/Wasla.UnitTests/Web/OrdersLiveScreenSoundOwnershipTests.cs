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
        var ordersPage = Read("Areas", "Tenant", "Views", "Orders", "Index.cshtml");

        Assert.DoesNotContain("!isLiveDisplayPage() && live && newIds.length > 0 && O.state.notificationSettings && O.audio", tableJs, StringComparison.Ordinal);
        Assert.Contains("isLiveDisplayPage() && newIds.length > 0 && O.state.notificationSettings && O.audio", tableJs, StringComparison.Ordinal);
        Assert.Contains("orders-audio.js", ordersPage, StringComparison.Ordinal);
        Assert.Contains("orders-notification-settings.js", ordersPage, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_InitializesNotificationDetectionBaseline()
    {
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("O.table.captureKnownOrderIdsFromContainer()", liveJs, StringComparison.Ordinal);
        Assert.Contains("O.notificationSettings.load", liveJs, StringComparison.Ordinal);
        Assert.Contains("O.table.initPolling()", liveJs, StringComparison.Ordinal);
        Assert.True(
            liveJs.IndexOf("captureKnownOrderIdsFromContainer", StringComparison.Ordinal)
            < liveJs.IndexOf("initPolling", StringComparison.Ordinal));
        Assert.Contains("orders-audio.js", liveView, StringComparison.Ordinal);
        Assert.Contains("orders-notification-settings.js", liveView, StringComparison.Ordinal);
        Assert.Contains("notificationSettingsJsonUrl: \"/notification-settings/current\"", liveView, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_BaselineCaptureHappensBeforePolling_SoInitialIdsDoNotNotify()
    {
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        var captureIdx = liveJs.IndexOf("O.table.captureKnownOrderIdsFromContainer()", StringComparison.Ordinal);
        var pollIdx = liveJs.IndexOf("O.table.initPolling()", StringComparison.Ordinal);
        Assert.True(captureIdx >= 0 && pollIdx > captureIdx);

        // Sound only after poll detects newIds — not at init.
        Assert.DoesNotContain("playSoundNow", liveJs, StringComparison.Ordinal);
        Assert.Contains("detectNewOrderIds(ids)", tableJs, StringComparison.Ordinal);
        Assert.Contains("isLiveDisplayPage() && newIds.length > 0", tableJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_NewIdsAfterBaseline_TriggerOnePlaySoundNowCall()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        Assert.Contains("const newIds = detectNewOrderIds(ids);", tableJs, StringComparison.Ordinal);
        Assert.Contains("await O.audio.playSoundNow({", tableJs, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(tableJs, "await O.audio.playSoundNow({"));

        var liveSoundBlockStart = tableJs.IndexOf(
            "isLiveDisplayPage() && newIds.length > 0 && O.state.notificationSettings && O.audio",
            StringComparison.Ordinal);
        Assert.True(liveSoundBlockStart >= 0);
        var liveSoundBlock = tableJs.Substring(liveSoundBlockStart, Math.Min(900, tableJs.Length - liveSoundBlockStart));
        Assert.Contains("playSoundNow", liveSoundBlock, StringComparison.Ordinal);
        Assert.Contains("newOrderSoundName", liveSoundBlock, StringComparison.Ordinal);
        Assert.Contains("newOrderSoundRepeatCount", liveSoundBlock, StringComparison.Ordinal);
        // Multiple new IDs still one sequence — gated by newIds.length > 0, not a per-id loop of playSoundNow.
        Assert.DoesNotContain("for (let i = 0; i < newIds.length", liveSoundBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("newIds.forEach", liveSoundBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_ReusesExistingNotificationSettingsWithoutBrowserNotificationMove()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("st.newOrderSoundEnabled", tableJs, StringComparison.Ordinal);
        Assert.Contains("st.newOrderSoundName", tableJs, StringComparison.Ordinal);
        Assert.Contains("st.newOrderSoundRepeatCount", tableJs, StringComparison.Ordinal);
        Assert.Contains("notificationSettingsJsonUrl", liveView, StringComparison.Ordinal);
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
