using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeDeviceManagementServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CentralDbContext _db;
    private readonly PrintBridgeDeviceManagementService _service;
    private readonly TestActivePrintJobChecker _activePrintJobChecker;
    private readonly Guid _tenantId;

    public PrintBridgeDeviceManagementServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new CentralDbContext(options);
        _db.Database.EnsureCreated();
        _tenantId = SeedTenant("sushim", "sushim.wasla.local");
        _activePrintJobChecker = new TestActivePrintJobChecker();
        _service = new PrintBridgeDeviceManagementService(
            _db,
            NullLogger<PrintBridgeDeviceManagementService>.Instance,
            _activePrintJobChecker);
    }

    [Fact]
    public async Task GetDeviceDetails_TenantDevice_ReturnsSafeDeviceDetails()
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak");

        var details = await _service.GetDeviceDetailsAsync(_tenantId, deviceId, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal("Ana Mutfak", details.Name);
        Assert.Equal("DESKTOP-CTFGHRE", details.MachineName);
        Assert.Null(details.LocalAlias);
        Assert.Equal("POS-58", details.PrinterName);
        Assert.Equal("1.0.0", details.AppVersion);
        Assert.True(details.IsActive);
        Assert.True(details.HasToken);
    }

    [Fact]
    public async Task GetDeviceDetails_ForeignTenantDevice_ReturnsNull()
    {
        var foreignTenantId = SeedTenant("foreign", "foreign.wasla.local");
        var foreignDeviceId = await SeedDeviceAsync("Foreign", foreignTenantId);

        var details = await _service.GetDeviceDetailsAsync(_tenantId, foreignDeviceId, CancellationToken.None);

        Assert.Null(details);
    }

    [Fact]
    public async Task ListDevices_UsesWebDisplayNameAsPrimaryName()
    {
        await SeedDeviceAsync("Ana Mutfak");

        var devices = await _service.ListDevicesAsync(_tenantId, CancellationToken.None);

        var device = Assert.Single(devices);
        Assert.Equal("Ana Mutfak", device.Name);
        Assert.Equal("DESKTOP-CTFGHRE", device.MachineName);
        Assert.Null(device.LocalAlias);
    }

    [Fact]
    public async Task RenameDevice_TenantDevice_ChangesOnlyWebDisplayName()
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak");
        var before = await LoadDeviceAsync(deviceId);
        var tokenHashBefore = before.TokenHash;
        var machineNameBefore = before.MachineName;
        var printerNameBefore = before.PrinterName;
        var appVersionBefore = before.AppVersion;
        var lastSeenBefore = before.LastSeenAt;
        var isActiveBefore = before.IsActive;

        var result = await _service.UpdateDeviceNameAsync(
            _tenantId,
            deviceId,
            "  Bar Yazıcısı  ",
            CancellationToken.None);

        Assert.True(result.Success);
        var after = await LoadDeviceAsync(deviceId);
        Assert.Equal("Bar Yazıcısı", after.Name);
        Assert.Equal(tokenHashBefore, after.TokenHash);
        Assert.Equal(machineNameBefore, after.MachineName);
        Assert.Equal(printerNameBefore, after.PrinterName);
        Assert.Equal(appVersionBefore, after.AppVersion);
        Assert.Equal(lastSeenBefore, after.LastSeenAt);
        Assert.Equal(isActiveBefore, after.IsActive);

        var details = await _service.GetDeviceDetailsAsync(_tenantId, deviceId, CancellationToken.None);
        Assert.Null(details!.LocalAlias);
        Assert.True(details.IsConnected);
        Assert.True(details.IsActive);
    }

    [Fact]
    public async Task RenameDevice_ForeignTenantDevice_IsRejected()
    {
        var foreignTenantId = SeedTenant("foreign", "foreign.wasla.local");
        var foreignDeviceId = await SeedDeviceAsync("Foreign", foreignTenantId);

        var result = await _service.UpdateDeviceNameAsync(
            _tenantId,
            foreignDeviceId,
            "Hacked",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("PrintBridge.DeviceNotFound", result.ErrorKey);
        var foreignDevice = await LoadDeviceAsync(foreignDeviceId);
        Assert.Equal("Foreign", foreignDevice.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RenameDevice_BlankName_IsRejected(string name)
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak");

        var result = await _service.UpdateDeviceNameAsync(_tenantId, deviceId, name, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("PrintBridge.RenameNameRequired", result.ErrorKey);
        var device = await LoadDeviceAsync(deviceId);
        Assert.Equal("Ana Mutfak", device.Name);
    }

    [Fact]
    public async Task RenameDevice_OversizedName_IsRejected()
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak");
        var oversized = new string('x', PrintBridgeDeviceNameRules.MaxWebDisplayNameLength + 1);

        var result = await _service.UpdateDeviceNameAsync(_tenantId, deviceId, oversized, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("PrintBridge.RenameNameTooLong", result.ErrorKey);
        var device = await LoadDeviceAsync(deviceId);
        Assert.Equal("Ana Mutfak", device.Name);
    }

    [Fact]
    public async Task RenameDevice_SameName_IsSafe()
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak");

        var result = await _service.UpdateDeviceNameAsync(_tenantId, deviceId, "Ana Mutfak", CancellationToken.None);

        Assert.True(result.Success);
        var device = await LoadDeviceAsync(deviceId);
        Assert.Equal("Ana Mutfak", device.Name);
    }

    [Fact]
    public async Task RemoveDevice_TenantDevice_RetiresDeviceAndRevokesToken()
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak");
        var before = await LoadDeviceAsync(deviceId);
        var tokenHashBefore = before.TokenHash;

        var result = await _service.RemoveDeviceAsync(_tenantId, deviceId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(RemovePrintBridgeDeviceOutcome.Removed, result.Outcome);
        var after = await LoadDeviceAsync(deviceId);
        Assert.NotNull(after.RemovedAtUtc);
        Assert.False(after.IsActive);
        Assert.NotEqual(tokenHashBefore, after.TokenHash);
        Assert.NotEmpty(after.TokenHash);
    }

    [Fact]
    public async Task RemoveDevice_ForeignTenantDevice_IsRejected()
    {
        var foreignTenantId = SeedTenant("foreign", "foreign.wasla.local");
        var foreignDeviceId = await SeedDeviceAsync("Foreign", foreignTenantId);

        var result = await _service.RemoveDeviceAsync(_tenantId, foreignDeviceId, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(RemovePrintBridgeDeviceOutcome.NotFound, result.Outcome);
        var foreignDevice = await LoadDeviceAsync(foreignDeviceId);
        Assert.Null(foreignDevice.RemovedAtUtc);
        Assert.True(foreignDevice.IsActive);
    }

    [Fact]
    public async Task RemoveDevice_RepeatedRemoval_IsIdempotent()
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak");
        var first = await _service.RemoveDeviceAsync(_tenantId, deviceId, CancellationToken.None);
        var afterFirst = await LoadDeviceAsync(deviceId);
        var removedAt = afterFirst.RemovedAtUtc;
        var tokenHash = afterFirst.TokenHash;

        var second = await _service.RemoveDeviceAsync(_tenantId, deviceId, CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(RemovePrintBridgeDeviceOutcome.AlreadyRemoved, second.Outcome);
        var afterSecond = await LoadDeviceAsync(deviceId);
        Assert.Equal(removedAt, afterSecond.RemovedAtUtc);
        Assert.Equal(tokenHash, afterSecond.TokenHash);
        Assert.False(afterSecond.IsActive);
    }

    [Fact]
    public async Task RemovedDevice_IsExcludedFromNormalQueries()
    {
        var removedId = await SeedDeviceAsync("Removed");
        var activeId = await SeedDeviceAsync("Active");
        await _service.RemoveDeviceAsync(_tenantId, removedId, CancellationToken.None);

        var list = await _service.ListDevicesAsync(_tenantId, CancellationToken.None);
        var quota = await _service.GetDeviceQuotaAsync(_tenantId, CancellationToken.None);
        var removedDetails = await _service.GetDeviceDetailsAsync(_tenantId, removedId, CancellationToken.None);

        Assert.DoesNotContain(list, d => d.Id == removedId);
        Assert.Contains(list, d => d.Id == activeId);
        Assert.Equal(1, quota.ActiveDeviceCount);
        Assert.Null(removedDetails);
    }

    [Fact]
    public async Task RemovedDevice_CannotBeManagedAgain()
    {
        var deviceId = await SeedDeviceAsync("Removed");
        await _service.RemoveDeviceAsync(_tenantId, deviceId, CancellationToken.None);

        var rename = await _service.UpdateDeviceNameAsync(_tenantId, deviceId, "New", CancellationToken.None);
        var setActive = await _service.SetDeviceActiveAsync(_tenantId, deviceId, true, CancellationToken.None);

        Assert.False(rename.Success);
        Assert.Equal("PrintBridge.DeviceNotFound", rename.ErrorKey);
        Assert.False(setActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RegenerateTokenAsync(_tenantId, deviceId, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveDevice_BlockedByLegacySameNamePrintingJob_AndDoesNotModifyJob()
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak");
        _activePrintJobChecker.SetActivePrintingJob(_tenantId, status: PrintJobStatus.Printing, lockedBy: "Ana Mutfak");

        var result = await _service.RemoveDeviceAsync(_tenantId, deviceId, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(RemovePrintBridgeDeviceOutcome.ActivePrintJobInProgress, result.Outcome);
        var device = await LoadDeviceAsync(deviceId);
        Assert.Null(device.RemovedAtUtc);
        Assert.True(device.IsActive);

        var job = _activePrintJobChecker.GetJob(_tenantId);
        Assert.Equal(PrintJobStatus.Printing, job.Status);
        Assert.Equal("Ana Mutfak", job.LockedBy);
    }

    [Fact]
    public async Task RemoveDevice_AllowedWhenDifferentDeviceHasPrintingJob()
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak", installationId: Guid.NewGuid());
        _activePrintJobChecker.SetActivePrintingJob(
            _tenantId,
            status: PrintJobStatus.Printing,
            lockedBy: "Different mutable name",
            installationId: Guid.NewGuid());

        var result = await _service.RemoveDeviceAsync(_tenantId, deviceId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(RemovePrintBridgeDeviceOutcome.Removed, result.Outcome);
    }

    [Fact]
    public async Task RemoveDevice_AllowedWhenNoPrintingJob()
    {
        var deviceId = await SeedDeviceAsync("Ana Mutfak");
        _activePrintJobChecker.SetActivePrintingJob(_tenantId, status: PrintJobStatus.Pending, lockedBy: "Ana Mutfak");

        var result = await _service.RemoveDeviceAsync(_tenantId, deviceId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(RemovePrintBridgeDeviceOutcome.Removed, result.Outcome);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Guid> SeedDeviceAsync(string name, Guid? tenantId = null, Guid? installationId = null)
    {
        var result = await _service.CreateDeviceAsync(tenantId ?? _tenantId, name, CancellationToken.None);
        var device = await LoadDeviceAsync(result.DeviceId);
        device.InstallationId = installationId;
        device.MachineName = "DESKTOP-CTFGHRE";
        device.PrinterName = "POS-58";
        device.AppVersion = "1.0.0";
        device.LastSeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return result.DeviceId;
    }

    private async Task<PrintBridgeDevice> LoadDeviceAsync(Guid deviceId) =>
        await _db.PrintBridgeDevices
            .SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);

    private Guid SeedTenant(string slug, string primaryDomain)
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

        _db.Tenants.Add(tenant);
        _db.SaveChanges();
        return tenant.Id;
    }

    private sealed class TestActivePrintJobChecker : IPrintBridgeActivePrintJobChecker
    {
        private readonly Dictionary<Guid, TestPrintJob> _jobsByTenant = new();

        public Task<bool> HasActivePrintingJobAsync(
            Guid customerId,
            Guid? installationId,
            string? legacyLockedBy,
            CancellationToken ct)
        {
            return Task.FromResult(
                _jobsByTenant.TryGetValue(customerId, out var job)
                && job.Status == PrintJobStatus.Printing
                && ((installationId.HasValue && job.InstallationId == installationId)
                    || (job.InstallationId is null
                        && !string.IsNullOrWhiteSpace(legacyLockedBy)
                        && job.LockedBy == legacyLockedBy)));
        }

        public void SetActivePrintingJob(
            Guid customerId,
            PrintJobStatus status,
            string? lockedBy,
            Guid? installationId = null)
        {
            _jobsByTenant[customerId] = new TestPrintJob(status, lockedBy, installationId);
        }

        public TestPrintJob GetJob(Guid customerId)
        {
            return _jobsByTenant[customerId];
        }
    }

    private sealed record TestPrintJob(PrintJobStatus Status, string? LockedBy, Guid? InstallationId);
}
