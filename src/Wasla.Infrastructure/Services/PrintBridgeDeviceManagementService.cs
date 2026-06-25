using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;

namespace Wasla.Infrastructure.Services;

public sealed class PrintBridgeDeviceManagementService : IPrintBridgeDeviceManagementService
{
    private readonly CentralDbContext _centralDb;
    private readonly IPrintBridgeActivePrintJobChecker _activePrintJobChecker;
    private readonly ILogger<PrintBridgeDeviceManagementService> _logger;

    public PrintBridgeDeviceManagementService(
        CentralDbContext centralDb,
        ILogger<PrintBridgeDeviceManagementService> logger,
        IPrintBridgeActivePrintJobChecker activePrintJobChecker)
    {
        _centralDb = centralDb;
        _logger = logger;
        _activePrintJobChecker = activePrintJobChecker;
    }

    public async Task<IReadOnlyList<PrintBridgeDeviceSummaryDto>> ListDevicesAsync(
        Guid customerId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var rows = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .WhereManageable(customerId)
            .OrderByDescending(d => d.LastSeenAt ?? d.CreatedAt)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.IsActive,
                d.LastSeenAt,
                d.MachineName,
                d.PrinterName,
                d.AppVersion
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(d => new PrintBridgeDeviceSummaryDto(
            d.Id,
            d.Name,
            d.IsActive,
            d.LastSeenAt,
            d.MachineName,
            LocalAlias: null,
            d.PrinterName,
            d.AppVersion,
            PrintBridgeConnectionStatusCalculator.Calculate(d.IsActive, d.LastSeenAt, now)))
            .ToList();
    }

    public async Task<PrintBridgeDeviceQuotaDto> GetDeviceQuotaAsync(Guid customerId, CancellationToken ct)
    {
        var activeCount = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .WhereManageable(customerId)
            .CountAsync(d => d.IsActive, ct)
            .ConfigureAwait(false);

        return BuildQuota(activeCount);
    }

    public async Task<PrintBridgeDeviceDetailsDto?> GetDeviceDetailsAsync(
        Guid customerId,
        Guid deviceId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var device = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .WhereManageable(customerId)
            .Where(d => d.Id == deviceId)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.IsActive,
                d.CreatedAt,
                d.LastSeenAt,
                d.MachineName,
                d.PrinterName,
                d.AppVersion,
                HasToken = d.TokenHash != string.Empty
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (device is null)
            return null;

        return new PrintBridgeDeviceDetailsDto(
            device.Id,
            device.Name,
            device.IsActive,
            device.CreatedAt,
            device.LastSeenAt,
            device.MachineName,
            LocalAlias: null,
            device.PrinterName,
            device.AppVersion,
            PrintBridgeConnectionStatusCalculator.Calculate(device.IsActive, device.LastSeenAt, now),
            device.HasToken);
    }

    public Task<GeneratePrintBridgeTokenResult> CreateDeviceAsync(
        Guid customerId,
        string deviceName,
        CancellationToken ct) =>
        CreateDeviceInternalAsync(customerId, deviceName, ct);

    public async Task<GeneratePrintBridgeTokenResult> RegenerateTokenAsync(
        Guid customerId,
        Guid deviceId,
        CancellationToken ct)
    {
        var device = await _centralDb.PrintBridgeDevices
            .WhereManageable(customerId)
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct)
            .ConfigureAwait(false);

        if (device is null)
            throw new InvalidOperationException("Print Bridge device not found.");

        var rawToken = PrintBridgeTokenHasher.GenerateRawToken();
        device.TokenHash = PrintBridgeTokenHasher.HashToken(rawToken);
        device.UpdatedAt = DateTime.UtcNow;

        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Print Bridge device token regenerated. DeviceId={DeviceId}, CustomerId={CustomerId}",
            device.Id,
            customerId);

        return new GeneratePrintBridgeTokenResult(device.Id, rawToken, device.Name);
    }

    public async Task<bool> SetDeviceActiveAsync(
        Guid customerId,
        Guid deviceId,
        bool isActive,
        CancellationToken ct)
    {
        var device = await _centralDb.PrintBridgeDevices
            .WhereManageable(customerId)
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct)
            .ConfigureAwait(false);

        if (device is null) return false;

        if (isActive && !device.IsActive)
        {
            var activeOthers = await _centralDb.PrintBridgeDevices
                .AsNoTracking()
                .WhereManageable(customerId)
                .CountAsync(d => d.IsActive && d.Id != deviceId, ct)
                .ConfigureAwait(false);

            if (activeOthers >= PrintBridgeDeviceLimits.AllowedActiveDeviceCount)
                throw new PrintBridgeDeviceLimitReachedException();
        }

        device.IsActive = isActive;
        device.UpdatedAt = DateTime.UtcNow;
        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Print Bridge device active state changed. DeviceId={DeviceId}, CustomerId={CustomerId}, IsActive={IsActive}",
            deviceId,
            customerId,
            isActive);

        return true;
    }

    public async Task<RemovePrintBridgeDeviceResult> RemoveDeviceAsync(
        Guid customerId,
        Guid deviceId,
        CancellationToken ct)
    {
        var device = await _centralDb.PrintBridgeDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.TenantId == customerId, ct)
            .ConfigureAwait(false);

        if (device is null)
            return new RemovePrintBridgeDeviceResult(
                RemovePrintBridgeDeviceOutcome.NotFound,
                "PrintBridge.DeviceNotFound");

        if (device.RemovedAtUtc is not null)
            return new RemovePrintBridgeDeviceResult(
                RemovePrintBridgeDeviceOutcome.AlreadyRemoved,
                "PrintBridge.DeviceAlreadyRemoved");

        if (await _activePrintJobChecker
                .HasActivePrintingJobAsync(customerId, device.InstallationId, device.Name, ct)
                .ConfigureAwait(false))
        {
            return new RemovePrintBridgeDeviceResult(RemovePrintBridgeDeviceOutcome.ActivePrintJobInProgress, "PrintBridge.DeviceRemoveBlockedByActivePrintJob");
        }

        var now = DateTime.UtcNow;
        device.RemovedAtUtc = now;
        device.IsActive = false;
        device.TokenHash = PrintBridgeTokenHasher.HashToken(PrintBridgeTokenHasher.GenerateRawToken());
        device.UpdatedAt = now;

        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Print Bridge device removed. DeviceId={DeviceId}, CustomerId={CustomerId}",
            deviceId,
            customerId);

        return new RemovePrintBridgeDeviceResult(RemovePrintBridgeDeviceOutcome.Removed, "PrintBridge.DeviceRemoveSucceeded");
    }

    public async Task<RenamePrintBridgeDeviceResult> UpdateDeviceNameAsync(
        Guid customerId,
        Guid deviceId,
        string deviceName,
        CancellationToken ct)
    {
        var name = (deviceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return new RenamePrintBridgeDeviceResult(false, "PrintBridge.RenameNameRequired");

        if (name.Length > PrintBridgeDeviceNameRules.MaxWebDisplayNameLength)
            return new RenamePrintBridgeDeviceResult(false, "PrintBridge.RenameNameTooLong");

        var device = await _centralDb.PrintBridgeDevices
            .WhereManageable(customerId)
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct)
            .ConfigureAwait(false);

        if (device is null)
            return new RenamePrintBridgeDeviceResult(false, "PrintBridge.DeviceNotFound");

        if (!string.Equals(device.Name, name, StringComparison.Ordinal))
        {
            device.Name = name;
            device.UpdatedAt = DateTime.UtcNow;
            await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new RenamePrintBridgeDeviceResult(true);
    }

    private async Task<GeneratePrintBridgeTokenResult> CreateDeviceInternalAsync(
        Guid customerId,
        string deviceName,
        CancellationToken ct)
    {
        var name = NormalizeName(deviceName);

        var customerExists = await _centralDb.Tenants
            .AsNoTracking()
            .AnyAsync(c => c.Id == customerId && c.IsActive, ct)
            .ConfigureAwait(false);

        if (!customerExists)
            throw new InvalidOperationException("Tenant not found or inactive.");

        var activeCount = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .WhereManageable(customerId)
            .CountAsync(d => d.IsActive, ct)
            .ConfigureAwait(false);

        if (activeCount >= PrintBridgeDeviceLimits.AllowedActiveDeviceCount)
            throw new PrintBridgeDeviceLimitReachedException();

        var rawToken = PrintBridgeTokenHasher.GenerateRawToken();
        var tokenHash = PrintBridgeTokenHasher.HashToken(rawToken);
        var now = DateTime.UtcNow;

        var device = new PrintBridgeDevice
        {
            TenantId = customerId,
            Name = name,
            TokenHash = tokenHash,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _centralDb.PrintBridgeDevices.Add(device);
        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Print Bridge device created. DeviceId={DeviceId}, CustomerId={CustomerId}, DeviceName={DeviceName}",
            device.Id,
            customerId,
            name);

        return new GeneratePrintBridgeTokenResult(device.Id, rawToken, name);
    }

    private static PrintBridgeDeviceQuotaDto BuildQuota(int activeCount) =>
        new(
            PrintBridgeDeviceLimits.AllowedActiveDeviceCount,
            activeCount,
            activeCount < PrintBridgeDeviceLimits.AllowedActiveDeviceCount,
            activeCount > PrintBridgeDeviceLimits.AllowedActiveDeviceCount);

    private static string NormalizeName(string deviceName)
    {
        var name = (deviceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Print Bridge";
        if (name.Length > 200)
            name = name[..200];
        return name;
    }
}

internal static class PrintBridgeDeviceQueryExtensions
{
    public static IQueryable<PrintBridgeDevice> WhereManageable(
        this IQueryable<PrintBridgeDevice> query,
        Guid tenantId) =>
        query.Where(d => d.TenantId == tenantId && d.RemovedAtUtc == null);
}
