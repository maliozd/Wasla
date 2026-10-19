using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.TrayExitTests;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// The real tray application with a usable WebView2 Runtime, on a temporary data root, driven through its tray menu.
/// A first run opens the WebView2 app on its connect step; a configured installation starts in the tray only; every
/// tray entry and a setup link open the one WebView2 window; when the app fails to start, the runtime notice opens
/// instead for the rest of the session. A configured engine listens to a local fake server with a fake token in test
/// mode, so nothing is printed.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
[Trait("Category", "WebView2Runtime")]
public sealed class ShellTrayRoutingTests : IDisposable
{
    private static readonly TimeSpan Timeout = TrayTestHost.Timeout;

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-tray-tests");
    private readonly CultureScope _cultureScope = new();
    private readonly TrayFakeServer _server = new();
    private readonly TrayTestHost _host;

    public ShellTrayRoutingTests() => _host = new TrayTestHost(_dataRoot);

    public void Dispose()
    {
        _dataRoot.Dispose();
        _server.Dispose();
        _cultureScope.Dispose();
    }

    [Fact]
    public Task FirstRunWithoutAToken_OpensTheWebView2App_OnItsConnectStep() =>
        _host.RunAsync(available: true, async (tray, localizer) =>
        {
            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            var core = shell.CoreWebView2ForTests!;

            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('connection').dataset.state") == "notConfigured");
            Assert.Equal("true", await EvalAsync(core, "document.getElementById('tab-overview').getAttribute('aria-selected')"));
            Assert.Contains(localizer["Shell.Action.Connect"], await EvalAsync(core, "document.getElementById('hero-actions').textContent"));
            Assert.Equal([typeof(PrintBridgeShellForm)], VisibleWindows());
            Assert.Null(tray.RuntimeNoticeForTests);
        });

    [Fact]
    public Task EveryTrayEntry_OpensTheOneWebView2Window_OnItsTab() =>
        _host.RunAsync(available: true, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);
            // Configured: the application started in the tray only.
            Assert.Null(tray.ShellFormForTests);

            Click(tray, localizer["Tray.PrintHistory"]);
            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            var core = shell.CoreWebView2ForTests!;
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('tab-history').getAttribute('aria-selected')") == "true");

            Click(tray, localizer["Tray.Settings"]);
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('tab-settings').getAttribute('aria-selected')") == "true");
            Assert.Same(shell, tray.ShellFormForTests);

            shell.Hide();
            Click(tray, localizer["Tray.Open"]);
            await WaitUntilAsync(() => Task.FromResult(shell.Visible));
            Assert.Same(shell, tray.ShellFormForTests);

            shell.Hide();
            DoubleClickTrayIcon(tray);
            await WaitUntilAsync(() => Task.FromResult(shell.Visible));
            Assert.Same(shell, tray.ShellFormForTests);

            Assert.Equal([typeof(PrintBridgeShellForm)], VisibleWindows());
            Assert.Null(tray.RuntimeNoticeForTests);
            Assert.True(tray.RuntimeForTests.IsRunning);
        }, configure: Listen);

    [Fact]
    public Task ASetupLink_IsHandledInTheWebView2Window_AndItsResultShownThere() =>
        _host.RunAsync(available: true, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);

            tray.HandleSetupUri("wasla-printbridge://setup?server=not-a-server");

            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            var core = shell.CoreWebView2ForTests!;
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('toast-text').textContent") == localizer["Auto.InvalidLink"]);
            Assert.Empty(_host.SetupResultMessages);
            Assert.Null(tray.RuntimeNoticeForTests);
            Assert.True(tray.RuntimeForTests.IsRunning);
        }, configure: Listen);

    [Fact]
    public Task WebView2StartupFailure_ShowsTheRuntimeNotice_ForTheRestOfTheSession_AndKeepsListening() =>
        _host.RunAsync(available: true, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);

            Click(tray, localizer["Tray.Settings"]);

            var notice = await WaitForAsync(() => tray.RuntimeNoticeForTests is { Visible: true } open ? open : null);
            await WaitUntilAsync(() => Task.FromResult(tray.ShellFormForTests is null));
            Assert.Equal(localizer["RuntimeNotice.Message"], notice.Message.Text);

            // For the rest of the session the tray shows the notice directly; no new app window is created.
            notice.CloseButton.PerformClick();
            Assert.True(notice.IsDisposed);
            Click(tray, localizer["Tray.PrintHistory"]);
            Assert.True(tray.RuntimeNoticeForTests is { Visible: true });
            Assert.Null(tray.ShellFormForTests);
            Assert.Equal([typeof(WebView2RuntimeNotice)], VisibleWindows());

            await _server.NextPoll().WaitAsync(Timeout);
            Assert.True(tray.RuntimeForTests.IsRunning);
        }, failShellStartup: true, configure: Listen);

    private void Listen(PrintBridgeSettingsStore.AppSettingsDocument document) => ListenToTheFakeServer(document, _server.Url);

    internal static Type[] VisibleWindows() =>
        [.. System.Windows.Forms.Application.OpenForms.Cast<Form>().Where(f => f.Visible).Select(f => f.GetType())];

    /// <summary>Raises the tray icon's double-click, as the user does, without a visible icon.</summary>
    internal static void DoubleClickTrayIcon(TrayApplicationContext tray)
    {
        var raise = typeof(NotifyIcon).GetMethod("OnDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NotifyIcon has no double-click handler to raise.");
        raise.Invoke(tray.TrayIconForTests, [EventArgs.Empty]);
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> find)
        where T : class
    {
        T? found = null;
        await WaitUntilAsync(() => Task.FromResult((found = find()) is not null));
        return found!;
    }

    private static async Task<string?> EvalAsync(CoreWebView2 core, string expression)
    {
        var request = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true, allowUnsafeEvalBlockedByCSP = false });
        var response = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request);
        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");
        return result.TryGetProperty("value", out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached in time.");
            await Task.Delay(50);
        }
    }
}
