namespace Wasla.UnitTests.Web;

public sealed class OrdersNotificationSoundWiringTests
{
    [Fact]
    public void OrdersIndex_IncludesAudioAndNotificationScripts()
    {
        var source = ReadWebFile("Areas", "Tenant", "Views", "Orders", "Index.cshtml");

        Assert.Contains("orders-audio.js", source, StringComparison.Ordinal);
        Assert.Contains("orders-notification-settings.js", source, StringComparison.Ordinal);
        Assert.Contains("soundUnlockHint", source, StringComparison.Ordinal);
        Assert.Contains("ordersEnableNotificationSound", source, StringComparison.Ordinal);
        Assert.Contains("Notification.EnableSoundButton", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveDisplay_IncludesAudioAndNotificationScripts()
    {
        var source = ReadWebFile("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("orders-audio.js", source, StringComparison.Ordinal);
        Assert.Contains("orders-notification-settings.js", source, StringComparison.Ordinal);
        Assert.Contains("notificationSettingsJsonUrl", source, StringComparison.Ordinal);
        Assert.Contains("soundUnlockHint", source, StringComparison.Ordinal);
        Assert.Contains("orders-live-display-page.js", source, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplaySoundBanner", source, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplayEnableNotificationSound", source, StringComparison.Ordinal);
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
    }

    [Fact]
    public void NotificationSettings_StopsPreviousSoundBeforeTestPlayback()
    {
        var source = ReadWebFile("wwwroot", "js", "orders", "orders-notification-settings.js");

        Assert.Contains("stopCurrentSound", source, StringComparison.Ordinal);
        Assert.Contains("playSoundPreview", source, StringComparison.Ordinal);
        Assert.Contains("testSelectedSoundBtn", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultNotificationSoundFiles_Exist()
    {
        var root = FindSolutionRoot();
        var soundsDir = Path.Combine(root, "src", "Wasla.Web", "wwwroot", "sounds");

        Assert.True(Directory.Exists(soundsDir), "wwwroot/sounds directory is missing.");
        Assert.True(File.Exists(Path.Combine(soundsDir, "bell1.mp3")), "Default bell1.mp3 is missing.");
        Assert.True(File.Exists(Path.Combine(soundsDir, "bell2.mp3")), "bell2.mp3 is missing.");
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
