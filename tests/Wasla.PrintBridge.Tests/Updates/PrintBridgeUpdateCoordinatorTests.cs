using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.Tests.Service;
using Wasla.PrintBridge.Tests.WebShell;
using Wasla.PrintBridge.Updates;

namespace Wasla.PrintBridge.Tests.Updates;

/// <summary>
/// WAS-63, the service's side of an update (docs/integrations/print-bridge-updates.md) with the production engine, pipe and
/// device client in this process (<see cref="InProcessPrintBridgeService"/>), a local fake Wasla server in test mode
/// (nothing is printed) and real Authenticode checks. The signed "setup" is a small file of the .NET runtime this test runs
/// on, signed by Microsoft: the installation is given Microsoft's publisher, so that file is a validly signed setup of the
/// same publisher, and its version resource is the release version. Windows Installer is a fake
/// (<see cref="FakeInstalledProduct"/>); the real installer is exercised by <c>PrintBridgeInstallerTests</c> on CI.
/// "Restarting the service" builds a new one on the same data folder; that is a simulated restart, not a process or machine
/// restart.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeUpdateCoordinatorTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly Version Installed = new(1, 0, 0);

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-update-tests");
    private readonly TrayFakeServer _server = new();
    private readonly List<IAsyncDisposable> _owned = [];
    private readonly TimeSpan _printingWait = PrintBridgeUpdateCoordinator.PrintingWait;
    private readonly TimeSpan _gateLifetime = PrintBridgeUpdateCoordinator.GateLifetime;
    private readonly TimeSpan _commitPollInterval = PrintBridgeUpdateCoordinator.CommitPollInterval;
    private readonly TimeSpan _commitIdleLimit = PrintBridgeUpdateCoordinator.CommitIdleLimit;
    private readonly TimeSpan _cleanupRetryInterval = PrintBridgeUpdateCoordinator.CleanupRetryInterval;

    public PrintBridgeUpdateCoordinatorTests()
    {
        PrintBridgeUpdateCoordinator.CommitPollInterval = TimeSpan.FromMilliseconds(100);
        PrintBridgeUpdateCoordinator.CleanupRetryInterval = TimeSpan.FromMilliseconds(200);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
            await owned.DisposeAsync();
        _server.Dispose();
        PrintBridgeUpdateCoordinator.PrintingWait = _printingWait;
        PrintBridgeUpdateCoordinator.GateLifetime = _gateLifetime;
        PrintBridgeUpdateCoordinator.CommitPollInterval = _commitPollInterval;
        PrintBridgeUpdateCoordinator.CommitIdleLimit = _commitIdleLimit;
        PrintBridgeUpdateCoordinator.CleanupRetryInterval = _cleanupRetryInterval;
        _dataRoot.Dispose();
    }

    // ---- Discovery -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnsignedInstallation_NeitherAsksTheServer_NorDownloads()
    {
        Configure();
        _server.Release = new TrayFakeRelease("9.9.9", [1, 2, 3]);
        var service = await StartAsync(publisher: null);
        var updates = service.Backend.Updates;

        Assert.False(updates.Snapshot().Supported);
        Assert.Equal("1.0.0", updates.Snapshot().InstalledVersion);
        Assert.Equal("Update.Error.NotSigned", (await Assert.ThrowsAsync<LocalizedApplicationException>(() => updates.CheckAsync(Ct))).ResourceKey);
        Assert.Equal("Update.Error.NotSigned", Assert.Throws<LocalizedApplicationException>(() => updates.Prepare(new Version(9, 9, 9))).ResourceKey);
        Assert.DoesNotContain(_server.Requests, r => r.Contains("releases", StringComparison.Ordinal));
        Assert.Null(Store().Load());
    }

    [Fact]
    public async Task TheCheck_OffersOnlyANewerValidRelease_AndChangesNothing()
    {
        Configure();
        var service = await StartAsync(Publisher());
        var updates = service.Backend.Updates;

        // No release (204), the installed version, an older one: nothing to offer.
        Assert.Equal(new IpcUpdateCheckResult("1.0.0", null), await updates.CheckAsync(Ct));
        foreach (var offered in new[] { "1.0.0", "0.9.9" })
        {
            _server.Release = new TrayFakeRelease(offered, [1]);
            Assert.Null((await updates.CheckAsync(Ct)).Available);
        }

        _server.Release = new TrayFakeRelease("1.0.1", [1]);
        Assert.Equal(new IpcUpdateCheckResult("1.0.0", "1.0.1"), await updates.CheckAsync(Ct));

        // A description out of the contract (a four-part version, a bad hash) is refused, never offered.
        foreach (var invalid in new[] { new TrayFakeRelease("1.0.1.0", [1]), new TrayFakeRelease("1.0.1", [1]) { Sha256 = "not-a-hash" } })
        {
            _server.Release = invalid;
            var refused = await Assert.ThrowsAsync<LocalizedApplicationException>(() => updates.CheckAsync(Ct));
            Assert.Equal("Update.Error.InvalidRelease", refused.ResourceKey);
        }

        Assert.Null(Store().Load());
        Assert.DoesNotContain(_server.Requests, r => r.EndsWith("/setup", StringComparison.Ordinal));
        Assert.True(service.Runtime.IsRunning);
    }

    // ---- A verified setup and the pause ----------------------------------------------------------------------------

    [Fact]
    public async Task AVerifiedSetup_IsStagedForTheApp_PrintingPausesAtASafePoint_AndCancellingResumesIt()
    {
        Configure();
        var signed = Signed();
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        var service = await StartAsync(signed.Publisher);
        await _server.NextPoll().WaitAsync(Wait, Ct);
        var app = await ConnectAsync(service);
        var updates = service.Backend.Updates;

        var attempt = updates.Prepare(Version.Parse(signed.Version));
        var ready = await WaitForStageAsync(updates, PrintBridgeUpdateStage.Ready);

        // The app is given the verified file, its size and hash (it checks them again before it starts it).
        Assert.Equal(attempt, ready.AttemptId);
        Assert.Equal("1.0.0", ready.FromVersion);
        Assert.Equal(signed.Version, ready.ToVersion);
        var staged = PrintBridgeUpdateStateStore.StagedSetupPath(Version.Parse(signed.Version));
        Assert.Equal(staged, ready.StagedSetupPath);
        Assert.Equal(signed.Bytes, File.ReadAllBytes(staged));
        Assert.Equal(signed.Bytes.LongLength, ready.SetupBytes);
        Assert.Equal(Sha256(signed.Bytes), ready.SetupSha256);
        Assert.False(File.Exists(PrintBridgeUpdateStateStore.PartialSetupPath(Version.Parse(signed.Version))));
        Assert.Equal(PrintBridgeUpdateStage.Ready, Store().Load()!.Stage);
        Assert.True(updates.PausesChanges);

        // Paused: the server is still asked, but a new job is not claimed.
        var job = _server.AddJob();
        await _server.NextPoll().WaitAsync(Wait, Ct);
        await _server.NextPoll().WaitAsync(Wait, Ct);
        Assert.Equal("Pending", _server.State(job));
        Assert.Empty(_server.ReportsFor(job));

        // Changes wait for the update, with the reason; the user's Stop is not lost silently, it is refused and not saved.
        var refused = await Assert.ThrowsAsync<LocalizedApplicationException>(() => app.StopAsync());
        Assert.Equal("Update.Error.InProgress", refused.ResourceKey);
        Assert.False(new PrintBridgeSettingsStore().Load().PrintBridge.PrintingStoppedByUser);
        Assert.Throws<PrintBridgeEngineBusyException>(() => updates.Prepare(Version.Parse(signed.Version)));

        // Cancel (the app's setup did not install): the file goes, printing resumes, the job prints once.
        await updates.CancelAsync(attempt);
        Assert.Equal(PrintBridgeUpdateStage.Cancelled, updates.Snapshot().Stage);
        Assert.Null(updates.Snapshot().StagedSetupPath);
        Assert.False(File.Exists(staged));
        Assert.Equal(PrintBridgeUpdateStage.Cancelled, Store().Load()!.Stage);
        Assert.False(updates.PausesChanges);
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(job));

        // Cancelling again, or another attempt's id, changes nothing.
        await updates.CancelAsync(attempt);
        await updates.CancelAsync(Guid.NewGuid());
        Assert.Equal(PrintBridgeUpdateStage.Cancelled, updates.Snapshot().Stage);
    }

    [Fact]
    public async Task ThePause_WaitsForTheReceiptInProgress_AndNeverCutsItShort()
    {
        Configure();
        var signed = Signed();
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        var service = await StartAsync(signed.Publisher);
        var updates = service.Backend.Updates;

        // A job is between its claim and its last report when the update is prepared.
        var claim = new TrayGate();
        _server.HoldClaimAnswer = claim;
        var job = _server.AddJob();
        await claim.Entered.Task.WaitAsync(Wait, Ct);
        updates.Prepare(Version.Parse(signed.Version));
        await WaitForStageAsync(updates, PrintBridgeUpdateStage.WaitingForPrinting);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Assert.Equal(PrintBridgeUpdateStage.WaitingForPrinting, updates.Snapshot().Stage);

        // The job finishes (printed and reported once), and only then is printing paused.
        _server.HoldClaimAnswer = null;
        claim.Open();
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
        await WaitForStageAsync(updates, PrintBridgeUpdateStage.Ready);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(job));
        Assert.Equal("Printed", _server.State(job));
    }

    [Fact]
    public async Task ThePause_WaitsForAnAcknowledgementRetry_AndKeepsItsJournalEntry()
    {
        // A receipt printed before a restart whose printed report is not confirmed yet: the journal holds it.
        Configure();
        var unconfirmed = Guid.NewGuid();
        new PrintBridgeJobJournal(NullLogger<PrintBridgeJobJournal>.Instance)
            .Record(unconfirmed, PrintJournalStage.Printed, new PrintBridgeSettingsStore().Load().Wasla);
        var signed = Signed();
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        var report = new TrayGate();
        _server.DropPrintedReports = report;
        var service = await StartAsync(signed.Publisher);
        var updates = service.Backend.Updates;

        // The retry of its report is on its way when the update is prepared: the pause waits for it.
        await report.Entered.Task.WaitAsync(Wait, Ct);
        updates.Prepare(Version.Parse(signed.Version));
        await WaitForStageAsync(updates, PrintBridgeUpdateStage.WaitingForPrinting);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Assert.Equal(PrintBridgeUpdateStage.WaitingForPrinting, updates.Snapshot().Stage);

        // The server does not confirm it (503): the entry stays for a later retry, never dropped or reprinted.
        _server.DropPrintedReports = null;
        report.Open();
        await WaitForStageAsync(updates, PrintBridgeUpdateStage.Ready);
        var entry = Assert.Single(new PrintBridgeJobJournal(NullLogger<PrintBridgeJobJournal>.Instance).Entries);
        Assert.Equal(unconfirmed, entry.JobId);
        Assert.Equal(PrintJournalStage.Printed, entry.Stage);
        Assert.Equal(["mark-printed"], _server.ReportsFor(unconfirmed));
    }

    [Fact]
    public async Task AReceiptThatOutlastsTheWait_EndsTheAttempt_AndTheReceiptFinishes()
    {
        PrintBridgeUpdateCoordinator.PrintingWait = TimeSpan.FromSeconds(1);
        Configure();
        var signed = Signed();
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        var service = await StartAsync(signed.Publisher);
        var updates = service.Backend.Updates;
        var claim = new TrayGate();
        _server.HoldClaimAnswer = claim;
        var job = _server.AddJob();
        await claim.Entered.Task.WaitAsync(Wait, Ct);

        updates.Prepare(Version.Parse(signed.Version));
        var failed = await WaitForStageAsync(updates, PrintBridgeUpdateStage.Failed);

        Assert.Equal(PrintBridgeUpdateFailure.PrintingInProgress, failed.Failure);
        Assert.False(updates.PausesChanges);
        Assert.Empty(StagedFiles());
        _server.HoldClaimAnswer = null;
        claim.Open();
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(job));
    }

    [Fact]
    public async Task AReadySetupNobodyRuns_ExpiresAndPrintingResumes()
    {
        PrintBridgeUpdateCoordinator.GateLifetime = TimeSpan.FromSeconds(2);
        Configure();
        var signed = Signed();
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        var service = await StartAsync(signed.Publisher);
        var updates = service.Backend.Updates;

        updates.Prepare(Version.Parse(signed.Version));
        await WaitForStageAsync(updates, PrintBridgeUpdateStage.Ready);
        var cancelled = await WaitForStageAsync(updates, PrintBridgeUpdateStage.Cancelled);

        Assert.Equal(PrintBridgeUpdateFailure.Expired, cancelled.Failure);
        Assert.Empty(StagedFiles());
        var job = _server.AddJob();
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
    }

    // ---- One attempt at a time, cancellation, interruption -----------------------------------------------------------

    [Fact]
    public async Task ConcurrentPrepares_StartOneAttempt()
    {
        Configure();
        var signed = Signed();
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        var download = new TrayGate();
        _server.HoldSetupDownload = download;
        var service = await StartAsync(signed.Publisher);
        var updates = service.Backend.Updates;
        var version = Version.Parse(signed.Version);

        using var start = new Barrier(8);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(object () =>
        {
            start.SignalAndWait(Ct);
            try
            {
                return updates.Prepare(version);
            }
            catch (PrintBridgeEngineBusyException ex)
            {
                return ex;
            }
        })));

        var attempt = Assert.Single(outcomes.OfType<Guid>());
        Assert.Equal(7, outcomes.OfType<PrintBridgeEngineBusyException>().Count());
        await download.Entered.Task.WaitAsync(Wait, Ct);
        Assert.Single(_server.Requests, r => r.EndsWith("/setup", StringComparison.Ordinal));
        Assert.Equal(attempt, updates.Snapshot().AttemptId);
        await updates.CancelAsync(attempt);
    }

    [Fact]
    public async Task CancellingADownload_StopsItAndDeletesThePartialFile()
    {
        Configure();
        var signed = Signed();
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        var download = new TrayGate();
        _server.HoldSetupDownload = download;
        var service = await StartAsync(signed.Publisher);
        var updates = service.Backend.Updates;

        var attempt = updates.Prepare(Version.Parse(signed.Version));
        await download.Entered.Task.WaitAsync(Wait, Ct);
        await WaitUntilAsync(() => updates.Snapshot().DownloadPercent is > 0);
        Assert.Equal(PrintBridgeUpdateStage.Downloading, updates.Snapshot().Stage);

        await updates.CancelAsync(attempt);

        Assert.Equal(PrintBridgeUpdateStage.Cancelled, updates.Snapshot().Stage);
        Assert.Empty(StagedFiles());
        Assert.Equal(PrintBridgeUpdateStage.Cancelled, Store().Load()!.Stage);
        var job = _server.AddJob();
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task TheAppDisconnecting_CancelsAnAttemptNotReady_ButNotAReadyOne()
    {
        Configure();
        var signed = Signed();
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        var download = new TrayGate();
        _server.HoldSetupDownload = download;
        var service = await StartAsync(signed.Publisher);
        var updates = service.Backend.Updates;

        var first = updates.Prepare(Version.Parse(signed.Version));
        await download.Entered.Task.WaitAsync(Wait, Ct);
        await updates.ConnectionClosedAsync(first);
        Assert.Equal(PrintBridgeUpdateStage.Cancelled, updates.Snapshot().Stage);

        _server.HoldSetupDownload = null;
        download.Open();
        var second = updates.Prepare(Version.Parse(signed.Version));
        await WaitForStageAsync(updates, PrintBridgeUpdateStage.Ready);

        // Ready: the app has started the setup, which closes the app; the attempt stays until the setup runs or it expires.
        await updates.ConnectionClosedAsync(second);
        Assert.Equal(PrintBridgeUpdateStage.Ready, updates.Snapshot().Stage);
        Assert.True(File.Exists(PrintBridgeUpdateStateStore.StagedSetupPath(Version.Parse(signed.Version))));
        await updates.CancelAsync(second);
    }

    [Fact]
    public async Task AServiceStopDuringTheDownload_IsReconciledAsInterrupted_AtTheNextStart()
    {
        Configure();
        var signed = Signed();
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        var download = new TrayGate();
        _server.HoldSetupDownload = download;
        var service = await StartAsync(signed.Publisher);
        service.Backend.Updates.Prepare(Version.Parse(signed.Version));
        await download.Entered.Task.WaitAsync(Wait, Ct);

        // The service stops (simulated: the in-process service is disposed, as a Windows stop or shutdown would).
        await service.DisposeAsync();
        Assert.Equal(PrintBridgeUpdateStage.Downloading, Store().Load()!.Stage);

        var restarted = await StartAsync(signed.Publisher);
        var after = restarted.Backend.Updates.Snapshot();
        AssertOutcome(after, PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.Interrupted);
        Assert.Empty(StagedFiles());
        Assert.True(restarted.Runtime.IsRunning);
    }

    // ---- What the service refuses to stage -----------------------------------------------------------------------------

    public static TheoryData<string, PrintBridgeUpdateFailure> Refusals => new()
    {
        { "tampered", PrintBridgeUpdateFailure.HashMismatch },
        { "longer", PrintBridgeUpdateFailure.SizeMismatch },
        { "shorter", PrintBridgeUpdateFailure.SizeMismatch },
        { "unsigned", PrintBridgeUpdateFailure.NotSigned },
        { "broken-signature", PrintBridgeUpdateFailure.SignatureInvalid },
        { "other-publisher", PrintBridgeUpdateFailure.PublisherMismatch },
        { "other-version", PrintBridgeUpdateFailure.VersionMismatch }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ASetupThatFailsAnyCheck_IsDeletedAndNeverStaged_AndPrintingContinues(string setup, PrintBridgeUpdateFailure expected)
    {
        Configure();
        var signed = Signed();
        var publisher = signed.Publisher;
        var version = signed.Version;
        TrayFakeRelease release;
        switch (setup)
        {
            case "tampered":
                // The description is the signed file's; the server sends another file of the same size.
                var other = (byte[])signed.Bytes.Clone();
                other[other.Length / 3] ^= 0xFF;
                release = new TrayFakeRelease(version, signed.Bytes) { ServedSetup = other };
                break;
            case "longer":
                release = new TrayFakeRelease(version, signed.Bytes) { ServedSetup = [.. signed.Bytes, 0] };
                break;
            case "shorter":
                release = new TrayFakeRelease(version, signed.Bytes) { ServedSetup = signed.Bytes[..^1] };
                break;
            case "unsigned":
                // Described truthfully, but no signature: a hash from the server is not proof of who made it.
                release = new TrayFakeRelease(version, File.ReadAllBytes(typeof(PrintBridgeUpdateCoordinatorTests).Assembly.Location));
                break;
            case "broken-signature":
                // Described truthfully, signed, but changed after signing.
                var changed = (byte[])signed.Bytes.Clone();
                changed[changed.Length / 3] ^= 0xFF;
                release = new TrayFakeRelease(version, changed);
                break;
            case "other-publisher":
                publisher = new PrintBridgePublisher(new X500DistinguishedName("CN=Wasla Print Bridge test publisher"));
                release = new TrayFakeRelease(version, signed.Bytes);
                break;
            default:
                // The file's own version resource is not the version described.
                version = "9.9.9";
                release = new TrayFakeRelease(version, signed.Bytes);
                break;
        }

        _server.Release = release;
        var service = await StartAsync(publisher);
        var updates = service.Backend.Updates;

        updates.Prepare(Version.Parse(version));
        var failed = await WaitForStageAsync(updates, PrintBridgeUpdateStage.Failed);

        Assert.Equal(expected, failed.Failure);
        Assert.Null(failed.StagedSetupPath);
        Assert.Empty(StagedFiles());
        var saved = Store().Load()!;
        Assert.Equal(PrintBridgeUpdateStage.Failed, saved.Stage);
        Assert.Equal(expected, saved.Failure);
        Assert.False(updates.PausesChanges);
        var job = _server.AddJob();
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task AReleaseNoLongerOffered_IsNotDownloaded()
    {
        Configure();
        var signed = Signed();
        _server.Release = new TrayFakeRelease("1.0.5", signed.Bytes);
        var service = await StartAsync(signed.Publisher);
        var updates = service.Backend.Updates;

        // The user confirmed 1.0.5; the server now offers another release.
        _server.Release = new TrayFakeRelease(signed.Version, signed.Bytes);
        updates.Prepare(new Version(1, 0, 5));
        var failed = await WaitForStageAsync(updates, PrintBridgeUpdateStage.Failed);

        Assert.Equal(PrintBridgeUpdateFailure.VersionMismatch, failed.Failure);
        Assert.DoesNotContain(_server.Requests, r => r.EndsWith("/setup", StringComparison.Ordinal));
        Assert.Equal("Update.Error.NotNewer", Assert.Throws<LocalizedApplicationException>(() => updates.Prepare(new Version(1, 0, 0))).ResourceKey);
    }

    // ---- Reconciliation at start ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PrintBridgeUpdateStage.Downloading)]
    [InlineData(PrintBridgeUpdateStage.Verifying)]
    [InlineData(PrintBridgeUpdateStage.WaitingForPrinting)]
    public async Task AnAttemptInterruptedBeforeReady_IsDroppedAtStart(PrintBridgeUpdateStage stage)
    {
        Configure();
        Seed(stage, from: "1.0.0", to: "1.0.1");

        var service = await StartAsync(Publisher());
        var after = service.Backend.Updates.Snapshot();

        AssertOutcome(after, PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.Interrupted);
        Assert.Empty(StagedFiles());
        Assert.Equal(PrintBridgeUpdateStage.Failed, Store().Load()!.Stage);
        await PrintsOnceAsync();
    }

    [Fact]
    public async Task ThePreviousVersionRunningAfterReady_MeansTheUpdateWasNotApplied()
    {
        // The setup was cancelled at Windows' prompt, failed, or rolled back: 1.0.0 runs again.
        Configure();
        Seed(PrintBridgeUpdateStage.Ready, from: "1.0.0", to: "1.0.1");

        var service = await StartAsync(Publisher());
        var after = service.Backend.Updates.Snapshot();

        Assert.Equal(PrintBridgeUpdateStage.NotApplied, after.Stage);
        Assert.Null(after.Failure);
        Assert.Empty(StagedFiles());
        Assert.False(service.Backend.Updates.PausesChanges);
        await PrintsOnceAsync();
    }

    [Fact]
    public async Task TheNewVersion_WritesAndPrintsNothing_UntilWindowsInstallerCommits()
    {
        Configure();
        Seed(PrintBridgeUpdateStage.Ready, from: "0.9.0", to: "1.0.0");
        var job = _server.AddJob();
        var installer = new FakeInstalledProduct { Installing = true, Version = new Version(0, 9, 0) };
        var settingsBefore = File.ReadAllBytes(_dataRoot.ConfigPath);

        var service = await StartAsync(Publisher(), installer);
        var updates = service.Backend.Updates;

        // The installation is still running (it can still roll back): no request, no print, no settings write.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.False(service.Runtime.IsRunning);
        Assert.Empty(_server.Requests);
        Assert.Equal(settingsBefore, File.ReadAllBytes(_dataRoot.ConfigPath));
        Assert.True(updates.PausesChanges);
        Assert.Equal(PrintBridgeUpdateStage.Ready, updates.Snapshot().Stage);
        Assert.Null(updates.Snapshot().StagedSetupPath);

        // Windows Installer finished and registered this version: the update succeeded and printing starts.
        installer.Version = new Version(1, 0, 0);
        installer.Installing = false;
        var succeeded = await WaitForStageAsync(updates, PrintBridgeUpdateStage.Succeeded);
        Assert.Null(succeeded.Failure);
        Assert.Equal(PrintBridgeUpdateStage.Succeeded, Store().Load()!.Stage);
        Assert.Empty(StagedFiles());
        Assert.False(updates.PausesChanges);
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(job));
    }

    [Fact]
    public async Task TheNewVersion_PrintsWithoutACommit_OnlyAfterTheInstallerIsGoneForTheIdleLimit()
    {
        PrintBridgeUpdateCoordinator.CommitIdleLimit = TimeSpan.FromSeconds(2);
        Configure();
        Seed(PrintBridgeUpdateStage.Ready, from: "0.9.0", to: "1.0.0");
        var installer = new FakeInstalledProduct { Installing = false, Version = new Version(0, 9, 0) };

        var service = await StartAsync(Publisher(), installer);
        var failed = await WaitForStageAsync(service.Backend.Updates, PrintBridgeUpdateStage.Failed);

        Assert.Equal(PrintBridgeUpdateFailure.UnexpectedVersion, failed.Failure);
        await PrintsOnceAsync();
    }

    [Fact]
    public async Task AnotherVersionRunningAfterReady_IsRecordedAsUnexpected()
    {
        Configure();
        Seed(PrintBridgeUpdateStage.Ready, from: "0.8.0", to: "0.9.0");

        var service = await StartAsync(Publisher());
        var after = service.Backend.Updates.Snapshot();

        AssertOutcome(after, PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.UnexpectedVersion);
        Assert.Empty(StagedFiles());
        await PrintsOnceAsync();
    }

    [Fact]
    public async Task AFinishedAttempt_IsShownAsItWas_AndAnUnreadableStateIsSetAside()
    {
        Configure();
        Seed(PrintBridgeUpdateStage.Succeeded, from: "0.9.0", to: "1.0.0");
        var service = await StartAsync(Publisher());
        Assert.Equal(PrintBridgeUpdateStage.Succeeded, service.Backend.Updates.Snapshot().Stage);
        Assert.Empty(StagedFiles());
        await service.DisposeAsync();

        File.WriteAllText(PrintBridgePaths.UpdateStatePath, "{ not json");
        File.WriteAllBytes(PrintBridgeUpdateStateStore.StagedSetupPath(new Version(1, 0, 1)), [1, 2, 3]);
        var restarted = await StartAsync(Publisher());

        Assert.Null(restarted.Backend.Updates.Snapshot().Stage);
        Assert.Single(Directory.GetFiles(PrintBridgePaths.UpdatesDirectory, "update-state.json.corrupt-*"));
        Assert.Empty(StagedFiles());
        await PrintsOnceAsync();
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    private sealed record SignedFile(byte[] Bytes, string Version, PrintBridgePublisher Publisher);

    /// <summary>
    /// A small file of the .NET runtime running this test, Authenticode-signed by Microsoft with its version resource
    /// (for example 8.0.31): a validly signed setup of release <see cref="SignedFile.Version"/> by publisher
    /// <see cref="SignedFile.Publisher"/>.
    /// </summary>
    private static SignedFile Signed()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll");
        Assert.Equal(PrintBridgeSignatureStatus.Valid, PrintBridgeAuthenticode.Verify(path, out var publisher));
        var version = PrintBridgeReleaseVersion.FromResource(FileVersionInfo.GetVersionInfo(path).ProductVersion);
        Assert.NotNull(version);
        Assert.True(version > Installed);
        return new SignedFile(File.ReadAllBytes(path), PrintBridgeReleaseVersion.Format(version), publisher!);
    }

    private static PrintBridgePublisher Publisher() => Signed().Publisher;

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static PrintBridgeUpdateStateStore Store() => new(NullLogger<PrintBridgeUpdateStateStore>.Instance);

    private static string[] StagedFiles() =>
        Directory.Exists(PrintBridgePaths.UpdatesDirectory)
            ? Directory.GetFiles(PrintBridgePaths.UpdatesDirectory, "Wasla.PrintBridge-Setup-x64-*")
            : [];

    /// <summary>A record left by an earlier run, with its staged and partial files.</summary>
    private static void Seed(PrintBridgeUpdateStage stage, string from, string to)
    {
        var version = Version.Parse(to);
        Store().Save(new PrintBridgeUpdateRecord(
            PrintBridgeUpdateRecord.CurrentSchemaVersion,
            Guid.NewGuid(),
            from,
            to,
            3,
            Sha256([1, 2, 3]),
            stage,
            DateTime.UtcNow.AddMinutes(-2),
            stage == PrintBridgeUpdateStage.Ready ? DateTime.UtcNow.AddMinutes(-1) : null,
            stage is PrintBridgeUpdateStage.Succeeded ? DateTime.UtcNow : null,
            null));
        File.WriteAllBytes(PrintBridgeUpdateStateStore.StagedSetupPath(version), [1, 2, 3]);
        File.WriteAllBytes(PrintBridgeUpdateStateStore.PartialSetupPath(version), [1]);
    }

    private void Configure()
    {
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        TrayExitTests.ListenToTheFakeServer(document, _server.Url);
        store.Save(document);
    }

    private async Task<InProcessPrintBridgeService> StartAsync(PrintBridgePublisher? publisher, FakeInstalledProduct? installer = null)
    {
        var service = await InProcessPrintBridgeService.StartAsync(
            services => services.AddSingleton(PrintBridgeServerTrustPolicy.ForTests("localhost")),
            updates: TestUpdates.Coordinator(publisher, Installed, installer));
        _owned.Add(new OnceDisposable(service));
        return service;
    }

    private async Task<PrintBridgeServiceClient> ConnectAsync(InProcessPrintBridgeService service)
    {
        var client = service.CreateClient();
        _owned.Add(client);
        await client.Connected.WaitAsync(Wait, Ct);
        return client;
    }

    private async Task PrintsOnceAsync()
    {
        var job = _server.AddJob();
        await _server.JobFinished(job).WaitAsync(Wait, Ct);
        Assert.Equal(["mark-printing", "mark-printed"], _server.ReportsFor(job));
    }

    private static void AssertOutcome(IpcUpdate snapshot, PrintBridgeUpdateStage stage, PrintBridgeUpdateFailure? failure)
    {
        Assert.Equal(stage, snapshot.Stage);
        Assert.Equal(failure, snapshot.Failure);
    }

    private static async Task<IpcUpdate> WaitForStageAsync(PrintBridgeUpdateCoordinator updates, PrintBridgeUpdateStage stage)
    {
        IpcUpdate snapshot = updates.Snapshot();
        await WaitUntilAsync(() => (snapshot = updates.Snapshot()).Stage == stage, () => $"stage {stage}; last {snapshot.Stage} {snapshot.Failure}");
        return snapshot;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? describe = null)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time: " + (describe?.Invoke() ?? "condition"));
            await Task.Delay(25, Ct);
        }
    }

    /// <summary>A service disposed by the test (a simulated restart) is not disposed again at the end.</summary>
    private sealed class OnceDisposable(IAsyncDisposable inner) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? inner.DisposeAsync() : ValueTask.CompletedTask;
    }
}
