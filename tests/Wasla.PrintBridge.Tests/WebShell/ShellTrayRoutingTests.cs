using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// The real tray application (services, engine, classic window, WebView2 window) on a temporary data root, driven
/// through its tray menu. By default every normal entry opens the one WebView2 window on the matching tab; the classic
/// window opens only for the explicit <c>WinForms</c> rollback or when WebView2 cannot run.
/// No device token is configured, so nothing contacts a server.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
[Trait("Category", "WebView2Runtime")]
public sealed class ShellTrayRoutingTests : IDisposable
{
    private static readonly TimeSpan Timeout = TrayTestHost.Timeout;

    // The tray application saves settings (window layout) when it exits; the data root waits for its thread.
    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-tray-tests");
    private readonly CultureScope _cultureScope = new();
    private readonly TrayTestHost _host;

    public ShellTrayRoutingTests() => _host = new TrayTestHost(_dataRoot);

    public void Dispose()
    {
        _dataRoot.Dispose();
        _cultureScope.Dispose();
    }

    [Fact]
    public Task TrayHistoryAndSettings_OpenTheWebView2Tabs_InOneWindow_AndNeverTheClassicWindow() =>
        _host.RunAsync(ShellSelection.WebView2Value, available: true, async (tray, localizer) =>
        {
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

            Assert.Single(System.Windows.Forms.Application.OpenForms.OfType<PrintBridgeShellForm>());
            Assert.False(tray.ClassicWindowForTests.Visible);

            // The classic window is no longer offered while the app works: no tray entry and no page button.
            OpenMenu(tray);
            Assert.DoesNotContain(tray.TrayMenuForTests.Items.OfType<ToolStripMenuItem>(), i => i.Text == localizer["Tray.OpenClassicFallback"]);
            Assert.Equal("null", await core.ExecuteScriptAsync("document.getElementById('open-classic')"));
            Assert.False(tray.ClassicWindowForTests.Visible);
        });

    [Fact]
    public Task MissingWebView2Runtime_TrayHistoryOpensTheClassicHistoryTab() =>
        _host.RunAsync(ShellSelection.WebView2Value, available: false, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.PrintHistory"]);

            Assert.Null(tray.ShellFormForTests);
            Assert.True(tray.ClassicWindowForTests.Visible);
            Assert.Equal(localizer["Tab.PrintHistory"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            Assert.Equal("Shell.Fallback.RuntimeMissing", tray.FallbackNoticeForTests);
            await Task.CompletedTask;
        });

    [Fact]
    public Task WebView2StartupFailure_FallsBackToTheClassicTabThatWasAskedFor() =>
        _host.RunAsync(ShellSelection.WebView2Value, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Settings"]);

            await WaitUntilAsync(() => Task.FromResult(tray.ClassicWindowForTests.Visible));
            Assert.Equal(localizer["Tab.Settings"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            Assert.Equal("Shell.Fallback.StartFailed", tray.FallbackNoticeForTests);
            await WaitUntilAsync(() => Task.FromResult(tray.ShellFormForTests is null));

            // For the rest of the session the tray opens the classic window directly.
            tray.ClassicWindowForTests.Hide();
            Click(tray, localizer["Tray.PrintHistory"]);
            Assert.True(tray.ClassicWindowForTests.Visible);
            Assert.Equal(localizer["Tab.PrintHistory"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            Assert.Null(tray.ShellFormForTests);
        }, failShellStartup: true);

    [Fact]
    public Task TheWinFormsRollback_KeepsEveryTrayEntryOnTheClassicWindow() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Settings"]);

            Assert.Null(tray.ShellFormForTests);
            Assert.True(tray.ClassicWindowForTests.Visible);
            Assert.Equal(localizer["Tab.Settings"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            Assert.Null(tray.FallbackNoticeForTests);
            await Task.CompletedTask;
        });

    private static ToolStripMenuItem Item(TrayApplicationContext tray, string text) =>
        tray.TrayMenuForTests.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == text);

    private static void Click(TrayApplicationContext tray, string text) => Item(tray, text).PerformClick();

    /// <summary>Raises the menu's Opening event, as right-clicking the tray icon does, without showing the menu.</summary>
    private static void OpenMenu(TrayApplicationContext tray)
    {
        var raise = typeof(ToolStripDropDown).GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        raise.Invoke(tray.TrayMenuForTests, [new System.ComponentModel.CancelEventArgs()]);
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
