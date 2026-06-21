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

        var devices = new PrintBridgeDeviceManagementService(_db, NullLogger<PrintBridgeDeviceManagementService>.Instance);
        _deviceId = devices.CreateDeviceAsync(_tenantId, "Kitchen PC", CancellationToken.None)
            .GetAwaiter().GetResult().DeviceId;

        _service = new PrintBridgeSetupSessionService(
            _db,
            devices,
            NullLogger<PrintBridgeSetupSessionService>.Instance);
    }

    [Fact]
    public async Task CreateSession_PersistsHashedCode_AndShortLivedExpiry()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
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
                _deviceId,
                ServerUrl,
                "Kitchen PC",
                confirmReplaceActiveToken: false,
                CancellationToken.None));
    }

    [Fact]
    public async Task Exchange_ValidCode_SucceedsOnce_AndSecondAttemptFails()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            _deviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);

        var first = await _service.ExchangeAsync(created.Code, CancellationToken.None);
        Assert.NotNull(first);
        Assert.False(string.IsNullOrWhiteSpace(first!.DeviceToken));
        Assert.False(string.IsNullOrWhiteSpace(first.CompletionCredential));
        Assert.Equal(ServerUrl, first.ServerUrl);

        // Single-use: a second exchange of the same code must fail.
        var second = await _service.ExchangeAsync(created.Code, CancellationToken.None);
        Assert.Null(second);
    }

    [Fact]
    public async Task Exchange_ExpiredCode_IsRejected()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
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

        var result = await _service.ExchangeAsync(created.Code, CancellationToken.None);
        Assert.Null(result);

        var status = await _service.GetStatusAsync(_tenantId, created.SessionId, CancellationToken.None);
        Assert.Equal(PrintBridgeSetupSessionStatus.Expired, status!.Status);
    }

    [Fact]
    public async Task Exchange_InvalidCode_ReturnsNull()
    {
        var result = await _service.ExchangeAsync("this-code-was-never-issued", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Complete_WithCorrectCredential_RecordsCompletion()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            _deviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);
        var exchange = await _service.ExchangeAsync(created.Code, CancellationToken.None);

        var wrong = await _service.CompleteAsync(created.SessionId, "wrong-credential", true, CancellationToken.None);
        Assert.False(wrong);

        var ok = await _service.CompleteAsync(created.SessionId, exchange!.CompletionCredential, true, CancellationToken.None);
        Assert.True(ok);

        var status = await _service.GetStatusAsync(_tenantId, created.SessionId, CancellationToken.None);
        Assert.Equal(PrintBridgeSetupSessionStatus.Completed, status!.Status);
        Assert.True(status.ConnectionVerified);
    }

    [Fact]
    public async Task GetStatus_ForDifferentTenant_ReturnsNull()
    {
        var created = await _service.CreateSessionAsync(
            _tenantId,
            _deviceId,
            ServerUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);

        var status = await _service.GetStatusAsync(Guid.NewGuid(), created.SessionId, CancellationToken.None);
        Assert.Null(status);
    }

    private Guid SeedTenant()
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Sushi M",
            Slug = "sushim",
            PrimaryDomain = "sushim.wasla.local",
            DatabaseName = "Wasla_Tenant_sushim",
            EncryptedConnectionString = "encrypted",
            EncryptionKeyVersion = 1,
            SchemaVersion = "1.0",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Tenants.Add(tenant);
        _db.SaveChanges();
        return tenant.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
