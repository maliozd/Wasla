using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.ShellTrayRoutingTests;
using static Wasla.PrintBridge.Tests.WebShell.TrayExitTests;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// The real tray application when the WebView2 Runtime is missing or too old, on a temporary data root: every tray entry
/// and a setup link show the one localized runtime notice, the tray and the engine keep running and printing, a setup
/// link is still applied (and still refused while a print job is being completed), and a configured installation
/// starts in the tray only. A configured engine listens to a local fake server with a fake token in test mode, so
/// nothing is printed on paper. No WebView2 Runtime is needed.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class TrayRuntimeNoticeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TrayTestHost.Timeout;

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-tray-notice-tests");
    private readonly CultureScope _cultureScope = new();
    private readonly TrayFakeServer _server = new();
    private readonly TrayTestHost _host;

    public TrayRuntimeNoticeTests() => _host = new TrayTestHost(_dataRoot);

    public void Dispose()
    {
        _dataRoot.Dispose();
        _server.Dispose();
        _cultureScope.Dispose();
    }

    [Fact]
    public Task TheTrayMenu_OffersExactlyOpenPrintHistoryPrinterTestSettingsAndExit() =>
        _host.RunAsync(available: false, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);

            var items = tray.TrayMenuForTests.Items.Cast<ToolStripItem>().ToArray();
            Assert.Equal(8, items.Length);
            Assert.False(items[0].Enabled);
            Assert.Equal(
                [localizer["Tray.Open"], localizer["Tray.PrintHistory"], localizer["Tray.PrinterTest"], localizer["Tray.Settings"], localizer["Tray.Exit"]],
                items.OfType<ToolStripMenuItem>().Skip(1).Select(i => i.Text));
            Assert.Equal(2, items.OfType<ToolStripSeparator>().Count());
        }, configure: Listen);

    [Fact]
    public Task AConfiguredInstallation_StartsInTheTrayOnly() =>
        _host.RunAsync(available: false, async (tray, _) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);
            // Let anything the start queued on the UI thread run first.
            await Task.Delay(500);

            Assert.True(tray.RuntimeForTests.IsRunning);
            Assert.Null(tray.ShellFormForTests);
            Assert.Null(tray.RuntimeNoticeForTests);
            Assert.Empty(VisibleWindows());
        }, configure: Listen);

    [Fact]
    public Task AFirstRunWithoutTheRuntime_ShowsTheRuntimeNotice() =>
        _host.RunAsync(available: false, async (tray, localizer) =>
        {
            var notice = await WaitForNoticeAsync(tray);

            Assert.Equal([typeof(WebView2RuntimeNotice)], VisibleWindows());
            Assert.Equal(localizer["RuntimeNotice.Title"], notice.Text);
            Assert.Equal(localizer["RuntimeNotice.Message"], notice.Message.Text);
            Assert.Equal(localizer["RuntimeNotice.Message"], notice.AccessibleDescription);
            Assert.Null(tray.ShellFormForTests);
            Assert.False(tray.RuntimeForTests.IsRunning);

            // Enter and Escape both close it through its one button; a closed notice is gone.
            Assert.Same(notice.CloseButton, notice.AcceptButton);
            Assert.Same(notice.CloseButton, notice.CancelButton);
            notice.CloseButton.PerformClick();
            Assert.True(notice.IsDisposed);
            Assert.Null(tray.RuntimeNoticeForTests);
            Assert.Empty(VisibleWindows());
        });

    [Theory]
    [InlineData(SupportedCultures.Turkish, false, "Kapat")]
    [InlineData(SupportedCultures.English, false, "Close")]
    [InlineData(SupportedCultures.Arabic, true, "إغلاق")]
    [InlineData(SupportedCultures.Russian, false, "Закрыть")]
    public Task TheRuntimeNotice_IsLocalized_MirroredForArabic_AndScaledForTheMonitor(string culture, bool rightToLeft, string close) =>
        _host.RunAsync(available: false, async (tray, localizer) =>
        {
            var notice = await WaitForNoticeAsync(tray);

            Assert.Equal(close, notice.CloseButton.Text);
            Assert.Equal(localizer["RuntimeNotice.Message"], notice.Message.Text);
            Assert.Contains("Microsoft Edge WebView2 Runtime", notice.Message.Text, StringComparison.Ordinal);
            Assert.Equal(rightToLeft ? RightToLeft.Yes : RightToLeft.No, notice.RightToLeft);
            Assert.Equal(rightToLeft, notice.RightToLeftLayout);
            Assert.Equal(AutoScaleMode.Dpi, notice.AutoScaleMode);
            Assert.Equal(rightToLeft ? RightToLeft.Yes : RightToLeft.No, tray.TrayMenuForTests.RightToLeft);
        }, configure: document => document.Ui.Language = culture);

    [Fact]
    public Task WithoutTheRuntime_EveryTrayEntryAndASetupLink_ShowTheOneNotice_AndPrintingContinues() =>
        _host.RunAsync(available: false, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);

            foreach (var entry in new[] { "Tray.Open", "Tray.PrintHistory", "Tray.Settings" })
            {
                Click(tray, localizer[entry]);
                var notice = tray.RuntimeNoticeForTests;
                Assert.True(notice is { Visible: true }, entry);
                // Asked again while it is open: the same notice is brought forward.
                Click(tray, localizer[entry]);
                Assert.Same(notice, tray.RuntimeNoticeForTests);
                notice!.Close();
                Assert.Null(tray.RuntimeNoticeForTests);
            }

            DoubleClickTrayIcon(tray);
            Assert.True(tray.RuntimeNoticeForTests is { Visible: true });

            tray.HandleSetupUri("wasla-printbridge://setup?server=not-a-server");
            await WaitUntilAsync(() => !_host.SetupResultMessages.IsEmpty);
            Assert.Equal([localizer["Auto.InvalidLink"]], _host.SetupResultMessages);
            Assert.Equal([typeof(WebView2RuntimeNotice)], VisibleWindows());
            Assert.Null(tray.ShellFormForTests);

            // The engine kept listening throughout: a new job is printed (test mode) and reported once.
            var jobId = _server.AddJob();
            await _server.JobFinished(jobId).WaitAsync(Timeout);
            Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(jobId));
            Assert.Equal("Printed", _server.State(jobId));
            Assert.True(tray.RuntimeForTests.IsRunning);
        }, configure: Listen);

    [Fact]
    public Task WithoutTheRuntime_ASetupLinkDuringAPrintJob_IsRefused_AndChangesNothing() =>
        _host.RunAsync(available: false, async (tray, localizer) =>
        {
            var claim = new TrayGate();
            _server.HoldClaimAnswer = claim;
            var jobId = _server.AddJob();
            await claim.Entered.Task.WaitAsync(Timeout);
            var finished = _server.JobFinished(jobId);
            var settingsBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

            tray.HandleSetupUri(PrintBridgeSetupLink(_server.Url));

            await WaitUntilAsync(() => !_host.SetupResultMessages.IsEmpty);
            Assert.Equal([localizer["Auto.PrintingInProgress"]], _host.SetupResultMessages);
            Assert.True(tray.RuntimeNoticeForTests is { Visible: true });
            Assert.Equal(settingsBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
            Assert.DoesNotContain(_server.Requests, r => r.Contains("setup", StringComparison.Ordinal));

            // The job finishes on the saved connection and is reported once.
            claim.Open();
            await finished.WaitAsync(Timeout);
            Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(jobId));
        }, configure: Listen);

    [Fact]
    public Task WithoutTheRuntime_ASetupLinkAfterPrinting_IsStillSentToTheServer() =>
        _host.RunAsync(available: false, async (tray, _) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);

            tray.HandleSetupUri(PrintBridgeSetupLink(_server.Url));

            // The fake server does not issue setup configurations, so the link ends as not applied; what matters here
            // is that the one-time code was exchanged with the server named in the link, without the app window.
            await WaitUntilAsync(() => !_host.SetupResultMessages.IsEmpty);
            Assert.Contains(_server.Requests, r => r.Contains("setup/exchange", StringComparison.Ordinal));
            Assert.True(tray.RuntimeNoticeForTests is { Visible: true });
            Assert.Null(tray.ShellFormForTests);
        }, configure: Listen);

    private void Listen(PrintBridgeSettingsStore.AppSettingsDocument document) => ListenToTheFakeServer(document, _server.Url);

    private static string PrintBridgeSetupLink(string serverUrl) =>
        $"wasla-printbridge://setup?server={Uri.EscapeDataString(serverUrl)}&code=setupcodenotarealcredential01";

    private static async Task<WebView2RuntimeNotice> WaitForNoticeAsync(TrayApplicationContext tray)
    {
        WebView2RuntimeNotice? notice = null;
        await WaitUntilAsync(() => (notice = tray.RuntimeNoticeForTests) is { Visible: true });
        return notice!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(20);
        }
    }
}
