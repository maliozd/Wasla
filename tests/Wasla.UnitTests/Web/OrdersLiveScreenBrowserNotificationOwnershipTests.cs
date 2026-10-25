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
    public void LiveScreenSettingsMenu_AndOrderTraining_OpenTheOneSharedNotificationSettingsModal()
    {
        // WAS-13. The behaviour (opening, focus return, permission) is covered by notification-settings-modal-entry.test.js.
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var training = Read("Areas", "Tenant", "Views", "Orders", "_GuidedTrainingPanel.cshtml");
        var modal = Read("Areas", "Tenant", "Views", "NotificationSettings", "_NotificationSettingsModal.cshtml");

        var menu = liveView[liveView.IndexOf("id=\"ordersLiveDisplaySettingsMenu\"", StringComparison.Ordinal)..liveView.IndexOf("class=\"wasla-live-display-brand\"", StringComparison.Ordinal)];
        var entry = menu.IndexOf("data-notification-settings-open", StringComparison.Ordinal);
        Assert.True(entry >= 0, "The settings menu has no notification settings entry.");
        // Every Live Screen user changes their own preferences; only the automation group's order-settings link needs
        // the order-settings policy, so the notification entry is outside that block (it sits in the Notifications group).
        var gate = menu.IndexOf("@if (navPermissions.CanManageOrderSettings)", StringComparison.Ordinal);
        var gateEnd = menu.IndexOf("</a>", gate, StringComparison.Ordinal);
        Assert.True(gate >= 0 && gateEnd > gate);
        Assert.True(entry > gateEnd);
        Assert.True(entry > menu.IndexOf("id=\"ordersLiveSettingsNotificationsHeading\"", StringComparison.Ordinal));
        // The existing shell, modal and save endpoint; no second modal on the page.
        Assert.Equal(1, CountOccurrences(liveView, "@await Html.PartialAsync(\"_NotificationSettingsModalShell\")"));
        Assert.DoesNotContain("id=\"notificationSettingsModal\"", liveView, StringComparison.Ordinal);
        Assert.Contains("[\"notificationSettingsSaveFailed\"]", liveView, StringComparison.Ordinal);

        // When the practice order arrives, training explains attention and browser permission and opens the same modal.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"data-training-when=""@GuidedTrainingSteps\.PracticeNew""[^>]*data-training-notifications>.*?GuidedSetup\.Training\.Notifications\.Body.*?data-notification-settings-open",
                System.Text.RegularExpressions.RegexOptions.Singleline),
            training);
        Assert.DoesNotContain("requestPermission", training, StringComparison.Ordinal);

        Assert.Contains("aria-describedby=\"browserNotificationPermissionHelp\"", modal, StringComparison.Ordinal);
        foreach (var state in new[] { "default", "granted", "denied", "unsupported" })
            Assert.Contains($"data-permission-{state}=", modal, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserPermission_IsAskedOnlyWhenBrowserNotificationsAreTurnedOn()
    {
        var settingsJs = ReadWwwroot("js", "orders", "orders-notification-settings.js");

        Assert.Equal(1, CountOccurrences(settingsJs, "maybeRequestBrowserNotificationPermission("));
        Assert.Contains("maybeRequestBrowserNotificationPermission({ showBrowserNotification: true })", settingsJs, StringComparison.Ordinal);
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
