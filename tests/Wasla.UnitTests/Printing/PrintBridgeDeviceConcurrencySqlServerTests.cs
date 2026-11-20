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
/// Print Bridge devices added at the same moment on a real SQL Server (a throwaway CentralDb, see
/// <see cref="SqlServerCentralDatabase"/>), each caller on its own DbContext and connection pool (see
/// <see cref="PrintBridgeSqlServerRace"/>). There is no device count limit (decision of 2026-11-17); these races prove
/// that removing it kept the protections that never depended on it.
/// <para>
/// Restaurants adding devices at once all succeed (WAS-190): the races hold every caller right before its device write
/// until all have reached it, so each has taken its locks and read while the others' transactions are open.
/// </para>
/// <para>
/// Within one restaurant that already has more active devices than the retired limit of three, the setup exchange, the
/// move (WAS-91), and the Web panel's create and reactivate still take the tenant's setup lock: one writer is held right
/// before its device write until SQL Server shows the other waiting for its lock, in both orders, and then both add
/// their device. One computer set up for two restaurants at once is registered to exactly one of them: the installation
/// lock makes the second wait and then refuses it.
/// </para>
/// Every writer's outcome is explicit. Any other failure, error log, deadlock or lock timeout fails the test; nothing
/// retries. SQLite runs no application locks, so these races are only meaningful here. Moves of one computer to two
/// restaurants are in <see cref="PrintBridgeTransferSqlServerTests"/>.
/// </summary>
public sealed class PrintBridgeDeviceConcurrencySqlServerTests : IAsyncLifetime
{
    private const int Rounds = 10;
    private const int ContendedRounds = 6;

    /// <summary>The retired commercial limit; the restaurants below already have this many active devices.</summary>
    private const int RetiredDeviceLimit = 3;

    private const string ServerUrl = "https://anadolu.app.wasla.tr/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ServiceLog _log = new();
    private readonly SqlServerCentralDatabase _database = new();
    private readonly PrintBridgeSqlServerRace _race;

    public PrintBridgeDeviceConcurrencySqlServerTests() => _race = new PrintBridgeSqlServerRace(_database);

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private enum Outcome
    {
        Added,
        RefusedAsRegisteredToAnotherRestaurant
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
            await AssertEveryInstallationRegisteredOnceAsync();
        }
    }

    /// <summary>
    /// Two writers of a restaurant that already has three active devices. The one holding the tenant's setup lock adds its
    /// device; the other is seen waiting for that lock, then adds its own. Each pair runs in both orders.
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
    public async Task TwoWritersBeyondThreeActiveDevices_OneWaitingForTheOthersLock_BothAdd(string firstKind, string secondKind)
    {
        for (var round = 0; round < ContendedRounds; round++)
        {
            var tenant = await TenantAsync();
            for (var i = 0; i < RetiredDeviceLimit; i++)
                await AddDeviceAsync(tenant, isActive: true);

            var (holderKind, waiterKind) = round % 2 == 0 ? (firstKind, secondKind) : (secondKind, firstKind);
            var holder = await PrepareAsync(holderKind, tenant);
            var waiter = await PrepareAsync(waiterKind, tenant);

            var (held, waited) = await _race.ContendAsync(holder, waiter);

            Assert.True(
                (held, waited) == (Outcome.Added, Outcome.Added),
                $"Round {round} ({holderKind} holding, {waiterKind} waiting): {held}, {waited}; log: {_log.Text()}");
            _log.AssertNoErrors();
            Assert.Equal(RetiredDeviceLimit + 2, await ActiveCountAsync(tenant));
            await AssertEveryInstallationRegisteredOnceAsync();
        }
    }

    /// <summary>
    /// One computer opens setup links of two restaurants at once, each of which already has more devices than the retired
    /// limit. The exchange holding the installation lock registers it; the other waits for that lock and is then refused,
    /// so the computer stays registered to exactly one restaurant. Runs in both orders.
    /// </summary>
    [Fact]
    public async Task OneComputerSetUpForTwoRestaurantsAtOnce_IsRegisteredToExactlyOne()
    {
        for (var round = 0; round < ContendedRounds; round++)
        {
            var first = await TenantAsync();
            var second = await TenantAsync();
            for (var i = 0; i < RetiredDeviceLimit + 1; i++)
            {
                await AddDeviceAsync(first, isActive: true);
                await AddDeviceAsync(second, isActive: true);
            }

            var installation = Guid.NewGuid();
            var (holderTenant, waiterTenant) = round % 2 == 0 ? (first, second) : (second, first);
            var holderCode = await CreateSessionAsync(holderTenant);
            var waiterCode = await CreateSessionAsync(waiterTenant);

            var (held, waited) = await _race.ContendAsync(
                db => ExchangeAsync(db, holderCode, installation),
                db => ExchangeAsync(db, waiterCode, installation));

            Assert.True(
                (held, waited) == (Outcome.Added, Outcome.RefusedAsRegisteredToAnotherRestaurant),
                $"Round {round}: {held}, {waited}; log: {_log.Text()}");
            _log.AssertNoErrors();
            Assert.Equal(RetiredDeviceLimit + 2, await ActiveCountAsync(holderTenant));
            Assert.Equal(RetiredDeviceLimit + 1, await ActiveCountAsync(waiterTenant));
            await using var inspector = _database.Open("Inspector");
            Assert.Equal(
                [holderTenant],
                await inspector.PrintBridgeDevices.AsNoTracking()
                    .Where(d => d.InstallationId == installation && d.RemovedAtUtc == null)
                    .Select(d => d.TenantId)
                    .ToListAsync(Ct));
        }
    }

    // Helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// Everything a writer of <paramref name="kind"/> needs for <paramref name="tenantId"/>, made beforehand, and the call
    /// itself. The call returns what the writer did; anything but adding a device fails it.
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
                    await Devices(db).CreateDeviceAsync(tenantId, "Web", CancellationToken.None);
                    return Outcome.Added;
                };
            case "reactivate":
            {
                var (deviceId, _) = await AddDeviceAsync(tenantId, isActive: false);
                return async db =>
                {
                    Assert.True(await Devices(db).SetDeviceActiveAsync(tenantId, deviceId, true, CancellationToken.None), "The device to reactivate was not found.");
                    return Outcome.Added;
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
                    return outcome == PrintBridgeTransferOutcome.Ready
                        ? Outcome.Added
                        : throw new Xunit.Sdk.XunitException($"The move ended {outcome}, not added; log: {_log.Text()}");
                };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>
    /// A new-device exchange. It adds a device, or, only for a computer already registered to another restaurant, is
    /// refused with that reason. Any other refusal (its public result is null) fails the test with the recorded reason.
    /// </summary>
    private async Task<Outcome> ExchangeAsync(CentralDbContext db, string code, Guid? installation = null)
    {
        PrintBridgeSetupExchangeResult? exchanged;
        try
        {
            exchanged = await Service(db).ExchangeAsync(
                code, new PrintBridgeSetupClientInfo("QA-PC", "1.0.0.0", null, installation ?? Guid.NewGuid()), CancellationToken.None);
        }
        catch (PrintBridgeSetupInstallationRegisteredToAnotherTenantException)
        {
            return Outcome.RefusedAsRegisteredToAnotherRestaurant;
        }

        if (exchanged is not null)
            return Outcome.Added;

        var codeHash = PrintBridgeSetupCode.Hash(code);
        await using var inspector = _database.Open("Inspector");
        var reason = await inspector.PrintBridgeSetupSessions.AsNoTracking()
            .Where(s => s.CodeHash == codeHash)
            .Select(s => s.FailureReason)
            .SingleAsync(CancellationToken.None);
        throw new Xunit.Sdk.XunitException($"The exchange failed with \"{reason ?? "no recorded reason"}\"; log: {_log.Text()}");
    }

    private async Task<int> ActiveCountAsync(Guid tenantId)
    {
        await using var db = _database.Open("Inspector");
        return await db.PrintBridgeDevices.CountAsync(d => d.TenantId == tenantId && d.IsActive && d.RemovedAtUtc == null, Ct);
    }

    /// <summary>No computer is registered (active or passive, not removed) to two devices, in one restaurant or across them.</summary>
    private async Task AssertEveryInstallationRegisteredOnceAsync()
    {
        await using var db = _database.Open("Inspector");
        var duplicates = await db.PrintBridgeDevices.AsNoTracking()
            .Where(d => d.InstallationId != null && d.RemovedAtUtc == null)
            .GroupBy(d => d.InstallationId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(Ct);
        Assert.Empty(duplicates);
    }

    private async Task<Guid> TenantAsync()
    {
        await using var db = _database.Open("Seed");
        var slug = "devices" + Guid.NewGuid().ToString("N")[..10];
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
