namespace Wasla.UnitTests.Web;

public sealed class OrdersNotificationSoundWiringTests
{
    [Fact]
    public void OrdersIndex_IncludesAudioAndNotificationScripts()
    {
        var source = ReadWebFile("Areas", "Tenant", "Views", "Orders", "Index.cshtml");

        Assert.Contains("orders-audio.js", source, StringComparison.Ordinal);
        Assert.Contains("orders-sound-control.js", source, StringComparison.Ordinal);
        Assert.Contains("orders-notification-settings.js", source, StringComparison.Ordinal);
        Assert.Contains("soundUnlockHint", source, StringComparison.Ordinal);
        Assert.Contains("ordersEnableNotificationSound", source, StringComparison.Ordinal);
        Assert.Contains("ordersSoundControl", source, StringComparison.Ordinal);
        Assert.Contains("ordersSoundSelect", source, StringComparison.Ordinal);
        Assert.Contains("ordersSoundTestBtn", source, StringComparison.Ordinal);
        Assert.Contains("Notification.EnableSoundButton", source, StringComparison.Ordinal);
        Assert.Contains("Notification.SoundControlLabel", source, StringComparison.Ordinal);
        Assert.Contains("Notification.SelectSound", source, StringComparison.Ordinal);
        Assert.Contains("Notification.TestSound", source, StringComparison.Ordinal);
        Assert.Contains("CanManageOrderSettings", source, StringComparison.Ordinal);
        Assert.Contains("CanViewOrderSettingsPage", source, StringComparison.Ordinal);
        Assert.Contains("/settings/orders#notifications", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">…</span>", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">...</span>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersIndex_HidesAutomationStatusForNonManagers()
    {
        var source = ReadWebFile("Areas", "Tenant", "Views", "Orders", "Index.cshtml");

        Assert.Contains("navPermissions.CanManageOrderSettings", source, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanViewOrderSettingsPage", source, StringComparison.Ordinal);
        Assert.Contains("ordersAutomationStatusGroup", source, StringComparison.Ordinal);
        Assert.Contains("automationStatusSync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveDisplay_IncludesAudioAndNotificationScripts()
    {
        var source = ReadWebFile("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("orders-audio.js", source, StringComparison.Ordinal);
        Assert.Contains("orders-sound-control.js", source, StringComparison.Ordinal);
        Assert.Contains("orders-notification-settings.js", source, StringComparison.Ordinal);
        Assert.Contains("notificationSettingsJsonUrl", source, StringComparison.Ordinal);
        Assert.Contains("soundUnlockHint", source, StringComparison.Ordinal);
        Assert.Contains("orders-live-display-page.js", source, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplaySoundBanner", source, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplayEnableNotificationSound", source, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplaySoundSelect", source, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplaySoundTestBtn", source, StringComparison.Ordinal);
        Assert.Contains("Notification.SoundMayBeOffTitle", source, StringComparison.Ordinal);
        Assert.Contains("Notification.EnableSoundButton", source, StringComparison.Ordinal);
        Assert.Contains("Notification.SoundActive", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersTable_PlaysSoundOnOrdersAndLiveDisplay()
    {
        var source = ReadWebFile("wwwroot", "js", "orders", "orders-table.js");

        Assert.Contains("O.audio.playSoundNow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("!isLiveDisplayPage() && live && newIds.length > 0 && O.state.notificationSettings && O.audio", source, StringComparison.Ordinal);
        Assert.Contains("live && newIds.length > 0 && O.state.notificationSettings && O.audio", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersAudio_HandlesPlayRejectionAndUnlockFlow()
    {
        var source = ReadWebFile("wwwroot", "js", "orders", "orders-audio.js");

        Assert.Contains("initAudioUnlock", source, StringComparison.Ordinal);
        Assert.Contains("bindUnlockGestures", source, StringComparison.Ordinal);
        Assert.Contains("showUnlockPromptOnce", source, StringComparison.Ordinal);
        Assert.Contains("stopCurrentSound", source, StringComparison.Ordinal);
        Assert.Contains("NotAllowedError", source, StringComparison.Ordinal);
        Assert.Contains("playResult.then", source, StringComparison.Ordinal);
        Assert.Contains(".catch(function (error)", source, StringComparison.Ordinal);
        Assert.Contains("pointerdown", source, StringComparison.Ordinal);
        Assert.Contains("enableNotificationSoundFromControl", source, StringComparison.Ordinal);
        Assert.Contains("syncSoundEnableUi", source, StringComparison.Ordinal);
        Assert.Contains("ordersEnableNotificationSound", source, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplayEnableNotificationSound", source, StringComparison.Ordinal);
        Assert.Contains("/sounds/", source, StringComparison.Ordinal);
        Assert.Contains(".mp3", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersSoundControl_StopsPreviousAudioAndPersistsSelection()
    {
        var source = ReadWebFile("wwwroot", "js", "orders", "orders-sound-control.js");

        Assert.Contains("Wasla.operatorSoundName", source, StringComparison.Ordinal);
        Assert.Contains("stopCurrentSound", source, StringComparison.Ordinal);
        Assert.Contains("playSoundPreview", source, StringComparison.Ordinal);
        Assert.Contains("ordersSoundSelect", source, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplaySoundSelect", source, StringComparison.Ordinal);
        Assert.Contains("/sounds/", source, StringComparison.Ordinal);
        Assert.Contains("bell1", source, StringComparison.Ordinal);
        Assert.Contains("bell6", source, StringComparison.Ordinal);
        Assert.Contains("notification-settings", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationStatus_HidesGroupOnLoadFailure()
    {
        var source = ReadWebFile("wwwroot", "js", "orders", "orders-automation-status.js");

        Assert.Contains("hideStatusGroup", source, StringComparison.Ordinal);
        Assert.Contains("ordersAutomationStatusGroup", source, StringComparison.Ordinal);
        Assert.DoesNotContain("textContent = \"…\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationSettings_StopsPreviousSoundBeforeTestPlayback()
    {
        var source = ReadWebFile("wwwroot", "js", "orders", "orders-notification-settings.js");

        Assert.Contains("stopCurrentSound", source, StringComparison.Ordinal);
        Assert.Contains("playSoundPreview", source, StringComparison.Ordinal);
        Assert.Contains("testSelectedSoundBtn", source, StringComparison.Ordinal);
        Assert.Contains("availableSounds", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultNotificationSoundFiles_Exist()
    {
        var root = FindSolutionRoot();
        var soundsDir = Path.Combine(root, "src", "Wasla.Web", "wwwroot", "sounds");

        Assert.True(Directory.Exists(soundsDir), "wwwroot/sounds directory is missing.");
        for (var i = 1; i <= 6; i++)
        {
            Assert.True(
                File.Exists(Path.Combine(soundsDir, $"bell{i}.mp3")),
                $"bell{i}.mp3 is missing.");
        }
    }

    [Fact]
    public void SoundUnlockHint_LocalizedForSupportedCultures()
    {
        var root = FindSolutionRoot();
        var resourcesDir = Path.Combine(root, "src", "Wasla.Web", "Resources");

        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.tr-TR.resx"),
            "Notification.SoundUnlockHint",
            "Bildirim sesi için ekrana bir kez tıklayın.");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.en-US.resx"),
            "Notification.SoundUnlockHint",
            "Click once to enable notification sounds.");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.ar-SA.resx"),
            "Notification.SoundUnlockHint",
            "انقر مرة واحدة لتفعيل أصوات الإشعارات.");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.ru-RU.resx"),
            "Notification.SoundUnlockHint",
            "Нажмите один раз, чтобы включить звуки уведомлений.");

        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.tr-TR.resx"),
            "Notification.EnableSoundButton",
            "Bildirim sesini etkinleştir");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.en-US.resx"),
            "Notification.EnableSoundButton",
            "Enable notification sound");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.tr-TR.resx"),
            "Notification.SoundMayBeOffTitle",
            "Bildirim sesi kapalı olabilir");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.tr-TR.resx"),
            "Notification.SoundActive",
            "Bildirim sesi aktif");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.tr-TR.resx"),
            "Notification.SoundControlLabel",
            "Bildirim sesi");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.en-US.resx"),
            "Notification.SoundControlLabel",
            "Notification sound");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.tr-TR.resx"),
            "Notification.SelectSound",
            "Ses seç");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.en-US.resx"),
            "Notification.SelectSound",
            "Select sound");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.tr-TR.resx"),
            "Notification.TestSound",
            "Test et");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.en-US.resx"),
            "Notification.TestSound",
            "Test");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.tr-TR.resx"),
            "Notification.SoundCouldNotPlay",
            "Ses başlatılamadı. Tarayıcı izinlerini ve sekme sesini kontrol edin.");
        AssertResourceContains(
            Path.Combine(resourcesDir, "SharedResource.en-US.resx"),
            "Notification.SoundCouldNotPlay",
            "Sound could not start. Check browser permissions and tab audio.");
    }

    private static void AssertResourceContains(string path, string key, string expectedValue)
    {
        Assert.True(File.Exists(path), $"Missing resource file: {path}");
        var xml = File.ReadAllText(path);
        Assert.Contains($"name=\"{key}\"", xml, StringComparison.Ordinal);
        Assert.Contains(expectedValue, xml, StringComparison.Ordinal);
    }

    private static string ReadWebFile(params string[] relativeParts)
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(new[] { root, "src", "Wasla.Web" }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Missing file: {path}");
        return File.ReadAllText(path);
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wasla.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate Wasla.sln from test base directory.");
    }
}
