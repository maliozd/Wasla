using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.Tests.WebShell;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// WAS-61: the engine as the service runs it, used by the app over the real pipe: starting at service start, the explicit
/// Stop that survives a restart, closing the app versus stopping printing, the saved token that never reaches the app,
/// sanitized history and setup links decided by the service. The service part runs in this process
/// (<see cref="InProcessPrintBridgeService"/>); "restarting the service" builds a new one on the same data folder. It
/// listens to a local fake server with a fake token in test mode (nothing is printed).
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeEngineHostTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-engine-host-tests");
    private readonly TrayFakeServer _server = new();
    private readonly List<IAsyncDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
            await owned.DisposeAsync();
        _server.Dispose();
        _dataRoot.Dispose();
    }

    [Fact]
    public async Task TheService_StartsPrintingOnItsOwn_AndARestartDoesSoAgain()
    {
        Configure();
        var service = await StartServiceAsync();
        Assert.True(service.Runtime.IsRunning);
        await _server.NextPoll().WaitAsync(Wait, Ct);

        // A service stop (shutdown, update, crash recovery) is not the user's Stop.
        await service.DisposeAsync();
        Assert.False(new PrintBridgeSettingsStore().Load().PrintBridge.PrintingStoppedByUser);

        var restarted = await StartServiceAsync();
        Assert.True(restarted.Runtime.IsRunning);
        await _server.NextPoll().WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task TheUsersStop_StaysStopped_AcrossServiceRestarts_UntilStart()
    {
        Configure();
        var service = await StartServiceAsync();
        var app = await ConnectAsync(service);

        await app.StopAsync();

        Assert.False(service.Runtime.IsRunning);
        Assert.True(new PrintBridgeSettingsStore().Load().PrintBridge.PrintingStoppedByUser);
        await WaitUntilAsync(() => app.Settings.Bridge.PrintingStoppedByUser);
        await service.DisposeAsync();

        var restarted = await StartServiceAsync();
        Assert.False(restarted.Runtime.IsRunning);
        var requestsBefore = _server.Requests.Count;
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(requestsBefore, _server.Requests.Count);

        var nextApp = await ConnectAsync(restarted);
        var poll = _server.NextPoll();
        await nextApp.StartAsync(Ct);
        Assert.True(restarted.Runtime.IsRunning);
        await poll.WaitAsync(Wait, Ct);
        Assert.False(new PrintBridgeSettingsStore().Load().PrintBridge.PrintingStoppedByUser);
    }

    [Fact]
    public async Task ClosingTheApp_IsNotAStop()
    {
        Configure();
        var service = await StartServiceAsync();
        var app = await ConnectAsync(service, own: false);
        await _server.NextPoll().WaitAsync(Wait, Ct);

        await app.DisposeAsync();

        Assert.True(service.Runtime.IsRunning);
        Assert.False(service.Engine.Settings.Bridge.PrintingStoppedByUser);
        var jobId = _server.AddJob();
        await _server.JobFinished(jobId).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(jobId));
    }

    [Fact]
    public async Task KeepingTheSavedToken_ChecksItInTheService_WithoutTheAppEverKnowingIt()
    {
        Configure();
        var service = await StartServiceAsync();
        var app = await ConnectAsync(service);

        Assert.True(app.Settings.HasSavedToken);
        var verified = await app.CheckConnectionAsync(new PrintBridgeConnectionCandidate(_server.Url, string.Empty, KeepSavedToken: true), Ct);
        var change = await app.ApplyVerifiedConnectionAsync(verified, startListening: false, Ct);

        Assert.Equal(PrintBridgeConnectionChange.AppliedListening, change);
        Assert.Equal(TrayFakeServer.Token, new PrintBridgeSettingsStore().Load().Wasla.AgentToken);
        Assert.Equal(_server.Url, app.Settings.ServerUrl);

        // A wrong token is checked with the server and refused; nothing is saved.
        var refused = await Assert.ThrowsAsync<PrintBridgeConnectionException>(() =>
            app.CheckConnectionAsync(new PrintBridgeConnectionCandidate(_server.Url, "another-token-not-a-real-credential", KeepSavedToken: false), Ct));
        Assert.Equal(PrintBridgeRuntimeIssueCode.ReconnectRequired, refused.IssueCode);
        Assert.Equal(TrayFakeServer.Token, new PrintBridgeSettingsStore().Load().Wasla.AgentToken);
    }

    [Fact]
    public async Task HistoryReachesTheApp_ByPage_WithACategoryInsteadOfErrorText()
    {
        Configure();
        var history = new LocalPrintJobHistoryStore(NullLogger<LocalPrintJobHistoryStore>.Instance);
        for (var i = 0; i < 25; i++)
        {
            history.Record(new LocalPrintJobRecord
            {
                JobId = Guid.NewGuid(),
                OrderDisplay = $"GTR-{i:D3}",
                Status = i == 0 ? LocalPrintJobStatus.Failed : LocalPrintJobStatus.Printed,
                ErrorMessage = i == 0 ? "Unable to open printer 'POS-58' at \\\\secret-host" : null,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-i),
                LastAttemptAtUtc = DateTime.UtcNow.AddMinutes(-i)
            });
        }

        var service = await StartServiceAsync();
        var app = await ConnectAsync(service);

        var first = await app.QueryPrintHistoryAsync(PrintHistoryDateFilter.Today, null, 0, 20, Ct);
        var last = await app.QueryPrintHistoryAsync(PrintHistoryDateFilter.Today, null, 99, 20, Ct);

        Assert.Equal(25, first.Total);
        Assert.Equal(20, first.Rows.Count);
        Assert.Equal(1, last.Page);
        Assert.Equal(5, last.Rows.Count);
        var failed = Assert.Single(first.Rows.Concat(last.Rows), r => r.Status == LocalPrintJobStatus.Failed);
        Assert.Null(failed.ErrorMessage);
        Assert.Equal(LocalPrintJobFailureCategory.Printer, failed.FailureCategory);
    }

    [Fact]
    public async Task ASetupLinkOfAnUntrustedServer_IsRefusedByTheService_BeforeAnyRequest()
    {
        Configure();
        var service = await StartServiceAsync();
        var app = await ConnectAsync(service);
        var asked = false;

        var outcome = await app.ApplySetupLinkAsync(
            "wasla-printbridge://setup?server=https%3A%2F%2Fevil.example&code=FAKEcode0not0a0real0setup0code",
            (_, _) =>
            {
                asked = true;
                return Task.FromResult(true);
            },
            Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.InvalidLink, outcome);
        Assert.False(asked);
        Assert.DoesNotContain(_server.Requests, r => r.Contains("setup", StringComparison.Ordinal));
    }

    private void Configure()
    {
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        TrayExitTests.ListenToTheFakeServer(document, _server.Url);
        store.Save(document);
    }

    private async Task<InProcessPrintBridgeService> StartServiceAsync()
    {
        var service = await InProcessPrintBridgeService.StartAsync(
            services => services.AddSingleton(PrintBridgeServerTrustPolicy.ForTests("localhost")));
        _owned.Add(new OnceDisposable(service));
        return service;
    }

    private async Task<PrintBridgeServiceClient> ConnectAsync(InProcessPrintBridgeService service, bool own = true)
    {
        var client = service.CreateClient();
        if (own)
            _owned.Add(client);
        await client.Connected.WaitAsync(Wait, Ct);
        return client;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(25, Ct);
        }
    }

    /// <summary>A service disposed by the test (a "restart") is not disposed again at the end.</summary>
    private sealed class OnceDisposable(IAsyncDisposable inner) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? inner.DisposeAsync() : ValueTask.CompletedTask;
    }
}
