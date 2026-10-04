using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;

namespace Wasla.Infrastructure.Services;

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
        var result = await AuthenticateDetailedAsync(rawToken, clientInfo, ct).ConfigureAwait(false);
        return result.Context;
    }

    public async Task<PrintBridgeAuthResult> AuthenticateDetailedAsync(
        string rawToken,
        PrintBridgeClientInfo clientInfo,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return PrintBridgeAuthResult.Failure(PrintBridgeAuthFailureCode.MissingToken);

        string tokenHash;
        try
        {
            tokenHash = PrintBridgeTokenHasher.HashToken(rawToken);
        }
        catch (ArgumentException)
        {
            return PrintBridgeAuthResult.Failure(PrintBridgeAuthFailureCode.InvalidToken);
        }

        var device = await _centralDb.PrintBridgeDevices
            .Include(d => d.Tenant)
            .FirstOrDefaultAsync(d => d.TokenHash == tokenHash, ct)
            .ConfigureAwait(false);

        if (device is null)
        {
            _logger.LogWarning("Print Bridge authentication failed: device not found or inactive.");
            return PrintBridgeAuthResult.Failure(PrintBridgeAuthFailureCode.InvalidToken);
        }

        if (device.RemovedAtUtc is not null)
        {
            _logger.LogWarning(
                "Print Bridge authentication failed: device removed. DeviceId={DeviceId}, TenantId={TenantId}",
                device.Id,
                device.TenantId);
            return PrintBridgeAuthResult.Failure(PrintBridgeAuthFailureCode.DeviceRemoved);
        }

        if (!device.IsActive)
        {
            _logger.LogWarning(
                "Print Bridge authentication failed: device disabled. DeviceId={DeviceId}, TenantId={TenantId}",
                device.Id,
                device.TenantId);
            return PrintBridgeAuthResult.Failure(PrintBridgeAuthFailureCode.DeviceDisabled);
        }

        if (device.Tenant is null || !device.Tenant.IsActive)
        {
            _logger.LogWarning(
                "Print Bridge authentication failed: tenant inactive. DeviceId={DeviceId}, TenantId={TenantId}",
                device.Id,
                device.TenantId);
            return PrintBridgeAuthResult.Failure(PrintBridgeAuthFailureCode.TenantInactive);
        }

        var now = DateTime.UtcNow;

        if (clientInfo.InstallationId.HasValue)
        {
            var installationId = clientInfo.InstallationId.Value;
            if (installationId == Guid.Empty)
            {
                _logger.LogWarning(
                    "Print Bridge authentication failed: installation identity is empty. DeviceId={DeviceId}, TenantId={TenantId}",
                    device.Id,
                    device.TenantId);
                return PrintBridgeAuthResult.Failure(PrintBridgeAuthFailureCode.InstallationIdInvalid);
            }

            if (device.InstallationId.HasValue && device.InstallationId.Value != installationId)
            {
                _logger.LogWarning(
                    "Print Bridge authentication failed: installation identity mismatch. DeviceId={DeviceId}, TenantId={TenantId}",
                    device.Id,
                    device.TenantId);
                return PrintBridgeAuthResult.Failure(PrintBridgeAuthFailureCode.InstallationIdMismatch);
            }

            if (!device.InstallationId.HasValue)
            {
                var activeDuplicate = await _centralDb.PrintBridgeDevices
                    .AsNoTracking()
                    .AnyAsync(d => d.TenantId == device.TenantId
                        && d.Id != device.Id
                        && d.InstallationId == installationId
                        && d.RemovedAtUtc == null, ct)
                    .ConfigureAwait(false);

                if (activeDuplicate)
                {
                    _logger.LogWarning(
                        "Print Bridge authentication failed: installation identity is already bound to another active device. TenantId={TenantId}",
                        device.TenantId);
                    return PrintBridgeAuthResult.Failure(PrintBridgeAuthFailureCode.InstallationIdAlreadyBound);
                }

                device.InstallationId = installationId;
            }
        }

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

        return PrintBridgeAuthResult.Success(new PrintBridgeAuthContext(
            device.Id,
            device.TenantId,
            device.Tenant.Name,
            device.Name,
            device.InstallationId,
            string.IsNullOrWhiteSpace(device.MachineName) ? null : device.MachineName));
    }
}
