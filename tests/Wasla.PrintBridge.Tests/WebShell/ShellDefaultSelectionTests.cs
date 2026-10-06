using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// The WebView2 app as the shipped default: the real tray application with the settings file the package ships
/// (no <c>Ui</c> section), the explicit <c>WinForms</c> rollback, an invalid value, first run, and the classic window
/// as a fallback only when the app fails. Windows stay off-screen and the tray icon stays hidden.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
[Trait("Category", "WebView2Runtime")]
public sealed class ShellDefaultSelectionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TrayTestHost.Timeout;

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-default-shell-tests");
    private readonly CultureScope _cultureScope = new();
    private readonly TrayTestHost _host;

    public ShellDefaultSelectionTests() => _host = new TrayTestHost(_dataRoot);

    public void Dispose()
    {
        _dataRoot.Dispose();
        _cultureScope.Dispose();
    }

    [Fact]
    public Task TheShippedSettingsFile_OpensTheWebView2App_NotTheClassicWindow()
    {
        // The settings file the package ships has no Ui section, so no Ui.Shell.
        var shipped = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Wasla.PrintBridge", "appsettings.json"));
        Assert.Null(JsonNode.Parse(shipped)!["Ui"]);
        File.WriteAllText(_dataRoot.ConfigPath, shipped);

        return _host.RunAsync(shell: null, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Open"]);

            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            Assert.False(tray.ClassicWindowForTests.Visible);
        });
    }

    [Theory]
    [InlineData("Edge")]
    [InlineData("true")]
    public Task AnInvalidShellValue_IsIgnored_AndTheWebView2AppOpens(string configured) =>
        _host.RunAsync(configured, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Settings"]);

            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            Assert.False(tray.ClassicWindowForTests.Visible);
        });

    [Fact]
    public Task TheExplicitWinFormsSetting_RollsBackToTheClassicWindow() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Open"]);

            Assert.Null(tray.ShellFormForTests);
            Assert.True(tray.ClassicWindowForTests.Visible);
            await Task.CompletedTask;
        });

    [Fact]
    public Task AClassicWindowOpenMessage_FromThePage_DoesNotOpenTheClassicWindow() =>
        _host.RunAsync(shell: null, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Open"]);
            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            var core = shell.CoreWebView2ForTests!;
            await WaitUntilAsync(() => Task.FromResult(shell.BridgeForTests.SentSnapshotCount > 0));

            // A valid contract message, then a snapshot request as a barrier: page messages are handled in order, so
            // once the extra snapshot arrives the classic-window request has been handled too.
            var before = shell.BridgeForTests.SentSnapshotCount;
            await core.ExecuteScriptAsync("""
                chrome.webview.postMessage('{"version":3,"type":"classicWindow.open","payload":{}}');
                chrome.webview.postMessage('{"version":3,"type":"snapshot.request","payload":{}}');
                """);
            await WaitUntilAsync(() => Task.FromResult(shell.BridgeForTests.SentSnapshotCount > before));

            Assert.False(tray.ClassicWindowForTests.Visible);
            Assert.True(shell.Visible);
            Assert.Same(shell, tray.ShellFormForTests);
        });

    [Fact]
    public Task AFirstRunWithoutAToken_OpensTheAppOnSettings_AndStartsTheConnectionDialog() =>
        _host.RunAsync(shell: null, available: true, async (tray, localizer) =>
        {
            tray.ShowSetupIfNotConnected();

            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            var dialog = await WaitForAsync(() => System.Windows.Forms.Application.OpenForms.OfType<ShellConnectionDialog>().FirstOrDefault());
            await shell.InitializationForTests!.WaitAsync(Timeout);
            var core = shell.CoreWebView2ForTests!;
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('tab-settings').getAttribute('aria-selected')") == "true");
            Assert.False(tray.ClassicWindowForTests.Visible);

            // Closing the dialog changes nothing; the page says so.
            dialog.Close();
            var cancelled = localizer["Shell.Setup.Cancelled"];
            await WaitUntilAsync(async () => (await EvalAsync(core, "document.getElementById('toast-text').textContent")) == cancelled);
            Assert.Empty(System.Windows.Forms.Application.OpenForms.OfType<ShellConnectionDialog>());
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASetupLink_TakesOverFromAnOpenConnectionDialog_AndOnlyItsResultIsShown(bool openedByThePage)
    {
        // The usual onboarding: Print Bridge starts without a token (the first-run dialog opens), then the user opens the
        // setup link from Wasla Web. The page's own Connect button is the same situation.
        using var server = new TrayFakeServer();
        await _host.RunAsync(shell: null, available: true, async (tray, localizer) =>
        {
            if (openedByThePage)
                Click(tray, localizer["Tray.Settings"]);
            else
                tray.ShowSetupIfNotConnected();

            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            var core = shell.CoreWebView2ForTests!;
            await WaitUntilAsync(() => Task.FromResult(shell.BridgeForTests.SentSnapshotCount > 0));
            if (openedByThePage)
            {
                var connect = "{\"version\":3,\"type\":\"connection.openSetup\",\"payload\":{\"requestId\":\"" + Guid.NewGuid().ToString("D") + "\"}}";
                await core.ExecuteScriptAsync("chrome.webview.postMessage('" + connect + "')");
            }

            await WaitForAsync(() => System.Windows.Forms.Application.OpenForms.OfType<ShellConnectionDialog>().FirstOrDefault());
            // Every message the page shows from here on.
            await core.ExecuteScriptAsync("""
                window.__toasts = [];
                new MutationObserver(function () { window.__toasts.push(document.getElementById('toast-text').textContent); })
                  .observe(document.getElementById('toast-text'), { childList: true, characterData: true, subtree: true });
                """);

            tray.HandleSetupUri($"wasla-printbridge://setup?server={Uri.EscapeDataString(server.Url)}&code=setupcodenotarealcredential01");

            var connected = new[] { localizer["Auto.Connected"], localizer["Auto.ConnectedPrinterMissing"] };
            await WaitUntilAsync(async () => connected.Contains(await EvalAsync(core, "document.getElementById('toast-text').textContent")));
            Assert.Empty(System.Windows.Forms.Application.OpenForms.OfType<ShellConnectionDialog>());
            Assert.Equal(server.Url, new Wasla.PrintBridge.Services.PrintBridgeSettingsStore().Load().OrderHub.ServerUrl);
            var shown = await EvalAsync(core, "JSON.stringify(window.__toasts)");
            Assert.DoesNotContain(localizer["Shell.Setup.Cancelled"], shown, StringComparison.Ordinal);
        });
    }

    [Fact]
    public Task AFirstRunWithTheWinFormsRollback_OpensTheClassicSettings() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: true, async (tray, localizer) =>
        {
            tray.ShowSetupIfNotConnected();

            Assert.Null(tray.ShellFormForTests);
            Assert.True(tray.ClassicWindowForTests.Visible);
            Assert.Equal(localizer["Tab.Settings"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            await Task.CompletedTask;
        });

    [Fact]
    public async Task AConfiguredStart_StaysInTheTray()
    {
        using var server = new TrayFakeServer();
        await _host.RunAsync(shell: null, available: true, async (tray, localizer) =>
        {
            await server.NextPoll().WaitAsync(Timeout);

            tray.ShowSetupIfNotConnected();

            Assert.Null(tray.ShellFormForTests);
            Assert.False(tray.ClassicWindowForTests.Visible);
            Assert.Empty(System.Windows.Forms.Application.OpenForms.OfType<ShellConnectionDialog>());
        }, configure: d => TrayExitTests.ListenToTheFakeServer(d, server.Url));
    }

    [Fact]
    public Task ASetupLink_IsHandledInTheWebView2App_ByDefault() =>
        _host.RunAsync(shell: null, available: true, async (tray, localizer) =>
        {
            // A malformed link: nothing is contacted, and the result is shown in the app window.
            tray.HandleSetupUri("wasla-printbridge://setup?unexpected=1");

            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            var invalid = localizer["Auto.InvalidLink"];
            await WaitUntilAsync(async () => (await EvalAsync(shell.CoreWebView2ForTests!, "document.getElementById('toast-text').textContent")) == invalid);
            Assert.False(tray.ClassicWindowForTests.Visible);
        });

    [Fact]
    public async Task AWebView2StartupFailure_OpensTheClassicFallback_AndTheEngineKeepsListening()
    {
        using var server = new TrayFakeServer();
        await _host.RunAsync(shell: null, available: true, async (tray, localizer) =>
        {
            await server.NextPoll().WaitAsync(Timeout);

            Click(tray, localizer["Tray.Settings"]);

            await WaitUntilAsync(() => Task.FromResult(tray.ClassicWindowForTests.Visible));
            Assert.Equal(localizer["Tab.Settings"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            Assert.Equal("Shell.Fallback.StartFailed", tray.FallbackNoticeForTests);
            await server.NextPoll().WaitAsync(Timeout);
            Assert.True(tray.RuntimeForTests.IsRunning);
        }, failShellStartup: true, configure: d => TrayExitTests.ListenToTheFakeServer(d, server.Url));
    }

    [Fact]
    public async Task ABrowserProcessFailure_OpensTheClassicFallback_AndTheEngineKeepsListening()
    {
        using var server = new TrayFakeServer();
        await _host.RunAsync(shell: null, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.PrintHistory"]);
            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            await server.NextPoll().WaitAsync(Timeout);

            // The browser process ends unexpectedly (as after a crash or a runtime update).
            System.Diagnostics.Process.GetProcessById((int)shell.CoreWebView2ForTests!.BrowserProcessId).Kill();

            await WaitUntilAsync(() => Task.FromResult(tray.ClassicWindowForTests.Visible));
            Assert.Equal(localizer["Tab.PrintHistory"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            Assert.Equal("Shell.Fallback.StartFailed", tray.FallbackNoticeForTests);
            await WaitUntilAsync(() => Task.FromResult(tray.ShellFormForTests is null));
            await server.NextPoll().WaitAsync(Timeout);
            Assert.True(tray.RuntimeForTests.IsRunning);
        }, configure: d => TrayExitTests.ListenToTheFakeServer(d, server.Url));
    }

    [Fact]
    public async Task ThePage_AndTheLogs_ShowNoToken_ServerAddress_OrMachineName()
    {
        using var server = new TrayFakeServer();
        await _host.RunAsync(shell: null, available: true, async (tray, localizer) =>
        {
            await server.NextPoll().WaitAsync(Timeout);
            Click(tray, localizer["Tray.Settings"]);
            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            var core = shell.CoreWebView2ForTests!;
            // Wait until the page shows the engine's state (a snapshot arrived), then read everything it holds.
            await WaitUntilAsync(async () => !string.IsNullOrEmpty(await EvalAsync(core, "document.getElementById('diag-printer').textContent")));
            var dom = await EvalAsync(core, "document.documentElement.outerHTML");

            var host = new Uri(server.Url).Authority;
            foreach (var secret in new[] { TrayFakeServer.Token, server.Url, host, Environment.MachineName })
                Assert.DoesNotContain(secret, dom, StringComparison.OrdinalIgnoreCase);

            var logs = string.Join("\n", Directory.GetFiles(Wasla.PrintBridge.Configuration.PrintBridgePaths.ProgramDataLogDirectory).Select(ReadShared));
            Assert.DoesNotContain(TrayFakeServer.Token, logs, StringComparison.Ordinal);
        }, configure: d => TrayExitTests.ListenToTheFakeServer(d, server.Url));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(50);
        }
    }

    private static void Click(TrayApplicationContext tray, string text) =>
        tray.TrayMenuForTests.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == text).PerformClick();

    private static async Task<T> WaitForAsync<T>(Func<T?> find)
        where T : class
    {
        var until = DateTime.UtcNow + Timeout;
        while (true)
        {
            if (find() is { } found)
                return found;
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The window did not appear in time.");
            await Task.Delay(50);
        }
    }

    private static async Task<string> EvalAsync(CoreWebView2 core, string expression) =>
        System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(expression)) ?? string.Empty;
}
