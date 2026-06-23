using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeSetupSessionServiceTests : IDisposable
{
    private const string ServerUrl = "https://sushim.wasla.local:7200/";

    private readonly SqliteConnection _connection;
    private readonly CentralDbContext _db;
    private readonly PrintBridgeSetupSessionService _service;
    private readonly PrintBridgeDeviceManagementService _devices;
    private readonly Guid _tenantId;
    private readonly Guid _deviceId;

    public PrintBridgeSetupSessionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new CentralDbContext(options);
        _db.Database.EnsureCreated();

        _tenantId = SeedTenant();

        _devices = new PrintBridgeDeviceManagementService(_db, NullLogger<PrintBridgeDeviceManagementService>.Instance);
        _deviceId = _devices.CreateDeviceAsync(_tenantId, "Kitchen PC", CancellationToken.None)
            .GetAwaiter().GetResult().DeviceId;

        _service = new PrintBridgeSetupSessionService(
            _db,
            _devices,
            SuccessfulTenantLock.Instance,
            NullLogger<PrintBridgeSetupSessionService>.Instance);
    }

    // ── Session creation ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_PersistsHashedCode_AndShortLivedExpiry()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.ReconnectExistingDevice,
            _deviceId,
            ServerUrl,
            "Kitchen PC",
            confirmReplaceActiveToken: true,
            CancellationToken.None);

        var stored = await _db.PrintBridgeSetupSessions.AsNoTracking()
            .SingleAsync(s => s.Id == created.SessionId, TestContext.Current.CancellationToken);

        // Plain code is never stored.
        Assert.NotEqual(created.Code, stored.CodeHash);
        Assert.Equal(PrintBridgeSetupCode.Hash(created.Code), stored.CodeHash);
        Assert.True(stored.ExpiresAtUtc > DateTime.UtcNow);
        Assert.True(stored.ExpiresAtUtc <= DateTime.UtcNow.AddMinutes(6));

        var status = await _service.GetStatusAsync(_tenantId, created.SessionId, CancellationToken.None);
        Assert.Equal(PrintBridgeSetupSessionStatus.Pending, status!.Status);
    }

    [Fact]
    public async Task CreateSession_ForExistingActiveDevice_RequiresTokenReplacementConfirmation()
    {
        await Assert.ThrowsAsync<PrintBridgeSetupTokenReplacementConfirmationRequiredException>(() =>
            _service.CreateSessionAsync(
                _tenantId,
                PrintBridgeSetupMode.ReconnectExistingDevice,
                _deviceId,
                ServerUrl,
                "Kitchen PC",
                confirmReplaceActiveToken: false,
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateSession_ReconnectWithNoDeviceId_IsRejected()
    {
        await Assert.ThrowsAsync<PrintBridgeSetupDeviceSelectionRequiredException>(() =>
            _service.CreateSessionAsync(
                _tenantId,
                PrintBridgeSetupMode.ReconnectExistingDevice,
                null,
                ServerUrl,
                null,
                confirmReplaceActiveToken: true,
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateSession_ReconnectSelectedTenantDevice_BindsExactDevice()
    {
        var second = await _devices.CreateDeviceAsync(_tenantId, "Bar PC", CancellationToken.None);

        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.ReconnectExistingDevice,
            second.DeviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);

        var stored = await _db.PrintBridgeSetupSessions.AsNoTracking()
            .SingleAsync(s => s.Id == created.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(second.DeviceId, created.DeviceId);
        Assert.Equal(second.DeviceId, stored.PrintBridgeDeviceId);
        Assert.Equal(PrintBridgeSetupModeValues.ReconnectExisting, stored.SetupMode);
    }

    [Fact]
    public async Task CreateSession_ReconnectForeignTenantDevice_IsRejected()
    {
        var foreignTenantId = SeedTenant("foreign", "foreign.wasla.local");
        var foreignDevice = await _devices.CreateDeviceAsync(foreignTenantId, "Foreign PC", CancellationToken.None);

        await Assert.ThrowsAsync<PrintBridgeSetupDeviceNotFoundException>(() =>
            _service.CreateSessionAsync(
                _tenantId,
                PrintBridgeSetupMode.ReconnectExistingDevice,
                foreignDevice.DeviceId,
                ServerUrl,
                null,
                confirmReplaceActiveToken: true,
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateSession_NewDevice_NoDeviceCreatedAndExistingTokenUnchanged()
    {
        var deviceCountBefore = await _db.PrintBridgeDevices
            .CountAsync(d => d.TenantId == _tenantId, TestContext.Current.CancellationToken);
        var existingHashBefore = await GetTokenHashAsync(_deviceId);

        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.NewDevice,
            null,
            ServerUrl,
            "New POS",
            confirmReplaceActiveToken: false,
            CancellationToken.None);

        var stored = await _db.PrintBridgeSetupSessions.AsNoTracking()
            .SingleAsync(s => s.Id == created.SessionId, TestContext.Current.CancellationToken);

        var deviceCountAfter = await _db.PrintBridgeDevices
            .CountAsync(d => d.TenantId == _tenantId, TestContext.Current.CancellationToken);
        var existingHashAfter = await GetTokenHashAsync(_deviceId);

        // Device is NOT created at session-creation time.
        Assert.Null(created.DeviceId);
        Assert.Null(stored.PrintBridgeDeviceId);
        Assert.Equal(deviceCountBefore, deviceCountAfter);
        // Existing device token must not be touched.
        Assert.Equal(existingHashBefore, existingHashAfter);
        Assert.Equal(PrintBridgeSetupModeValues.NewDevice, stored.SetupMode);
    }

    [Fact]
    public async Task SetupCode_CannotAuthenticateNormalPrintBridgeApis()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.NewDevice,
            null,
            ServerUrl,
            "Manual setup",
            confirmReplaceActiveToken: false,
            CancellationToken.None);
        var auth = new PrintBridgeAuthService(_db, NullLogger<PrintBridgeAuthService>.Instance);

        var result = await auth.AuthenticateAsync(
            created.Code,
            new PrintBridgeClientInfo("DESKTOP", "1.0.0", "POS-58", "127.0.0.1"),
            CancellationToken.None);

        Assert.Null(result);
    }

    // ── Exchange ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exchange_ValidCode_SucceedsOnce_AndSecondAttemptFails()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.ReconnectExistingDevice,
            _deviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);

        var first = await _service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);
        Assert.NotNull(first);
        Assert.False(string.IsNullOrWhiteSpace(first!.DeviceToken));
        Assert.False(string.IsNullOrWhiteSpace(first.CompletionCredential));
        Assert.Equal(ServerUrl, first.ServerUrl);

        // Single-use: second exchange of the same code must fail.
        var second = await _service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);
        Assert.Null(second);
    }

    [Fact]
    public async Task Exchange_ReconnectMode_RotatesOnlySelectedDeviceToken()
    {
        var other = await _devices.CreateDeviceAsync(_tenantId, "Bar PC", CancellationToken.None);
        var selectedHashBefore = await GetTokenHashAsync(_deviceId);
        var otherHashBefore = await GetTokenHashAsync(other.DeviceId);

        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.ReconnectExistingDevice,
            _deviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);

        var exchange = await _service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);

        Assert.NotNull(exchange);
        // Only the selected device token changed.
        Assert.NotEqual(selectedHashBefore, await GetTokenHashAsync(_deviceId));
        Assert.Equal(otherHashBefore, await GetTokenHashAsync(other.DeviceId));
    }

    [Fact]
    public async Task Exchange_NewDeviceMode_CreatesDeviceAtExchangeAndBindsToSession()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.NewDevice,
            null,
            ServerUrl,
            null,
            confirmReplaceActiveToken: false,
            CancellationToken.None);

        var existingHashBefore = await GetTokenHashAsync(_deviceId);

        var exchange = await _service.ExchangeAsync(
            created.Code,
            new PrintBridgeSetupClientInfo("VIVO-PC", "1.2.3", "Thermal-80"),
            CancellationToken.None);

        Assert.NotNull(exchange);
        Assert.False(string.IsNullOrWhiteSpace(exchange!.DeviceToken));

        // Session now has a device bound.
        var session = await _db.PrintBridgeSetupSessions.AsNoTracking()
            .SingleAsync(s => s.Id == created.SessionId, TestContext.Current.CancellationToken);

        Assert.NotNull(session.PrintBridgeDeviceId);
        Assert.NotEqual(_deviceId, session.PrintBridgeDeviceId!.Value);

        // New device carries the client-reported metadata.
        var device = await _db.PrintBridgeDevices.AsNoTracking()
            .SingleAsync(d => d.Id == session.PrintBridgeDeviceId.Value, TestContext.Current.CancellationToken);

        Assert.Equal("VIVO-PC", device.Name);
        Assert.Equal("VIVO-PC", device.MachineName);
        Assert.Equal("1.2.3", device.AppVersion);
        Assert.Equal("Thermal-80", device.PrinterName);

        // Existing device token must not be touched.
        Assert.Equal(existingHashBefore, await GetTokenHashAsync(_deviceId));
    }

    [Fact]
    public async Task Exchange_NewDeviceMode_NoClientInfo_UsesDefaultDeviceName()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId, PrintBridgeSetupMode.NewDevice,
            null, ServerUrl, null,
            confirmReplaceActiveToken: false, CancellationToken.None);

        var exchange = await _service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);

        Assert.NotNull(exchange);

        var session = await _db.PrintBridgeSetupSessions.AsNoTracking()
            .SingleAsync(s => s.Id == created.SessionId, TestContext.Current.CancellationToken);

        var device = await _db.PrintBridgeDevices.AsNoTracking()
            .SingleAsync(d => d.Id == session.PrintBridgeDeviceId!.Value, TestContext.Current.CancellationToken);

        // Falls back to "Print Bridge" when no machine name is provided.
        Assert.Equal("Print Bridge", device.Name);
        Assert.Null(device.MachineName);
    }

    [Fact]
    public async Task Exchange_NewDeviceMode_AbandonedSession_LeavesNoDeviceRecord()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId, PrintBridgeSetupMode.NewDevice,
            null, ServerUrl, null,
            confirmReplaceActiveToken: false, CancellationToken.None);

        var deviceCountBefore = await _db.PrintBridgeDevices
            .CountAsync(d => d.TenantId == _tenantId, TestContext.Current.CancellationToken);

        // Expire the session without ever exchanging.
        await _db.PrintBridgeSetupSessions
            .Where(s => s.Id == created.SessionId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(s => s.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)),
                TestContext.Current.CancellationToken);

        var result = await _service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);

        var deviceCountAfter = await _db.PrintBridgeDevices
            .CountAsync(d => d.TenantId == _tenantId, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(deviceCountBefore, deviceCountAfter);
    }

    [Fact]
    public async Task Exchange_NewDeviceMode_QuotaExceeded_ReturnsNullAndLeavesNoDevice()
    {
        // Fill the device quota (allowed = 3; 1 already created in constructor).
        await _devices.CreateDeviceAsync(_tenantId, "Device 2", CancellationToken.None);
        await _devices.CreateDeviceAsync(_tenantId, "Device 3", CancellationToken.None);

        var deviceCountBefore = await _db.PrintBridgeDevices
            .CountAsync(d => d.TenantId == _tenantId, TestContext.Current.CancellationToken);

        var created = await _service.CreateSessionAsync(
            _tenantId, PrintBridgeSetupMode.NewDevice,
            null, ServerUrl, null,
            confirmReplaceActiveToken: false, CancellationToken.None);

        // Exchange is attempted when quota is already at the limit.
        var result = await _service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);

        var deviceCountAfter = await _db.PrintBridgeDevices
            .CountAsync(d => d.TenantId == _tenantId, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(deviceCountBefore, deviceCountAfter); // no device leaked

        var status = await _service.GetStatusAsync(_tenantId, created.SessionId, CancellationToken.None);
        Assert.Equal(PrintBridgeSetupSessionStatus.Failed, status!.Status);
    }

    [Fact]
    public async Task Exchange_NewDeviceMode_WhenTenantLockFails_FailsSessionAndLeavesNoDevice()
    {
        var service = new PrintBridgeSetupSessionService(
            _db,
            _devices,
            new FixedTenantLockResult(-1),
            NullLogger<PrintBridgeSetupSessionService>.Instance);

        var deviceCountBefore = await _db.PrintBridgeDevices
            .CountAsync(d => d.TenantId == _tenantId, TestContext.Current.CancellationToken);

        var created = await service.CreateSessionAsync(
            _tenantId, PrintBridgeSetupMode.NewDevice,
            null, ServerUrl, null,
            confirmReplaceActiveToken: false, CancellationToken.None);

        var result = await service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);

        var deviceCountAfter = await _db.PrintBridgeDevices
            .CountAsync(d => d.TenantId == _tenantId, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(deviceCountBefore, deviceCountAfter);

        var status = await service.GetStatusAsync(_tenantId, created.SessionId, CancellationToken.None);
        Assert.Equal(PrintBridgeSetupSessionStatus.Failed, status!.Status);
    }

    [Fact]
    public async Task Exchange_NewDeviceMode_ConcurrentExchanges_DoNotExceedQuotaWithSqliteProvider()
    {
        var databaseName = $"wasla_print_bridge_concurrency_{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";

        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync(TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite(connectionString)
            .Options;

        Guid tenantId;
        string code1;
        string code2;

        await using (var setupDb = new CentralDbContext(options))
        {
            await setupDb.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            tenantId = SeedTenant(setupDb, "concurrent", "concurrent.wasla.local");

            var deviceService = CreateDeviceService(setupDb);
            await deviceService.CreateDeviceAsync(tenantId, "Device 1", TestContext.Current.CancellationToken);
            await deviceService.CreateDeviceAsync(tenantId, "Device 2", TestContext.Current.CancellationToken);

            var setupService = CreateSetupService(setupDb, deviceService);
            var session1 = await setupService.CreateSessionAsync(
                tenantId, PrintBridgeSetupMode.NewDevice,
                null, ServerUrl, null,
                confirmReplaceActiveToken: false,
                TestContext.Current.CancellationToken);
            var session2 = await setupService.CreateSessionAsync(
                tenantId, PrintBridgeSetupMode.NewDevice,
                null, ServerUrl, null,
                confirmReplaceActiveToken: false,
                TestContext.Current.CancellationToken);

            code1 = session1.Code;
            code2 = session2.Code;
        }

        var exchanges = await Task.WhenAll(
            ExchangeWithNewContextAsync(options, code1),
            ExchangeWithNewContextAsync(options, code2));

        await using var verifyDb = new CentralDbContext(options);
        var activeCount = await verifyDb.PrintBridgeDevices
            .CountAsync(d => d.TenantId == tenantId && d.IsActive, TestContext.Current.CancellationToken);
        var boundNewSessions = await verifyDb.PrintBridgeSetupSessions
            .CountAsync(s => s.TenantId == tenantId && s.PrintBridgeDeviceId != null, TestContext.Current.CancellationToken);

        Assert.True(exchanges.Count(r => r is not null) <= 1);
        Assert.True(activeCount <= PrintBridgeDeviceLimits.AllowedActiveDeviceCount);
        Assert.Equal(1, boundNewSessions);

        async Task<PrintBridgeSetupExchangeResult?> ExchangeWithNewContextAsync(
            DbContextOptions<CentralDbContext> dbOptions,
            string code)
        {
            await using var db = new CentralDbContext(dbOptions);
            var deviceService = CreateDeviceService(db);
            var setupService = CreateSetupService(db, deviceService);
            return await setupService.ExchangeAsync(
                code,
                new PrintBridgeSetupClientInfo("Concurrent POS", null, null),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Exchange_ExpiredCode_IsRejected()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.ReconnectExistingDevice,
            _deviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);

        await _db.PrintBridgeSetupSessions
            .Where(s => s.Id == created.SessionId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(s => s.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)),
                TestContext.Current.CancellationToken);

        var result = await _service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);
        Assert.Null(result);

        var status = await _service.GetStatusAsync(_tenantId, created.SessionId, CancellationToken.None);
        Assert.Equal(PrintBridgeSetupSessionStatus.Expired, status!.Status);
    }

    [Fact]
    public async Task Exchange_InvalidCode_ReturnsNull()
    {
        var result = await _service.ExchangeAsync("this-code-was-never-issued", EmptyClientInfo(), CancellationToken.None);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("ReconnectExistingDevice")]
    public async Task Exchange_InvalidPersistedSetupMode_FailsClosedAndDoesNotRotateToken(string setupMode)
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.ReconnectExistingDevice,
            _deviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);
        var tokenHashBefore = await GetTokenHashAsync(_deviceId);

        await _db.PrintBridgeSetupSessions
            .Where(s => s.Id == created.SessionId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(s => s.SetupMode, setupMode),
                TestContext.Current.CancellationToken);

        var result = await _service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(tokenHashBefore, await GetTokenHashAsync(_deviceId));

        var status = await _service.GetStatusAsync(_tenantId, created.SessionId, CancellationToken.None);
        Assert.Equal(PrintBridgeSetupSessionStatus.Failed, status!.Status);
    }

    // ── Completion ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Complete_WithCorrectCredential_RecordsCompletion()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.ReconnectExistingDevice,
            _deviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);
        var exchange = await _service.ExchangeAsync(created.Code, EmptyClientInfo(), CancellationToken.None);

        var wrong = await _service.CompleteAsync(created.SessionId, "wrong-credential", true, CancellationToken.None);
        Assert.False(wrong);

        var ok = await _service.CompleteAsync(created.SessionId, exchange!.CompletionCredential, true, CancellationToken.None);
        Assert.True(ok);

        var status = await _service.GetStatusAsync(_tenantId, created.SessionId, CancellationToken.None);
        Assert.Equal(PrintBridgeSetupSessionStatus.Completed, status!.Status);
        Assert.True(status.ConnectionVerified);
    }

    // ── Status isolation ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_ForDifferentTenant_ReturnsNull()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            PrintBridgeSetupMode.ReconnectExistingDevice,
            _deviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);

        var status = await _service.GetStatusAsync(Guid.NewGuid(), created.SessionId, CancellationToken.None);
        Assert.Null(status);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<string> GetTokenHashAsync(Guid deviceId) =>
        await _db.PrintBridgeDevices
            .AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => d.TokenHash)
            .SingleAsync(TestContext.Current.CancellationToken);

    private static PrintBridgeSetupClientInfo EmptyClientInfo() =>
        new(null, null, null);

    private Guid SeedTenant(string slug = "sushim", string primaryDomain = "sushim.wasla.local")
        => SeedTenant(_db, slug, primaryDomain);

    private static Guid SeedTenant(CentralDbContext db, string slug, string primaryDomain)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = slug,
            PrimaryDomain = primaryDomain,
            DatabaseName = $"Wasla_Tenant_{slug}",
            EncryptedConnectionString = "encrypted",
            EncryptionKeyVersion = 1,
            SchemaVersion = "1.0",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant.Id;
    }

    private static PrintBridgeDeviceManagementService CreateDeviceService(CentralDbContext db) =>
        new(db, NullLogger<PrintBridgeDeviceManagementService>.Instance);

    private static PrintBridgeSetupSessionService CreateSetupService(
        CentralDbContext db,
        PrintBridgeDeviceManagementService devices,
        IPrintBridgeSetupTenantLock? tenantLock = null) =>
        new(
            db,
            devices,
            tenantLock ?? SuccessfulTenantLock.Instance,
            NullLogger<PrintBridgeSetupSessionService>.Instance);

    private sealed class SuccessfulTenantLock : IPrintBridgeSetupTenantLock
    {
        public static SuccessfulTenantLock Instance { get; } = new();

        private SuccessfulTenantLock()
        {
        }

        public Task<int> AcquireNewDeviceExchangeLockAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult(0);
    }

    private sealed class FixedTenantLockResult : IPrintBridgeSetupTenantLock
    {
        private readonly int _result;

        public FixedTenantLockResult(int result)
        {
            _result = result;
        }

        public Task<int> AcquireNewDeviceExchangeLockAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
