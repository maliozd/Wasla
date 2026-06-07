using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Printing;
using OrderHub.Domain.Entities.Central;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Security;

namespace OrderHub.Infrastructure.Services;

public sealed class PrintBridgeDeviceManagementService : IPrintBridgeDeviceManagementService
{
    private readonly CentralDbContext _centralDb;
    private readonly ILogger<PrintBridgeDeviceManagementService> _logger;

    public PrintBridgeDeviceManagementService(
        CentralDbContext centralDb,
        ILogger<PrintBridgeDeviceManagementService> logger)
    {
        _centralDb = centralDb;
        _logger = logger;
    }

    public async Task<GeneratePrintBridgeTokenResult> GenerateTokenAsync(
        Guid customerId,
        string deviceName,
        CancellationToken ct)
    {
        var name = (deviceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Print Bridge";

        if (name.Length > 200)
            name = name[..200];

        var customerExists = await _centralDb.Customers
            .AsNoTracking()
            .AnyAsync(c => c.Id == customerId && c.IsActive, ct)
            .ConfigureAwait(false);

        if (!customerExists)
            throw new InvalidOperationException("Customer not found or inactive.");

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
            "Print Bridge device token generated. DeviceId={DeviceId}, CustomerId={CustomerId}, DeviceName={DeviceName}",
            device.Id,
            customerId,
            name);

        return new GeneratePrintBridgeTokenResult(device.Id, rawToken, name);
    }
}
