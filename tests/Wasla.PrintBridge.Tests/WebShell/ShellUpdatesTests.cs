using System.ComponentModel;
using System.Text.Json;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Tests.Service;
using Wasla.PrintBridge.Updates;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// WAS-63, the app's part of an update, against a fake service state, a fake setup launcher and a fake verifier: the check,
/// the native confirmation, preparing exactly the version the last check found, opening the verified setup once, cancelling
/// when the setup ends without installing or the staged file is refused, and what the page is told at every stage. Nothing
/// is downloaded, verified for real or started here (the real verifier is the service's, tested with it).
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class ShellUpdatesTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly CultureScope _cultureScope = new();
    private readonly ShellTestRig _rig;
    private readonly PrintBridgeLocalizer _localizer;

    public ShellUpdatesTests()
    {
        _rig = new ShellTestRig(Culture(SupportedCultures.English));
        _localizer = new PrintBridgeLocalizer(_rig.Culture);
    }

    public void Dispose()
    {
        _rig.Dispose();
        _cultureScope.Dispose();
    }

    private FakeUpdates Updates => _rig.Updates;

    private Guid AttemptId => Updates.NextAttempt;

    [Fact]
    public async Task Check_RemembersTheNewerVersion_AndOffersTheUpdate()
    {
        var before = View();
        Assert.Equal((ShellUpdateStages.Idle, string.Empty, FakeUpdates.Installed), (before.Stage, before.Label, before.InstalledVersion));
        Assert.Equal(new ShellUpdateActionsView(true, false, false), before.Actions);

        var result = await RunAsync(ShellCommandType.UpdateCheck);

        var available = _localizer.GetString("Shell.Update.Status.Available", FakeUpdates.Newer);
        Assert.Equal((ShellOperationOutcome.Succeeded, available), (result.Outcome, result.Message));
        var after = View();
        Assert.Equal((ShellUpdateStages.Available, available, FakeUpdates.Newer), (after.Stage, after.Label, after.AvailableVersion));
        Assert.Equal(new ShellUpdateActionsView(true, true, false), after.Actions);
        Assert.Equal(1, Updates.CheckCalls);
    }

    [Fact]
    public async Task Check_WithNothingNewer_SaysUpToDate_AndOffersNoUpdate()
    {
        Updates.Available = null;

        var result = await RunAsync(ShellCommandType.UpdateCheck);

        Assert.Equal((ShellOperationOutcome.Succeeded, _localizer["Shell.Update.Status.UpToDate"]), (result.Outcome, result.Message));
        Assert.Equal(ShellUpdateStages.UpToDate, View().Stage);
        Assert.Null(View().AvailableVersion);
        Assert.False(View().Actions.Start);
    }

    [Fact]
    public async Task Start_BeforeACheck_IsRejected_AndAsksNothing()
    {
        var result = await RunAsync(ShellCommandType.UpdateStart);

        Assert.Equal((ShellOperationOutcome.Rejected, _localizer["Shell.Update.CheckFirst"]), (result.Outcome, result.Message));
        Assert.Empty(_rig.Native.UpdateConfirmations);
        Assert.Empty(Updates.Prepared);
    }

    [Fact]
    public async Task ConfirmationDeclined_DoesNothing()
    {
        await RunAsync(ShellCommandType.UpdateCheck);
        _rig.Native.ConfirmUpdate = false;

        var result = await RunAsync(ShellCommandType.UpdateStart);

        // Cancel or Escape in the native dialog: no request to the service, and nothing to announce.
        Assert.Equal((ShellOperationOutcome.Cancelled, string.Empty), (result.Outcome, result.Message));
        Assert.Equal([(FakeUpdates.Installed, FakeUpdates.Newer)], _rig.Native.UpdateConfirmations);
        Assert.Empty(Updates.Prepared);
        Assert.Equal(ShellUpdateStages.Available, View().Stage);
    }

    [Fact]
    public async Task Confirmed_PreparesTheVersionTheLastCheckFound()
    {
        await RunAsync(ShellCommandType.UpdateCheck);

        var result = await RunAsync(ShellCommandType.UpdateStart);

        Assert.Equal((ShellOperationOutcome.Succeeded, _localizer.GetString("Shell.Update.Started", FakeUpdates.Newer)), (result.Outcome, result.Message));
        Assert.Equal([(FakeUpdates.Installed, FakeUpdates.Newer)], _rig.Native.UpdateConfirmations);
        Assert.Equal([FakeUpdates.Newer], Updates.Prepared);
        Assert.Empty(_rig.Launcher.Started());
    }

    [Fact]
    public async Task ReadyAndVerified_StartsTheSetupOnce_AndCancellingIsLeftToTheSetup()
    {
        await StartAttemptAsync();

        Updates.Current = Ready();
        await WaitForAsync(() => _rig.Launcher.Started().Length == 1);
        // More states for the same attempt (every service state carries it) never start it again.
        Updates.Current = Ready();
        _rig.Engine.Raise();
        _rig.Engine.Raise();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal([FakeUpdates.StagedPath()], _rig.Launcher.Started());
        var check = Assert.Single(_rig.Verifier.Checked());
        Assert.Equal(FakeUpdates.StagedPath(), check.Path);
        Assert.Equal(
            new PrintBridgeRelease(new Version(1, 0, 1), $"Wasla.PrintBridge-Setup-x64-{FakeUpdates.Newer}.exe", FakeUpdates.SetupBytes, FakeUpdates.SetupSha256),
            check.Release);
        Assert.Equal(new Version(1, 0, 0), check.AppVersion);
        await WaitForAsync(() => View().Stage == ShellUpdateStages.SetupOpen);
        Assert.Equal(ShellUpdateActionsView.None, View().Actions);

        var cancel = await RunAsync(ShellCommandType.UpdateCancel);
        Assert.Equal((ShellOperationOutcome.Rejected, _localizer["Shell.Update.Status.SetupOpen"]), (cancel.Outcome, cancel.Message));
        Assert.Empty(Updates.CancelledSoFar());
    }

    [Fact]
    public async Task SetupExitsWithoutInstalling_CancelsTheAttempt_AndSaysThePreviousVersionStays()
    {
        await StartAttemptAsync();
        Updates.OnCancel = id => Updates.Current = FakeUpdates.Attempt(id, PrintBridgeUpdateStage.Cancelled);
        Updates.Current = Ready();
        await WaitForAsync(() => _rig.Launcher.Started().Length == 1);

        _rig.Launcher.Exit();
        await _rig.ShellUpdates.SetupFlowForTests.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal([AttemptId], Updates.CancelledSoFar());
        var view = View();
        Assert.Equal(ShellUpdateStages.NotApplied, view.Stage);
        Assert.Equal(_localizer.GetString("Shell.Update.Status.NotApplied", FakeUpdates.Installed), view.Label);
        // The version the last check found can be tried again.
        Assert.Equal(new ShellUpdateActionsView(true, true, false), view.Actions);
    }

    public static TheoryData<string> ServiceMovedOn => new() { "disconnected", "installing", "another attempt" };

    [Theory]
    [MemberData(nameof(ServiceMovedOn))]
    public async Task SetupExits_AfterTheServiceMovedOn_DoesNotCancel(string state)
    {
        await StartAttemptAsync();
        Updates.Current = Ready();
        await WaitForAsync(() => _rig.Launcher.Started().Length == 1);

        Updates.Current = state switch
        {
            "disconnected" => null,
            "installing" => FakeUpdates.Attempt(AttemptId, PrintBridgeUpdateStage.Ready, stagedPath: null, installed: FakeUpdates.Newer),
            _ => FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Ready)
        };
        _rig.Launcher.Exit();
        await _rig.ShellUpdates.SetupFlowForTests.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Empty(Updates.CancelledSoFar());
    }

    [Theory]
    [InlineData(PrintBridgeSetupVerification.HashMismatch)]
    [InlineData(PrintBridgeSetupVerification.SizeMismatch)]
    [InlineData(PrintBridgeSetupVerification.NotSigned)]
    [InlineData(PrintBridgeSetupVerification.SignatureInvalid)]
    [InlineData(PrintBridgeSetupVerification.PublisherMismatch)]
    [InlineData(PrintBridgeSetupVerification.VersionMismatch)]
    [InlineData(PrintBridgeSetupVerification.Missing)]
    public async Task FailedVerification_CancelsAndNeverStarts(PrintBridgeSetupVerification outcome)
    {
        _rig.Verifier.Outcome = outcome;
        await StartAttemptAsync();
        Updates.OnCancel = id => Updates.Current = FakeUpdates.Attempt(id, PrintBridgeUpdateStage.Cancelled);

        Updates.Current = Ready();
        await WaitForAsync(() => Updates.CancelledSoFar().Length == 1);
        await _rig.ShellUpdates.SetupFlowForTests.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Empty(_rig.Launcher.Started());
        Assert.Equal([AttemptId], Updates.CancelledSoFar());
        AssertFailed(View(), ShellUpdateFailures.Verification);
    }

    [Fact]
    public async Task AnAppThatIsNotValidlySigned_TrustsNoSetup()
    {
        _rig.Verifier.AppSigner = null;
        await StartAttemptAsync();
        Updates.OnCancel = id => Updates.Current = FakeUpdates.Attempt(id, PrintBridgeUpdateStage.Cancelled);

        Updates.Current = Ready();
        await WaitForAsync(() => Updates.CancelledSoFar().Length == 1);
        await _rig.ShellUpdates.SetupFlowForTests.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Empty(_rig.Verifier.Checked());
        Assert.Empty(_rig.Launcher.Started());
        AssertFailed(View(), ShellUpdateFailures.Verification);
    }

    [Theory]
    [InlineData("public folder")]
    [InlineData("parent of the updates folder")]
    [InlineData("folder inside the updates folder")]
    [InlineData("network share")]
    [InlineData("relative file name")]
    [InlineData("relative folder")]
    [InlineData("not a setup program")]
    public async Task AStagedSetupOutsideTheUpdatesFolder_IsRefused_BeforeItIsOpened(string where)
    {
        const string setup = "Wasla.PrintBridge-Setup-x64-1.0.1.exe";
        var updates = Wasla.PrintBridge.Configuration.PrintBridgePaths.UpdatesDirectory;
        var path = where switch
        {
            "public folder" => Path.Combine(@"C:\Users\Public", setup),
            "parent of the updates folder" => Path.Combine(updates, "..", setup),
            "folder inside the updates folder" => Path.Combine(updates, "nested", setup),
            "network share" => Path.Combine(@"\\print-server\share", setup),
            "relative file name" => setup,
            "relative folder" => Path.Combine("updates", setup),
            _ => Path.Combine(updates, "setup.cmd")
        };
        await StartAttemptAsync();

        Updates.Current = FakeUpdates.Attempt(AttemptId, PrintBridgeUpdateStage.Ready, stagedPath: path);
        await WaitForAsync(() => Updates.CancelledSoFar().Length == 1);
        await _rig.ShellUpdates.SetupFlowForTests.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Empty(_rig.Verifier.Checked());
        Assert.Empty(_rig.Launcher.Started());
        Assert.Equal([AttemptId], Updates.CancelledSoFar());
    }

    [Fact]
    public async Task ASetupThatCannotStart_CancelsTheAttempt_AndSaysSo()
    {
        _rig.Launcher.Failure = new Win32Exception(2);
        await StartAttemptAsync();
        Updates.OnCancel = id => Updates.Current = FakeUpdates.Attempt(id, PrintBridgeUpdateStage.Cancelled);

        Updates.Current = Ready();
        await WaitForAsync(() => Updates.CancelledSoFar().Length == 1);
        await _rig.ShellUpdates.SetupFlowForTests.WaitAsync(Wait, TestContext.Current.CancellationToken);

        AssertFailed(View(), ShellUpdateFailures.Setup);
    }

    [Fact]
    public async Task ARefusedPermissionPrompt_CancelsTheAttempt_AsNotInstalled()
    {
        _rig.Launcher.Failure = new Win32Exception(1223);
        await StartAttemptAsync();
        Updates.OnCancel = id => Updates.Current = FakeUpdates.Attempt(id, PrintBridgeUpdateStage.Cancelled);

        Updates.Current = Ready();
        await WaitForAsync(() => Updates.CancelledSoFar().Length == 1);
        await _rig.ShellUpdates.SetupFlowForTests.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(ShellUpdateStages.NotApplied, View().Stage);
    }

    [Fact]
    public async Task ASetupWhoseEndCannotBeObserved_IsLeftToTheService()
    {
        _rig.Launcher.Observable = false;
        await StartAttemptAsync();

        Updates.Current = Ready();
        await WaitForAsync(() => _rig.Launcher.Started().Length == 1);
        await _rig.ShellUpdates.SetupFlowForTests.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Empty(Updates.CancelledSoFar());
        Assert.Equal(ShellUpdateStages.SetupOpen, View().Stage);
    }

    [Fact]
    public async Task OnlyTheAttemptThisAppStarted_IsOpened()
    {
        await RunAsync(ShellCommandType.UpdateCheck);

        // Another app (or this one before a restart) started it: shown as ready, cancellable, never opened from here.
        Updates.Current = FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Ready);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(_rig.Launcher.Started());
        Assert.Equal(ShellUpdateStages.Ready, View().Stage);
        Assert.True(View().Actions.Cancel);
    }

    [Fact]
    public async Task Cancel_WhileTheStagedSetupIsChecked_KeepsItClosed()
    {
        _rig.Verifier.Gate = new ManualResetEventSlim(false);
        await StartAttemptAsync();
        Updates.Current = Ready();
        await WaitForAsync(() => _rig.Verifier.Checked().Length == 1);
        Assert.Equal(ShellUpdateStages.Opening, View().Stage);
        Assert.True(View().Actions.Cancel);

        var result = await RunAsync(ShellCommandType.UpdateCancel);
        _rig.Verifier.Gate.Set();
        await _rig.ShellUpdates.SetupFlowForTests.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal((ShellOperationOutcome.Succeeded, _localizer["Shell.Update.Status.Cancelled"]), (result.Outcome, result.Message));
        Assert.Equal([AttemptId], Updates.CancelledSoFar());
        Assert.Empty(_rig.Launcher.Started());
    }

    [Theory]
    [InlineData(PrintBridgeUpdateStage.Downloading)]
    [InlineData(PrintBridgeUpdateStage.Verifying)]
    [InlineData(PrintBridgeUpdateStage.WaitingForPrinting)]
    [InlineData(PrintBridgeUpdateStage.Ready)]
    public async Task Cancel_CancelsTheAttemptShown(PrintBridgeUpdateStage stage)
    {
        var attempt = Guid.NewGuid();
        Updates.Current = FakeUpdates.Attempt(attempt, stage, percent: stage == PrintBridgeUpdateStage.Downloading ? 10 : null);
        Assert.True(View().Actions.Cancel);

        var result = await RunAsync(ShellCommandType.UpdateCancel);

        Assert.Equal(ShellOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal([attempt], Updates.CancelledSoFar());
    }

    [Fact]
    public async Task Cancel_WhileInstalling_OrWithNothingToCancel_IsRefused()
    {
        Assert.Equal(
            (ShellOperationOutcome.Rejected, _localizer["Shell.Update.NothingToCancel"]),
            ToTuple(await RunAsync(ShellCommandType.UpdateCancel)));

        Updates.Current = FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Ready, stagedPath: null, installed: FakeUpdates.Newer);
        Assert.False(View().Actions.Cancel);
        var installing = await RunAsync(ShellCommandType.UpdateCancel);

        Assert.Equal(ShellOperationOutcome.Rejected, installing.Outcome);
        Assert.Equal(_localizer.GetString("Shell.Update.Status.Installing", FakeUpdates.Newer), installing.Message);
        Assert.Empty(Updates.CancelledSoFar());
    }

    [Fact]
    public async Task ASecondStartWhileTheConfirmationIsOpen_IsBusy_AndSoIsACheck()
    {
        await RunAsync(ShellCommandType.UpdateCheck);
        _rig.Native.UpdateConfirmGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = RunAsync(ShellCommandType.UpdateStart);
        await WaitForAsync(() => View().Busy.Start);
        var second = await RunAsync(ShellCommandType.UpdateStart);
        var check = await RunAsync(ShellCommandType.UpdateCheck);
        Assert.False(View().Actions.Start);
        Assert.False(View().Actions.Check);
        _rig.Native.UpdateConfirmGate.SetResult();

        Assert.Equal(ShellOperationOutcome.Busy, second.Outcome);
        Assert.Equal(ShellOperationOutcome.Busy, check.Outcome);
        Assert.Equal(ShellOperationOutcome.Succeeded, (await first).Outcome);
        Assert.Single(Updates.Prepared);
        Assert.Single(_rig.Native.UpdateConfirmations);
        Assert.Equal(1, Updates.CheckCalls);
    }

    [Fact]
    public async Task AStartWhileAnotherAttemptRuns_IsBusy()
    {
        await RunAsync(ShellCommandType.UpdateCheck);

        // The service answers busy: another app or user started one meanwhile.
        Updates.PrepareFailure = new PrintBridgeEngineBusyException();
        var refused = await RunAsync(ShellCommandType.UpdateStart);
        Assert.Equal((ShellOperationOutcome.Busy, _localizer["Update.Error.InProgress"]), (refused.Outcome, refused.Message));

        // This app already sees one running: it does not even ask.
        Updates.Current = FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Downloading, percent: 5);
        var running = await RunAsync(ShellCommandType.UpdateStart);
        Assert.Equal((ShellOperationOutcome.Busy, _localizer["Update.Error.InProgress"]), (running.Outcome, running.Message));
        Assert.Single(_rig.Native.UpdateConfirmations);
    }

    [Fact]
    public async Task AnUnsignedInstallation_SaysItIsADevelopmentBuild_AndOffersNothing()
    {
        Updates.Current = FakeUpdates.Idle(supported: false);

        var view = View();
        Assert.Equal((ShellUpdateStages.Unsupported, _localizer["Shell.Update.Status.Unsupported"], false), (view.Stage, view.Label, view.Supported));
        Assert.Equal(ShellUpdateActionsView.None, view.Actions);

        var check = await RunAsync(ShellCommandType.UpdateCheck);
        var start = await RunAsync(ShellCommandType.UpdateStart);
        Assert.Equal((ShellOperationOutcome.Rejected, _localizer["Shell.Update.Status.Unsupported"]), ToTuple(check));
        Assert.Equal((ShellOperationOutcome.Rejected, _localizer["Shell.Update.Status.Unsupported"]), ToTuple(start));
        Assert.Equal(0, Updates.CheckCalls);
        Assert.Empty(_rig.Native.UpdateConfirmations);
    }

    [Fact]
    public async Task WithoutTheService_TheSectionSaysSo_AndCommandsExplainWhy()
    {
        Updates.Current = null;

        var view = View();
        Assert.Equal(ShellUpdateStages.Unavailable, view.Stage);
        Assert.Equal(_localizer["Shell.Update.Status.Unavailable"], view.Label);
        Assert.Equal(ShellUpdateActionsView.None, view.Actions);
        Assert.Equal((ShellOperationOutcome.Failed, _localizer["Shell.Update.Status.Unavailable"]), ToTuple(await RunAsync(ShellCommandType.UpdateStart)));
        Assert.Null(ShellUpdates.None.CurrentUpdate);
        var none = await Assert.ThrowsAsync<LocalizedApplicationException>(() => ShellUpdates.None.CheckForUpdateAsync(CancellationToken.None));
        Assert.Equal(new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServiceUnavailable).EffectiveDetailResourceKey, none.ResourceKey);
    }

    [Theory]
    [InlineData("Update.Error.NotSigned")]
    [InlineData("Update.Error.NotReady")]
    [InlineData("Update.Error.InvalidRelease")]
    [InlineData("Update.Error.NotNewer")]
    [InlineData("Update.Error.Storage")]
    [InlineData("Update.Error.InProgress")]
    public async Task ServiceRefusals_AreShownInTheUsersLanguage(string key)
    {
        Updates.CheckFailure = new LocalizedApplicationException(key);

        var result = await RunAsync(ShellCommandType.UpdateCheck);

        Assert.Equal(ShellOperationOutcome.Failed, result.Outcome);
        Assert.Equal(_localizer[key], result.Message);
        Assert.NotEqual(key, result.Message);
    }

    [Fact]
    public async Task ANetworkFailureOfTheCheck_IsShownAsTheConnectionIssue()
    {
        Updates.CheckFailure = new PrintBridgeConnectionException(
            "Connection.ServerUnavailable",
            string.Empty,
            string.Empty,
            issueCode: PrintBridgeRuntimeIssueCode.ServerUnreachable);

        var result = await RunAsync(ShellCommandType.UpdateCheck);

        Assert.Equal(ShellOperationOutcome.Failed, result.Outcome);
        Assert.Equal(
            _localizer.GetRuntimeIssueDetail(new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable, "Connection.ServerUnavailable", [])),
            result.Message);
    }

    public static TheoryData<PrintBridgeUpdateStage, PrintBridgeUpdateFailure?, string, string?, string> Stages => new()
    {
        { PrintBridgeUpdateStage.Verifying, null, ShellUpdateStages.Verifying, null, "Shell.Update.Status.Verifying" },
        { PrintBridgeUpdateStage.WaitingForPrinting, null, ShellUpdateStages.WaitingForPrinting, null, "Shell.Update.Status.WaitingForPrinting" },
        { PrintBridgeUpdateStage.Ready, null, ShellUpdateStages.Ready, null, "Shell.Update.Status.Ready" },
        { PrintBridgeUpdateStage.Succeeded, null, ShellUpdateStages.Succeeded, null, "Shell.Update.Status.Succeeded" },
        { PrintBridgeUpdateStage.NotApplied, null, ShellUpdateStages.NotApplied, null, "Shell.Update.Status.NotApplied" },
        { PrintBridgeUpdateStage.Cancelled, null, ShellUpdateStages.Cancelled, null, "Shell.Update.Status.Cancelled" },
        { PrintBridgeUpdateStage.Cancelled, PrintBridgeUpdateFailure.Expired, ShellUpdateStages.Failed, ShellUpdateFailures.Expired, "Shell.Update.Failure.Expired" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.Interrupted, ShellUpdateStages.Failed, ShellUpdateFailures.Interrupted, "Shell.Update.Failure.Interrupted" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.Download, ShellUpdateStages.Failed, ShellUpdateFailures.Download, "Shell.Update.Failure.Download" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.SizeMismatch, ShellUpdateStages.Failed, ShellUpdateFailures.Verification, "Shell.Update.Failure.Verification" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.HashMismatch, ShellUpdateStages.Failed, ShellUpdateFailures.Verification, "Shell.Update.Failure.Verification" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.NotSigned, ShellUpdateStages.Failed, ShellUpdateFailures.Verification, "Shell.Update.Failure.Verification" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.SignatureInvalid, ShellUpdateStages.Failed, ShellUpdateFailures.Verification, "Shell.Update.Failure.Verification" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.PublisherMismatch, ShellUpdateStages.Failed, ShellUpdateFailures.Verification, "Shell.Update.Failure.Verification" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.VersionMismatch, ShellUpdateStages.Failed, ShellUpdateFailures.Verification, "Shell.Update.Failure.Verification" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.PrintingInProgress, ShellUpdateStages.Failed, ShellUpdateFailures.PrintingInProgress, "Shell.Update.Failure.PrintingInProgress" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.UnexpectedVersion, ShellUpdateStages.Failed, ShellUpdateFailures.UnexpectedVersion, "Shell.Update.Failure.UnexpectedVersion" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.Expired, ShellUpdateStages.Failed, ShellUpdateFailures.Expired, "Shell.Update.Failure.Expired" },
        { PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.Storage, ShellUpdateStages.Failed, ShellUpdateFailures.Storage, "Shell.Update.Failure.Storage" }
    };

    [Theory]
    [MemberData(nameof(Stages))]
    public void EveryStageAndFailure_IsShownInTheUsersWords_WithoutItsCode(
        PrintBridgeUpdateStage stage,
        PrintBridgeUpdateFailure? failure,
        string expectedStage,
        string? expectedFailure,
        string key)
    {
        foreach (var culture in SupportedCultures.All)
        {
            _rig.Culture.Initialize(culture);
            Updates.Current = FakeUpdates.Attempt(Guid.NewGuid(), stage, failure);

            var view = View();

            Assert.Equal((expectedStage, expectedFailure), (view.Stage, view.Failure));
            var version = stage == PrintBridgeUpdateStage.NotApplied ? FakeUpdates.Installed : FakeUpdates.Newer;
            Assert.Equal(_localizer.GetString(key, version), view.Label);
            Assert.False(string.IsNullOrWhiteSpace(view.Label));
            Assert.Equal(FakeUpdates.Newer, view.TargetVersion);
            Assert.Equal(stage is PrintBridgeUpdateStage.Verifying or PrintBridgeUpdateStage.WaitingForPrinting or PrintBridgeUpdateStage.Ready, view.Actions.Cancel);
            // The section as the page receives it: no failure code, no hash, no path.
            var json = JsonSerializer.Serialize(_rig.Factory.Create(Status()).Update, ShellMessageSerializer.Options);
            if (failure is { } code)
                Assert.DoesNotContain(code.ToString(), json, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeUpdates.SetupSha256, json, StringComparison.Ordinal);
            Assert.DoesNotContain("Setup-x64", json, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeUpdates.SetupBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Downloading_ShowsThePercentage_FormattedForTheLanguage()
    {
        foreach (var culture in SupportedCultures.All)
        {
            _rig.Culture.Initialize(culture);
            Updates.Current = FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Downloading, percent: 40);

            var view = View();

            Assert.Equal((ShellUpdateStages.Downloading, 40), (view.Stage, view.DownloadPercent));
            Assert.Equal((0.4).ToString("P0", System.Globalization.CultureInfo.GetCultureInfo(culture)), view.Progress);
            if (culture == SupportedCultures.English)
                Assert.Equal("40%", view.Progress);
            Assert.Equal(_localizer.GetString("Shell.Update.Status.Downloading", FakeUpdates.Newer), view.Label);
            Assert.Equal(new ShellUpdateActionsView(false, false, true), view.Actions);
        }

        Updates.Current = FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Downloading);
        Assert.Null(View().DownloadPercent);
        Assert.Null(View().Progress);
    }

    [Fact]
    public void InstallingTheNewVersion_CannotBeCancelled()
    {
        Updates.Current = FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Ready, stagedPath: null, installed: FakeUpdates.Newer);

        var view = View();

        Assert.Equal(ShellUpdateStages.Installing, view.Stage);
        Assert.Equal(_localizer.GetString("Shell.Update.Status.Installing", FakeUpdates.Newer), view.Label);
        Assert.Equal(ShellUpdateActionsView.None, view.Actions);
    }

    [Fact]
    public async Task ACheckAfterAnAttemptEnded_ReplacesItsResult_UntilTheNextAttempt()
    {
        Updates.Current = FakeUpdates.Attempt(Guid.NewGuid(), PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.Download);
        AssertFailed(View(), ShellUpdateFailures.Download);

        await RunAsync(ShellCommandType.UpdateCheck);
        Assert.Equal(ShellUpdateStages.Available, View().Stage);

        await RunAsync(ShellCommandType.UpdateStart);
        Updates.Current = FakeUpdates.Attempt(AttemptId, PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.PrintingInProgress);
        AssertFailed(View(), ShellUpdateFailures.PrintingInProgress);
        // "Try again" starts the version the check found once more.
        Assert.True(View().Actions.Start);
    }

    [Fact]
    public async Task ThroughThePage_TheResultAndTheFreshSectionArrive()
    {
        Assert.Equal(ShellMessageOutcome.Accepted, _rig.Bridge.HandleWebMessage(ShellDocument, Command("ui.ready")));
        var requestId = NewRequestId();

        _rig.Bridge.HandleWebMessage(ShellDocument, Tracked("update.check", requestId: requestId));
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.OperationResult).Count == 1);
        await WaitForAsync(() =>
        {
            _rig.Host.RunQueued();
            return _rig.LastSnapshot().GetProperty("update").GetProperty("stage").GetString() == ShellUpdateStages.Available;
        });

        var result = _rig.Messages(ShellMessageContract.OperationResult)[0].GetProperty("payload");
        Assert.Equal((requestId, "update.check", "succeeded"), (result.GetProperty("requestId").GetString(), result.GetProperty("operation").GetString(), result.GetProperty("outcome").GetString()));
        var update = _rig.LastSnapshot().GetProperty("update");
        Assert.Equal(ShellUpdateStages.Available, update.GetProperty("stage").GetString());
        Assert.Equal(FakeUpdates.Newer, update.GetProperty("availableVersion").GetString());
        Assert.True(update.GetProperty("actions").GetProperty("start").GetBoolean());
        Assert.Equal(JsonValueKind.Null, update.GetProperty("failure").ValueKind);
    }

    [Fact]
    public async Task TheAttemptsTechnicalResult_GoesToTheAppLogOnce()
    {
        var logs = new CapturingLoggerFactory();
        using var updates = new ShellUpdates(
            Updates,
            _rig.Engine,
            _rig.Native,
            _rig.Launcher,
            _rig.Verifier,
            _localizer,
            _rig.Culture,
            logs.CreateLogger("test"),
            new Version(1, 0, 0));

        var attempt = Guid.NewGuid();
        Updates.Current = FakeUpdates.Attempt(attempt, PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.SignatureInvalid);
        _rig.Engine.Raise();
        await Task.Yield();

        Assert.Single(logs.Lines, line => line.Contains("SignatureInvalid", StringComparison.Ordinal) && line.Contains(attempt.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain("SignatureInvalid", JsonSerializer.Serialize(updates.CreateView(ShellUpdateBusyView.Idle)), StringComparison.Ordinal);
    }

    // Helpers ------------------------------------------------------------------------------------

    private ShellUpdateView View() => _rig.Operations.UpdateView();

    private Task<ShellOperationResult> RunAsync(ShellCommandType type) =>
        _rig.Operations.ExecuteAsync(new ShellCommand(type, NewRequestId()));

    /// <summary>A check, then a confirmed start: the attempt <see cref="AttemptId"/> is this app's.</summary>
    private async Task StartAttemptAsync()
    {
        Assert.Equal(ShellOperationOutcome.Succeeded, (await RunAsync(ShellCommandType.UpdateCheck)).Outcome);
        Assert.Equal(ShellOperationOutcome.Succeeded, (await RunAsync(ShellCommandType.UpdateStart)).Outcome);
        Updates.Current = FakeUpdates.Attempt(AttemptId, PrintBridgeUpdateStage.Downloading, percent: 0);
    }

    private Wasla.PrintBridge.Ipc.IpcUpdate Ready() => FakeUpdates.Attempt(AttemptId, PrintBridgeUpdateStage.Ready);

    private void AssertFailed(ShellUpdateView view, string group)
    {
        Assert.Equal((ShellUpdateStages.Failed, group), (view.Stage, view.Failure));
        Assert.Equal(_localizer[ShellUpdates.FailureKey(group)], view.Label);
        Assert.False(view.Actions.Cancel);
    }

    private static (ShellOperationOutcome, string) ToTuple(ShellOperationResult result) => (result.Outcome, result.Message);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
