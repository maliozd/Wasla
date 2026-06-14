using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Domain.Entities.Central;
using OrderHub.Infrastructure.Persistence.Central;

namespace OrderHub.Infrastructure.ReferenceData;

public sealed class TurkeyReferenceDataSeeder
{
    private const string EmbeddedResourceName = "OrderHub.Infrastructure.ReferenceData.turkey-reference-data.json";

    private readonly CentralDbContext _central;
    private readonly ILogger<TurkeyReferenceDataSeeder> _logger;

    public TurkeyReferenceDataSeeder(CentralDbContext central, ILogger<TurkeyReferenceDataSeeder> logger)
    {
        _central = central;
        _logger = logger;
    }

    public async Task<TurkeyReferenceDataSeedResult> SeedAsync(CancellationToken ct)
    {
        var data = LoadEmbeddedData();
        var countryCode = string.IsNullOrWhiteSpace(data.CountryCode) ? "TR" : data.CountryCode.Trim().ToUpperInvariant();

        var cityCount = 0;
        var districtCount = 0;

        foreach (var cityData in data.Cities.OrderBy(c => c.SortOrder))
        {
            ct.ThrowIfCancellationRequested();

            var plateCode = cityData.PlateCode.Trim();
            var cityName = cityData.Name.Trim();
            var phoneAreaCode = NormalizePhoneAreaCode(cityData.PhoneAreaCode, cityName);

            var city = await _central.Cities
                .FirstOrDefaultAsync(c =>
                    c.CountryCode == countryCode
                    && c.PlateCode == plateCode, ct);

            if (city is null)
            {
                city = await _central.Cities
                    .FirstOrDefaultAsync(c =>
                        c.CountryCode == countryCode
                        && c.Name == cityName, ct);
            }

            if (city is null)
            {
                city = new City
                {
                    CountryCode = countryCode,
                    Name = cityName,
                    PlateCode = plateCode,
                    PhoneAreaCode = phoneAreaCode,
                    SortOrder = cityData.SortOrder,
                    IsActive = true
                };
                _central.Cities.Add(city);
                cityCount++;
            }
            else
            {
                city.Name = cityName;
                city.PlateCode = plateCode;
                city.PhoneAreaCode = phoneAreaCode;
                city.SortOrder = cityData.SortOrder;
                city.IsActive = true;
            }

            await _central.SaveChangesAsync(ct);

            var sortOrder = 1;
            foreach (var districtName in cityData.Districts.Select(d => d.Trim()).Where(d => d.Length > 0).OrderBy(d => d, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), false)))
            {
                var district = await _central.Districts
                    .FirstOrDefaultAsync(d => d.CityId == city.Id && d.Name == districtName, ct);

                if (district is null)
                {
                    _central.Districts.Add(new District
                    {
                        CityId = city.Id,
                        Name = districtName,
                        SortOrder = sortOrder,
                        IsActive = true
                    });
                    districtCount++;
                }
                else
                {
                    district.SortOrder = sortOrder;
                    district.IsActive = true;
                }

                sortOrder++;
            }

            await _central.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Turkey reference data seed completed. Cities inserted={CityInsertCount}, districts inserted={DistrictInsertCount}, total cities={TotalCities}",
            cityCount,
            districtCount,
            data.Cities.Count);

        return new TurkeyReferenceDataSeedResult(cityCount, districtCount, data.Cities.Count);
    }

    private static string? NormalizePhoneAreaCode(string? phoneAreaCode, string cityName)
    {
        if (string.IsNullOrWhiteSpace(phoneAreaCode))
            return null;

        // TODO: Istanbul also uses 216; store a single primary landline prefix for now.
        if (string.Equals(cityName, "İstanbul", StringComparison.Ordinal))
            return "212";

        return phoneAreaCode.Trim();
    }

    private static TurkeyReferenceDataFile LoadEmbeddedData()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedResourceName}' was not found.");

        var data = JsonSerializer.Deserialize<TurkeyReferenceDataFile>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (data?.Cities is null || data.Cities.Count == 0)
            throw new InvalidOperationException("Turkey reference data file is empty.");

        return data;
    }
}

public sealed record TurkeyReferenceDataSeedResult(int CitiesInserted, int DistrictsInserted, int TotalCities);
