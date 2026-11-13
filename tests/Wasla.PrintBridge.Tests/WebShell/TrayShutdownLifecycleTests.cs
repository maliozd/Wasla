using System.Text.Json.Nodes;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.TrayExitTests;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// WAS-59 and WAS-61: the order and uniqueness of the tray application's one shutdown sequence, and that closing the app
/// never stops printing or acknowledgement: the engine is the service's. The real tray application is a client of the
/// service's engine (in this process, over a real pipe), which listens to a local fake server with a fake token in test
/// mode (nothing is printed) on an isolated data root.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class TrayShutdownLifecycleTests : IDisposable
{
    private static readonly TimeSpan Timeout = TrayTestHost.Timeout;

    private static readonly string[] FullSequence =
    [
        "shutdown-started",
        "ui-hidden",
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
    public Task Close_DisposesEachComponentOnce_EndsTheThread_AndPrintingContinues() =>
        _host.RunAsync(available: false, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);
            var iconDisposals = 0;
            tray.TrayIconForTests.Disposed += (_, _) => iconDisposals++;
            var threadExits = 0;
            tray.ThreadExit += (_, _) => threadExits++;

            await tray.ExitForTests().WaitAsync(Timeout);

            Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
            // A configured installation runs in the tray only: no window was ever created.
            Assert.Null(tray.ShellFormForTests);
            Assert.Null(tray.RuntimeNoticeForTests);
            Assert.Equal(1, iconDisposals);
            Assert.Equal(1, threadExits);

            // The engine is the service's: it is still listening, and still polls, with no app running.
            Assert.True(_host.Runtime.IsRunning);
            await _server.NextPoll().WaitAsync(Timeout);
            Assert.False(_host.Engine.Settings.Bridge.PrintingStoppedByUser);
        }, configure: Listen);

    [Fact]
    public Task RepeatedAndConcurrentCloseRequests_JoinTheOneShutdown() =>
        _host.RunAsync(available: false, async (tray, localizer) =>
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
            Assert.True(_host.Runtime.IsRunning);
        }, configure: Listen);

    [Fact]
    public Task ATrayUpdateQueuedBeforeClose_AndDeliveredAfterItBegan_DoesNothing() =>
        _host.RunAsync(available: false, async (tray, localizer) =>
        {
            var ignored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.TrayUpdateIgnoredForTests = () => ignored.TrySetResult();

            // The service's engine reports a status change. This (the UI) thread stays busy until the app has received
            // it, so the tray update the app queues for this thread arrives after the shutdown began.
            Task.Run(() => _host.Runtime.RecordConnectionFailure(new InvalidOperationException("status change"), applyCredentialFailureFallback: false))
                .Wait(Timeout);
            Assert.True(SpinWait.SpinUntil(
                () => tray.EngineForTests.GetStatus().LastIssue?.Code == PrintBridgeRuntimeIssueCode.Unexpected,
                Timeout));
            var exit = tray.ExitForTests();

            await ignored.Task.WaitAsync(Timeout);
            await exit.WaitAsync(Timeout);
            Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
        });

    [Fact]
    public Task CloseWhileAJobIsBeingClaimed_DoesNotWaitForIt_AndTheServicePrintsAndReportsItOnce()
    {
        Guid jobId = default;
        Task? finished = null;
        TrayGate? claim = null;
        return _host.RunAsync(
            available: false,
            async (tray, localizer) =>
            {
                claim = new TrayGate();
                _server.HoldClaimAnswer = claim;
                jobId = _server.AddJob();
                await claim.Entered.Task.WaitAsync(Timeout);
                finished = _server.JobFinished(jobId);

                // Closing the app is not a stop: it completes while the claim is still unanswered.
                Click(tray, localizer["Tray.Exit"]);
                await tray.ExitForTests().WaitAsync(Timeout);
                Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
                Assert.True(_host.Runtime.IsRunning);
                Assert.Equal("Printing", _server.State(jobId));
            },
            configure: Listen,
            afterAppExit: async () =>
            {
                // With no app running, the service finishes the job: printed (test mode) and reported once.
                claim!.Open();
                await finished!.WaitAsync(Timeout);
                Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(jobId));
                Assert.Equal("Printed", _server.State(jobId));
                Assert.Equal(LocalPrintJobStatus.Printed, HistoryEntry(jobId)?.Status);
                Assert.True(_host.Runtime.IsRunning);
            });
    }

    [Fact]
    public Task JobsCreatedAfterTheAppClosed_ArePrintedAndAcknowledged()
    {
        Guid jobId = default;
        return _host.RunAsync(
            available: false,
            async (tray, localizer) =>
            {
                await _server.NextPoll().WaitAsync(Timeout);
                await tray.ExitForTests().WaitAsync(Timeout);
            },
            configure: Listen,
            afterAppExit: async () =>
            {
                jobId = _server.AddJob();
                await _server.JobFinished(jobId).WaitAsync(Timeout);
                Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(jobId));
                Assert.Equal("Printed", _server.State(jobId));
            });
    }

    [Fact]
    public Task ASetupLinkArrivingAfterCloseBegan_IsDropped() =>
        _host.RunAsync(available: false, async (tray, localizer) =>
        {
            await _server.NextPoll().WaitAsync(Timeout);
            var exit = tray.ExitForTests();

            tray.HandleSetupUri($"wasla-printbridge://setup?server={Uri.EscapeDataString(_server.Url)}&code=setupcodenotarealcredential01");
            Click(tray, localizer["Tray.Open"]);
            Click(tray, localizer["Tray.Settings"]);
            await exit.WaitAsync(Timeout);

            Assert.DoesNotContain(_server.Requests, r => r.Contains("setup", StringComparison.Ordinal));
            Assert.Null(tray.RuntimeNoticeForTests);
            Assert.Null(tray.ShellFormForTests);
            Assert.Empty(_host.SetupResultMessages);
            Assert.Equal(FullSequence, tray.ShutdownStepsForTests);
        }, configure: Listen);

    [Fact]
    public Task Close_LeavesTheSettingsAndThePrintHistoryValid_AndTheConnectionUnchanged()
    {
        Guid jobId = default;
        JsonNode? before = null;
        return _host.RunAsync(available: false, async (tray, localizer) =>
        {
            jobId = _server.AddJob();
            await _server.JobFinished(jobId).WaitAsync(Timeout);
            await _server.NextPoll().WaitAsync(Timeout);
            before = JsonNode.Parse(File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath));

            await tray.ExitForTests().WaitAsync(Timeout);

            // Closing writes no settings: the connection, printer and UI settings are left exactly as they were.
            var after = JsonNode.Parse(File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath))!;
            Assert.True(JsonNode.DeepEquals(before, after));
            var history = JsonNode.Parse(File.ReadAllText(PrintBridgePaths.ProgramDataHistoryPath));
            Assert.NotNull(history);
            Assert.Equal(LocalPrintJobStatus.Printed, HistoryEntry(jobId)?.Status);
        }, configure: Listen);
    }

    [Fact]
    [Trait("Category", "WebView2Runtime")]
    public Task CloseWithTheAppWindowOpen_DisposesIt_AndPrintingContinues() =>
        _host.RunAsync(available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Open"]);
            var shell = tray.ShellFormForTests!;
            await shell.InitializationForTests!.WaitAsync(Timeout);
            await _server.NextPoll().WaitAsync(Timeout);

            await tray.ExitForTests().WaitAsync(Timeout);

            Assert.Equal(WithAppWindow(), tray.ShutdownStepsForTests);
            Assert.True(shell.IsDisposed);
            Assert.Null(tray.ShellFormForTests);
            Assert.True(_host.Runtime.IsRunning);
        }, configure: Listen);

    [Fact]
    [Trait("Category", "WebView2Runtime")]
    public Task CloseWithTheAppWindowClosedToTheTray_DisposesIt_AndPrintingContinues() =>
        _host.RunAsync(available: true, async (tray, localizer) =>
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
            Assert.True(_host.Runtime.IsRunning);
        }, configure: Listen);

    private static string[] WithAppWindow() =>
        [FullSequence[0], FullSequence[1], "app-window-disposed", .. FullSequence[2..]];

    private void Listen(PrintBridgeSettingsStore.AppSettingsDocument document) => ListenToTheFakeServer(document, _server.Url);

    private static LocalPrintJobRecord? HistoryEntry(Guid jobId) =>
        new LocalPrintJobHistoryStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalPrintJobHistoryStore>.Instance)
            .FindByJobId(jobId);
}
