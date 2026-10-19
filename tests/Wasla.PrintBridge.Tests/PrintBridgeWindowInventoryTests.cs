using System.Reflection;
using System.Text.Json.Nodes;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// The complete set of Print Bridge windows and UI settings. The WebView2 app is the only application window; the
/// runtime notice replaces it when the WebView2 Runtime cannot be used. Any other window type or UI setting fails here.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeWindowInventoryTests
{
    [Fact]
    public void TheDesktopApp_ContainsExactlyTheWebView2AppWindow_TheRuntimeNotice_AndTheAppWindowsOwnDialogs()
    {
        var forms = typeof(TrayApplicationContext).Assembly.GetTypes()
            .Where(t => typeof(Form).IsAssignableFrom(t))
            .ToHashSet();

        // The two windows the tray opens.
        Type[] windows = [typeof(PrintBridgeShellForm), typeof(WebView2RuntimeNotice)];
        // Native dialogs of the WebView2 app (the connection dialog and the confirmations), shown modally over it.
        Type[] appWindowDialogs = [typeof(ShellConnectionDialog), typeof(ShellConfirmDialog)];

        Assert.Equal(windows.Concat(appWindowDialogs).ToHashSet(), forms);
        Assert.All(forms, form => Assert.True(form.IsSealed && form.IsNotPublic, form.Name));
    }

    [Fact]
    public void TheTray_HoldsOnlyTheWebView2AppWindow_AndTheRuntimeNotice()
    {
        var held = typeof(TrayApplicationContext)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => typeof(Form).IsAssignableFrom(f.FieldType))
            .Select(f => f.FieldType)
            .ToHashSet();

        Assert.Equal(new HashSet<Type> { typeof(PrintBridgeShellForm), typeof(WebView2RuntimeNotice) }, held);
    }

    [Fact]
    public void TheUiSettings_AreExactlyLanguageAndTheTwoStartupFlags()
    {
        var properties = typeof(UiOptions)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["Language", "MinimizeToTray", "StartWithWindows"], properties);
    }

    [Fact]
    public void SavingSettings_WritesExactlyTheUiSettings_AndNothingElseFromTheFile()
    {
        using var root = new IsolatedDataRoot("wasla-pb-ui-settings-tests");
        var store = new PrintBridgeSettingsStore();
        store.Load();
        var file = JsonNode.Parse(File.ReadAllText(root.ConfigPath))!;
        file["Ui"]!["NotASetting"] = "kept nowhere";
        File.WriteAllText(root.ConfigPath, file.ToJsonString());

        var document = store.Load();
        document.Ui.Language = "ar-SA";
        store.Save(document);

        var ui = JsonNode.Parse(File.ReadAllText(root.ConfigPath))!["Ui"]!.AsObject();
        Assert.Equal(["Language", "MinimizeToTray", "StartWithWindows"], ui.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("ar-SA", ui["Language"]!.GetValue<string>());
    }

    [Fact]
    public void TheAppWindowsBrowserProfile_StaysInsideTheRedirectedDataRoot()
    {
        using var root = new IsolatedDataRoot("wasla-pb-shell-profile-tests");

        Assert.StartsWith(root.Path, ShellPaths.UserDataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "shell-ui"), ShellPaths.AssetDirectory);
    }
}
