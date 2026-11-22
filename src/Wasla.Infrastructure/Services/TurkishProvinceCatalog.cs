using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.ReferenceData;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Reads the active Turkish provinces of the CentralDb <c>Cities</c> catalog (81 rows when the reference data is
/// imported) in one query, plate code and name only, and orders them by their Turkish names.
/// </summary>
public sealed class TurkishProvinceCatalog : ITurkishProvinceCatalog
{
    private static readonly StringComparer TurkishOrder = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: false);

    private readonly CentralDbContext _central;

    public TurkishProvinceCatalog(CentralDbContext central)
    {
        _central = central;
    }

    public async Task<IReadOnlyList<TurkishProvince>> GetActiveProvincesAsync(CancellationToken ct)
    {
        var rows = await _central.Cities
            .AsNoTracking()
            .Where(c => c.IsActive && c.CountryCode == "TR" && c.PlateCode != null)
            .Select(c => new { c.PlateCode, c.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Where(r => ContactMessage.IsPlateCode(r.PlateCode))
            .Select(r => new TurkishProvince(r.PlateCode!, r.Name))
            .OrderBy(p => p.Name, TurkishOrder)
            .ToList();
    }
}
