using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Printing;
using Wasla.Infrastructure.Security;
using static Wasla.PrintBridge.Tests.PrintBridgeTransferBackend;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-91, server side: moving one Print Bridge installation from restaurant A to restaurant B with the real
/// <c>PrintBridgeSetupSessionService</c> over SQLite and fake credentials. A move needs A's own valid device token and
/// B's unexpired, unused setup code for the link's server, is bound to that installation and device, retires A's device
/// without deleting it, and can be repeated identically. The ordinary exchange no longer takes a computer still
/// registered to another restaurant. Concurrency on SQL Server: <c>PrintBridgeTransferSqlServerTests</c>.
/// </summary>
public sealed class PrintBridgeTransferServiceTests : IDisposable
{
    private readonly PrintBridgeTransferBackend _backend = new();
    private readonly Guid _installation = Guid.NewGuid();

    public void Dispose() => _backend.Dispose();

    [Fact]
    public async Task Preview_ReturnsTheVerifiedNames_AndUsesNothing()
    {
        var (deviceA, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        var deviceBefore = await _backend.DeviceAsync(deviceA);

        var preview = await PreviewAsync(Request(tokenA, code));

        Assert.Equal(PrintBridgeTransferOutcome.Ready, preview.Outcome);
        Assert.Equal((NameA, NameB, UrlB), (preview.CurrentTenantName, preview.TargetTenantName, preview.TargetServerUrl));

        // Read-only: the code is unused, no device was created, changed or seen.
        var session = await _backend.SessionAsync(code);
        Assert.Null(session.ExchangedAtUtc);
        Assert.Null(session.PrintBridgeDeviceId);
        var deviceAfter = await _backend.DeviceAsync(deviceA);
        Assert.Equal(
            (deviceBefore.TokenHash, deviceBefore.IsActive, deviceBefore.RemovedAtUtc, deviceBefore.LastSeenAt, deviceBefore.UpdatedAt),
            (deviceAfter.TokenHash, deviceAfter.IsActive, deviceAfter.RemovedAtUtc, deviceAfter.LastSeenAt, deviceAfter.UpdatedAt));
        Assert.Equal(1, await _backend.DeviceCountAsync());
    }

    [Fact]
    public async Task Preview_OfTheDevicesOwnRestaurant_IsSameTenant()
    {
        var (deviceA, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantAId, UrlA, PrintBridgeSetupMode.ReconnectExistingDevice, deviceA);

        Assert.Equal(PrintBridgeTransferOutcome.SameTenant, (await PreviewAsync(Request(tokenA, code, UrlA))).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.SameTenant, (await TransferAsync(Request(tokenA, code, UrlA))).Outcome);
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
    }

    [Fact]
    public async Task Transfer_ToANewDevice_RetiresTheSource_KeepsItsRecord_AndFreesItsQuota()
    {
        var (deviceA, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Anadolu kitchen", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        var request = Request(tokenA, code);

        var result = await TransferAsync(request);

        Assert.Equal(PrintBridgeTransferOutcome.Ready, result.Outcome);
        Assert.Equal(UrlB, result.ServerUrl);
        Assert.Equal(_installation, result.InstallationId);

        // The source keeps its record (and A keeps its print history), retired with a time and out of the quota.
        var source = await _backend.DeviceAsync(deviceA);
        Assert.NotNull(source.RemovedAtUtc);
        Assert.False(source.IsActive);
        Assert.Equal(TenantAId, source.TenantId);
        Assert.Equal("Anadolu kitchen", source.Name);
        await using (var db = _backend.OpenDb())
        {
            var devicesA = _backend.Devices(db);
            Assert.Empty(await devicesA.ListDevicesAsync(TenantAId, CancellationToken.None));
            Assert.Equal(0, (await devicesA.GetDeviceQuotaAsync(TenantAId, CancellationToken.None)).ActiveDeviceCount);
            Assert.Equal(1, (await devicesA.GetDeviceQuotaAsync(TenantBId, CancellationToken.None)).ActiveDeviceCount);
        }

        // The old token stops working at once; the app-generated token now authenticates for B only.
        Assert.False((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
        var auth = await _backend.AuthenticateAsync(request.NewDeviceToken!, _installation);
        Assert.True(auth.Succeeded);
        Assert.Equal((TenantBId, NameB), (auth.Context!.TenantId, auth.Context.TenantName));

        // B's device is named after the machine, never after A's name for it.
        var destination = await _backend.DeviceAsync(auth.Context.DeviceId);
        Assert.Equal("QA-PC", destination.Name);
        Assert.Equal(_installation, destination.InstallationId);

        var session = await _backend.SessionAsync(code);
        Assert.NotNull(session.ExchangedAtUtc);
        Assert.Equal(deviceA, session.SourcePrintBridgeDeviceId);
        Assert.Equal(destination.Id, session.PrintBridgeDeviceId);
        Assert.Equal(PrintBridgeTokenHasher.HashToken(tokenA), session.SourceTokenHash);
        Assert.Single(await _backend.DevicesWithAsync(_installation), d => d.RemovedAtUtc == null);
    }

    [Fact]
    public async Task Transfer_ToAReconnectDevice_BindsThatDevice_AndKeepsTheDestinationQuota()
    {
        var (deviceA, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var (deviceB, _) = await _backend.CreateDeviceAsync(TenantBId, "Bahçe counter", installationId: null);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB, PrintBridgeSetupMode.ReconnectExistingDevice, deviceB);
        var request = Request(tokenA, code);

        var result = await TransferAsync(request);

        Assert.Equal((PrintBridgeTransferOutcome.Ready, "Bahçe counter"), (result.Outcome, result.DeviceName));
        var bound = await _backend.DeviceAsync(deviceB);
        Assert.Equal(_installation, bound.InstallationId);
        Assert.Equal(PrintBridgeTokenHasher.HashToken(request.NewDeviceToken!), bound.TokenHash);
        Assert.NotNull((await _backend.DeviceAsync(deviceA)).RemovedAtUtc);
        Assert.Equal(2, await _backend.DeviceCountAsync());
    }

    [Fact]
    public async Task Transfer_RepeatedIdentically_AnswersTheSame_AndChangesNothing()
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        var request = Request(tokenA, code);
        var first = await TransferAsync(request);
        var stored = await Snapshot();

        // A lost answer, a restart or a failed settings write: the app repeats the identical request.
        var repeated = await TransferAsync(request);

        Assert.Equal(first, repeated);
        Assert.Equal(stored, await Snapshot());

        // Anything but the identical request is refused and changes nothing either.
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(request with { NewDeviceToken = Secret() })).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(request with { CompletionCredential = Secret() })).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(request with { SourceDeviceToken = Secret() })).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(request with { InstallationId = Guid.NewGuid() })).Outcome);
        Assert.Equal(stored, await Snapshot());
    }

    [Fact]
    public async Task Completion_ForgetsTheRetiredToken_SoTheMoveCanNoLongerBeRepeated()
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        var request = Request(tokenA, code);
        var result = await TransferAsync(request);

        await using (var db = _backend.OpenDb())
            Assert.True(await _backend.SetupService(db).CompleteAsync(result.SessionId!.Value, request.CompletionCredential!, true, CancellationToken.None));

        var session = await _backend.SessionAsync(code);
        Assert.NotNull(session.CompletedAtUtc);
        Assert.Null(session.SourceTokenHash);
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(request)).Outcome);
    }

    [Fact]
    public async Task Transfer_AfterTheRecoveryWindow_IsNoLongerRepeated()
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        var request = Request(tokenA, code);
        Assert.Equal(PrintBridgeTransferOutcome.Ready, (await TransferAsync(request)).Outcome);

        await using (var db = _backend.OpenDb())
        {
            var hash = PrintBridgeSetupCode.Hash(code);
            var session = await db.PrintBridgeSetupSessions.SingleAsync(s => s.CodeHash == hash, TestContext.Current.CancellationToken);
            session.ExchangedAtUtc = DateTime.UtcNow - Wasla.Infrastructure.Services.PrintBridgeSetupSessionService.TransferRecoveryWindow - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(request)).Outcome);
    }

    [Theory]
    [InlineData("unknown-token")]
    [InlineData("removed")]
    [InlineData("disabled")]
    [InlineData("other-installation")]
    [InlineData("no-token")]
    [InlineData("unbound-device")]
    public async Task Transfer_WithoutTheSourcesOwnValidCredential_IsRefused_AndChangesNothing(string source)
    {
        var (deviceA, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        var request = Request(tokenA, code);

        switch (source)
        {
            case "unknown-token":
                request = request with { SourceDeviceToken = Secret() };
                break;
            case "removed":
                await using (var db = _backend.OpenDb())
                    await _backend.Devices(db).RemoveDeviceAsync(TenantAId, deviceA, CancellationToken.None);
                break;
            case "disabled":
                await using (var db = _backend.OpenDb())
                    await _backend.Devices(db).SetDeviceActiveAsync(TenantAId, deviceA, false, CancellationToken.None);
                break;
            case "other-installation":
                // A different InstallationId never stands in for the bound one (no bypass by a new identity).
                request = request with { InstallationId = Guid.NewGuid() };
                break;
            case "no-token":
                // The installation id and machine name alone are not authorization.
                request = request with { SourceDeviceToken = string.Empty };
                break;
            case "unbound-device":
            {
                var (_, unboundToken) = await _backend.CreateDeviceAsync(TenantAId, "Legacy PC", installationId: null);
                request = request with { SourceDeviceToken = unboundToken };
                break;
            }
        }

        var before = await Snapshot();

        Assert.Equal(PrintBridgeTransferOutcome.SourceRejected, (await PreviewAsync(request)).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.SourceRejected, (await TransferAsync(request)).Outcome);
        Assert.Equal(before, await Snapshot());
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
    }

    [Fact]
    public async Task Transfer_WithAnExpiredUsedOrUnknownCode_IsRefused()
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);

        var expired = await _backend.CreateSessionAsync(TenantBId, UrlB);
        await _backend.ExpireSessionAsync(expired);
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await PreviewAsync(Request(tokenA, expired))).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(Request(tokenA, expired))).Outcome);

        // A code another computer already exchanged cannot be replayed as a move.
        var used = await _backend.CreateSessionAsync(TenantBId, UrlB);
        await using (var db = _backend.OpenDb())
            Assert.NotNull(await _backend.SetupService(db).ExchangeAsync(used, new PrintBridgeSetupClientInfo("Other-PC", null, null, Guid.NewGuid()), CancellationToken.None));
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(Request(tokenA, used))).Outcome);

        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(Request(tokenA, PrintBridgeSetupCode.Generate()))).Outcome);
        Assert.Null((await _backend.DevicesWithAsync(_installation)).Single().RemovedAtUtc);
    }

    [Theory]
    [InlineData("https://cinar.app.wasla.tr/")]
    [InlineData("http://bahce.app.wasla.tr/")]
    [InlineData("https://bahce.app.wasla.tr:8443/")]
    [InlineData("https://bahce.app.wasla.tr.evil.example/")]
    [InlineData("")]
    public async Task Transfer_ForALinkWhoseServerIsNotTheSessions_IsRefused(string linkServer)
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        var request = Request(tokenA, code, linkServer);

        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await PreviewAsync(request)).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(request)).Outcome);
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
    }

    [Fact]
    public async Task Transfer_IntoAFullRestaurant_IsRefused_AndTheCodeStaysUsable()
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        var full = new List<Guid>();
        for (var i = 0; i < PrintBridgeDeviceLimits.AllowedActiveDeviceCount; i++)
            full.Add((await _backend.CreateDeviceAsync(TenantBId, $"Bahçe {i}", Guid.NewGuid())).DeviceId);
        var before = await Snapshot();

        Assert.Equal(PrintBridgeTransferOutcome.DeviceLimitReached, (await PreviewAsync(Request(tokenA, code))).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.DeviceLimitReached, (await TransferAsync(Request(tokenA, code))).Outcome);
        Assert.Equal(before, await Snapshot());

        // B frees a place; the same link then works.
        await using (var db = _backend.OpenDb())
            await _backend.Devices(db).RemoveDeviceAsync(TenantBId, full[0], CancellationToken.None);
        Assert.Equal(PrintBridgeTransferOutcome.Ready, (await TransferAsync(Request(tokenA, code))).Outcome);
    }

    [Fact]
    public async Task Transfer_WhileTheSourceTenantStillPrintsForThisComputer_IsRefused()
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        _backend.PrintingInSourceTenant = true;
        var before = await Snapshot();

        Assert.Equal(PrintBridgeTransferOutcome.PrintingInProgress, (await PreviewAsync(Request(tokenA, code))).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.PrintingInProgress, (await TransferAsync(Request(tokenA, code))).Outcome);
        Assert.Equal(before, await Snapshot());

        _backend.PrintingInSourceTenant = false;
        Assert.Equal(PrintBridgeTransferOutcome.Ready, (await TransferAsync(Request(tokenA, code))).Outcome);
    }

    [Fact]
    public async Task Transfer_OfAComputerAlsoRegisteredToAThirdRestaurant_IsRefused()
    {
        // A duplicate registration left from before WAS-91: the move may retire only the device whose token it has.
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var (deviceC, _) = await _backend.CreateDeviceAsync(TenantCId, "Çınar PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);

        Assert.Equal(PrintBridgeTransferOutcome.InstallationConflict, (await TransferAsync(Request(tokenA, code))).Outcome);
        Assert.Null((await _backend.DeviceAsync(deviceC)).RemovedAtUtc);
        Assert.Equal(2, (await _backend.DevicesWithAsync(_installation)).Count(d => d.RemovedAtUtc == null));
    }

    [Fact]
    public async Task Transfer_ToAReconnectDeviceBoundToAnotherComputer_IsRefused()
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var (deviceB, _) = await _backend.CreateDeviceAsync(TenantBId, "Bahçe counter", Guid.NewGuid());
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB, PrintBridgeSetupMode.ReconnectExistingDevice, deviceB);

        Assert.Equal(PrintBridgeTransferOutcome.InstallationConflict, (await TransferAsync(Request(tokenA, code))).Outcome);
    }

    [Fact]
    public async Task Transfer_ToAReconnectDeviceTheDestinationTurnedOff_IsRefused_AndTheSourceKeepsPrinting()
    {
        var (deviceA, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var (deviceB, _) = await _backend.CreateDeviceAsync(TenantBId, "Bahçe counter", installationId: null);
        await using (var db = _backend.OpenDb())
            await _backend.Devices(db).SetDeviceActiveAsync(TenantBId, deviceB, false, CancellationToken.None);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB, PrintBridgeSetupMode.ReconnectExistingDevice, deviceB);

        Assert.Equal(PrintBridgeTransferOutcome.DestinationDeviceDisabled, (await PreviewAsync(Request(tokenA, code))).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.DestinationDeviceDisabled, (await TransferAsync(Request(tokenA, code))).Outcome);
        Assert.Null((await _backend.DeviceAsync(deviceA)).RemovedAtUtc);
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
    }

    [Fact]
    public async Task ARepeatThatLooked_BeforeTheFirstRequestStoredTheMove_IsAnsweredAsTheRepeat()
    {
        // Review finding: the app repeats while its first request is still on the server. The repeat finds no stored move,
        // then the first one commits before the repeat's own checks run: they now see the code used and the source retired.
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);
        var request = Request(tokenA, code);
        PrintBridgeTransferResult? first = null;
        var commitFirstMeanwhile = new RunOnceBeforeCommand(async () => first = await TransferAsync(request), commandNumber: 2);

        PrintBridgeTransferResult repeat;
        await using (var db = _backend.OpenDb(commitFirstMeanwhile))
            repeat = await _backend.SetupService(db).TransferAsync(request, CancellationToken.None);

        Assert.True(commitFirstMeanwhile.Ran);
        Assert.Equal(PrintBridgeTransferOutcome.Ready, first!.Outcome);
        Assert.Equal(first, repeat);
        Assert.Single(await _backend.DevicesWithAsync(_installation), d => d.RemovedAtUtc == null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has spaces and is long enough to pass the length check of course")]
    public async Task Transfer_WithAMalformedNewToken_IsRefused(string newToken)
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);

        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(Request(tokenA, code) with { NewDeviceToken = newToken })).Outcome);
        Assert.Equal(PrintBridgeTransferOutcome.InvalidOrExpired, (await TransferAsync(Request(tokenA, code) with { NewDeviceToken = tokenA })).Outcome);
        Assert.Null((await _backend.SessionAsync(code)).ExchangedAtUtc);
    }

    [Fact]
    public async Task OrdinaryNewDeviceExchange_ForAComputerRegisteredToAnotherRestaurant_IsRefused()
    {
        var (deviceA, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);

        await using (var db = _backend.OpenDb())
        {
            // The same InstallationId and machine name as the registered computer, but no proof: refused.
            await Assert.ThrowsAsync<PrintBridgeSetupInstallationRegisteredToAnotherTenantException>(() =>
                _backend.SetupService(db).ExchangeAsync(code, new PrintBridgeSetupClientInfo("QA-PC", null, null, _installation), CancellationToken.None));
        }

        var session = await _backend.SessionAsync(code);
        Assert.Equal(PrintBridgeSetupFailureReasons.InstallationRegisteredToAnotherTenant, session.FailureReason);
        Assert.Equal(1, await _backend.DeviceCountAsync());
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
        Assert.Null((await _backend.DeviceAsync(deviceA)).RemovedAtUtc);
    }

    [Fact]
    public async Task OrdinaryReconnectExchange_ForAComputerRegisteredToAnotherRestaurant_IsRefused()
    {
        var (_, tokenA) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        var (deviceB, _) = await _backend.CreateDeviceAsync(TenantBId, "Bahçe counter", installationId: null);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB, PrintBridgeSetupMode.ReconnectExistingDevice, deviceB);
        var tokenHashB = (await _backend.DeviceAsync(deviceB)).TokenHash;

        await using (var db = _backend.OpenDb())
        {
            await Assert.ThrowsAsync<PrintBridgeSetupInstallationRegisteredToAnotherTenantException>(() =>
                _backend.SetupService(db).ExchangeAsync(code, new PrintBridgeSetupClientInfo("QA-PC", null, null, _installation), CancellationToken.None));
        }

        var bound = await _backend.DeviceAsync(deviceB);
        Assert.Null(bound.InstallationId);
        Assert.Equal(tokenHashB, bound.TokenHash);
        Assert.True((await _backend.AuthenticateAsync(tokenA, _installation)).Succeeded);
    }

    [Fact]
    public async Task OrdinaryExchange_AfterTheOtherRestaurantRemovedTheDevice_IsAllowed()
    {
        var (deviceA, _) = await _backend.CreateDeviceAsync(TenantAId, "Kitchen PC", _installation);
        await using (var db = _backend.OpenDb())
            await _backend.Devices(db).RemoveDeviceAsync(TenantAId, deviceA, CancellationToken.None);
        var code = await _backend.CreateSessionAsync(TenantBId, UrlB);

        await using (var db = _backend.OpenDb())
        {
            var result = await _backend.SetupService(db).ExchangeAsync(code, new PrintBridgeSetupClientInfo("QA-PC", null, null, _installation), CancellationToken.None);
            Assert.NotNull(result);
        }
    }

    // Helpers ------------------------------------------------------------------------------------

    private Guid TenantAId => _backend.TenantA;

    private Guid TenantBId => _backend.TenantB;

    private Guid TenantCId => _backend.TenantC;

    private PrintBridgeTransferRequest Request(string sourceToken, string code, string serverUrl = UrlB) =>
        new(sourceToken, _installation, code, serverUrl, Secret(), Secret(), "QA-PC", "1.0.0.0", "QA printer");

    private static string Secret() => Wasla.PrintBridge.Models.PrintBridgePendingConnectionChange.GenerateSecret();

    private async Task<PrintBridgeTransferPreview> PreviewAsync(PrintBridgeTransferRequest request)
    {
        await using var db = _backend.OpenDb();
        return await _backend.SetupService(db).PreviewTransferAsync(request, CancellationToken.None);
    }

    private async Task<PrintBridgeTransferResult> TransferAsync(PrintBridgeTransferRequest request)
    {
        await using var db = _backend.OpenDb();
        return await _backend.SetupService(db).TransferAsync(request, CancellationToken.None);
    }

    /// <summary>
    /// Runs <paramref name="run"/> once, just before the context's <paramref name="commandNumber"/>-th database command
    /// executes: another request committing in that window.
    /// </summary>
    private sealed class RunOnceBeforeCommand(Func<Task> run, int commandNumber) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _commands;

        public bool Ran { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (++_commands == commandNumber && !Ran)
            {
                Ran = true;
                await run();
            }

            return result;
        }
    }

    /// <summary>Every device and session row that a move could change, as comparable text.</summary>
    private async Task<string> Snapshot()
    {
        await using var db = _backend.OpenDb();
        var devices = (await db.PrintBridgeDevices.AsNoTracking().ToListAsync())
            .OrderBy(d => d.Id)
            .Select(d => $"{d.Id}|{d.TenantId}|{d.TokenHash}|{d.IsActive}|{d.RemovedAtUtc:O}|{d.InstallationId}|{d.Name}");
        var sessions = (await db.PrintBridgeSetupSessions.AsNoTracking().ToListAsync())
            .OrderBy(s => s.Id)
            .Select(s => $"{s.Id}|{s.ExchangedAtUtc:O}|{s.CompletedAtUtc:O}|{s.FailedAtUtc:O}|{s.PrintBridgeDeviceId}|{s.SourcePrintBridgeDeviceId}|{s.SourceTokenHash}|{s.CompletionCredentialHash}");
        return string.Join("\n", devices.Concat(sessions));
    }
}
