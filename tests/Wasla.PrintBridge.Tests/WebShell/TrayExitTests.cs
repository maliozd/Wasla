using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// WAS-59: exiting the real tray application from its menu. The engine listens to a local fake server with a fake token
/// in test mode (nothing is printed) on an isolated data root. Exit must end the application without an exception,
/// whatever the engine is doing, and must never turn a printed receipt into a failed one.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class TrayExitTests : IDisposable
{
    private static readonly TimeSpan Timeout = TrayTestHost.Timeout;

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-tray-exit-tests");
    private readonly CultureScope _cultureScope = new();
    private readonly TrayFakeServer _server = new();
    private readonly TrayTestHost _host;

    public TrayExitTests() => _host = new TrayTestHost(_dataRoot);

    public void Dispose()
    {
        _dataRoot.Dispose();
        _server.Dispose();
        _cultureScope.Dispose();
    }

    [Fact]
    public Task ExitWhileListening_EndsTheApplication_WithoutAnException() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);
            var exited = ThreadExited(tray);

            Click(tray, localizer["Tray.Exit"]);

            await exited.WaitAsync(Timeout);
            Assert.False(tray.TrayIconForTests.Visible);
            Assert.DoesNotContain(_server.Requests, r => r.EndsWith("/mark-failed", StringComparison.Ordinal));
        }, configure: ListenToTheFakeServer);

    [Fact]
    public Task ExitWhileStopped_EndsTheApplication_WithoutAnException() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            var exited = ThreadExited(tray);

            Click(tray, localizer["Tray.Exit"]);

            await exited.WaitAsync(Timeout);
            Assert.False(tray.TrayIconForTests.Visible);
            Assert.Empty(_server.Requests);
        });

    [Fact]
    public Task ExitFromTheClassicWindowMode_WithTheWindowOpen_EndsTheApplication() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Open"]);
            Assert.True(tray.ClassicWindowForTests.Visible);
            await _server.NextPoll().WaitAsync(Timeout);
            var exited = ThreadExited(tray);

            Click(tray, localizer["Tray.Exit"]);

            await exited.WaitAsync(Timeout);
            Assert.True(tray.ClassicWindowForTests.IsDisposed);
        }, configure: ListenToTheFakeServer);

    [Fact]
    public Task ExitClickedAgain_AfterTheApplicationEnded_DoesNothing() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);
            var exits = 0;
            tray.ThreadExit += (_, _) => exits++;

            Click(tray, localizer["Tray.Exit"]);
            Click(tray, localizer["Tray.Exit"]);

            await WaitUntilAsync(() => exits > 0);
            Assert.Equal(1, exits);
        }, configure: ListenToTheFakeServer);

    internal static void ListenToTheFakeServer(PrintBridgeSettingsStore.AppSettingsDocument document, string url)
    {
        document.OrderHub.ServerUrl = url;
        document.OrderHub.AgentToken = TrayFakeServer.Token;
        document.PrintBridge.DryRun = true;
        document.PrintBridge.PrinterName = FirstInstalledPrinterOrSkip();
        document.PrintBridge.DisplayName = "QA Kasa";
        document.PrintBridge.ServerDeviceNameResolved = true;
        document.PrintBridge.IdlePollIntervalSeconds = 1;
        document.PrintBridge.BusyPollIntervalSeconds = 1;
        document.PrintBridge.ErrorPollIntervalSeconds = 1;
    }

    private void ListenToTheFakeServer(PrintBridgeSettingsStore.AppSettingsDocument document) =>
        ListenToTheFakeServer(document, _server.Url);

    internal static string FirstInstalledPrinterOrSkip()
    {
        // Start() checks that the printer exists in Windows (read-only). Test mode never sends anything to it.
        foreach (string installed in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            return installed;

        Assert.Skip("No Windows printer is installed; the engine refuses to start without one.");
        return string.Empty;
    }

    internal static Task ThreadExited(TrayApplicationContext tray)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tray.ThreadExit += (_, _) => exited.TrySetResult();
        return exited.Task;
    }

    internal static void Click(TrayApplicationContext tray, string text) =>
        tray.TrayMenuForTests.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == text).PerformClick();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(50);
        }
    }
}
