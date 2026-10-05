using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.WebShell;

public sealed class ShellSelectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WinForms")]
    [InlineData("winforms")]
    public void ClassicWindow_IsTheDefault_AndWebView2IsNeverProbed(string? configured)
    {
        var probe = new FakeProbe(available: true);

        var decision = ShellSelection.Decide(configured, probe);

        Assert.Equal(new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.NotRequested), decision);
        Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData("WebView2")]
    [InlineData("webview2")]
    [InlineData(" WebView2 ")]
    public void WebView2Shell_IsUsedOnlyWhenRequestedAndAvailable(string configured)
    {
        var probe = new FakeProbe(available: true);

        var decision = ShellSelection.Decide(configured, probe);

        Assert.Equal(new ShellDecision(PrintBridgeShellMode.WebView2, ShellFallbackReason.None), decision);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void MissingRuntime_FallsBackToTheClassicWindow()
    {
        var decision = ShellSelection.Decide("WebView2", new FakeProbe(available: false));

        Assert.Equal(new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.RuntimeUnavailable), decision);
    }

    [Theory]
    [InlineData("Edge")]
    [InlineData("WebView")]
    [InlineData("true")]
    public void UnrecognizedValues_FallBackToTheClassicWindowWithoutProbing(string configured)
    {
        var probe = new FakeProbe(available: true);

        var decision = ShellSelection.Decide(configured, probe);

        Assert.Equal(new ShellDecision(PrintBridgeShellMode.WinForms, ShellFallbackReason.UnrecognizedSetting), decision);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void UiOptions_DefaultToNoShellSwitch()
    {
        Assert.Null(new UiOptions().Shell);
    }

    [Fact]
    public void ClassicWindowSettingsClone_KeepsTheShellSwitch()
    {
        var clone = MainForm.CloneUiOptions(new UiOptions { Language = "ar-SA", Shell = "WebView2", WindowWidth = 900 });

        Assert.Equal("WebView2", clone.Shell);
        Assert.Equal("ar-SA", clone.Language);
        Assert.Equal(900, clone.WindowWidth);
    }

    private sealed class FakeProbe(bool available) : IWebView2RuntimeProbe
    {
        public int Calls { get; private set; }

        public WebView2RuntimeAvailability Probe()
        {
            Calls++;
            return new WebView2RuntimeAvailability(available, available ? "154.0.0.0" : null);
        }
    }
}

[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class ShellSwitchPersistenceTests : IDisposable
{
    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-shell-tests");

    public void Dispose() => _dataRoot.Dispose();

    [Fact]
    public void ExistingConfigurations_AreSavedWithoutAShellKey()
    {
        var store = new PrintBridgeSettingsStore();
        store.Save(store.Load());

        Assert.DoesNotContain("\"Shell\"", File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath), StringComparison.Ordinal);
        Assert.Null(store.Load().Ui.Shell);
    }

    [Fact]
    public void ShellSwitch_RoundTripsThroughTheSettingsStoreAndLanguageSaves()
    {
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        document.Ui.Shell = "WebView2";
        store.Save(document);

        store.SaveLanguage("ru-RU");
        var reloaded = store.Load();

        Assert.Equal("WebView2", reloaded.Ui.Shell);
        Assert.Equal("ru-RU", reloaded.Ui.Language);
    }

    [Fact]
    public void ShellWebViewProfile_StaysInsideTheRedirectedDataRoot()
    {
        Assert.StartsWith(_dataRoot.Path, ShellPaths.UserDataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "shell-ui"), ShellPaths.AssetDirectory);
    }
}
