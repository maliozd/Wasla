using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Printing;
using OrderHub.Domain.Entities.Central;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Security;

namespace OrderHub.Infrastructure.Services;

public sealed class PrintBridgeDeviceManagementService : IPrintBridgeDeviceManagementService
{
    private static readonly TimeSpan ConnectedThreshold = TimeSpan.FromMinutes(5);

    private readonly CentralDbContext _centralDb;
    private readonly ILogger<PrintBridgeDeviceManagementService> _logger;

    public PrintBridgeDeviceManagementService(
        CentralDbContext centralDb,
        ILogger<PrintBridgeDeviceManagementService> logger)
    {
        _centralDb = centralDb;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PrintBridgeDeviceSummaryDto>> ListDevicesAsync(
        Guid customerId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var rows = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .Where(d => d.CustomerId == customerId)
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
            d.PrinterName,
            d.AppVersion,
            d.IsActive && d.LastSeenAt.HasValue && now - d.LastSeenAt.Value <= ConnectedThreshold))
            .ToList();
    }

    public async Task<PrintBridgeDeviceQuotaDto> GetDeviceQuotaAsync(Guid customerId, CancellationToken ct)
    {
        var activeCount = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .CountAsync(d => d.CustomerId == customerId && d.IsActive, ct)
            .ConfigureAwait(false);

        return BuildQuota(activeCount);
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
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.CustomerId == customerId, ct)
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
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.CustomerId == customerId, ct)
            .ConfigureAwait(false);

        if (device is null) return false;

        if (isActive && !device.IsActive)
        {
            var activeOthers = await _centralDb.PrintBridgeDevices
                .AsNoTracking()
                .CountAsync(d => d.CustomerId == customerId && d.IsActive && d.Id != deviceId, ct)
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

    public async Task<bool> UpdateDeviceNameAsync(
        Guid customerId,
        Guid deviceId,
        string deviceName,
        CancellationToken ct)
    {
        var name = NormalizeName(deviceName);
        var device = await _centralDb.PrintBridgeDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.CustomerId == customerId, ct)
            .ConfigureAwait(false);

        if (device is null) return false;

        device.Name = name;
        device.UpdatedAt = DateTime.UtcNow;
        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<GeneratePrintBridgeTokenResult> CreateDeviceInternalAsync(
        Guid customerId,
        string deviceName,
        CancellationToken ct)
    {
        var name = NormalizeName(deviceName);

        var customerExists = await _centralDb.Customers
            .AsNoTracking()
            .AnyAsync(c => c.Id == customerId && c.IsActive, ct)
            .ConfigureAwait(false);

        if (!customerExists)
            throw new InvalidOperationException("Customer not found or inactive.");

        var activeCount = await _centralDb.PrintBridgeDevices
            .AsNoTracking()
            .CountAsync(d => d.CustomerId == customerId && d.IsActive, ct)
            .ConfigureAwait(false);

        if (activeCount >= PrintBridgeDeviceLimits.AllowedActiveDeviceCount)
            throw new PrintBridgeDeviceLimitReachedException();

        var rawToken = PrintBridgeTokenHasher.GenerateRawToken();
        var tokenHash = PrintBridgeTokenHasher.HashToken(rawToken);
        var now = DateTime.UtcNow;

        var device = new PrintBridgeDevice
        {
            CustomerId = customerId,
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
