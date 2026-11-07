using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Billing;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// WAS-190 on a real SQL Server (a throwaway CentralDb, see <see cref="SqlServerCentralDatabase"/>): Print Bridge devices
/// added at the same moment. Every caller has its own DbContext and connection pool, as separate requests or Web
/// instances would, and all are released at one barrier. Restaurants never wait for each other's quota: the quota count
/// took serializable range locks across every restaurant's devices, so two new-device setups of different restaurants
/// deadlocked on their inserts and one failed. Within one restaurant the tenant's setup lock, which every writer that
/// adds or reactivates an active device takes, keeps the device limit exact. Nothing retries a failed exchange, so a
/// deadlock fails these tests. SQLite runs no application locks, so these races are only meaningful here.
/// </summary>
public sealed class PrintBridgeDeviceQuotaSqlServerTests : IAsyncLifetime
{
    private const int Rounds = 10;
    private const string ServerUrl = "https://anadolu.app.wasla.tr/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Unexpected failures logged by the services, shown in an assertion's message.</summary>
    private readonly ErrorLog _errors = new();
    private readonly SqlServerCentralDatabase _database = new();

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

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

            var results = await RaceAsync(
                db => Exchange(db, firstCode),
                db => Exchange(db, secondCode));

            Assert.True(results.All(added => added), $"Round {round}: {string.Join(", ", results)}; errors: {_errors.Text()}");
            Assert.Equal(1, await ActiveCountAsync(first));
            Assert.Equal(1, await ActiveCountAsync(second));
        }
    }

    [Fact]
    public async Task DifferentRestaurantsAddingDevicesEveryWay_AtOnce_AllSucceed()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var writers = new List<Func<CentralDbContext, Task<bool>>>();
            foreach (var kind in new[] { "exchange", "exchange", "create", "reactivate", "move" })
                writers.Add(await PrepareAsync(kind, await TenantAsync()));

            var results = await RaceAsync([.. writers]);

            Assert.True(results.All(added => added), $"Round {round}: {string.Join(", ", results)}; errors: {_errors.Text()}");
        }
    }

    /// <summary>
    /// Two writers race for a restaurant's last device place: exactly one gets it and the limit holds. Each writer that
    /// adds or turns on an active device is covered: the setup exchange, the move (WAS-91), and the Web panel's create
    /// and reactivate.
    /// </summary>
    [Theory]
    [InlineData("exchange", "exchange")]
    [InlineData("exchange", "create")]
    [InlineData("exchange", "reactivate")]
    [InlineData("move", "exchange")]
    [InlineData("move", "reactivate")]
    [InlineData("create", "reactivate")]
    [InlineData("reactivate", "reactivate")]
    public async Task TwoWritersForTheLastPlace_AtOnce_FillItOnce(string firstKind, string secondKind)
    {
        for (var round = 0; round < Rounds / 2; round++)
        {
            var tenant = await TenantAsync();
            for (var i = 0; i < PrintBridgeDeviceLimits.AllowedActiveDeviceCount - 1; i++)
                await AddDeviceAsync(tenant, isActive: true);

            var first = await PrepareAsync(firstKind, tenant);
            var second = await PrepareAsync(secondKind, tenant);

            var results = await RaceAsync(first, second);

            Assert.True(results.Count(added => added) == 1, $"Round {round}: {string.Join(", ", results)}; errors: {_errors.Text()}");
            Assert.Equal(PrintBridgeDeviceLimits.AllowedActiveDeviceCount, await ActiveCountAsync(tenant));
        }
    }

    // Helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// Everything a writer of <paramref name="kind"/> needs for <paramref name="tenantId"/>, made beforehand, and the call
    /// itself: true when it added (or turned on) an active device, false when the device limit refused it.
    /// </summary>
    private async Task<Func<CentralDbContext, Task<bool>>> PrepareAsync(string kind, Guid tenantId)
    {
        switch (kind)
        {
            case "exchange":
            {
                var code = await CreateSessionAsync(tenantId);
                return db => Exchange(db, code);
            }
            case "create":
                return async db =>
                {
                    try
                    {
                        await Devices(db).CreateDeviceAsync(tenantId, "Web", CancellationToken.None);
                        return true;
                    }
                    catch (PrintBridgeDeviceLimitReachedException)
                    {
                        return false;
                    }
                };
            case "reactivate":
            {
                var (deviceId, _) = await AddDeviceAsync(tenantId, isActive: false);
                return async db =>
                {
                    try
                    {
                        Assert.True(await Devices(db).SetDeviceActiveAsync(tenantId, deviceId, true, CancellationToken.None));
                        return true;
                    }
                    catch (PrintBridgeDeviceLimitReachedException)
                    {
                        return false;
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
                    Assert.Contains(outcome, new[] { PrintBridgeTransferOutcome.Ready, PrintBridgeTransferOutcome.DeviceLimitReached });
                    return outcome == PrintBridgeTransferOutcome.Ready;
                };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private async Task<bool> Exchange(CentralDbContext db, string code) =>
        await Service(db).ExchangeAsync(code, new PrintBridgeSetupClientInfo("QA-PC", "1.0.0.0", null, Guid.NewGuid()), CancellationToken.None)
            is not null;

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
        new(db, Devices(db), new SqlServerPrintBridgeSetupTenantLock(db), NoPrinting.Instance, _errors);

    private static PrintBridgeDeviceManagementService Devices(CentralDbContext db) =>
        new(db, NullLogger<PrintBridgeDeviceManagementService>.Instance, NoPrinting.Instance, new SqlServerPrintBridgeSetupTenantLock(db));

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
