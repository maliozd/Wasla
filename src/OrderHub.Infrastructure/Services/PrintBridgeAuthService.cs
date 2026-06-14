using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Printing;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Security;

namespace OrderHub.Infrastructure.Services;

public sealed class PrintBridgeAuthService : IPrintBridgeAuthService
{
    private readonly CentralDbContext _centralDb;
    private readonly ILogger<PrintBridgeAuthService> _logger;

    public PrintBridgeAuthService(CentralDbContext centralDb, ILogger<PrintBridgeAuthService> logger)
    {
        _centralDb = centralDb;
        _logger = logger;
    }

    public async Task<PrintBridgeAuthContext?> AuthenticateAsync(
        string rawToken,
        PrintBridgeClientInfo clientInfo,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return null;

        string tokenHash;
        try
        {
            tokenHash = PrintBridgeTokenHasher.HashToken(rawToken);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var device = await _centralDb.PrintBridgeDevices
            .Include(d => d.Tenant)
            .FirstOrDefaultAsync(d => d.TokenHash == tokenHash && d.IsActive, ct)
            .ConfigureAwait(false);

        if (device is null)
        {
            _logger.LogWarning("Print Bridge authentication failed: device not found or inactive.");
            return null;
        }

        if (device.Tenant is null || !device.Tenant.IsActive)
        {
            _logger.LogWarning(
                "Print Bridge authentication failed: tenant inactive. DeviceId={DeviceId}, TenantId={TenantId}",
                device.Id,
                device.TenantId);
            return null;
        }

        var now = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(clientInfo.BridgeName))
        {
            var machineName = clientInfo.BridgeName.Trim();
            if (machineName.Length > 200)
                machineName = machineName[..200];

            device.MachineName = machineName;
        }

        if (!string.IsNullOrWhiteSpace(clientInfo.AppVersion))
            device.AppVersion = clientInfo.AppVersion.Trim();

        if (!string.IsNullOrWhiteSpace(clientInfo.PrinterName))
            device.PrinterName = clientInfo.PrinterName.Trim();

        if (!string.IsNullOrWhiteSpace(clientInfo.IpAddress))
            device.LastIpAddress = clientInfo.IpAddress.Trim();

        device.LastSeenAt = now;
        device.UpdatedAt = now;

        await _centralDb.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Print Bridge authenticated. DeviceId={DeviceId}, TenantId={TenantId}",
            device.Id,
            device.TenantId);

        return new PrintBridgeAuthContext(
            device.Id,
            device.TenantId,
            device.Tenant.Name,
            device.Name,
            string.IsNullOrWhiteSpace(device.MachineName) ? null : device.MachineName);
    }
}
