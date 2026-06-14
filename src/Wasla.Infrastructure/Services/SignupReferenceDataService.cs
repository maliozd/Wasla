using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Signup;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

public sealed class SignupReferenceDataService : ISignupReferenceDataService
{
    private readonly CentralDbContext _central;

    public SignupReferenceDataService(CentralDbContext central)
    {
        _central = central;
    }

    public async Task<IReadOnlyList<SignupBusinessTypeOption>> GetActiveBusinessTypesAsync(CancellationToken ct) =>
        await _central.BusinessTypes.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .Select(x => new SignupBusinessTypeOption(x.Code, x.DisplayName))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SignupCityOption>> GetActiveCitiesAsync(string countryCode, CancellationToken ct)
    {
        var normalized = NormalizeCountryCode(countryCode);
        return await _central.Cities.AsNoTracking()
            .Where(x => x.IsActive && x.CountryCode == normalized)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new SignupCityOption(x.Id, x.Name, x.PhoneAreaCode))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SignupDistrictOption>> GetDistrictsByCityIdAsync(int cityId, CancellationToken ct) =>
        await _central.Districts.AsNoTracking()
            .Where(x => x.IsActive && x.CityId == cityId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new SignupDistrictOption(x.Id, x.Name))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SignupNeighborhoodOption>> GetNeighborhoodsByDistrictIdAsync(int districtId, CancellationToken ct) =>
        await _central.Neighborhoods.AsNoTracking()
            .Where(x => x.IsActive && x.DistrictId == districtId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new SignupNeighborhoodOption(x.Id, x.Name))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SignupStreetOption>> GetStreetsByNeighborhoodIdAsync(int neighborhoodId, CancellationToken ct) =>
        await _central.Streets.AsNoTracking()
            .Where(x => x.IsActive && x.NeighborhoodId == neighborhoodId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new SignupStreetOption(x.Id, x.Name, x.StreetType))
            .ToListAsync(ct);

    public async Task<SignupCityOption?> GetCityByIdAsync(int cityId, CancellationToken ct) =>
        await _central.Cities.AsNoTracking()
            .Where(x => x.IsActive && x.Id == cityId)
            .Select(x => new SignupCityOption(x.Id, x.Name, x.PhoneAreaCode))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SignupBusinessTypeOption>> ResolveBusinessTypesByCodesAsync(
        IReadOnlyList<string> codes,
        CancellationToken ct)
    {
        if (codes.Count == 0)
            return Array.Empty<SignupBusinessTypeOption>();

        var normalized = codes
            .Select(c => c.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        var rows = await _central.BusinessTypes.AsNoTracking()
            .Where(x => x.IsActive && normalized.Contains(x.Code))
            .OrderBy(x => x.SortOrder)
            .Select(x => new SignupBusinessTypeOption(x.Code, x.DisplayName))
            .ToListAsync(ct);

        return rows;
    }

    public async Task<SignupCityDistrictNames?> ResolveCityDistrictAsync(
        int cityId,
        int districtId,
        string countryCode,
        CancellationToken ct)
    {
        var normalizedCountry = NormalizeCountryCode(countryCode);
        return await (
            from d in _central.Districts.AsNoTracking()
            join c in _central.Cities.AsNoTracking() on d.CityId equals c.Id
            where d.IsActive
                  && c.IsActive
                  && d.Id == districtId
                  && d.CityId == cityId
                  && c.CountryCode == normalizedCountry
            select new SignupCityDistrictNames(c.Id, c.Name, d.Id, d.Name))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<SignupNeighborhoodNames?> ResolveNeighborhoodAsync(
        int districtId,
        int neighborhoodId,
        CancellationToken ct) =>
        await _central.Neighborhoods.AsNoTracking()
            .Where(x => x.IsActive && x.Id == neighborhoodId && x.DistrictId == districtId)
            .Select(x => new SignupNeighborhoodNames(x.Id, x.Name))
            .FirstOrDefaultAsync(ct);

    public async Task<SignupStreetNames?> ResolveStreetAsync(
        int neighborhoodId,
        int streetId,
        CancellationToken ct) =>
        await _central.Streets.AsNoTracking()
            .Where(x => x.IsActive && x.Id == streetId && x.NeighborhoodId == neighborhoodId)
            .Select(x => new SignupStreetNames(x.Id, x.Name, x.StreetType))
            .FirstOrDefaultAsync(ct);

    private static string NormalizeCountryCode(string countryCode) =>
        string.Equals(countryCode.Trim(), "Türkiye", StringComparison.OrdinalIgnoreCase)
        || string.Equals(countryCode.Trim(), "Turkey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(countryCode.Trim(), "TR", StringComparison.OrdinalIgnoreCase)
            ? "TR"
            : countryCode.Trim().ToUpperInvariant();
}
