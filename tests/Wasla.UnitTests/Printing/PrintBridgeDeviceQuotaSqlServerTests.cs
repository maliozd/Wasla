using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Billing;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// WAS-190 on a real SQL Server (a throwaway CentralDb, see <see cref="SqlServerCentralDatabase"/>): Print Bridge devices
/// added at the same moment, each caller on its own DbContext and connection pool (see <see cref="PrintBridgeSqlServerRace"/>).
/// <para>
/// Restaurants adding devices at once all succeed. The quota count took serializable key-range locks across every
/// restaurant's devices, so two new-device setups of different restaurants deadlocked on their inserts and one failed.
/// Those races hold every caller right before its device write until all have reached it, so each has taken its locks and
/// counted while the others' transactions are open.
/// </para>
/// <para>
/// Within one restaurant, the tenant's setup lock keeps the device limit exact for the writers that take it: the setup
/// exchange, the move (WAS-91), and the Web panel's create and reactivate. Those races hold one writer right before its
/// device write and let it go only once SQL Server shows the other waiting for its tenant lock, in both orders.
/// </para>
/// Every writer's outcome is explicit: it added an active device, or the device limit refused it. Any other failure,
/// error log, deadlock or lock timeout fails the test; nothing retries. SQLite runs no application locks, so these races
/// are only meaningful here. Two moves for one place left are in <see cref="PrintBridgeTransferSqlServerTests"/>.
/// </summary>
public sealed class PrintBridgeDeviceQuotaSqlServerTests : IAsyncLifetime
{
    private const int Rounds = 10;
    private const int ContendedRounds = 6;
    private const string ServerUrl = "https://anadolu.app.wasla.tr/";

    /// <summary>The reason a setup session records when the device limit refused its exchange.</summary>
    private const string QuotaExceeded = "quota_exceeded";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ServiceLog _log = new();
    private readonly SqlServerCentralDatabase _database = new();
    private readonly PrintBridgeSqlServerRace _race;

    public PrintBridgeDeviceQuotaSqlServerTests() => _race = new PrintBridgeSqlServerRace(_database);

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private enum Outcome
    {
        Added,
        RefusedByDeviceLimit
    }

    [Fact]
    public async Task NewDeviceExchangesOfDifferentRestaurants_AtOnce_AllSucceed()
    {
        for (var round = 0; round < Rounds; round++)
        {
            // Fresh restaurants each round, as the reported failure had them: no devices of their own yet.
            var first = await TenantAsync();
            var second = await TenantAsync();
            var firstCode = await CreateSessionAsync(first);
            var secondCode = await CreateSessionAsync(second);

            var results = await _race.RaceAsync(
                meetAtDeviceWrite: true,
                db => ExchangeAsync(db, firstCode),
                db => ExchangeAsync(db, secondCode));

            Assert.True(results.All(r => r == Outcome.Added), $"Round {round}: {string.Join(", ", results)}; log: {_log.Text()}");
            _log.AssertNoErrors();
            Assert.Equal(1, await ActiveCountAsync(first));
            Assert.Equal(1, await ActiveCountAsync(second));
        }
    }

    [Fact]
    public async Task DifferentRestaurantsAddingDevicesEveryWay_AtOnce_AllSucceed()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var tenants = new List<Guid>();
            var writers = new List<Func<CentralDbContext, Task<Outcome>>>();
            foreach (var kind in new[] { "exchange", "exchange", "create", "reactivate", "move" })
            {
                var tenant = await TenantAsync();
                tenants.Add(tenant);
                writers.Add(await PrepareAsync(kind, tenant));
            }

            var results = await _race.RaceAsync(meetAtDeviceWrite: true, [.. writers]);

            Assert.True(results.All(r => r == Outcome.Added), $"Round {round}: {string.Join(", ", results)}; log: {_log.Text()}");
            _log.AssertNoErrors();
            foreach (var tenant in tenants)
                Assert.Equal(1, await ActiveCountAsync(tenant));
        }
    }

    /// <summary>
    /// Two writers for a restaurant's last device place. The one holding the tenant lock adds its device; the other waited
    /// for that lock and is then refused by the device limit, and nothing else. Each pair runs in both orders.
    /// </summary>
    [Theory]
    [InlineData("exchange", "exchange")]
    [InlineData("exchange", "create")]
    [InlineData("exchange", "reactivate")]
    [InlineData("move", "exchange")]
    [InlineData("move", "create")]
    [InlineData("move", "reactivate")]
    [InlineData("create", "create")]
    [InlineData("create", "reactivate")]
    [InlineData("reactivate", "reactivate")]
    public async Task TwoWritersForTheLastPlace_OneWaitingForTheOther_FillItOnce(string firstKind, string secondKind)
    {
        for (var round = 0; round < ContendedRounds; round++)
        {
            var tenant = await TenantAsync();
            for (var i = 0; i < PrintBridgeDeviceLimits.AllowedActiveDeviceCount - 1; i++)
                await AddDeviceAsync(tenant, isActive: true);

            var (holderKind, waiterKind) = round % 2 == 0 ? (firstKind, secondKind) : (secondKind, firstKind);
            var holder = await PrepareAsync(holderKind, tenant);
            var waiter = await PrepareAsync(waiterKind, tenant);

            var (held, waited) = await _race.ContendAsync(holder, waiter);

            Assert.True(
                (held, waited) == (Outcome.Added, Outcome.RefusedByDeviceLimit),
                $"Round {round} ({holderKind} holding, {waiterKind} waiting): {held}, {waited}; log: {_log.Text()}");
            _log.AssertNoErrors();
            Assert.Equal(PrintBridgeDeviceLimits.AllowedActiveDeviceCount, await ActiveCountAsync(tenant));
        }
    }

    // Helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// Everything a writer of <paramref name="kind"/> needs for <paramref name="tenantId"/>, made beforehand, and the call
    /// itself. The call returns what the writer did; anything but adding a device or a device-limit refusal fails it.
    /// </summary>
    private async Task<Func<CentralDbContext, Task<Outcome>>> PrepareAsync(string kind, Guid tenantId)
    {
        switch (kind)
        {
            case "exchange":
            {
                var code = await CreateSessionAsync(tenantId);
                return db => ExchangeAsync(db, code);
            }
            case "create":
                return async db =>
                {
                    try
                    {
                        await Devices(db).CreateDeviceAsync(tenantId, "Web", CancellationToken.None);
                        return Outcome.Added;
                    }
                    catch (PrintBridgeDeviceLimitReachedException)
                    {
                        return Outcome.RefusedByDeviceLimit;
                    }
                };
            case "reactivate":
            {
                var (deviceId, _) = await AddDeviceAsync(tenantId, isActive: false);
                return async db =>
                {
                    try
                    {
                        Assert.True(await Devices(db).SetDeviceActiveAsync(tenantId, deviceId, true, CancellationToken.None), "The device to reactivate was not found.");
                        return Outcome.Added;
                    }
                    catch (PrintBridgeDeviceLimitReachedException)
                    {
                        return Outcome.RefusedByDeviceLimit;
                    }
                };
            }
            case "move":
            {
                // A computer of another restaurant, moved here with this restaurant's setup link (WAS-91).
                var installation = Guid.NewGuid();
                var (_, sourceToken) = await AddDeviceAsync(await TenantAsync(), isActive: true, installation);
                var code = await CreateSessionAsync(tenantId);
                var request = new PrintBridgeTransferRequest(
                    sourceToken, installation, code, ServerUrl, PrintBridgeTokenHasher.GenerateRawToken(),
                    PrintBridgeSetupCode.Generate(), "QA-PC", "1.0.0.0", null);
                return async db =>
                {
                    var outcome = (await Service(db).TransferAsync(request, CancellationToken.None)).Outcome;
                    return outcome switch
                    {
                        PrintBridgeTransferOutcome.Ready => Outcome.Added,
                        PrintBridgeTransferOutcome.DeviceLimitReached => Outcome.RefusedByDeviceLimit,
                        _ => throw new Xunit.Sdk.XunitException($"The move ended {outcome}, not added or refused by the device limit; log: {_log.Text()}")
                    };
                };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>
    /// A new-device exchange. Its public result is null for every refusal, so a refusal is read from the setup session's
    /// recorded failure reason, which must be the device limit.
    /// </summary>
    private async Task<Outcome> ExchangeAsync(CentralDbContext db, string code)
    {
        var exchanged = await Service(db).ExchangeAsync(
            code, new PrintBridgeSetupClientInfo("QA-PC", "1.0.0.0", null, Guid.NewGuid()), CancellationToken.None);
        if (exchanged is not null)
            return Outcome.Added;

        var codeHash = PrintBridgeSetupCode.Hash(code);
        await using var inspector = _database.Open("Inspector");
        var reason = await inspector.PrintBridgeSetupSessions.AsNoTracking()
            .Where(s => s.CodeHash == codeHash)
            .Select(s => s.FailureReason)
            .SingleAsync(CancellationToken.None);
        if (reason != QuotaExceeded)
            throw new Xunit.Sdk.XunitException($"The exchange failed with \"{reason ?? "no recorded reason"}\", not the device limit; log: {_log.Text()}");
        return Outcome.RefusedByDeviceLimit;
    }

    private async Task<int> ActiveCountAsync(Guid tenantId)
    {
        await using var db = _database.Open("Inspector");
        return await db.PrintBridgeDevices.CountAsync(d => d.TenantId == tenantId && d.IsActive && d.RemovedAtUtc == null, Ct);
    }

    private async Task<Guid> TenantAsync()
    {
        await using var db = _database.Open("Seed");
        var slug = "quota" + Guid.NewGuid().ToString("N")[..10];
        var tenant = new Tenant
        {
            Name = "Anadolu Sofrası",
            Slug = slug,
            PrimaryDomain = $"{slug}.app.wasla.tr",
            DatabaseName = $"Wasla_Tenant_{slug}",
            EncryptedConnectionString = "encrypted-not-real",
            EncryptionKeyVersion = 1,
            SchemaVersion = "1.0",
            IsActive = true
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(Ct);
        return tenant.Id;
    }

    private async Task<(Guid DeviceId, string Token)> AddDeviceAsync(Guid tenantId, bool isActive, Guid? installation = null)
    {
        await using var db = _database.Open("Seed");
        var token = PrintBridgeTokenHasher.GenerateRawToken();
        var device = new PrintBridgeDevice
        {
            TenantId = tenantId,
            Name = "QA device",
            InstallationId = installation ?? Guid.NewGuid(),
            TokenHash = PrintBridgeTokenHasher.HashToken(token),
            IsActive = isActive
        };
        db.PrintBridgeDevices.Add(device);
        await db.SaveChangesAsync(Ct);
        return (device.Id, token);
    }

    private async Task<string> CreateSessionAsync(Guid tenantId)
    {
        await using var db = _database.Open("Seed");
        var created = await Service(db).CreateSessionAsync(tenantId, PrintBridgeSetupMode.NewDevice, null, ServerUrl, null, false, CancellationToken.None);
        return created.Code;
    }

    private PrintBridgeSetupSessionService Service(CentralDbContext db) =>
        new(db, Devices(db), new SqlServerPrintBridgeSetupTenantLock(db), NoPrinting.Instance, _log.For<PrintBridgeSetupSessionService>());

    private PrintBridgeDeviceManagementService Devices(CentralDbContext db) =>
        new(db, _log.For<PrintBridgeDeviceManagementService>(), NoPrinting.Instance, new SqlServerPrintBridgeSetupTenantLock(db));

    /// <summary>What both services log at Warning and above. An Error entry fails the test.</summary>
    private sealed class ServiceLog
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

        public ILogger<T> For<T>() => new CapturingLogger<T>(this);

        public string Text() => _entries.IsEmpty ? "(empty)" : string.Join(" | ", _entries.Select(e => $"{e.Level}: {e.Text}"));

        public void AssertNoErrors() =>
            Assert.False(_entries.Any(e => e.Level >= LogLevel.Error), $"A service logged an error: {Text()}");

        private sealed class CapturingLogger<T>(ServiceLog log) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    log._entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception.GetBaseException().Message)));
            }
        }
    }

    private sealed class NoPrinting : IPrintBridgeActivePrintJobChecker
    {
        public static NoPrinting Instance { get; } = new();

        public Task<bool> HasActivePrintingJobAsync(Guid tenantId, Guid? installationId, string? legacyLockedBy, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
