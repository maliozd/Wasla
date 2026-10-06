using System.Text.Json.Nodes;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.TrayExitTests;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// WAS-59: the order and uniqueness of the tray application's one shutdown sequence, and what happens to callbacks,
/// setup links and an active print job while it runs. The real tray application listens to a local fake server with a
/// fake token in test mode (nothing is printed) on an isolated data root.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class TrayShutdownLifecycleTests : IDisposable
{
    private static readonly TimeSpan Timeout = TrayTestHost.Timeout;

    private static readonly string[] FullSequence =
    [
        "shutdown-started",
        "runtime-stopped",
        "classic-window-disposed",
        "tray-icon-disposed",
        "services-disposed",
        "thread-exited"
    ];

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-tray-shutdown-tests");
    private readonly CultureScope _cultureScope = new();
    private readonly TrayFakeServer _server = new();
    private readonly TrayTestHost _host;

    public TrayShutdownLifecycleTests() => _host = new TrayTestHost(_dataRoot);

    public void Dispose()
    {
        _dataRoot.Dispose();
        _server.Dispose();
        _cultureScope.Dispose();
    }

    [Fact]
    public Task Exit_StopsTheEngineFirst_ThenDisposesEachComponentOnce_ThenEndsTheThread() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);
            var iconDisposals = 0;
            tray.TrayIconForTests.Disposed += (_, _) => iconDisposals++;
            var threadExits = 0;
            tray.ThreadExit += (_, _) => threadExits++;

            await tray.ExitForTests().WaitAsync(Timeout);

            Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
            Assert.False(tray.RuntimeForTests.IsRunning);
            Assert.True(tray.ClassicWindowForTests.IsDisposed);
            Assert.Equal(1, iconDisposals);
            Assert.Equal(1, threadExits);
        }, configure: Listen);

    [Fact]
    public Task RepeatedAndConcurrentExitRequests_JoinTheOneShutdown() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);
            var threadExits = 0;
            tray.ThreadExit += (_, _) => threadExits++;

            // Four threads at once, plus the menu twice, plus one more after the shutdown has finished.
            using var start = new Barrier(4);
            var concurrent = Enumerable.Range(0, 4)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        start.SignalAndWait(Timeout);
                        return tray.ExitForTests();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();
            Click(tray, localizer["Tray.Exit"]);
            Click(tray, localizer["Tray.Exit"]);
            var requests = await Task.WhenAll(concurrent).WaitAsync(Timeout);

            await Task.WhenAll(requests).WaitAsync(Timeout);
            Assert.All(requests, request => Assert.Same(requests[0], request));
            Assert.Same(requests[0], tray.ExitForTests());
            Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
            Assert.Equal(1, threadExits);
        }, configure: Listen);

    [Fact]
    public Task ATrayUpdateQueuedBeforeExit_AndDeliveredAfterItBegan_DoesNothing() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            var ignored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.TrayUpdateIgnoredForTests = () => ignored.TrySetResult();
            // The classic window marshals with its own handle; it exists before any engine thread asks for it.
            _ = tray.ClassicWindowForTests.Handle;

            // An engine thread reports a status change: the tray queues its update on this (the UI) thread, which is
            // busy until Exit has been requested, so the update arrives after the shutdown began.
            Task.Run(() => tray.RuntimeForTests.RecordConnectionFailure(new InvalidOperationException("status change"), applyCredentialFailureFallback: false))
                .Wait(Timeout);
            var exit = tray.ExitForTests();

            await ignored.Task.WaitAsync(Timeout);
            await exit.WaitAsync(Timeout);
            Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
        });

    [Fact]
    public Task ExitWhileAJobIsBeingClaimed_WaitsForIt_AndItIsReportedPrintedOnce() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            var claim = new TrayGate();
            _server.HoldClaimAnswer = claim;
            var jobId = _server.AddJob();
            await claim.Entered.Task.WaitAsync(Timeout);
            var finished = _server.JobFinished(jobId);

            Click(tray, localizer["Tray.Exit"]);
            var exit = tray.ExitForTests();
            await WaitUntilAsync(() => tray.ShutdownStepsForTests.Count > 0 && !tray.RuntimeForTests.IsRunning);

            // Listening has stopped, but the claimed job is not cancelled: the shutdown waits for it (WAS-56).
            Assert.False(exit.IsCompleted);
            Assert.Equal(["shutdown-started"], tray.ShutdownStepsForTests);

            // The job finishes while the shutdown waits; its status changes reach no disposed component.
            claim.Open();
            await finished.WaitAsync(Timeout);
            await exit.WaitAsync(Timeout);

            Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
            Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(jobId));
            Assert.Equal("Printed", _server.State(jobId));
            Assert.Equal(LocalPrintJobStatus.Printed, HistoryEntry(jobId)?.Status);
        }, configure: Listen);

    [Fact]
    public Task ExitAfterTheStopLimit_EndsTheApplication_AndTheJobIsNeverReportedFailed() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            tray.RuntimeForTests.StopGracePeriod = TimeSpan.FromMilliseconds(200);
            var claim = new TrayGate();
            _server.HoldClaimAnswer = claim;
            var jobId = _server.AddJob();
            await claim.Entered.Task.WaitAsync(Timeout);
            var finished = _server.JobFinished(jobId);

            // Stop returns at its limit while the job still waits for the claim answer: the shutdown completes.
            await tray.ExitForTests().WaitAsync(Timeout);
            Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
            Assert.Equal("Printing", _server.State(jobId));

            // In this test process the job can still finish in the background (in the real process, which ends here, it
            // cannot; it stays Printing on the server, the known WAS-56 limit). Either way it is never reported failed.
            claim.Open();
            await finished.WaitAsync(Timeout);
            Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(jobId));
        }, configure: Listen);

    [Fact]
    public Task ASetupLinkArrivingAfterExitBegan_IsDropped() =>
        _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);
            var exit = tray.ExitForTests();

            tray.HandleSetupUri($"wasla-printbridge://setup?server={Uri.EscapeDataString(_server.Url)}&code=setupcodenotarealcredential01");
            Click(tray, localizer["Tray.Open"]);
            Click(tray, localizer["Tray.Settings"]);
            await exit.WaitAsync(Timeout);

            Assert.DoesNotContain(_server.Requests, r => r.Contains("setup", StringComparison.Ordinal));
            Assert.False(tray.ClassicWindowForTests.Visible);
            Assert.Null(tray.ShellFormForTests);
            Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
        }, configure: Listen);

    [Fact]
    public Task Exit_LeavesTheSettingsAndThePrintHistoryValid_AndTheConnectionUnchanged()
    {
        Guid jobId = default;
        JsonNode? before = null;
        return _host.RunAsync(ShellSelection.WinFormsValue, available: false, async (tray, localizer) =>
        {
            jobId = _server.AddJob();
            await _server.JobFinished(jobId).WaitAsync(Timeout);
            await _server.NextPoll().WaitAsync(Timeout);
            before = JsonNode.Parse(File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath));

            await tray.ExitForTests().WaitAsync(Timeout);

            // Exit may save the window layout (Ui); the connection and printer settings are never touched.
            var after = JsonNode.Parse(File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath))!;
            Assert.True(JsonNode.DeepEquals(before!["OrderHub"], after["OrderHub"]));
            Assert.True(JsonNode.DeepEquals(before["PrintBridge"], after["PrintBridge"]));
            var history = JsonNode.Parse(File.ReadAllText(PrintBridgePaths.ProgramDataHistoryPath));
            Assert.NotNull(history);
            Assert.Equal(LocalPrintJobStatus.Printed, HistoryEntry(jobId)?.Status);
        }, configure: Listen);
    }

    [Fact]
    [Trait("Category", "WebView2Runtime")]
    public Task ExitWithTheAppWindowOpen_DisposesItAfterTheEngineStopped() =>
        _host.RunAsync(ShellSelection.WebView2Value, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Open"]);
            var shell = tray.ShellFormForTests!;
            await shell.InitializationForTests!.WaitAsync(Timeout);
            await _server.NextPoll().WaitAsync(Timeout);

            await tray.ExitForTests().WaitAsync(Timeout);

            Assert.Equal(WithAppWindow(), tray.ShutdownStepsForTests);
            Assert.True(shell.IsDisposed);
            Assert.Null(tray.ShellFormForTests);
        }, configure: Listen);

    [Fact]
    [Trait("Category", "WebView2Runtime")]
    public Task ExitWithTheAppWindowClosedToTheTray_DisposesItAfterTheEngineStopped() =>
        _host.RunAsync(ShellSelection.WebView2Value, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Open"]);
            var shell = tray.ShellFormForTests!;
            await shell.InitializationForTests!.WaitAsync(Timeout);
            shell.Close();
            Assert.False(shell.Visible);
            Assert.False(shell.IsDisposed);
            await _server.NextPoll().WaitAsync(Timeout);

            Click(tray, localizer["Tray.Exit"]);
            await tray.ExitForTests().WaitAsync(Timeout);

            Assert.Equal(WithAppWindow(), tray.ShutdownStepsForTests);
            Assert.True(shell.IsDisposed);
        }, configure: Listen);

    private static string[] WithAppWindow() =>
        [FullSequence[0], FullSequence[1], "app-window-disposed", .. FullSequence[2..]];

    private void Listen(PrintBridgeSettingsStore.AppSettingsDocument document) => ListenToTheFakeServer(document, _server.Url);

    private static LocalPrintJobRecord? HistoryEntry(Guid jobId) =>
        new LocalPrintJobHistoryStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalPrintJobHistoryStore>.Instance)
            .FindByJobId(jobId);

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
