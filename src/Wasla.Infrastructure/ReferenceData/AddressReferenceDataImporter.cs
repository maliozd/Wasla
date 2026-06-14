using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.ReferenceData;

/// <summary>
/// Idempotent importer for CentralDb address reference data (country, city, district, neighborhood, street).
/// Large datasets are loaded from embedded JSON via CLI, not EF migrations.
/// </summary>
public sealed class AddressReferenceDataImporter
{
    private const string NeighborhoodResourceName =
        "Wasla.Infrastructure.ReferenceData.turkey-neighborhoods-reference-data.json";

    private const string StreetResourceName =
        "Wasla.Infrastructure.ReferenceData.turkey-streets-reference-data.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CentralDbContext _central;
    private readonly TurkeyReferenceDataSeeder _turkeySeeder;
    private readonly ILogger<AddressReferenceDataImporter> _logger;

    public AddressReferenceDataImporter(
        CentralDbContext central,
        TurkeyReferenceDataSeeder turkeySeeder,
        ILogger<AddressReferenceDataImporter> logger)
    {
        _central = central;
        _turkeySeeder = turkeySeeder;
        _logger = logger;
    }

    public async Task<AddressReferenceImportResult> ImportAsync(CancellationToken ct)
    {
        var countriesInserted = await ImportCountriesAsync(ct);
        var cityDistrictResult = await _turkeySeeder.SeedAsync(ct);
        var neighborhoodsInserted = await ImportNeighborhoodsAsync(ct);
        var streetsInserted = await ImportStreetsAsync(ct);

        _logger.LogInformation(
            "Address reference import completed. Countries inserted={CountriesInserted}, cities inserted={CitiesInserted}, districts inserted={DistrictsInserted}, neighborhoods inserted={NeighborhoodsInserted}, streets inserted={StreetsInserted}",
            countriesInserted,
            cityDistrictResult.CitiesInserted,
            cityDistrictResult.DistrictsInserted,
            neighborhoodsInserted,
            streetsInserted);

        return new AddressReferenceImportResult(
            countriesInserted,
            cityDistrictResult.CitiesInserted,
            cityDistrictResult.DistrictsInserted,
            neighborhoodsInserted,
            streetsInserted,
            cityDistrictResult.TotalCities);
    }

    private async Task<int> ImportCountriesAsync(CancellationToken ct)
    {
        var entries = new[]
        {
            new { Code = "TR", Name = "Türkiye" }
        };

        var inserted = 0;
        foreach (var entry in entries)
        {
            var country = await _central.Countries
                .FirstOrDefaultAsync(c => c.Code == entry.Code, ct);

            if (country is null)
            {
                _central.Countries.Add(new Country
                {
                    Code = entry.Code,
                    Name = entry.Name,
                    IsActive = true
                });
                inserted++;
            }
            else
            {
                country.Name = entry.Name;
                country.IsActive = true;
            }
        }

        await _central.SaveChangesAsync(ct);
        return inserted;
    }

    private async Task<int> ImportNeighborhoodsAsync(CancellationToken ct)
    {
        var data = TryLoadEmbeddedData<AddressNeighborhoodDataFile>(NeighborhoodResourceName);
        if (data?.Entries is null || data.Entries.Count == 0)
        {
            _logger.LogInformation("Neighborhood reference file not found or empty; skipping neighborhood import.");
            return 0;
        }

        var inserted = 0;
        foreach (var entry in data.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var district = await ResolveDistrictAsync(entry.CityPlateCode, entry.DistrictName, ct);
            if (district is null)
            {
                _logger.LogWarning(
                    "Skipping neighborhood import for unknown district. PlateCode={PlateCode} District={DistrictName}",
                    entry.CityPlateCode,
                    entry.DistrictName);
                continue;
            }

            var sortOrder = 1;
            foreach (var neighborhoodData in entry.Neighborhoods
                         .OrderBy(n => n.SortOrder > 0 ? n.SortOrder : int.MaxValue)
                         .ThenBy(n => n.Name, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), false)))
            {
                var name = neighborhoodData.Name.Trim();
                if (name.Length == 0)
                    continue;

                var neighborhood = await _central.Neighborhoods
                    .FirstOrDefaultAsync(n => n.DistrictId == district.Id && n.Name == name, ct);

                var order = neighborhoodData.SortOrder > 0 ? neighborhoodData.SortOrder : sortOrder;
                if (neighborhood is null)
                {
                    _central.Neighborhoods.Add(new Neighborhood
                    {
                        DistrictId = district.Id,
                        Name = name,
                        ExternalCode = string.IsNullOrWhiteSpace(neighborhoodData.ExternalCode)
                            ? null
                            : neighborhoodData.ExternalCode.Trim(),
                        SortOrder = order,
                        IsActive = true
                    });
                    inserted++;
                }
                else
                {
                    neighborhood.ExternalCode = string.IsNullOrWhiteSpace(neighborhoodData.ExternalCode)
                        ? null
                        : neighborhoodData.ExternalCode.Trim();
                    neighborhood.SortOrder = order;
                    neighborhood.IsActive = true;
                }

                sortOrder++;
            }

            await _central.SaveChangesAsync(ct);
        }

        return inserted;
    }

    private async Task<int> ImportStreetsAsync(CancellationToken ct)
    {
        var data = TryLoadEmbeddedData<AddressStreetDataFile>(StreetResourceName);
        if (data?.Entries is null || data.Entries.Count == 0)
        {
            _logger.LogInformation("Street reference file not found or empty; skipping street import.");
            return 0;
        }

        var inserted = 0;
        foreach (var entry in data.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var district = await ResolveDistrictAsync(entry.CityPlateCode, entry.DistrictName, ct);
            if (district is null)
            {
                _logger.LogWarning(
                    "Skipping street import for unknown district. PlateCode={PlateCode} District={DistrictName}",
                    entry.CityPlateCode,
                    entry.DistrictName);
                continue;
            }

            var neighborhoodName = entry.NeighborhoodName.Trim();
            var neighborhood = await _central.Neighborhoods
                .FirstOrDefaultAsync(n => n.DistrictId == district.Id && n.Name == neighborhoodName, ct);

            if (neighborhood is null)
            {
                _logger.LogWarning(
                    "Skipping street import for unknown neighborhood. DistrictId={DistrictId} Neighborhood={NeighborhoodName}",
                    district.Id,
                    neighborhoodName);
                continue;
            }

            var sortOrder = 1;
            foreach (var streetData in entry.Streets
                         .OrderBy(s => s.SortOrder > 0 ? s.SortOrder : int.MaxValue)
                         .ThenBy(s => s.Name, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), false)))
            {
                var name = streetData.Name.Trim();
                if (name.Length == 0)
                    continue;

                var streetType = NormalizeStreetType(streetData.StreetType);
                var street = await _central.Streets
                    .FirstOrDefaultAsync(s =>
                        s.NeighborhoodId == neighborhood.Id
                        && s.Name == name
                        && s.StreetType == streetType, ct);

                var order = streetData.SortOrder > 0 ? streetData.SortOrder : sortOrder;
                if (street is null)
                {
                    _central.Streets.Add(new Street
                    {
                        NeighborhoodId = neighborhood.Id,
                        Name = name,
                        StreetType = streetType,
                        ExternalCode = string.IsNullOrWhiteSpace(streetData.ExternalCode)
                            ? null
                            : streetData.ExternalCode.Trim(),
                        SortOrder = order,
                        IsActive = true
                    });
                    inserted++;
                }
                else
                {
                    street.ExternalCode = string.IsNullOrWhiteSpace(streetData.ExternalCode)
                        ? null
                        : streetData.ExternalCode.Trim();
                    street.SortOrder = order;
                    street.IsActive = true;
                }

                sortOrder++;
            }

            await _central.SaveChangesAsync(ct);
        }

        return inserted;
    }

    private async Task<District?> ResolveDistrictAsync(string cityPlateCode, string districtName, CancellationToken ct)
    {
        var plateCode = cityPlateCode.Trim();
        var name = districtName.Trim();

        return await (
            from d in _central.Districts
            join c in _central.Cities on d.CityId equals c.Id
            where c.CountryCode == "TR"
                  && c.PlateCode == plateCode
                  && d.Name == name
            select d).FirstOrDefaultAsync(ct);
    }

    private static string? NormalizeStreetType(string? streetType) =>
        string.IsNullOrWhiteSpace(streetType) ? null : streetType.Trim();

    private static T? TryLoadEmbeddedData<T>(string resourceName)
        where T : class
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;

        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }
}

public sealed record AddressReferenceImportResult(
    int CountriesInserted,
    int CitiesInserted,
    int DistrictsInserted,
    int NeighborhoodsInserted,
    int StreetsInserted,
    int TotalCities);
