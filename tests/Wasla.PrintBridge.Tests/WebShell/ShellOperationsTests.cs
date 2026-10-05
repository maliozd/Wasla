using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

[Collection(ProcessCultureCollection.Name)]
public sealed class ShellOperationsTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly CultureScope _cultureScope = new();
    private readonly ShellTestRig _rig;
    private readonly PrintBridgeLocalizer _localizer;

    public ShellOperationsTests()
    {
        _rig = new ShellTestRig(Culture());
        _localizer = new PrintBridgeLocalizer(_rig.Culture);
    }

    public void Dispose()
    {
        _rig.Dispose();
        _cultureScope.Dispose();
    }

    [Theory]
    [InlineData("engine.start", "Shell.Op.Started")]
    [InlineData("engine.stop", "Shell.Op.Stopped")]
    [InlineData("connection.test", "Message.ConnectionSuccess")]
    [InlineData("connection.reset", "Shell.Op.ResetDone")]
    [InlineData("printers.refresh", "Shell.Op.PrintersRefreshed")]
    [InlineData("printer.testPrint", "Message.TestPrintSent")]
    [InlineData("logs.openFolder", "Shell.Op.LogsOpened")]
    public async Task EachOperation_RunsOnceAndAnswersWithItsLocalizedMessage(string type, string messageKey)
    {
        if (type == "engine.start")
            _rig.Engine.Set(Status(BridgeServerConnectionStatus.Stopped, isRunning: false));
        var requestId = NewRequestId();

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked(type, requestId: requestId)));

        Assert.Equal(new ShellOperationResult(type, requestId, ShellOperationOutcome.Succeeded, _localizer[messageKey]), result);
        Assert.Equal(1, CallsFor(type));
        Assert.Equal(ShellBusyState.Idle, _rig.Operations.Busy);
    }

    [Fact]
    public async Task RepeatedRequestId_IsExecutedOnlyOnce_EvenForAnotherCommand()
    {
        var requestId = NewRequestId();

        var first = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint", requestId: requestId)));
        var again = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint", requestId: requestId)));
        var reused = await _rig.Operations.ExecuteAsync(Parse(Tracked("connection.reset", requestId: requestId)));

        Assert.Equal(ShellOperationOutcome.Succeeded, first.Outcome);
        Assert.Equal(ShellOperationOutcome.Duplicate, again.Outcome);
        Assert.Equal(ShellOperationOutcome.Duplicate, reused.Outcome);
        Assert.Equal(1, _rig.Engine.TestPrintCalls);
        Assert.Equal(0, _rig.Engine.ResetCalls);
        Assert.Equal(0, _rig.Native.ResetConfirmations);
    }

    [Fact]
    public async Task RequestIdMemory_IsBounded()
    {
        var first = NewRequestId();
        await _rig.Operations.ExecuteAsync(Parse(Tracked("logs.openFolder", requestId: first)));
        for (var i = 0; i < 300; i++)
            await _rig.Operations.ExecuteAsync(Parse(Tracked("logs.openFolder")));

        var reused = await _rig.Operations.ExecuteAsync(Parse(Tracked("logs.openFolder", requestId: first)));

        Assert.Equal(ShellOperationOutcome.Succeeded, reused.Outcome);
    }

    [Fact]
    public async Task SecondTestPrintWhileOneRuns_IsAnsweredBusy_AndThePrinterIsUsedOnce()
    {
        _rig.Engine.TestPrintGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint")));
        Assert.True(_rig.Operations.Busy.TestPrint);

        var second = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint")));
        Assert.Equal(ShellOperationOutcome.Busy, second.Outcome);
        Assert.Equal(_localizer["Shell.Op.Busy"], second.Message);
        Assert.Equal(1, _rig.Engine.TestPrintCalls);

        _rig.Engine.TestPrintGate.SetResult();
        Assert.Equal(ShellOperationOutcome.Succeeded, (await running.WaitAsync(Wait, TestContext.Current.CancellationToken)).Outcome);
        Assert.False(_rig.Operations.Busy.TestPrint);

        var third = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint")));
        Assert.Equal(ShellOperationOutcome.Succeeded, third.Outcome);
        Assert.Equal(2, _rig.Engine.TestPrintCalls);
    }

    [Fact]
    public async Task OtherOperationGroups_StayAvailableWhileATestPrintRuns()
    {
        _rig.Engine.TestPrintGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint")));

        var check = await _rig.Operations.ExecuteAsync(Parse(Tracked("connection.test")));
        var refresh = await _rig.Operations.ExecuteAsync(Parse(Tracked("printers.refresh")));

        Assert.Equal(ShellOperationOutcome.Succeeded, check.Outcome);
        Assert.Equal(ShellOperationOutcome.Succeeded, refresh.Outcome);
        _rig.Engine.TestPrintGate.SetResult();
        await running.WaitAsync(Wait, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAndStopShareOneGroup_SoTheEngineIsNeverToggledConcurrently()
    {
        _rig.Engine.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stopping = _rig.Operations.ExecuteAsync(Parse(Tracked("engine.stop")));
        var start = await _rig.Operations.ExecuteAsync(Parse(Tracked("engine.start")));

        Assert.True(_rig.Operations.Busy.Engine);
        Assert.Equal(ShellOperationOutcome.Busy, start.Outcome);
        Assert.Equal(0, _rig.Engine.StartCalls);

        _rig.Engine.StopGate.SetResult();
        Assert.Equal(ShellOperationOutcome.Succeeded, (await stopping.WaitAsync(Wait, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ShellOperationOutcome.Succeeded, (await _rig.Operations.ExecuteAsync(Parse(Tracked("engine.start")))).Outcome);
        Assert.Equal(1, _rig.Engine.StopCalls);
        Assert.Equal(1, _rig.Engine.StartCalls);
    }

    [Fact]
    public async Task BusyChanges_AreAnnouncedWhenAnOperationStartsAndEnds()
    {
        var observed = new List<bool>();
        _rig.Operations.StateChanged += (_, _) => observed.Add(_rig.Operations.Busy.TestPrint);

        await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint")));

        Assert.Equal([true, false], observed);
    }

    [Fact]
    public async Task EngineStart_WhenAlreadyRunning_DoesNotStartASecondLoop()
    {
        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("engine.start")));

        Assert.Equal(ShellOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, _rig.Engine.StartCalls);
    }

    [Fact]
    public async Task EngineStop_WhenStopped_DoesNothing()
    {
        _rig.Engine.Set(Status(BridgeServerConnectionStatus.Stopped, isRunning: false));

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("engine.stop")));

        Assert.Equal(ShellOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, _rig.Engine.StopCalls);
    }

    [Fact]
    public async Task EngineStart_WithoutAToken_IsRejectedAndPointsToReconnect()
    {
        using var rig = new ShellTestRig(Culture(), ShellTestRig.NewSettings(string.Empty));
        rig.Engine.Set(Status(BridgeServerConnectionStatus.NotConfigured, isRunning: false));

        var result = await rig.Operations.ExecuteAsync(Parse(Tracked("engine.start")));

        Assert.Equal(ShellOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(_localizer["Message.ReconnectFromWebToContinue"], result.Message);
        Assert.Equal(0, rig.Engine.StartCalls);
    }

    [Fact]
    public async Task EngineStart_AfterTheServerRequiredAReconnect_IsRejected()
    {
        _rig.Engine.Set(Status(
            BridgeServerConnectionStatus.Error,
            new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ReconnectRequired),
            isRunning: false));

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("engine.start")));

        Assert.Equal(ShellOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(0, _rig.Engine.StartCalls);
    }

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("null-reference")]
    public async Task UnexpectedFailures_AreReportedGenerically_WithoutExceptionText(string kind)
    {
        _rig.Engine.TestPrintFailure = kind switch
        {
            "invalid-operation" => new InvalidOperationException("secret-host.internal:4431 refused, X-PrintBridge-Token=" + SentinelToken),
            "http" => new HttpRequestException("Response from " + SentinelServerUrl + "/api/print-bridge/jobs was 500"),
            "io" => new IOException(@"C:\ProgramData\Wasla\PrintBridge\appsettings.json is locked"),
            _ => new NullReferenceException()
        };

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint")));

        Assert.Equal(ShellOperationOutcome.Failed, result.Outcome);
        Assert.Equal(_localizer["Shell.Op.Failed"], result.Message);
        AssertSafe(result.Message);
        Assert.False(_rig.Operations.Busy.TestPrint);
    }

    [Fact]
    public async Task KnownFailures_UseTheirLocalizedResourceText()
    {
        _rig.Engine.TestPrintFailure = new LocalizedApplicationException("Error.DryRunEnabled");
        var dryRun = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint")));

        _rig.Engine.TestConnectionFailure = PrintBridgeConnectionException.ServerUnavailable(
            "api/print-bridge/health",
            SentinelServerUrl,
            new HttpRequestException("secret-host.internal refused"));
        var offline = await _rig.Operations.ExecuteAsync(Parse(Tracked("connection.test")));

        Assert.Equal(ShellOperationOutcome.Failed, dryRun.Outcome);
        Assert.Equal(_localizer["Error.DryRunEnabled"], dryRun.Message);
        Assert.Equal(ShellOperationOutcome.Failed, offline.Outcome);
        Assert.Equal(
            _localizer.GetRuntimeIssueDetail(new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable, "Connection.ServerUnreachable")),
            offline.Message);
        AssertSafe(offline.Message);
    }

    [Fact]
    public async Task SlowOperations_TimeOut_AndFreeTheirGroup()
    {
        var operations = new ShellOperations(
            _rig.Engine,
            _rig.Catalog,
            _rig.PrinterSettings,
            _rig.OperationalSettings,
            _rig.Native,
            _rig.History,
            _rig.Settings,
            _localizer,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(150));
        _rig.Engine.TestPrintGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await operations.ExecuteAsync(Parse(Tracked("printer.testPrint"))).WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(ShellOperationOutcome.Failed, result.Outcome);
        Assert.Equal(_localizer["Shell.Op.TimedOut"], result.Message);
        Assert.False(operations.Busy.TestPrint);
    }

    [Fact]
    public async Task ConnectionReset_WithoutNativeConfirmation_KeepsTheToken()
    {
        _rig.Native.ConfirmReset = false;

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("connection.reset")));

        Assert.Equal(ShellOperationOutcome.Cancelled, result.Outcome);
        Assert.Equal(_localizer["Shell.Op.ResetCancelled"], result.Message);
        Assert.Equal(1, _rig.Native.ResetConfirmations);
        Assert.Equal(0, _rig.Engine.ResetCalls);
    }

    [Fact]
    public async Task PrinterSave_StoresAnInstalledPrinter()
    {
        await _rig.Catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.save", "\"name\":\"Microsoft Print to PDF\"")));

        Assert.Equal(ShellOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(_localizer["Shell.Op.PrinterSaved"], result.Message);
        Assert.Equal(["Microsoft Print to PDF"], _rig.PrinterSettings.Saved);
    }

    [Theory]
    [InlineData("Not-A-Printer")]
    [InlineData(@"\\\\print-server\\Kitchen")]
    [InlineData("POS-58 (copy)")]
    public async Task PrinterSave_RejectsAnythingWindowsDidNotReport(string name)
    {
        await _rig.Catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.save", $"\"name\":\"{name}\"")));

        Assert.Equal(ShellOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(_localizer["Shell.Printer.NotInstalled"], result.Message);
        Assert.Empty(_rig.PrinterSettings.Saved);
    }

    [Fact]
    public async Task PrinterSave_DiscoversPrintersFirst_WhenTheListWasNeverLoaded()
    {
        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.save", "\"name\":\"POS-58\"")));

        Assert.Equal(ShellOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, _rig.Catalog.RefreshCalls);
        Assert.Equal(["POS-58"], _rig.PrinterSettings.Saved);
    }

    [Fact]
    public async Task PrinterSave_ReportsTheSettingsValidatorMessage()
    {
        await _rig.Catalog.RefreshAsync(TestContext.Current.CancellationToken);
        _rig.PrinterSettings.FailWithKey = "Validation.InvalidPollingIntervals";

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.save", "\"name\":\"POS-58\"")));

        Assert.Equal(ShellOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(_localizer["Validation.InvalidPollingIntervals"], result.Message);
    }

    [Fact]
    public async Task Reprint_ResolvesOnlyHandlesIssuedByTheHistory()
    {
        var jobId = Guid.NewGuid();
        _rig.Engine.History = [new LocalPrintJobRecord { JobId = jobId, OrderDisplay = "GTR-1001", Status = LocalPrintJobStatus.Printed, CreatedAtUtc = DateTime.UtcNow }];
        var itemRef = Assert.Single(_rig.History.Query(PrintHistoryDateFilter.Today, 0, null).Items).Ref;

        var unknown = await _rig.Operations.ExecuteAsync(Parse(Tracked("history.reprint", "\"itemRef\":\"h0000000000000000\"")));
        var known = await _rig.Operations.ExecuteAsync(Parse(Tracked("history.reprint", $"\"itemRef\":\"{itemRef}\"")));

        Assert.Equal(ShellOperationOutcome.Rejected, unknown.Outcome);
        Assert.Equal(_localizer.GetReprintMessage("Reprint.JobNotFound"), unknown.Message);
        Assert.Equal(ShellOperationOutcome.Succeeded, known.Outcome);
        Assert.Equal(_localizer.GetReprintMessage("Reprint.Created"), known.Message);
        Assert.Equal([jobId], _rig.Engine.ReprintedJobs);
    }

    [Theory]
    [InlineData("PrintBridge.ReprintCreated")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Reprint_SuccessAlwaysReadsAsCreated(string? serverKey)
    {
        var jobId = Guid.NewGuid();
        _rig.Engine.History = [new LocalPrintJobRecord { JobId = jobId, Status = LocalPrintJobStatus.Printed, CreatedAtUtc = DateTime.UtcNow }];
        _rig.Engine.ReprintMessageKey = serverKey!;
        var itemRef = Assert.Single(_rig.History.Query(PrintHistoryDateFilter.Today, 0, null).Items).Ref;

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("history.reprint", $"\"itemRef\":\"{itemRef}\"")));

        Assert.Equal(ShellOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(_localizer.GetReprintMessage("Reprint.Created"), result.Message);
    }

    [Fact]
    public async Task Reprint_ServerRefusal_UsesTheReprintMessage()
    {
        var jobId = Guid.NewGuid();
        _rig.Engine.History = [new LocalPrintJobRecord { JobId = jobId, Status = LocalPrintJobStatus.Printed, CreatedAtUtc = DateTime.UtcNow }];
        var itemRef = Assert.Single(_rig.History.Query(PrintHistoryDateFilter.Today, 0, null).Items).Ref;
        _rig.Engine.ReprintFailure = new LocalizedApplicationException("PrintBridge.ReprintAlreadyPending");

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("history.reprint", $"\"itemRef\":\"{itemRef}\"")));

        Assert.Equal(ShellOperationOutcome.Failed, result.Outcome);
        Assert.Equal(_localizer.GetReprintMessage("Reprint.AlreadyPending"), result.Message);
    }

    [Fact]
    public async Task LogsOpenFolder_ReportsAMissingFolder()
    {
        _rig.Native.LogFolderExists = false;

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("logs.openFolder")));

        Assert.Equal(ShellOperationOutcome.Failed, result.Outcome);
        Assert.Equal(_localizer["Message.LogsFolderMissing"], result.Message);
        Assert.Equal(1, _rig.Native.LogFolderRequests);
    }

    [Fact]
    public async Task SettingsSave_StoresTheTypedValues_AndSaysNoRestartIsNeeded()
    {
        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("settings.save", SettingsFields(false, 10, 2, 30))));

        Assert.Equal(ShellOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(_localizer["Shell.Op.SettingsSaved"], result.Message);
        Assert.Equal([new ShellOperationalSettingsChange(false, 10, 2, 30)], _rig.OperationalSettings.Saved);
        Assert.Equal(0, _rig.Native.TestModeConfirmations);
        Assert.False(_rig.Operations.Busy.SettingsSave);
    }

    [Theory]
    [InlineData(0, 1, 15)]
    [InlineData(301, 1, 15)]
    [InlineData(5, 61, 15)]
    [InlineData(5, 1, 0)]
    public async Task SettingsSave_OutOfRange_IsRejectedWithTheValidatorMessage_AndNothingIsSaved(int idle, int busy, int error)
    {
        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("settings.save", SettingsFields(false, idle, busy, error))));

        Assert.Equal(ShellOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(_localizer["Validation.InvalidPollingIntervals"], result.Message);
        Assert.Empty(_rig.OperationalSettings.Saved);
    }

    [Fact]
    public async Task TurningTestModeOn_NeedsTheNativeConfirmation()
    {
        _rig.Native.ConfirmTestMode = false;

        var declined = await _rig.Operations.ExecuteAsync(Parse(Tracked("settings.save", SettingsFields(true, 5, 1, 15))));

        Assert.Equal(ShellOperationOutcome.Cancelled, declined.Outcome);
        Assert.Equal(_localizer["Shell.Op.TestModeCancelled"], declined.Message);
        Assert.Empty(_rig.OperationalSettings.Saved);

        _rig.Native.ConfirmTestMode = true;
        var confirmed = await _rig.Operations.ExecuteAsync(Parse(Tracked("settings.save", SettingsFields(true, 5, 1, 15))));

        Assert.Equal(ShellOperationOutcome.Succeeded, confirmed.Outcome);
        Assert.Equal(2, _rig.Native.TestModeConfirmations);
        Assert.Equal([new ShellOperationalSettingsChange(true, 5, 1, 15)], _rig.OperationalSettings.Saved);
    }

    [Fact]
    public async Task TurningTestModeOff_OrKeepingItOn_NeedsNoConfirmation()
    {
        var off = await _rig.Operations.ExecuteAsync(Parse(Tracked("settings.save", SettingsFields(false, 5, 1, 15))));
        _rig.Settings.Replace(_rig.Settings.OrderHub, new Wasla.PrintBridge.Options.PrintBridgeOptions { PrinterName = "POS-58", DryRun = true });
        var stillOn = await _rig.Operations.ExecuteAsync(Parse(Tracked("settings.save", SettingsFields(true, 7, 1, 15))));

        Assert.Equal(ShellOperationOutcome.Succeeded, off.Outcome);
        Assert.Equal(ShellOperationOutcome.Succeeded, stillOn.Outcome);
        Assert.Equal(0, _rig.Native.TestModeConfirmations);
    }

    [Theory]
    [InlineData(ShellConnectionSetupOutcome.Connected, ShellOperationOutcome.Succeeded, null)]
    [InlineData(ShellConnectionSetupOutcome.ConnectedChoosePrinter, ShellOperationOutcome.Succeeded, "printer")]
    [InlineData(ShellConnectionSetupOutcome.Cancelled, ShellOperationOutcome.Cancelled, null)]
    [InlineData(ShellConnectionSetupOutcome.SaveFailed, ShellOperationOutcome.Failed, null)]
    public async Task ConnectionSetup_ReportsOnlyTheDialogsLocalizedMessage(
        ShellConnectionSetupOutcome dialog,
        ShellOperationOutcome expected,
        string? navigateTo)
    {
        _rig.Native.SetupResult = new ShellConnectionSetupResult(dialog, "dialog message");
        var requestId = NewRequestId();

        var result = await _rig.Operations.ExecuteAsync(Parse(Tracked("connection.openSetup", requestId: requestId)));

        Assert.Equal(new ShellOperationResult("connection.openSetup", requestId, expected, "dialog message", navigateTo), result);
        Assert.Equal(1, _rig.Native.ConnectionSetupRequests);
        Assert.Equal(0, _rig.Native.ClassicWindowRequests);
    }

    [Fact]
    public async Task ConnectionSetup_AndEngineCommands_NeverRunAtTheSameTime()
    {
        _rig.Native.SetupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = _rig.Operations.ExecuteAsync(Parse(Tracked("connection.openSetup")));

        var stop = await _rig.Operations.ExecuteAsync(Parse(Tracked("engine.stop")));
        var test = await _rig.Operations.ExecuteAsync(Parse(Tracked("connection.test")));
        var reset = await _rig.Operations.ExecuteAsync(Parse(Tracked("connection.reset")));
        var print = await _rig.Operations.ExecuteAsync(Parse(Tracked("printer.testPrint")));

        Assert.Equal(ShellOperationOutcome.Busy, stop.Outcome);
        Assert.Equal(ShellOperationOutcome.Busy, test.Outcome);
        Assert.Equal(ShellOperationOutcome.Busy, reset.Outcome);
        Assert.Equal(0, _rig.Engine.StopCalls + _rig.Engine.TestConnectionCalls + _rig.Engine.ResetCalls);
        // Printing does not touch the connection and stays available.
        Assert.Equal(ShellOperationOutcome.Succeeded, print.Outcome);

        _rig.Native.SetupGate.SetResult();
        await setup.WaitAsync(Wait, TestContext.Current.CancellationToken);

        _rig.Engine.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = _rig.Operations.ExecuteAsync(Parse(Tracked("engine.stop")));
        var blocked = await _rig.Operations.ExecuteAsync(Parse(Tracked("connection.openSetup")));
        Assert.Equal(ShellOperationOutcome.Busy, blocked.Outcome);
        Assert.Equal(1, _rig.Native.ConnectionSetupRequests);
        _rig.Engine.StopGate.SetResult();
        await stopping.WaitAsync(Wait, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ShellCommandType.UiReady)]
    [InlineData(ShellCommandType.LanguageChange)]
    [InlineData(ShellCommandType.ClassicWindowOpen)]
    [InlineData(ShellCommandType.HistoryQuery)]
    public async Task NonOperations_AreNeverExecuted(ShellCommandType type)
    {
        var result = await _rig.Operations.ExecuteAsync(new ShellCommand(type, NewRequestId()));

        Assert.Equal(ShellOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(0, _rig.Engine.StartCalls + _rig.Engine.TestPrintCalls + _rig.Engine.ResetCalls + _rig.Native.ConnectionSetupRequests);
    }

    private int CallsFor(string type) => type switch
    {
        "engine.start" => _rig.Engine.StartCalls,
        "engine.stop" => _rig.Engine.StopCalls,
        "connection.test" => _rig.Engine.TestConnectionCalls,
        "connection.reset" => _rig.Engine.ResetCalls,
        "printers.refresh" => _rig.Catalog.RefreshCalls,
        "printer.testPrint" => _rig.Engine.TestPrintCalls,
        "logs.openFolder" => _rig.Native.LogFolderRequests,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static void AssertSafe(string message)
    {
        foreach (var leak in new[] { "secret-host", SentinelToken, SentinelServerUrl, "api/print-bridge", "ProgramData", "Exception", "Token=" })
            Assert.DoesNotContain(leak, message, StringComparison.OrdinalIgnoreCase);
    }

    private static string SettingsFields(bool testMode, int idle, int busy, int error) =>
        $"\"testMode\":{(testMode ? "true" : "false")},\"idlePollSeconds\":{idle},\"busyPollSeconds\":{busy},\"errorPollSeconds\":{error}";

    private static ShellCommand Parse(string raw)
    {
        Assert.True(ShellMessageParser.TryParse(raw, out var command, out var rejection), rejection.ToString());
        return command!;
    }
}
