using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// The connection dialog's logic against the real engine (<see cref="PrintBridgeRuntime"/>), API client and settings
/// store, with a fake Wasla API. Every path points at a temporary data root, so the machine's real settings and token
/// are never read or written, and nothing is printed.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class ShellConnectionSetupTests : IDisposable
{
    private const string SavedUrl = "http://saved.print-bridge.test";
    private const string NewUrl = "http://new.print-bridge.test";
    private const string SavedToken = "saved-token-not-a-real-credential";
    private const string NewToken = "new-token-not-a-real-credential";
    private const string MissingPrinter = "Printer-That-Is-Not-Installed";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Each test disposes its engine (which waits for the polling loop) before this root is released.
    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-setup-tests");
    // Not initialized: the default Turkish culture without changing the process-wide thread cultures.
    private readonly PrintBridgeLocalizer _localizer = new(new PrintBridgeCultureService());

    public void Dispose()
    {
        _dataRoot.Dispose();
    }

    [Fact]
    public async Task FirstSetup_ChecksTheNewConnectionBeforeSaving_ThenSavesAndIsConnectedAtOnce()
    {
        var api = new FakeApi(NewToken) { DeviceName = "Kasa 2" };
        using var harness = Create(api, token: string.Empty);
        Assert.Equal(ShellConnectionSetupMode.FirstSetup, harness.Setup.Mode);
        Assert.False(harness.Runtime.GetStatus().IsConnected);

        var result = await harness.Setup.ConnectAsync(" " + NewUrl + "/ ", " " + NewToken + " ", TestContext.Current.CancellationToken);

        Assert.Equal(ShellConnectionSetupOutcome.ConnectedChoosePrinter, result.Outcome);
        Assert.Equal(_localizer["Shell.Setup.ConnectedChoosePrinter"], result.Message);
        // One check with the candidate values; the saved connection was never used for it.
        Assert.Equal([("new.print-bridge.test", NewToken, "api/print-bridge/health")], api.Requests.ToArray());

        var saved = harness.Store.Load();
        Assert.Equal(NewUrl, saved.OrderHub.ServerUrl);
        Assert.Equal(NewToken, saved.OrderHub.AgentToken);
        Assert.Equal("Kasa 2", saved.PrintBridge.DisplayName);
        Assert.True(saved.PrintBridge.ServerDeviceNameResolved);
        Assert.Equal(MissingPrinter, saved.PrintBridge.PrinterName);
        Assert.Equal(harness.InstallationId, saved.PrintBridge.InstallationId);

        // Connected without a restart or a second check; not listening because no usable printer is chosen.
        var status = harness.Runtime.GetStatus();
        Assert.True(status.IsConnected);
        Assert.Null(status.LastIssue);
        Assert.False(status.IsRunning);
        Assert.Equal(NewToken, harness.Holder.OrderHub.AgentToken);
    }

    [Fact]
    public async Task RejectedToken_SavesNothing_AndLeavesTheSavedConnectionAsItWas()
    {
        var api = new FakeApi(SavedToken);
        using var harness = Create(api, token: SavedToken);
        var fileBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        var result = await harness.Setup.ConnectAsync(SavedUrl, NewToken, TestContext.Current.CancellationToken);

        Assert.Equal(ShellConnectionSetupOutcome.VerificationFailed, result.Outcome);
        Assert.Equal(ShellConnectionSetupField.Token, result.Field);
        Assert.Equal(_localizer["Shell.Setup.TokenRejected"], result.Message);
        Assert.Equal(fileBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Equal(SavedToken, harness.Holder.OrderHub.AgentToken);
        // A rejected candidate is not the saved token: it must not clear it or mark the device for reconnect.
        Assert.Null(harness.Runtime.GetStatus().LastIssue);
        AssertNoSecrets(result.Message);
    }

    [Fact]
    public async Task OfflineServer_ReportsAnActionableError_AndSavesNothing()
    {
        var api = new FakeApi(NewToken) { Offline = true };
        using var harness = Create(api, token: string.Empty);
        var fileBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        var result = await harness.Setup.ConnectAsync(NewUrl, NewToken, TestContext.Current.CancellationToken);

        Assert.Equal(ShellConnectionSetupOutcome.VerificationFailed, result.Outcome);
        Assert.Equal(ShellConnectionSetupField.ServerUrl, result.Field);
        Assert.Equal(
            _localizer.GetRuntimeIssueDetail(new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable, "Connection.ServerUnreachable")),
            result.Message);
        Assert.Equal(fileBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.False(harness.Runtime.GetStatus().IsConnected);
        AssertNoSecrets(result.Message);
    }

    [Theory]
    [InlineData("", NewToken, "Validation.ServerUrlRequired", ShellConnectionSetupField.ServerUrl)]
    [InlineData("print-bridge.test", NewToken, "Validation.InvalidServerUrl", ShellConnectionSetupField.ServerUrl)]
    [InlineData("ftp://print-bridge.test", NewToken, "Validation.InvalidServerUrl", ShellConnectionSetupField.ServerUrl)]
    [InlineData("https://print-bridge.test/?tenant=1", NewToken, "Validation.InvalidServerUrl", ShellConnectionSetupField.ServerUrl)]
    [InlineData(NewUrl, "", "Validation.AgentTokenRequired", ShellConnectionSetupField.Token)]
    [InlineData(NewUrl, "   ", "Validation.AgentTokenRequired", ShellConnectionSetupField.Token)]
    [InlineData(NewUrl, "wasla-printbridge://setup?server=https://x.test&code=123456", "Settings.AgentTokenPasteGuard", ShellConnectionSetupField.Token)]
    public async Task InvalidInput_IsRejectedLocally_WithoutARequestOrAWrite(string url, string token, string messageKey, ShellConnectionSetupField field)
    {
        var api = new FakeApi(NewToken);
        using var harness = Create(api, token: string.Empty);
        var fileBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        var result = await harness.Setup.ConnectAsync(url, token, TestContext.Current.CancellationToken);

        Assert.Equal(new ShellConnectionSetupResult(ShellConnectionSetupOutcome.Invalid, _localizer[messageKey], field), result);
        Assert.Empty(api.Requests);
        Assert.Equal(fileBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
    }

    [Fact]
    public async Task ChangingOnlyTheAddress_KeepsTheSavedToken_WithoutEverReturningIt()
    {
        var api = new FakeApi(SavedToken);
        using var harness = Create(api, token: SavedToken, displayName: "Kasa 1");
        Assert.Equal(ShellConnectionSetupMode.Change, harness.Setup.Mode);
        Assert.True(harness.Setup.CanKeepSavedToken);
        Assert.Equal(SavedUrl, harness.Setup.SavedServerUrl);

        var result = await harness.Setup.ConnectAsync(NewUrl, string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(ShellConnectionSetupOutcome.ConnectedChoosePrinter, result.Outcome);
        Assert.Equal([("new.print-bridge.test", SavedToken, "api/print-bridge/health")], api.Requests.ToArray());
        var saved = harness.Store.Load();
        Assert.Equal(NewUrl, saved.OrderHub.ServerUrl);
        Assert.Equal(SavedToken, saved.OrderHub.AgentToken);
        Assert.Equal("Kasa 1", saved.PrintBridge.DisplayName);
        // The setup exposes the address for the native field, never the token.
        Assert.DoesNotContain(typeof(ShellConnectionSetup).GetProperties(), p => p.Name.Contains("Token", StringComparison.Ordinal) && p.PropertyType == typeof(string));
    }

    [Fact]
    public async Task CancelWhileChecking_WritesNothing()
    {
        var api = new FakeApi(NewToken) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var harness = Create(api, token: SavedToken);
        var fileBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);
        using var cancel = new CancellationTokenSource();

        var connecting = harness.Setup.ConnectAsync(NewUrl, NewToken, cancel.Token);
        await api.FirstRequest.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        cancel.Cancel();
        var result = await connecting.WaitAsync(Wait, TestContext.Current.CancellationToken);
        api.Gate.TrySetResult();

        Assert.Equal(ShellConnectionSetupOutcome.Cancelled, result.Outcome);
        Assert.Equal(_localizer["Shell.Setup.Cancelled"], result.Message);
        Assert.Equal(fileBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Equal(SavedToken, harness.Holder.OrderHub.AgentToken);
    }

    [Fact]
    public async Task AppClosingWhileChecking_WritesNothing()
    {
        var api = new FakeApi(NewToken) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var lifetime = new CancellationTokenSource();
        using var harness = Create(api, token: SavedToken, lifetime: lifetime);
        var fileBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        var connecting = harness.Setup.ConnectAsync(NewUrl, NewToken, TestContext.Current.CancellationToken);
        await api.FirstRequest.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        lifetime.Cancel();
        var result = await connecting.WaitAsync(Wait, TestContext.Current.CancellationToken);
        api.Gate.TrySetResult();

        Assert.Equal(ShellConnectionSetupOutcome.Cancelled, result.Outcome);
        Assert.Equal(fileBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
    }

    [Fact]
    public async Task ServerThatDoesNotAnswer_TimesOut_WithAMessageAboutTheAddress()
    {
        var api = new FakeApi(NewToken) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var harness = Create(api, token: string.Empty, timeout: TimeSpan.FromMilliseconds(300));
        var fileBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        var result = await harness.Setup.ConnectAsync(NewUrl, NewToken, TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);
        api.Gate.TrySetResult();

        Assert.Equal(ShellConnectionSetupOutcome.VerificationFailed, result.Outcome);
        Assert.Equal(ShellConnectionSetupField.ServerUrl, result.Field);
        Assert.Equal(_localizer["Shell.Setup.TimedOut"], result.Message);
        Assert.Equal(fileBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
    }

    [Fact]
    public async Task SettingsThatCannotBeWritten_KeepThePreviousConnection()
    {
        var api = new FakeApi(NewToken);
        using var harness = Create(api, token: SavedToken);
        var fileBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);
        File.SetAttributes(PrintBridgePaths.ProgramDataConfigPath, FileAttributes.ReadOnly);
        ShellConnectionSetupResult result;
        try
        {
            result = await harness.Setup.ConnectAsync(NewUrl, NewToken, TestContext.Current.CancellationToken);
        }
        finally
        {
            File.SetAttributes(PrintBridgePaths.ProgramDataConfigPath, FileAttributes.Normal);
        }

        Assert.Equal(ShellConnectionSetupOutcome.SaveFailed, result.Outcome);
        Assert.Equal(_localizer["Shell.Setup.SaveFailed"], result.Message);
        Assert.Equal(fileBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Equal(SavedToken, harness.Holder.OrderHub.AgentToken);
        Assert.Equal(SavedUrl, harness.Holder.OrderHub.ServerUrl);
        Assert.False(harness.Runtime.GetStatus().IsConnected);
    }

    [Fact]
    public async Task ReplacingTheTokenWhileListening_StopsAndResumes_AndOnlyTheNewTokenIsUsedAfterwards()
    {
        var printer = FirstInstalledPrinterOrSkip();
        var api = new FakeApi(SavedToken, NewToken);
        using var harness = Create(api, token: SavedToken, printer: printer, fastPolling: true);
        harness.Runtime.Start();
        await WaitUntilAsync(() => api.Requests.Count(r => r.Path == "api/print-bridge/jobs/pending") >= 1);

        var result = await harness.Setup.ConnectAsync(SavedUrl, NewToken, TestContext.Current.CancellationToken);
        var changedAt = api.Requests.Count;
        await WaitUntilAsync(() => api.Requests.Count >= changedAt + 2);
        await harness.Runtime.StopAsync();

        Assert.Equal(ShellConnectionSetupOutcome.Connected, result.Outcome);
        Assert.Equal(_localizer["Shell.Setup.Connected"], result.Message);
        Assert.Equal(NewToken, harness.Store.Load().OrderHub.AgentToken);
        Assert.All(api.Requests.Skip(changedAt), r => Assert.Equal(NewToken, r.Token));
    }

    [Fact]
    public async Task AfterAReset_TheDialogReconnects_AndThePrinterPreferencesSurvive()
    {
        var api = new FakeApi(NewToken);
        using var harness = Create(api, token: SavedToken, displayName: "Kasa 1");
        var before = harness.Store.Load().PrintBridge;

        await harness.Runtime.ResetConnectionForReconnectAsync();

        Assert.Equal(ShellConnectionSetupMode.Reconnect, harness.Setup.Mode);
        Assert.False(harness.Setup.CanKeepSavedToken);
        var afterReset = harness.Store.Load();
        Assert.Equal(string.Empty, afterReset.OrderHub.AgentToken);
        Assert.Equal(SavedUrl, afterReset.OrderHub.ServerUrl);
        Assert.Equal(before.PrinterName, afterReset.PrintBridge.PrinterName);
        Assert.Equal(before.DryRun, afterReset.PrintBridge.DryRun);
        Assert.Equal(before.IdlePollIntervalSeconds, afterReset.PrintBridge.IdlePollIntervalSeconds);
        Assert.Equal(before.InstallationId, afterReset.PrintBridge.InstallationId);

        var empty = await harness.Setup.ConnectAsync(SavedUrl, string.Empty, TestContext.Current.CancellationToken);
        var reconnected = await harness.Setup.ConnectAsync(SavedUrl, NewToken, TestContext.Current.CancellationToken);

        Assert.Equal(_localizer["Validation.AgentTokenRequired"], empty.Message);
        Assert.True(reconnected.IsConnected);
        Assert.Null(harness.Runtime.GetStatus().LastIssue);
        Assert.Equal(before.PrinterName, harness.Store.Load().PrintBridge.PrinterName);
    }

    public enum ActiveJobPhase
    {
        /// <summary>The claim (mark-printing) was sent and is not answered yet.</summary>
        Claiming,

        /// <summary>Claimed; the printer is printing the receipt.</summary>
        Printing,

        /// <summary>Printed; the printed report (mark-printed) is in flight.</summary>
        ReportingPrinted
    }

    [Theory]
    [InlineData(ActiveJobPhase.Claiming)]
    [InlineData(ActiveJobPhase.Printing)]
    [InlineData(ActiveJobPhase.ReportingPrinted)]
    public async Task ANewConnectionWhileAJobIsInProgress_IsRefusedWithoutSaving_AndTheJobFinishesOnceOnTheSavedConnection(ActiveJobPhase phase)
    {
        var printerName = FirstInstalledPrinterOrSkip();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi(SavedToken, NewToken)
        {
            ClaimGate = phase == ActiveJobPhase.Claiming ? hold : null,
            PrintedReportGate = phase == ActiveJobPhase.ReportingPrinted ? hold : null
        };
        // Test mode is off so the job goes to the printer, which is a recording fake.
        var receipts = new GatedPrinter { Gate = phase == ActiveJobPhase.Printing ? hold : null };
        var jobId = Guid.NewGuid();
        api.EnqueueJob(jobId);
        using var harness = Create(api, token: SavedToken, printer: printerName, fastPolling: true, dryRun: false, receiptPrinter: receipts);
        var fileBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);
        harness.Runtime.Start();

        var inPhase = phase switch
        {
            ActiveJobPhase.Claiming => api.ClaimReceived.Task,
            ActiveJobPhase.Printing => receipts.Started.Task,
            _ => api.PrintedReportReceived.Task
        };
        await inPhase.WaitAsync(Wait, TestContext.Current.CancellationToken);

        var refused = await harness.Setup.ConnectAsync(NewUrl, NewToken, TestContext.Current.CancellationToken);

        // Verified, then refused before anything was stopped, written or switched; nothing reports it as saved.
        Assert.Equal(ShellConnectionSetupOutcome.PrintingInProgress, refused.Outcome);
        Assert.False(refused.IsConnected);
        Assert.Equal(_localizer["Shell.Setup.PrintingInProgress"], refused.Message);
        Assert.Equal(fileBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Equal((SavedUrl, SavedToken, printerName), (harness.Holder.OrderHub.ServerUrl, harness.Holder.OrderHub.AgentToken, harness.Holder.Bridge.PrinterName));
        Assert.True(harness.Runtime.IsRunning);

        hold.SetResult();
        await api.PolledAfterJobReported.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Claimed, printed and reported printed exactly once, all on the saved connection; never reported failed.
        Assert.Equal(new[] { ("mark-printing", SavedToken), ("mark-printed", SavedToken) }, api.JobReports(jobId));
        Assert.Equal(1, receipts.Calls);
        Assert.Equal(LocalPrintJobStatus.Printed, Assert.Single(harness.Runtime.GetStatus().RecentJobs).Status);

        // Idle again: the same change now succeeds, keeps the printer, and only the new connection is used afterwards.
        var retried = await harness.Setup.ConnectAsync(NewUrl, NewToken, TestContext.Current.CancellationToken);
        var changedAt = api.Requests.Count;
        await WaitUntilAsync(() => api.Requests.Skip(changedAt).Any(r => r.Path == "api/print-bridge/jobs/pending"));
        await harness.Runtime.StopAsync();

        Assert.Equal(ShellConnectionSetupOutcome.Connected, retried.Outcome);
        var saved = harness.Store.Load();
        Assert.Equal((NewUrl, NewToken, printerName), (saved.OrderHub.ServerUrl, saved.OrderHub.AgentToken, saved.PrintBridge.PrinterName));
        Assert.All(api.Requests.Skip(changedAt), r => Assert.Equal(("new.print-bridge.test", NewToken), (r.Host, r.Token)));
        Assert.Equal(new[] { ("mark-printing", SavedToken), ("mark-printed", SavedToken) }, api.JobReports(jobId));
        Assert.Equal(1, receipts.Calls);
    }

    [Fact]
    public async Task ANewConnectionWhileAPollIsInFlight_StopsBeforeAnyClaim_AndTheJobRunsOnceOnTheNewConnection()
    {
        var printerName = FirstInstalledPrinterOrSkip();
        // The saved connection's pending poll is never answered, so the job is still pending when the connection changes.
        var api = new FakeApi(SavedToken, NewToken)
        {
            PendingPollGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            PendingPollGateToken = SavedToken
        };
        var receipts = new GatedPrinter();
        var jobId = Guid.NewGuid();
        api.EnqueueJob(jobId);
        using var harness = Create(api, token: SavedToken, printer: printerName, fastPolling: true, dryRun: false, receiptPrinter: receipts);
        harness.Runtime.Start();
        await api.HeldPendingPoll.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        var result = await harness.Setup.ConnectAsync(NewUrl, NewToken, TestContext.Current.CancellationToken);
        await api.PolledAfterJobReported.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await harness.Runtime.StopAsync();

        Assert.Equal(ShellConnectionSetupOutcome.Connected, result.Outcome);
        // Nothing was claimed with the saved token; the job ran once, entirely on the new connection.
        Assert.Equal(new[] { ("mark-printing", NewToken), ("mark-printed", NewToken) }, api.JobReports(jobId));
        Assert.Equal(1, receipts.Calls);
        Assert.Equal(LocalPrintJobStatus.Printed, Assert.Single(harness.Runtime.GetStatus().RecentJobs).Status);
    }

    [Fact]
    public async Task AJobOfferedAfterTheChangeWasAccepted_IsLeftPending_AndRunsOnceOnTheNewConnection()
    {
        var printerName = FirstInstalledPrinterOrSkip();
        var pendingAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi(SavedToken, NewToken) { PendingPollGate = pendingAnswer, PendingPollGateToken = SavedToken };
        var receipts = new GatedPrinter();
        var jobId = Guid.NewGuid();
        api.EnqueueJob(jobId);
        using var harness = Create(api, token: SavedToken, printer: printerName, fastPolling: true, dryRun: false, receiptPrinter: receipts);
        harness.Runtime.Start();
        await api.HeldPendingPoll.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        // No job is in progress, so the change is accepted. Before listening stops, the saved connection's poll is
        // answered with the job: the loop must leave it pending instead of claiming it on the way out.
        harness.Runtime.WhileJobsAreHeldForTests = async () =>
        {
            var tookTheJob = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStatusChanged(object? sender, EventArgs e) =>
                tookTheJob.TrySetResult(harness.Runtime.GetStatus().RecentJobs.Count > 0);

            harness.Runtime.StatusChanged += OnStatusChanged;
            pendingAnswer.SetResult();
            var took = await tookTheJob.Task.WaitAsync(Wait);
            harness.Runtime.StatusChanged -= OnStatusChanged;
            if (took)
                await api.ClaimReceived.Task.WaitAsync(Wait);
        };

        var result = await harness.Setup.ConnectAsync(NewUrl, NewToken, TestContext.Current.CancellationToken);
        await api.PolledAfterJobReported.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await harness.Runtime.StopAsync();

        Assert.Equal(ShellConnectionSetupOutcome.Connected, result.Outcome);
        Assert.Equal(new[] { ("mark-printing", NewToken), ("mark-printed", NewToken) }, api.JobReports(jobId));
        Assert.Equal(1, receipts.Calls);
        Assert.Equal(LocalPrintJobStatus.Printed, Assert.Single(harness.Runtime.GetStatus().RecentJobs).Status);
    }

    [Fact]
    public async Task NoTokenEverReachesALogLine_OrAResultMessage()
    {
        var logs = new RecordingLogs();
        var rejecting = new FakeApi(SavedToken);
        using (var harness = Create(rejecting, token: SavedToken, logs: logs))
        {
            AssertNoSecrets((await harness.Setup.ConnectAsync(SavedUrl, NewToken, TestContext.Current.CancellationToken)).Message);
            AssertNoSecrets((await harness.Setup.ConnectAsync(NewUrl, "wasla-printbridge://setup?server=x&code=1", TestContext.Current.CancellationToken)).Message);
        }

        var accepting = new FakeApi(NewToken);
        using (var harness = Create(accepting, token: string.Empty, logs: logs))
            AssertNoSecrets((await harness.Setup.ConnectAsync(NewUrl, NewToken, TestContext.Current.CancellationToken)).Message);

        Assert.NotEmpty(logs.Lines);
        foreach (var line in logs.Lines)
        {
            Assert.DoesNotContain(NewToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SavedToken, line, StringComparison.Ordinal);
        }
    }

    private Harness Create(
        FakeApi api,
        string token,
        string printer = MissingPrinter,
        string displayName = "",
        bool fastPolling = false,
        TimeSpan? timeout = null,
        CancellationTokenSource? lifetime = null,
        RecordingLogs? logs = null,
        bool dryRun = true,
        IReceiptPrinter? receiptPrinter = null)
    {
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        document.OrderHub.ServerUrl = SavedUrl;
        document.OrderHub.AgentToken = token;
        document.PrintBridge.PrinterName = printer;
        document.PrintBridge.DryRun = dryRun;
        document.PrintBridge.DisplayName = displayName;
        document.PrintBridge.ServerDeviceNameResolved = displayName.Length > 0;
        if (fastPolling)
        {
            document.PrintBridge.IdlePollIntervalSeconds = 1;
            document.PrintBridge.BusyPollIntervalSeconds = 1;
            document.PrintBridge.ErrorPollIntervalSeconds = 1;
        }

        store.Save(document);
        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.OrderHub, document.PrintBridge, document.Ui);

        var loggers = logs ?? (ILoggerFactory)NullLoggerFactory.Instance;
        var http = new HttpClient(api);
        var client = new WaslaPrintBridgeClient(
            http,
            holder,
            new PrintBridgeSetupHttpClientFactory(),
            loggers.CreateLogger<WaslaPrintBridgeClient>(),
            "1.0.0.0");
        var runtime = new PrintBridgeRuntime(
            client,
            new ReceiptFormatter(),
            receiptPrinter ?? new NoPrinter(),
            holder,
            store,
            new PrintBridgeDeviceMetadataSync(store, holder, loggers.CreateLogger<PrintBridgeDeviceMetadataSync>()),
            new LocalPrintJobHistoryStore(loggers.CreateLogger<LocalPrintJobHistoryStore>()),
            new AppVersionInfo(typeof(ShellConnectionSetupTests).Assembly),
            loggers.CreateLogger<PrintBridgeRuntime>());
        var setup = new ShellConnectionSetup(runtime, holder, _localizer, loggers.CreateLogger("Shell"), lifetime?.Token ?? CancellationToken.None, timeout);
        return new Harness(runtime, setup, store, holder, http, document.PrintBridge.InstallationId);
    }

    private static void AssertNoSecrets(string message)
    {
        foreach (var secret in new[] { NewToken, SavedToken, "new.print-bridge.test", "saved.print-bridge.test", "api/print-bridge", "Exception", "401" })
            Assert.DoesNotContain(secret, message, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstInstalledPrinterOrSkip()
    {
        // Listening needs a printer Windows reports (read-only check); output goes to NoPrinter and test mode is on.
        foreach (string installed in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            return installed;

        Assert.Skip("No Windows printer is installed; the runtime refuses to start without one.");
        return string.Empty;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached in time.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private sealed record Harness(
        PrintBridgeRuntime Runtime,
        ShellConnectionSetup Setup,
        PrintBridgeSettingsStore Store,
        PrintBridgeSettingsHolder Holder,
        HttpClient Http,
        string InstallationId) : IDisposable
    {
        public void Dispose()
        {
            Runtime.Dispose();
            Http.Dispose();
        }
    }

    private sealed class NoPrinter : IReceiptPrinter
    {
        public Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct) =>
            throw new InvalidOperationException("These tests never print.");
    }

    /// <summary>Counts each print the engine sends instead of printing, and can hold one until opened.</summary>
    private sealed class GatedPrinter : IReceiptPrinter
    {
        private int _calls;

        public TaskCompletionSource? Gate { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public async Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            if (Gate is { } gate)
                await gate.Task.WaitAsync(ct);
        }
    }

    /// <summary>
    /// A Wasla API that accepts only the given device tokens, records every request and can be offline or slow. Like the
    /// server, it offers a print job in every pending poll until the job is claimed. It can hold a claim, a printed
    /// report or a pending poll after recording it.
    /// </summary>
    private sealed class FakeApi(params string[] acceptedTokens) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(Guid Id, object Job)> _jobs = new();
        private readonly ConcurrentDictionary<Guid, bool> _claimed = new();
        private int _jobReported;

        public ConcurrentQueue<(string Host, string Token, string Path)> Requests { get; } = new();

        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Gate { get; init; }

        public bool Offline { get; init; }

        public string DeviceName { get; init; } = string.Empty;

        /// <summary>Holds every claim (mark-printing) until opened; the caller's cancellation still ends the wait.</summary>
        public TaskCompletionSource? ClaimGate { get; init; }

        /// <summary>Holds every printed report (mark-printed) until opened.</summary>
        public TaskCompletionSource? PrintedReportGate { get; init; }

        /// <summary>Holds pending polls sent with <see cref="PendingPollGateToken"/> until opened; cancellation ends the wait.</summary>
        public TaskCompletionSource? PendingPollGate { get; init; }

        public string? PendingPollGateToken { get; init; }

        public TaskCompletionSource ClaimReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PrintedReportReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HeldPendingPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The first pending poll after a job was reported printed or failed: the engine has finished that job.</summary>
        public TaskCompletionSource PolledAfterJobReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueJob(Guid jobId) =>
            _jobs.Enqueue((jobId, new
            {
                id = jobId,
                orderId = Guid.NewGuid(),
                type = "Receipt",
                copyCount = 1,
                payloadJson = """{"platform":"Getir","externalOrderCode":"QA-2001"}""",
                createdAtUtc = DateTime.UtcNow
            }));

        /// <summary>The job's reports in the order received (mark-printing, mark-printed, mark-failed) with the token used.</summary>
        public (string Report, string Token)[] JobReports(Guid jobId) =>
            Requests
                .Where(r => r.Path.Contains(jobId.ToString("D"), StringComparison.Ordinal))
                .Select(r => (r.Path[(r.Path.LastIndexOf('/') + 1)..], r.Token))
                .ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.TryGetValues("X-PrintBridge-Token", out var values) ? values.Single() : string.Empty;
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Requests.Enqueue((request.RequestUri.Host, token, path));
            FirstRequest.TrySetResult();

            if (Gate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);
            if (Offline)
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            if (!acceptedTokens.Contains(token, StringComparer.Ordinal))
                return Json(HttpStatusCode.Unauthorized, new { error = "device_auth_invalid" });

            if (path == "api/print-bridge/health")
                return Json(HttpStatusCode.OK, new { success = true, customerName = "QA", deviceName = DeviceName, serverTimeUtc = DateTime.UtcNow });

            if (path == "api/print-bridge/jobs/pending")
            {
                if (PendingPollGate is { } pollGate && string.Equals(token, PendingPollGateToken, StringComparison.Ordinal))
                {
                    HeldPendingPoll.TrySetResult();
                    await pollGate.Task.WaitAsync(cancellationToken);
                }

                if (Volatile.Read(ref _jobReported) == 1)
                    PolledAfterJobReported.TrySetResult();
                var jobs = _jobs.Where(j => !_claimed.ContainsKey(j.Id)).Select(j => j.Job).ToArray();
                return Json(HttpStatusCode.OK, new { jobs });
            }

            if (path.EndsWith("/mark-printing", StringComparison.Ordinal))
            {
                // Claimed when the request arrives, as on the server, even if the answer never reaches the client.
                _claimed.TryAdd(Guid.Parse(path.Split('/')[^2]), true);
                ClaimReceived.TrySetResult();
                if (ClaimGate is { } claimGate)
                    await claimGate.Task.WaitAsync(cancellationToken);
                return Json(HttpStatusCode.OK, new { success = true, skipped = false, result = "claimed" });
            }

            if (path.EndsWith("/mark-printed", StringComparison.Ordinal) || path.EndsWith("/mark-failed", StringComparison.Ordinal))
            {
                if (path.EndsWith("/mark-printed", StringComparison.Ordinal))
                {
                    PrintedReportReceived.TrySetResult();
                    if (PrintedReportGate is { } reportGate)
                        await reportGate.Task.WaitAsync(cancellationToken);
                }

                Volatile.Write(ref _jobReported, 1);
                return Json(HttpStatusCode.OK, new { success = true, skipped = false, result = "ok" });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    /// <summary>Every formatted log line and exception from the engine, client and setup.</summary>
    private sealed class RecordingLogs : ILoggerFactory, ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(RecordingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}
