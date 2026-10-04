using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Signup;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

public sealed class TenantBusinessSubtypeReader : ITenantBusinessSubtypeReader
{
    private readonly CentralDbContext _central;

    public TenantBusinessSubtypeReader(CentralDbContext central)
    {
        _central = central;
    }

    public async Task<IReadOnlyList<string>?> GetSubtypeCodesAsync(Guid tenantId, CancellationToken ct)
    {
        var registrationId = await _central.PendingRegistrations.AsNoTracking()
            .Where(registration => registration.TenantId == tenantId)
            .OrderByDescending(registration => registration.CreatedAtUtc)
            .Select(registration => (Guid?)registration.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (registrationId is null)
            return null;

        var codes = await (
            from link in _central.PendingRegistrationBusinessTypes.AsNoTracking()
            join businessType in _central.BusinessTypes.AsNoTracking()
                on link.BusinessTypeId equals businessType.Id
            where link.PendingRegistrationId == registrationId
            orderby businessType.SortOrder
            select businessType.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return codes
            .Where(BusinessSubtypeCatalog.IsSubtypeCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
