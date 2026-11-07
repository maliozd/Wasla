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
/// pool, as separate requests or Web instances would, and all are released at one barrier. The installation and tenant
/// application locks (<c>sp_getapplock</c>) and the serializable transaction must leave exactly one registration, one
/// retired source and one used code. SQLite runs no application locks, so these races are only meaningful here.
/// </summary>
public sealed class PrintBridgeTransferSqlServerTests : IAsyncLifetime
{
    private const int Rounds = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Unexpected failures logged by the services, shown in an assertion's message.</summary>
    private static readonly ErrorLog Errors = new();
    private readonly SqlServerCentralDatabase _database = new();

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

            Assert.True(
                results.Count(r => r.Outcome == PrintBridgeTransferOutcome.Ready) == 1 && results.Count(r => r.Outcome == PrintBridgeTransferOutcome.SourceRejected) == 1,
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
    public async Task MovesIntoARestaurantWithOnePlaceLeft_AtOnce_FillItOnce()
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

        var results = await RaceAsync(
            db => Service(db).TransferAsync(Request(firstToken, first, firstCode, "https://dolu.app.wasla.tr/"), CancellationToken.None),
            db => Service(db).TransferAsync(Request(secondToken, second, secondCode, "https://dolu.app.wasla.tr/"), CancellationToken.None));

        Assert.True(
            results.Count(r => r.Outcome == PrintBridgeTransferOutcome.Ready) == 1 && results.Count(r => r.Outcome == PrintBridgeTransferOutcome.DeviceLimitReached) == 1,
            $"{string.Join(", ", results.Select(r => r.Outcome))}; errors: {Errors.Text()}");
        await using var db = _database.Open("Inspector");
        Assert.Equal(PrintBridgeDeviceLimits.AllowedActiveDeviceCount, await db.PrintBridgeDevices.CountAsync(d => d.TenantId == full && d.IsActive && d.RemovedAtUtc == null, Ct));
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

    /// <summary>Runs each call on its own context and connection pool, all released together.</summary>
    private async Task<T[]> RaceAsync<T>(params Func<CentralDbContext, Task<T>>[] calls)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select((call, index) => Task.Run(async () =>
        {
            await using var db = _database.Open($"Racer{index}");
            await db.Database.OpenConnectionAsync();
            await start.Task;
            return await call(db);
        })).ToArray();

        start.SetResult();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
    }

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
