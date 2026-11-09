using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Billing;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// WAS-91 on a real SQL Server (a throwaway CentralDb, see <see cref="SqlServerCentralDatabase"/>): moves of one Print
/// Bridge installation racing each other and the ordinary exchange. Every caller has its own DbContext and connection
/// pool, as separate requests or Web instances would, and opens its connection before one start gate releases them all
/// (<see cref="PrintBridgeSqlServerRace"/>). The installation and tenant application locks (<c>sp_getapplock</c>) and the
/// serializable transaction must leave exactly one registration, one retired source and one used code. Two moves for a
/// restaurant's last device place are held so that one waits for the other's tenant lock. SQLite runs no application
/// locks, so these races are only meaningful here.
/// </summary>
public sealed class PrintBridgeTransferSqlServerTests : IAsyncLifetime
{
    private const int Rounds = 5;

    /// <summary>
    /// Other restaurants' devices, enough for SQL Server to change its plans: on a nearly empty table the registration
    /// read goes through another index, and the active-device count scans the clustered index.
    /// </summary>
    private const int ManyDevices = 300;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Unexpected failures logged by the services, shown in an assertion's message.</summary>
    private static readonly ErrorLog Errors = new();
    private readonly SqlServerCentralDatabase _database = new();
    private readonly PrintBridgeSqlServerRace _race;

    public PrintBridgeTransferSqlServerTests() => _race = new PrintBridgeSqlServerRace(_database);

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task TwoMovesOfOneComputerToTwoRestaurants_AtOnce_MoveItExactlyOnce()
    {
        for (var round = 0; round < Rounds; round++)
        {
            // Fresh restaurants each round, so earlier rounds never fill a device limit.
            var (tenantA, tenantB, tenantC) = await TenantsAsync();
            var installation = Guid.NewGuid();
            var (sourceId, sourceToken) = await CreateDeviceAsync(tenantA, installation);
            var codeB = await CreateSessionAsync(tenantB, "https://bahce.app.wasla.tr/");
            var codeC = await CreateSessionAsync(tenantC, "https://cinar.app.wasla.tr/");

            var results = await RaceAsync(
                db => Service(db).TransferAsync(Request(sourceToken, installation, codeB, "https://bahce.app.wasla.tr/"), CancellationToken.None),
                db => Service(db).TransferAsync(Request(sourceToken, installation, codeC, "https://cinar.app.wasla.tr/"), CancellationToken.None));

            // The loser is refused either way. Under the locks its source is gone (SourceRejected); in its check before the
            // locks it can read the source just before the winner commits and the winner's new registration just after
            // (InstallationConflict). A deadlock or any other failure is not a refusal and fails the test.
            Assert.True(
                results.Count(r => r.Outcome == PrintBridgeTransferOutcome.Ready) == 1
                    && results.Count(r => r.Outcome is PrintBridgeTransferOutcome.SourceRejected or PrintBridgeTransferOutcome.InstallationConflict) == 1,
                $"Round {round}: {string.Join(", ", results.Select(r => r.Outcome))}; errors: {Errors.Text()}");

            await using var db = _database.Open("Inspector");
            var registered = await db.PrintBridgeDevices.AsNoTracking().Where(d => d.InstallationId == installation).ToListAsync(Ct);
            Assert.Single(registered, d => d.RemovedAtUtc == null);
            Assert.NotNull(registered.Single(d => d.Id == sourceId).RemovedAtUtc);
            var exchanged = await db.PrintBridgeSetupSessions.AsNoTracking()
                .Where(s => s.CodeHash == PrintBridgeSetupCode.Hash(codeB) || s.CodeHash == PrintBridgeSetupCode.Hash(codeC))
                .CountAsync(s => s.ExchangedAtUtc != null, Ct);
            Assert.Equal(1, exchanged);
        }
    }

    [Fact]
    public async Task AMoveAndAnOrdinaryExchangeOfTheSameCode_AtOnce_UseItExactlyOnce()
    {
        for (var round = 0; round < Rounds; round++)
        {
            // Fresh restaurants each round, so earlier rounds never fill a device limit.
            var (tenantA, tenantB, _) = await TenantsAsync();
            var movedInstallation = Guid.NewGuid();
            var otherInstallation = Guid.NewGuid();
            var (sourceId, sourceToken) = await CreateDeviceAsync(tenantA, movedInstallation);
            var code = await CreateSessionAsync(tenantB, "https://bahce.app.wasla.tr/");

            PrintBridgeTransferResult? moved = null;
            PrintBridgeSetupExchangeResult? exchanged = null;
            await RaceAsync(
                async db =>
                {
                    moved = await Service(db).TransferAsync(Request(sourceToken, movedInstallation, code, "https://bahce.app.wasla.tr/"), CancellationToken.None);
                    return true;
                },
                async db =>
                {
                    exchanged = await Service(db).ExchangeAsync(code, new PrintBridgeSetupClientInfo("Other-PC", null, null, otherInstallation), CancellationToken.None);
                    return true;
                });

            Assert.True((moved!.Outcome == PrintBridgeTransferOutcome.Ready) ^ (exchanged is not null), $"Round {round}: moved {moved.Outcome}, exchanged {exchanged is not null}; errors: {Errors.Text()}");

            await using var db = _database.Open("Inspector");
            var source = await db.PrintBridgeDevices.AsNoTracking().SingleAsync(d => d.Id == sourceId, Ct);
            Assert.Equal(moved.Outcome == PrintBridgeTransferOutcome.Ready, source.RemovedAtUtc != null);
            Assert.Equal(1, await db.PrintBridgeDevices.CountAsync(d => d.TenantId == tenantB && (d.InstallationId == movedInstallation || d.InstallationId == otherInstallation), Ct));
        }
    }

    [Fact]
    public async Task TheSameMoveSentTwiceAtOnce_AnswersBothTheSame_AndStoresItOnce()
    {
        // The app repeats a request whose answer it has not had yet, while the first is still on the server.
        for (var round = 0; round < Rounds; round++)
        {
            // Fresh restaurants each round, so earlier rounds never fill a device limit.
            var (tenantA, tenantB, _) = await TenantsAsync();
            var installation = Guid.NewGuid();
            var (_, sourceToken) = await CreateDeviceAsync(tenantA, installation);
            var code = await CreateSessionAsync(tenantB, "https://bahce.app.wasla.tr/");
            var request = Request(sourceToken, installation, code, "https://bahce.app.wasla.tr/");

            var results = await RaceAsync(
                db => Service(db).TransferAsync(request, CancellationToken.None),
                db => Service(db).TransferAsync(request, CancellationToken.None));

            Assert.True(results.All(r => r.Outcome == PrintBridgeTransferOutcome.Ready), $"Round {round}: {string.Join(", ", results.Select(r => r.Outcome))}; errors: {Errors.Text()}");
            Assert.Equal(results[0], results[1]);

            await using var db = _database.Open("Inspector");
            Assert.Equal(1, await db.PrintBridgeDevices.CountAsync(d => d.InstallationId == installation && d.RemovedAtUtc == null, Ct));
            Assert.Equal(2, await db.PrintBridgeDevices.CountAsync(d => d.InstallationId == installation, Ct));
        }
    }

    [Fact]
    public async Task TwoMovesIntoARestaurantWithOnePlaceLeft_OneWaitingForTheOther_FillItOnce()
    {
        var (tenantA, _, tenantC) = await TenantsAsync();
        var full = Seed(_database.Open("Seed"), "dolu", "Dolu Lokanta", save: true);
        for (var i = 0; i < PrintBridgeDeviceLimits.AllowedActiveDeviceCount - 1; i++)
            await CreateDeviceAsync(full, Guid.NewGuid());

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var (_, firstToken) = await CreateDeviceAsync(tenantA, first);
        var (_, secondToken) = await CreateDeviceAsync(tenantC, second);
        var firstCode = await CreateSessionAsync(full, "https://dolu.app.wasla.tr/");
        var secondCode = await CreateSessionAsync(full, "https://dolu.app.wasla.tr/");

        // The first move is held right before its device write, holding the restaurant's tenant lock and having counted
        // one place left; it goes on only once the second is seen waiting for that lock.
        var (held, waited) = await _race.ContendAsync(
            db => Service(db).TransferAsync(Request(firstToken, first, firstCode, "https://dolu.app.wasla.tr/"), CancellationToken.None),
            db => Service(db).TransferAsync(Request(secondToken, second, secondCode, "https://dolu.app.wasla.tr/"), CancellationToken.None));

        Assert.True(
            (held.Outcome, waited.Outcome) == (PrintBridgeTransferOutcome.Ready, PrintBridgeTransferOutcome.DeviceLimitReached),
            $"{held.Outcome}, {waited.Outcome}; errors: {Errors.Text()}");
        await using var db = _database.Open("Inspector");
        Assert.Equal(PrintBridgeDeviceLimits.AllowedActiveDeviceCount, await db.PrintBridgeDevices.CountAsync(d => d.TenantId == full && d.IsActive && d.RemovedAtUtc == null, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ManyDevices)]
    public async Task TheRegistrationCheckOfAMove_WhileTheSourceIsRetired_WaitsInsteadOfDeadlocking(int otherDevices)
    {
        var move = await ArrangeMoveAsync(otherDevices);

        // Another move of this computer retires the source: the row, then the RemovedAtUtc index key.
        var result = await _race.ReadDuringTwoStepDeviceWriteAsync(
            move.SourceId,
            db => db.Database.ExecuteSqlAsync(
                $"UPDATE [PrintBridgeDevices] SET [RemovedAtUtc] = SYSUTCDATETIME(), [IsActive] = 0 WHERE [Id] = {move.SourceId}",
                Ct),
            command => PrintBridgeSqlServerRace.IsLockingDeviceReadBy(command, "InstallationId"),
            db => Service(db).TransferAsync(move.Request, CancellationToken.None));

        Assert.True(result.Outcome == PrintBridgeTransferOutcome.SourceRejected, $"{result.Outcome}; errors: {Errors.Text()}");
        await AssertDidNotMoveAsync(move);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ManyDevices)]
    public async Task TheSourceTokenCheckOfAMove_WhileTheTokenIsRevoked_WaitsInsteadOfDeadlocking(int otherDevices)
    {
        var move = await ArrangeMoveAsync(otherDevices);
        var replacement = PrintBridgeTokenHasher.HashToken(PrintBridgeTokenHasher.GenerateRawToken());

        // Another move of this computer revokes the source token: the row, then the TokenHash index key.
        var result = await _race.ReadDuringTwoStepDeviceWriteAsync(
            move.SourceId,
            db => db.Database.ExecuteSqlAsync(
                $"UPDATE [PrintBridgeDevices] SET [TokenHash] = {replacement} WHERE [Id] = {move.SourceId}",
                Ct),
            readFrom: null,
            db => Service(db).TransferAsync(move.Request, CancellationToken.None));

        Assert.True(result.Outcome == PrintBridgeTransferOutcome.SourceRejected, $"{result.Outcome}; errors: {Errors.Text()}");
        await AssertDidNotMoveAsync(move);
    }

    [Fact]
    public async Task TheDeviceCountOfAMove_WhileADeviceOfTheDestinationIsRemoved_WaitsInsteadOfDeadlocking()
    {
        var move = await ArrangeMoveAsync(ManyDevices);
        var (leavingId, _) = await CreateDeviceAsync(move.Destination, Guid.NewGuid());

        var result = await _race.ReadDuringTwoStepDeviceWriteAsync(
            leavingId,
            db => db.Database.ExecuteSqlAsync(
                $"UPDATE [PrintBridgeDevices] SET [RemovedAtUtc] = SYSUTCDATETIME(), [IsActive] = 0 WHERE [Id] = {leavingId}",
                Ct),
            command => PrintBridgeSqlServerRace.IsLockingDeviceReadBy(command, "TenantId"),
            db => Service(db).TransferAsync(move.Request, CancellationToken.None));

        Assert.True(result.Outcome == PrintBridgeTransferOutcome.Ready, $"{result.Outcome}; errors: {Errors.Text()}");
        await using var db = _database.Open("Inspector");
        Assert.Equal(1, await db.PrintBridgeDevices.CountAsync(d => d.InstallationId == move.Installation && d.RemovedAtUtc == null && d.TenantId == move.Destination, Ct));
    }

    [Fact]
    public async Task TwoMovesOfOneComputer_RepeatedAfterTheRace_KeepTheWinnerAndRefuseTheLoser()
    {
        // After a lost answer the app repeats its request. The winner must get the same move back, for as long as it is
        // recoverable; the loser's source token is revoked, so it stays refused and changes nothing.
        for (var round = 0; round < Rounds; round++)
        {
            var (tenantA, tenantB, tenantC) = await TenantsAsync();
            var installation = Guid.NewGuid();
            var (sourceId, sourceToken) = await CreateDeviceAsync(tenantA, installation);
            var requestB = Request(sourceToken, installation, await CreateSessionAsync(tenantB, "https://bahce.app.wasla.tr/"), "https://bahce.app.wasla.tr/");
            var requestC = Request(sourceToken, installation, await CreateSessionAsync(tenantC, "https://cinar.app.wasla.tr/"), "https://cinar.app.wasla.tr/");

            var results = await RaceAsync(
                db => Service(db).TransferAsync(requestB, CancellationToken.None),
                db => Service(db).TransferAsync(requestC, CancellationToken.None));
            Assert.True(results.Count(r => r.Outcome == PrintBridgeTransferOutcome.Ready) == 1, $"Round {round}: {string.Join(", ", results.Select(r => r.Outcome))}; errors: {Errors.Text()}");

            var winnerIsB = results[0].Outcome == PrintBridgeTransferOutcome.Ready;
            var (winner, winnerRequest, loserRequest, destination) = winnerIsB
                ? (results[0], requestB, requestC, tenantB)
                : (results[1], requestC, requestB, tenantC);

            await using (var db = _database.Open("Repeater"))
            {
                Assert.Equal(winner, await Service(db).TransferAsync(winnerRequest, Ct));
                Assert.Equal(PrintBridgeTransferOutcome.SourceRejected, (await Service(db).TransferAsync(loserRequest, Ct)).Outcome);
            }

            await using var check = _database.Open("Inspector");
            var registered = await check.PrintBridgeDevices.AsNoTracking().Where(d => d.InstallationId == installation).ToListAsync(Ct);
            Assert.Equal(2, registered.Count);
            var active = Assert.Single(registered, d => d.RemovedAtUtc == null);
            Assert.Equal(destination, active.TenantId);
            Assert.True(active.IsActive);
            Assert.Equal(PrintBridgeTokenHasher.HashToken(winnerRequest.NewDeviceToken!), active.TokenHash);
            var source = registered.Single(d => d.Id == sourceId);
            Assert.False(source.IsActive);
            Assert.NotEqual(PrintBridgeTokenHasher.HashToken(sourceToken), source.TokenHash);
        }
    }

    [Fact]
    public async Task TheRestaurantAMoveLeft_CanStillBeDeleted_AndTheMovesRecordStays()
    {
        // Review finding: the session of B remembers A's retired device; deleting A (which cascades to its devices) must
        // not be stopped by that reference.
        var (tenantA, tenantB, _) = await TenantsAsync();
        var installation = Guid.NewGuid();
        var (sourceId, sourceToken) = await CreateDeviceAsync(tenantA, installation);
        var code = await CreateSessionAsync(tenantB, "https://bahce.app.wasla.tr/");
        await using (var db = _database.Open("Mover"))
            Assert.Equal(PrintBridgeTransferOutcome.Ready, (await Service(db).TransferAsync(Request(sourceToken, installation, code, "https://bahce.app.wasla.tr/"), Ct)).Outcome);

        await using (var db = _database.Open("Deleter"))
        {
            db.Tenants.Remove(await db.Tenants.SingleAsync(t => t.Id == tenantA, Ct));
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _database.Open("Inspector");
        Assert.False(await check.PrintBridgeDevices.AnyAsync(d => d.Id == sourceId, Ct));
        var session = await check.PrintBridgeSetupSessions.AsNoTracking().SingleAsync(s => s.TenantId == tenantB, Ct);
        Assert.Equal(sourceId, session.SourcePrintBridgeDeviceId);
        Assert.Equal(1, await check.PrintBridgeDevices.CountAsync(d => d.InstallationId == installation && d.RemovedAtUtc == null, Ct));
    }

    [Fact]
    public async Task TheMigration_AddsTwoNullableColumns_AndRevertsCleanly()
    {
        const string previous = "20261027132431_AddContactMessages";
        const string transfer = "20261107105613_AddPrintBridgeSetupTransfer";
        await using var db = _database.Open("Setup");

        Assert.Equal([("SourcePrintBridgeDeviceId", "uniqueidentifier", true), ("SourceTokenHash", "nvarchar", true)], await TransferColumnsAsync(db));

        // Rolling back drops only the move columns (and their foreign key); the sessions themselves stay.
        var (tenant, _, _) = await TenantsAsync();
        await CreateSessionAsync(tenant, "https://anadolu.app.wasla.tr/");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        Assert.Empty(await TransferColumnsAsync(db));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [PrintBridgeSetupSessions]").SingleAsync(Ct));

        await migrator.MigrateAsync(transfer, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await TransferColumnsAsync(db)).Length);
    }

    // Helpers ------------------------------------------------------------------------------------

    private static async Task<(string Name, string Type, bool Nullable)[]> TransferColumnsAsync(CentralDbContext db)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.name, t.name, c.is_nullable FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            "WHERE c.object_id = OBJECT_ID('PrintBridgeSetupSessions') AND c.name IN ('SourcePrintBridgeDeviceId', 'SourceTokenHash') ORDER BY c.name";
        var columns = new List<(string, string, bool)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        return [.. columns];
    }

    private sealed record ArrangedMove(Guid SourceId, Guid Installation, Guid Destination, string Code, PrintBridgeTransferRequest Request);

    private async Task<ArrangedMove> ArrangeMoveAsync(int otherDevices)
    {
        await AddOtherRestaurantsDevicesAsync(otherDevices);
        var (tenantA, tenantB, _) = await TenantsAsync();
        var installation = Guid.NewGuid();
        var (sourceId, sourceToken) = await CreateDeviceAsync(tenantA, installation);
        var code = await CreateSessionAsync(tenantB, "https://bahce.app.wasla.tr/");
        return new ArrangedMove(sourceId, installation, tenantB, code, Request(sourceToken, installation, code, "https://bahce.app.wasla.tr/"));
    }

    private async Task AddOtherRestaurantsDevicesAsync(int count)
    {
        await using var db = _database.Open("Seed");
        for (var i = 0; i < count; i++)
        {
            db.PrintBridgeDevices.Add(new PrintBridgeDevice
            {
                TenantId = Seed(db, "diger" + Guid.NewGuid().ToString("N")[..10], "Diğer Lokanta"),
                Name = "Other device",
                InstallationId = Guid.NewGuid(),
                TokenHash = PrintBridgeTokenHasher.HashToken(PrintBridgeTokenHasher.GenerateRawToken()),
                IsActive = true
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The refused move stored nothing: no registration in the destination and its code still unused.</summary>
    private async Task AssertDidNotMoveAsync(ArrangedMove move)
    {
        await using var db = _database.Open("Inspector");
        Assert.False(await db.PrintBridgeDevices.AnyAsync(d => d.TenantId == move.Destination && d.InstallationId == move.Installation, Ct));
        var session = await db.PrintBridgeSetupSessions.AsNoTracking().SingleAsync(s => s.CodeHash == PrintBridgeSetupCode.Hash(move.Code), Ct);
        Assert.Null(session.ExchangedAtUtc);
        Assert.Null(session.SourcePrintBridgeDeviceId);
    }

    /// <summary>Runs each call on its own context and connection pool, released together once all are connected.</summary>
    private Task<T[]> RaceAsync<T>(params Func<CentralDbContext, Task<T>>[] calls) =>
        _race.RaceAsync(meetAtDeviceWrite: false, calls);

    private async Task<(Guid A, Guid B, Guid C)> TenantsAsync()
    {
        await using var db = _database.Open("Seed");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenants = (Seed(db, "anadolu" + suffix, "Anadolu Sofrası"), Seed(db, "bahce" + suffix, "Bahçe Burger"), Seed(db, "cinar" + suffix, "Çınar Kebap"));
        await db.SaveChangesAsync();
        return tenants;
    }

    private static PrintBridgeSetupSessionService Service(CentralDbContext db) =>
        new(
            db,
            new PrintBridgeDeviceManagementService(db, NullLogger<PrintBridgeDeviceManagementService>.Instance, NoPrinting.Instance, new SqlServerPrintBridgeSetupTenantLock(db)),
            new SqlServerPrintBridgeSetupTenantLock(db),
            NoPrinting.Instance,
            Errors);

    private static PrintBridgeTransferRequest Request(string sourceToken, Guid installation, string code, string serverUrl) =>
        new(sourceToken, installation, code, serverUrl, PrintBridgeTokenHasher.GenerateRawToken(), PrintBridgeSetupCode.Generate(), "QA-PC", "1.0.0.0", null);

    private async Task<(Guid DeviceId, string Token)> CreateDeviceAsync(Guid tenantId, Guid installation)
    {
        await using var db = _database.Open("Seed");
        var token = PrintBridgeTokenHasher.GenerateRawToken();
        var device = new PrintBridgeDevice
        {
            TenantId = tenantId,
            Name = "QA device",
            InstallationId = installation,
            TokenHash = PrintBridgeTokenHasher.HashToken(token),
            IsActive = true
        };
        db.PrintBridgeDevices.Add(device);
        await db.SaveChangesAsync();
        return (device.Id, token);
    }

    private async Task<string> CreateSessionAsync(Guid tenantId, string serverUrl)
    {
        await using var db = _database.Open("Seed");
        var created = await Service(db).CreateSessionAsync(tenantId, PrintBridgeSetupMode.NewDevice, null, serverUrl, null, false, CancellationToken.None);
        return created.Code;
    }

    private static Guid Seed(CentralDbContext db, string slug, string name, bool save = false)
    {
        var tenant = new Tenant
        {
            Name = name,
            Slug = slug,
            PrimaryDomain = $"{slug}.app.wasla.tr",
            DatabaseName = $"Wasla_Tenant_{slug}_{Guid.NewGuid():N}",
            EncryptedConnectionString = "encrypted-not-real",
            EncryptionKeyVersion = 1,
            SchemaVersion = "1.0",
            IsActive = true
        };
        db.Tenants.Add(tenant);
        if (save)
        {
            db.SaveChanges();
            db.Dispose();
        }

        return tenant.Id;
    }

    private sealed class ErrorLog : Microsoft.Extensions.Logging.ILogger<PrintBridgeSetupSessionService>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _errors = new();

        public string Text() => string.Join(" | ", _errors);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Error;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                _errors.Enqueue(formatter(state, exception) + " " + exception?.GetBaseException().Message);
        }
    }

    private sealed class NoPrinting : IPrintBridgeActivePrintJobChecker
    {
        public static NoPrinting Instance { get; } = new();

        public Task<bool> HasActivePrintingJobAsync(Guid tenantId, Guid? installationId, string? legacyLockedBy, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
