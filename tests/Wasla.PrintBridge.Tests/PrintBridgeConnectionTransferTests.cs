using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;
using static Wasla.PrintBridge.Tests.PrintBridgeTransferBackend;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-91, end to end: the real setup coordinator, API client and settings store of Print Bridge against the real setup
/// and device services (<see cref="PrintBridgeTransferBackend"/>), connected to restaurant A when a setup link of
/// restaurant B is opened. Every value is fake and every path is an isolated folder. The hosts are production Wasla
/// hosts, so the production trust policy is the one in force.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeConnectionTransferTests : IDisposable
{
    private const string TransferPath = "api/print-bridge/setup/transfer";
    private const string PreviewPath = "api/print-bridge/setup/transfer/preview";
    private const string ExchangePath = "api/print-bridge/setup/exchange";

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-transfer-tests");
    private readonly PrintBridgeTransferBackend _backend = new();
    private readonly PrintBridgeTransferHandler _handler;
    private readonly Guid _installation = Guid.NewGuid();
    private readonly CapturingLogs _logs = new();

    public PrintBridgeConnectionTransferTests() => _handler = new PrintBridgeTransferHandler(_backend);

    public void Dispose()
    {
        _handler.Dispose();
        _backend.Dispose();
        _dataRoot.Dispose();
    }

    [Fact]
    public async Task Declining_SendsNoMove_AndLeavesTheSettingsByteForByteUnchanged()
    {
        var (app, tokenA, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        var before = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);
        var consent = new Consent(approve: false);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), consent.Ask, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.Cancelled, outcome);
        // The confirmation showed what the server verified: both restaurants and both servers.
        var asked = Assert.Single(consent.Requests);
        Assert.Equal(new PrintBridgeConnectionTransferConsentRequest(NameA, HostA, NameB, HostB), asked);

        // Only the read-only preview went out, to A; nothing at all went to B, and no code was used.
        Assert.Equal(new[] { (HostA, PreviewPath) }, _handler.Requests.Select(r => (r.Host, r.Path)));
        Assert.Equal(before, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
    }

    [Fact]
    public async Task Approving_MovesToB_RetiresA_AndSavesTheNewConnection()
    {
        var (app, tokenA, deviceA) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct);

        Assert.Contains(outcome, new[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        var saved = app.Store.Load();
        Assert.Equal(UrlB.TrimEnd('/'), saved.Wasla.ServerUrl);
        Assert.NotEqual(tokenA, saved.Wasla.AgentToken);
        Assert.Equal(_installation.ToString("D"), saved.PrintBridge.InstallationId);
        // The destination device is named after this machine, never after the old restaurant's name for it.
        Assert.Equal(Environment.MachineName, saved.PrintBridge.DisplayName);
        Assert.Equal((saved.Wasla.ServerUrl, saved.Wasla.AgentToken), (app.Holder.Connection.ServerUrl.TrimEnd('/'), app.Holder.Connection.AgentToken));

        // A's token no longer works anywhere; the saved one is B's.
        Assert.False((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
        var auth = await _backend.AuthenticateAsync(saved.Wasla.AgentToken, _installation);
        Assert.True(auth.Succeeded);
        Assert.Equal(_backend.TenantB, auth.Context!.TenantId);
        Assert.NotNull((await _backend.DeviceAsync(deviceA)).RemovedAtUtc);

        // The move is completed: nothing pending here, and the retired token can no longer repeat it there.
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        var session = await _backend.SessionAsync(code);
        Assert.NotNull(session.CompletedAtUtc);
        Assert.True(session.ConnectionVerified);
        Assert.Null(session.SourceTokenHash);

        // The old token went only to A, its own server; B never saw it, and the link's server got no code before approval.
        Assert.All(_handler.Requests.Where(r => r.Token == tokenA), r => Assert.Equal(HostA, r.Host));
        Assert.Equal(1, _handler.Count(TransferPath));
        Assert.Equal(0, _handler.Count(ExchangePath));
        AssertNoSecretsLogged(tokenA, code, saved.Wasla.AgentToken);
    }

    [Fact]
    public async Task ALinkOfTheSameRestaurant_TakesTheOrdinaryReconnect_WithoutAsking()
    {
        var (app, tokenA, deviceA) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantA, UrlA, PrintBridgeSetupMode.ReconnectExistingDevice, deviceA);
        var consent = new Consent(approve: false);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlA, code), consent.Ask, Ct);

        Assert.Contains(outcome, new[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        Assert.Empty(consent.Requests);
        Assert.Equal(1, _handler.Count(ExchangePath));
        Assert.Equal(0, _handler.Count(TransferPath));
        var saved = app.Store.Load().Wasla;
        Assert.NotEqual(tokenA, saved.AgentToken);
        Assert.True((await _backend.AuthenticateAsync(saved.AgentToken, _installation)).Succeeded);
        Assert.Null((await _backend.DeviceAsync(deviceA)).RemovedAtUtc);
    }

    [Fact]
    public async Task WithoutASavedConnection_FirstTimeSetupStillExchangesTheCode()
    {
        var app = await NotConnectedAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: false).Ask, Ct);

        Assert.Contains(outcome, new[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        Assert.Equal(0, _handler.Count(PreviewPath));
        Assert.Equal(UrlB.TrimEnd('/'), app.Store.Load().Wasla.ServerUrl);
    }

    [Fact]
    public async Task ATokenTheOtherRestaurantRemoved_IsNoConnectionToKeep_SoTheLinkConnectsWithoutAsking()
    {
        var (app, _, deviceA) = await ConnectedToAAsync();
        await using (var db = _backend.OpenDb())
            await _backend.Devices(db).RemoveDeviceAsync(_backend.TenantA, deviceA, CancellationToken.None);
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        var consent = new Consent(approve: false);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), consent.Ask, Ct);

        Assert.Contains(outcome, new[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        Assert.Empty(consent.Requests);
        Assert.Equal(1, _handler.Count(ExchangePath));
    }

    [Fact]
    public async Task ARejectedTokenOfADeviceStillRegistered_FailsClosed_WithLocalizedGuidance()
    {
        // A disabled the device: its token is rejected, but the computer is still registered to A.
        var (app, _, deviceA) = await ConnectedToAAsync();
        await using (var db = _backend.OpenDb())
            await _backend.Devices(db).SetDeviceActiveAsync(_backend.TenantA, deviceA, false, CancellationToken.None);
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        var before = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        var refused = await Assert.ThrowsAsync<LocalizedApplicationException>(() =>
            app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct));

        Assert.Equal("Auto.InstallationRegisteredToAnotherRestaurant", refused.ResourceKey);
        Assert.Equal(before, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Equal(1, await _backend.DeviceCountAsync());
        Assert.Null((await _backend.DeviceAsync(deviceA)).RemovedAtUtc);
    }

    [Fact]
    public async Task WhenTheCurrentServerCannotBeReached_TheLinkFailsClosed_AndNothingGoesToTheLinksServer()
    {
        var (app, tokenA, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        _handler.Unreachable.Add(HostA);
        var before = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.CurrentConnectionUnverified, outcome);
        Assert.DoesNotContain(_handler.Requests, r => r.Host == HostB);
        Assert.Equal(before, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
        _handler.Unreachable.Clear();
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
    }

    [Fact]
    public async Task ASavedServerSetupLinksCannotVerify_GetsNeitherTheCodeNorAnyRequest()
    {
        var (app, _, _) = await ConnectedToAAsync(serverUrl: "https://print.example.com");
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.CurrentConnectionUnverified, outcome);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("https://bahce.app.wasla.tr.evil.example/")]
    [InlineData("http://bahce.app.wasla.tr/")]
    [InlineData("https://localhost:7200/")]
    public async Task ALinkToAnUntrustedServer_IsRefused_BeforeAnyRequest(string linkServer)
    {
        var (app, _, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);

        var outcome = await app.Coordinator.ApplyAsync(Link(linkServer, code), new Consent(approve: true).Ask, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.InvalidOrExpired, outcome);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ALostAnswer_IsRepeated_AndTheMoveIsStoredOnce()
    {
        var (app, tokenA, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        _handler.LoseTransferAnswers = 1;

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct);

        Assert.Contains(outcome, new[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        Assert.Equal(2, _handler.Count(TransferPath));
        Assert.Single(await _backend.DevicesWithAsync(_installation), d => d.RemovedAtUtc == null);
        Assert.Equal(2, await _backend.DeviceCountAsync());
        Assert.True((await _backend.AuthenticateAsync(app.Store.Load().Wasla.AgentToken, _installation)).Succeeded);
        Assert.False((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
    }

    [Fact]
    public async Task EveryAnswerLost_EndsPending_AndARestartFinishesTheSameMove()
    {
        var (app, tokenA, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        _handler.LoseTransferAnswers = int.MaxValue;
        var before = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct);

        // The server stored the move, but this app does not know it yet: it keeps the pending change, not the old token's
        // power. A's token cannot print for A any more, so no job of the wrong restaurant is ever resumed.
        Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, outcome);
        Assert.True(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        Assert.Equal(before, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.NotNull((await _backend.SessionAsync(code)).ExchangedAtUtc);
        Assert.False((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);

        // A restart: everything is read from disk again; the identical move is repeated and its answer saved.
        _handler.LoseTransferAnswers = 0;
        var restarted = App.Load(_handler, _logs);
        var recovered = await restarted.Coordinator.RecoverPendingChangeAsync(Ct);

        Assert.Contains(recovered, new PrintBridgeAutoSetupOutcome?[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        var saved = restarted.Store.Load().Wasla;
        Assert.Equal(UrlB.TrimEnd('/'), saved.ServerUrl);
        Assert.True((await _backend.AuthenticateAsync(saved.AgentToken, _installation)).Succeeded);
        Assert.Single(await _backend.DevicesWithAsync(_installation), d => d.RemovedAtUtc == null);
        Assert.NotNull((await _backend.SessionAsync(code)).CompletedAtUtc);

        // Nothing left to do on the next start.
        Assert.Null(await App.Load(_handler, _logs).Coordinator.RecoverPendingChangeAsync(Ct));
    }

    [Fact]
    public async Task ARequestThatNeverArrived_IsDiscardedAfterTheCodeExpires_AndAKeepsWorking()
    {
        var (app, tokenA, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        _handler.DropTransfersBeforeServer = int.MaxValue;

        Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct));
        // Nothing was stored: A still owns this computer, so printing for A is still right.
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);

        _handler.DropTransfersBeforeServer = 0;
        await _backend.ExpireSessionAsync(code);
        var restarted = App.Load(_handler, _logs);

        Assert.Null(await restarted.Coordinator.RecoverPendingChangeAsync(Ct));
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        Assert.Equal(tokenA, restarted.Store.Load().Wasla.AgentToken);
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
    }

    [Fact]
    public async Task ASettingsWriteFailure_KeepsThePendingChange_AndTheNextAttemptSavesIt()
    {
        var (app, tokenA, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        var settings = new FileInfo(PrintBridgePaths.ProgramDataConfigPath);
        var before = File.ReadAllBytes(settings.FullName);
        settings.IsReadOnly = true;
        try
        {
            var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct);

            Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, outcome);
            Assert.Equal(before, File.ReadAllBytes(settings.FullName));
            Assert.Equal(tokenA, app.Holder.Connection.AgentToken);
            Assert.True(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        }
        finally
        {
            settings.IsReadOnly = false;
        }

        // The same process tries again (the tray does every minute, and a setup link does first).
        var recovered = await app.Coordinator.RecoverPendingChangeAsync(Ct);

        Assert.Contains(recovered, new PrintBridgeAutoSetupOutcome?[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        Assert.Equal(UrlB.TrimEnd('/'), app.Store.Load().Wasla.ServerUrl);
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        Assert.Equal(1, (await _backend.DevicesWithAsync(_installation)).Count(d => d.RemovedAtUtc == null));
    }

    [Fact]
    public async Task APendingChange_IsNotAppliedOverAConnectionChangedByHandMeanwhile()
    {
        var (app, _, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        _handler.LoseTransferAnswers = int.MaxValue;
        Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct));

        // The user connects to C by hand before the move is finished: that choice wins.
        var (_, tokenC) = await _backend.CreateDeviceAsync(_backend.TenantC, "Çınar PC", installationId: null);
        var document = app.Store.Load();
        document.Wasla.ServerUrl = UrlC.TrimEnd('/');
        document.Wasla.AgentToken = tokenC;
        app.Store.Save(document);
        _handler.LoseTransferAnswers = 0;

        var restarted = App.Load(_handler, _logs);
        Assert.Null(await restarted.Coordinator.RecoverPendingChangeAsync(Ct));
        Assert.Equal((UrlC.TrimEnd('/'), tokenC), (restarted.Store.Load().Wasla.ServerUrl, restarted.Store.Load().Wasla.AgentToken));
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
    }

    [Fact]
    public async Task AFullRestaurant_IsReportedBeforeAsking_AndNothingChanges()
    {
        var (app, _, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        for (var i = 0; i < PrintBridgeDeviceLimits.AllowedActiveDeviceCount; i++)
            await _backend.CreateDeviceAsync(_backend.TenantB, $"Bahçe {i}", Guid.NewGuid());
        var consent = new Consent(approve: true);
        var before = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        Assert.Equal(PrintBridgeAutoSetupOutcome.DeviceLimitReached, await app.Coordinator.ApplyAsync(Link(UrlB, code), consent.Ask, Ct));
        Assert.Empty(consent.Requests);
        Assert.Equal(before, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
    }

    [Fact]
    public async Task ARestaurantThatFillsUpWhileTheUserDecides_RefusesTheMove_AndNothingChanges()
    {
        var (app, tokenA, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        var before = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);
        var consent = new Consent(approve: true)
        {
            WhileAsking = async () =>
            {
                for (var i = 0; i < PrintBridgeDeviceLimits.AllowedActiveDeviceCount; i++)
                    await _backend.CreateDeviceAsync(_backend.TenantB, $"Bahçe {i}", Guid.NewGuid());
            }
        };

        Assert.Equal(PrintBridgeAutoSetupOutcome.DeviceLimitReached, await app.Coordinator.ApplyAsync(Link(UrlB, code), consent.Ask, Ct));
        Assert.Equal(before, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
    }

    [Fact]
    public async Task APrintJobStartedWhileTheUserDecided_RefusesTheMoveAfterApproval_BeforeAnyRequest()
    {
        var (app, _, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        var before = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);
        // WAS-58: the first lease (the check before asking) is granted, the one after approval is not.
        app.Guard.RefuseFromLease = 2;

        Assert.Equal(PrintBridgeAutoSetupOutcome.PrintingInProgress, await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct));
        Assert.Equal(0, _handler.Count(TransferPath));
        Assert.Equal(before, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
    }

    [Fact]
    public async Task AReceiptStillPrintingForTheSourceOnTheServer_RefusesTheMove_WithoutAsking()
    {
        var (app, _, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        _backend.PrintingInSourceTenant = true;
        var consent = new Consent(approve: true);

        Assert.Equal(PrintBridgeAutoSetupOutcome.PrintingInProgress, await app.Coordinator.ApplyAsync(Link(UrlB, code), consent.Ask, Ct));
        Assert.Empty(consent.Requests);
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
    }

    [Fact]
    public async Task WithoutAWayToAsk_AMoveIsNeverApplied()
    {
        var (app, tokenA, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);

        Assert.Equal(PrintBridgeAutoSetupOutcome.Cancelled, await app.Coordinator.ApplyAsync(Link(UrlB, code), Ct));
        Assert.Equal(0, _handler.Count(TransferPath));
        Assert.Equal(tokenA, app.Store.Load().Wasla.AgentToken);
    }

    [Fact]
    public async Task AnExchangeAnswerNamingAnotherOrigin_IsIgnored_AndTheLinksServerIsSaved()
    {
        var app = await NotConnectedAsync();
        // A session whose stored server differs from the link: the answer's ServerUrl must not be trusted.
        var code = await _backend.CreateSessionAsync(_backend.TenantB, "https://evil.example/");

        await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: false).Ask, Ct);

        Assert.Equal(UrlB.TrimEnd('/'), app.Store.Load().Wasla.ServerUrl);
        Assert.DoesNotContain(_handler.Requests, r => r.Host == "evil.example");
    }

    [Fact]
    public async Task APendingChangeThatCannotBeReadNow_IsKept_AndNothingElseChangesTheConnection()
    {
        // Review finding: antivirus or backup software holding the file must not throw the move away.
        var (app, tokenA, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        _handler.LoseTransferAnswers = int.MaxValue;
        Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct));
        _handler.LoseTransferAnswers = 0;
        var otherCode = await _backend.CreateSessionAsync(_backend.TenantC, UrlC);
        var requestsBefore = _handler.Requests.Count;

        using (new FileStream(PrintBridgePaths.PendingConnectionChangePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => app.Store.LoadPendingConnectionChange());
            Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, await app.Coordinator.RecoverPendingChangeAsync(Ct));
            Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, await app.Coordinator.ApplyAsync(Link(UrlC, otherCode), new Consent(approve: true).Ask, Ct));
        }

        Assert.Equal(requestsBefore, _handler.Requests.Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(PrintBridgePaths.PendingConnectionChangePath)!, "*.corrupt-*"));
        Assert.Equal(tokenA, app.Store.Load().Wasla.AgentToken);

        // Readable again: the move is finished.
        Assert.Contains(await app.Coordinator.RecoverPendingChangeAsync(Ct), new PrintBridgeAutoSetupOutcome?[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        Assert.Equal(UrlB.TrimEnd('/'), app.Store.Load().Wasla.ServerUrl);
    }

    [Fact]
    public void APendingChangeThatIsNotAPendingChange_IsSetAside()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PrintBridgePaths.PendingConnectionChangePath)!);
        File.WriteAllText(PrintBridgePaths.PendingConnectionChangePath, "{ not json");

        Assert.Null(new PrintBridgeSettingsStore().LoadPendingConnectionChange());
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(PrintBridgePaths.PendingConnectionChangePath)!, "pending-connection-change.json.corrupt-*"));
    }

    [Fact]
    public async Task AnEarlierPendingMove_FinishedWhileAnotherLinkIsOpened_IsReported_AndThatLinkIsNotUsed()
    {
        // Review finding: the earlier move to B wins, and the user is told that C's link was not applied.
        var (app, _, _) = await ConnectedToAAsync();
        var codeB = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        _handler.LoseTransferAnswers = int.MaxValue;
        Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, await app.Coordinator.ApplyAsync(Link(UrlB, codeB), new Consent(approve: true).Ask, Ct));
        _handler.LoseTransferAnswers = 0;
        var codeC = await _backend.CreateSessionAsync(_backend.TenantC, UrlC);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlC, codeC), new Consent(approve: true).Ask, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.PreviousChangeCompleted, outcome);
        Assert.Equal(UrlB.TrimEnd('/'), app.Store.Load().Wasla.ServerUrl);
        Assert.Null((await _backend.SessionAsync(codeC)).ExchangedAtUtc);
    }

    [Fact]
    public async Task ReopeningThePendingMovesOwnLink_FinishesIt_AndReportsItConnected()
    {
        var (app, _, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        _handler.LoseTransferAnswers = int.MaxValue;
        Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct));
        _handler.LoseTransferAnswers = 0;

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct);

        Assert.Contains(outcome, new[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        Assert.Equal(UrlB.TrimEnd('/'), app.Store.Load().Wasla.ServerUrl);
    }

    [Fact]
    public async Task ACheckCutShortAfterTheMoveWasSaved_IsSavedButUnverified_NotAFailure()
    {
        // Review finding: once the new connection is saved, running out of time must neither throw nor lose it.
        var (app, _, _) = await ConnectedToAAsync();
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB);
        app.Coordinator.PhaseTimeout = TimeSpan.FromSeconds(2);
        _handler.HealthDelay = TimeSpan.FromSeconds(10);

        var outcome = await app.Coordinator.ApplyAsync(Link(UrlB, code), new Consent(approve: true).Ask, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.SavedButUnverified, outcome);
        var saved = app.Store.Load().Wasla;
        Assert.Equal(UrlB.TrimEnd('/'), saved.ServerUrl);
        Assert.True((await _backend.AuthenticateAsync(saved.AgentToken, _installation)).Succeeded);
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
    }

    [Fact]
    public async Task AReconnectDeviceTheNewRestaurantTurnedOff_IsReportedBeforeAsking()
    {
        var (app, tokenA, _) = await ConnectedToAAsync();
        var (deviceB, _) = await _backend.CreateDeviceAsync(_backend.TenantB, "Bahçe counter", installationId: null);
        await using (var db = _backend.OpenDb())
            await _backend.Devices(db).SetDeviceActiveAsync(_backend.TenantB, deviceB, false, CancellationToken.None);
        var code = await _backend.CreateSessionAsync(_backend.TenantB, UrlB, PrintBridgeSetupMode.ReconnectExistingDevice, deviceB);
        var consent = new Consent(approve: true);

        Assert.Equal(PrintBridgeAutoSetupOutcome.DestinationDeviceDisabled, await app.Coordinator.ApplyAsync(Link(UrlB, code), consent.Ask, Ct));
        Assert.Empty(consent.Requests);
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
    }

    // Redirects, over real sockets ---------------------------------------------------------------

    /// <summary>
    /// The current server answers the confirmed move with a redirect, before or after it stored the move. The redirect is
    /// never followed, so nothing reaches the other origin. The result is unknown, so the pending change is kept with the
    /// settings untouched and finished once the server answers again.
    /// </summary>
    [Theory]
    [InlineData(307, false)]
    [InlineData(307, true)]
    [InlineData(308, false)]
    [InlineData(308, true)]
    [InlineData(302, true)]
    public async Task AMoveAnsweredWithARedirect_IsNotFollowed_AndThePendingChangeIsFinishedLater(int status, bool storedBeforeTheRedirect)
    {
        using var serverA = new LoopbackSetupServer(_handler);
        using var serverB = new LoopbackSetupServer(_handler);
        using var elsewhere = new LoopbackSetupServer(_handler);
        var (_, tokenA, _) = await ConnectedToAAsync(serverA.Url);
        var app = App.Load(_handler, _logs, overLoopback: true);
        var code = await _backend.CreateSessionAsync(_backend.TenantB, serverB.Url + "/");
        serverA.Redirect = new LoopbackRedirect(status, elsewhere.Url, TransferPath, AfterForwarding: storedBeforeTheRedirect);
        var before = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);

        var outcome = await app.Coordinator.ApplyAsync(Link(serverB.Url, code), new Consent(approve: true).Ask, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.ChangePending, outcome);
        Assert.Empty(elsewhere.Requests);
        Assert.Empty(serverB.Requests);
        // The identical request was repeated to A only, and every answer was the redirect.
        Assert.Equal(1 + app.Coordinator.TransferRetryDelays.Count, serverA.Count(TransferPath));
        Assert.True(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        Assert.Equal(before, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Equal(tokenA, app.Holder.Connection.AgentToken);
        // Exactly one working binding either way: A's, or (once stored) the one only the pending change can save.
        Assert.Equal(!storedBeforeTheRedirect, (await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
        Assert.Equal(storedBeforeTheRedirect, (await _backend.SessionAsync(code)).ExchangedAtUtc is not null);

        // The server answers again: the pending change is repeated and saved.
        serverA.Redirect = null;
        var recovered = await app.Coordinator.RecoverPendingChangeAsync(Ct);

        Assert.Contains(recovered, new PrintBridgeAutoSetupOutcome?[] { PrintBridgeAutoSetupOutcome.Connected, PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing });
        var saved = app.Store.Load().Wasla;
        Assert.Equal(serverB.Url, saved.ServerUrl);
        Assert.Equal(_backend.TenantB, (await _backend.AuthenticateAsync(saved.AgentToken, _installation)).Context!.TenantId);
        Assert.False((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        Assert.Single(await _backend.DevicesWithAsync(_installation), d => d.RemovedAtUtc == null);
        Assert.NotNull((await _backend.SessionAsync(code)).CompletedAtUtc);
        Assert.Empty(elsewhere.Requests);
    }

    [Fact]
    public async Task APreviewAnsweredWithARedirect_FailsClosed_WithoutAskingOrSendingAnythingElsewhere()
    {
        using var serverA = new LoopbackSetupServer(_handler);
        using var serverB = new LoopbackSetupServer(_handler);
        using var elsewhere = new LoopbackSetupServer(_handler);
        var (_, tokenA, _) = await ConnectedToAAsync(serverA.Url);
        var app = App.Load(_handler, _logs, overLoopback: true);
        var code = await _backend.CreateSessionAsync(_backend.TenantB, serverB.Url + "/");
        serverA.Redirect = new LoopbackRedirect(307, elsewhere.Url, PreviewPath);
        var before = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);
        var consent = new Consent(approve: true);

        var outcome = await app.Coordinator.ApplyAsync(Link(serverB.Url, code), consent.Ask, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.CurrentConnectionUnverified, outcome);
        Assert.Empty(consent.Requests);
        Assert.Equal(1, serverA.Count(PreviewPath));
        Assert.Empty(elsewhere.Requests);
        Assert.Empty(serverB.Requests);
        Assert.Equal(before, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.False(File.Exists(PrintBridgePaths.PendingConnectionChangePath));
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
    }

    // Helpers ------------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PrintBridgeProtocolSetupRequest Link(string server, string code) => new(server, code);

    /// <summary>Print Bridge connected to restaurant A with a device bound to this installation.</summary>
    private async Task<(App App, string TokenA, Guid DeviceA)> ConnectedToAAsync(string? serverUrl = null)
    {
        var (deviceA, tokenA) = await _backend.CreateDeviceAsync(_backend.TenantA, "Anadolu kitchen", _installation);
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        document.Wasla.ServerUrl = (serverUrl ?? UrlA).TrimEnd('/');
        document.Wasla.AgentToken = tokenA;
        document.PrintBridge.InstallationId = _installation.ToString("D");
        document.PrintBridge.DisplayName = "Anadolu kitchen";
        document.PrintBridge.ServerDeviceNameResolved = true;
        document.PrintBridge.DryRun = true;
        store.Save(document);
        return (App.Load(_handler, _logs), tokenA, deviceA);
    }

    private Task<App> NotConnectedAsync()
    {
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        document.Wasla.AgentToken = string.Empty;
        document.PrintBridge.InstallationId = _installation.ToString("D");
        document.PrintBridge.DryRun = true;
        store.Save(document);
        return Task.FromResult(App.Load(_handler, _logs));
    }

    private void AssertNoSecretsLogged(params string[] secrets)
    {
        var text = _logs.Text();
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        var pending = Assert.Single(_handler.Bodies, b => b.Path == TransferPath).Body;
        Assert.DoesNotContain(pending, text, StringComparison.Ordinal);
    }

    /// <summary>The app's own setup pieces over the isolated settings, as at a start of Print Bridge.</summary>
    private sealed record App(
        PrintBridgeSettingsStore Store,
        PrintBridgeSettingsHolder Holder,
        PrintBridgeAutoSetupCoordinator Coordinator,
        TestGuard Guard)
    {
        /// <param name="overLoopback">
        /// Setup requests go through the production setup factory (a real handler) to loopback servers, trusted by name;
        /// otherwise straight to <paramref name="handler"/> under the production policy.
        /// </param>
        public static App Load(PrintBridgeTransferHandler handler, CapturingLogs logs, bool overLoopback = false)
        {
            var store = new PrintBridgeSettingsStore();
            var document = store.Load();
            var holder = new PrintBridgeSettingsHolder();
            holder.Replace(document.Wasla, document.PrintBridge, document.Ui);
            var client = new WaslaPrintBridgeClient(
                new HttpClient(handler, disposeHandler: false),
                holder,
                overLoopback ? new PrintBridgeSetupHttpClientFactory() : new PrintBridgeSetupHttpClientFactory(handler),
                logs.CreateLogger<WaslaPrintBridgeClient>(),
                "1.0.0.0");
            var guard = new TestGuard();
            var coordinator = new PrintBridgeAutoSetupCoordinator(
                client,
                store,
                holder,
                guard,
                overLoopback ? PrintBridgeServerTrustPolicy.ForTests("localhost") : PrintBridgeServerTrustPolicy.Production,
                logs.CreateLogger<PrintBridgeAutoSetupCoordinator>())
            {
                TransferRetryDelays = [TimeSpan.Zero, TimeSpan.Zero]
            };
            return new App(store, holder, coordinator, guard);
        }
    }

    /// <summary>The native confirmation's stand-in: records what it was shown and answers as told.</summary>
    private sealed class Consent(bool approve)
    {
        public List<PrintBridgeConnectionTransferConsentRequest> Requests { get; } = [];

        /// <summary>Runs while the question is open, as another person or process acting meanwhile.</summary>
        public Func<Task>? WhileAsking { get; init; }

        public async Task<bool> Ask(PrintBridgeConnectionTransferConsentRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (WhileAsking is { } act)
                await act();
            return approve;
        }
    }

    /// <summary>The WAS-58 connection guard: grants leases until <see cref="RefuseFromLease"/>.</summary>
    private sealed class TestGuard : IPrintBridgeConnectionGuard
    {
        private int _leases;

        public int RefuseFromLease { get; set; } = int.MaxValue;

        public IDisposable? TryBeginConnectionChange() =>
            Interlocked.Increment(ref _leases) >= RefuseFromLease ? null : new Lease();

        private sealed class Lease : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>Keeps every log message and exception text.</summary>
    private sealed class CapturingLogs : ILoggerFactory, ILogger
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public string Text() => string.Join(Environment.NewLine, _entries);

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue($"{formatter(state, exception)} {state} {exception}");

        public void Dispose()
        {
        }
    }
}
