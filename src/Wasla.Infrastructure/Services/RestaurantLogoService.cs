using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Branding;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class RestaurantLogoService : IRestaurantLogoService
{
    private static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly ILogger<RestaurantLogoService> _logger;

    public RestaurantLogoService(ITenantDbContextFactory dbFactory, ILogger<RestaurantLogoService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<RestaurantLogoSummary?> GetCurrentAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        return await db.RestaurantLogos
            .AsNoTracking()
            .Where(x => x.Id == SingletonId)
            .Select(x => new RestaurantLogoSummary(x.Version, x.Background))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> SetBackgroundAsync(Guid tenantId, RestaurantLogoBackground background, CancellationToken ct)
    {
        if (!Enum.IsDefined(background))
            throw new ArgumentOutOfRangeException(nameof(background), background, "Unknown restaurant logo background.");

        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var updated = await db.RestaurantLogos
            .Where(x => x.Id == SingletonId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Background, background)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct)
            .ConfigureAwait(false);
        if (updated > 0)
            _logger.LogInformation("Restaurant logo background set to {Background} for tenant {TenantId}", background, tenantId);
        return updated > 0;
    }

    public async Task<RestaurantLogoContent?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        return await db.RestaurantLogos
            .AsNoTracking()
            .Where(x => x.Id == SingletonId)
            .Select(x => new RestaurantLogoContent(x.Content, x.ContentType, x.Version))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<RestaurantLogoSaveResult> SaveAsync(Guid tenantId, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var inspection = RestaurantLogoImage.Inspect(content);
        if (!inspection.IsValid)
            return new RestaurantLogoSaveResult(inspection.Error);

        var version = Convert.ToHexString(SHA256.HashData(content), 0, 16).ToLowerInvariant();

        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var row = await db.RestaurantLogos.FirstOrDefaultAsync(x => x.Id == SingletonId, ct).ConfigureAwait(false);
        if (row is null)
        {
            db.RestaurantLogos.Add(new RestaurantLogo
            {
                Id = SingletonId,
                Content = content,
                ContentType = inspection.ContentType!,
                Version = version
            });
        }
        else
        {
            row.Content = content;
            row.ContentType = inspection.ContentType!;
            row.Version = version;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Restaurant logo saved for tenant {TenantId}: {ContentType}, {Width}x{Height}, {Bytes} bytes",
            tenantId, inspection.ContentType, inspection.Width, inspection.Height, content.Length);
        return new RestaurantLogoSaveResult(RestaurantLogoError.None, version);
    }

    public async Task<bool> RemoveAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var removed = await db.RestaurantLogos
            .Where(x => x.Id == SingletonId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        if (removed > 0)
            _logger.LogInformation("Restaurant logo removed for tenant {TenantId}", tenantId);
        return removed > 0;
    }
}
