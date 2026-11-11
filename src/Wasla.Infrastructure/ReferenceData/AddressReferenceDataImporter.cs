using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.ReferenceData;

/// <summary>
/// Idempotent importer for CentralDb address reference data: the country, Türkiye's cities and districts (embedded
/// JSON), and, when a catalog source is given, the pinned neighbourhood and street catalog (WAS-214, see
/// <see cref="AddressCatalogImporter"/>). Large datasets are loaded via the CLI, not EF migrations.
/// </summary>
public sealed class AddressReferenceDataImporter
{
    private readonly CentralDbContext _central;
    private readonly TurkeyReferenceDataSeeder _turkeySeeder;
    private readonly AddressCatalogImporter _catalog;
    private readonly ILogger<AddressReferenceDataImporter> _logger;

    public AddressReferenceDataImporter(
        CentralDbContext central,
        TurkeyReferenceDataSeeder turkeySeeder,
        AddressCatalogImporter catalog,
        ILogger<AddressReferenceDataImporter> logger)
    {
        _central = central;
        _turkeySeeder = turkeySeeder;
        _catalog = catalog;
        _logger = logger;
    }

    /// <param name="catalog">
    /// The neighbourhood and street catalog to import; null imports none and says so in the result. Its source is
    /// verified before anything is written, so a bad source leaves every table as it was. A dry run writes nothing at
    /// all, so the country, cities and districts are not seeded either.
    /// </param>
    public async Task<AddressReferenceImportResult> ImportAsync(AddressCatalogImportOptions? catalog, CancellationToken ct)
    {
        var source = catalog is null ? null : await _catalog.OpenSourceAsync(catalog, ct);
        if (catalog is { DryRun: true })
            return new AddressReferenceImportResult(0, 0, 0, 0, await _catalog.RunAsync(source!, catalog, ct));

        var countriesInserted = await ImportCountriesAsync(ct);
        var cityDistrictResult = await _turkeySeeder.SeedAsync(ct);
        var report = catalog is null ? null : await _catalog.RunAsync(source!, catalog, ct);

        _logger.LogInformation(
            "Address reference import completed. Countries inserted={CountriesInserted}, cities inserted={CitiesInserted}, districts inserted={DistrictsInserted}, neighbourhoods inserted={NeighborhoodsInserted}, streets inserted={StreetsInserted}",
            countriesInserted,
            cityDistrictResult.CitiesInserted,
            cityDistrictResult.DistrictsInserted,
            report?.Neighborhoods.Insert ?? 0,
            report?.Streets.Insert ?? 0);

        return new AddressReferenceImportResult(
            countriesInserted,
            cityDistrictResult.CitiesInserted,
            cityDistrictResult.DistrictsInserted,
            cityDistrictResult.TotalCities,
            report);
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
}

/// <param name="Catalog">The neighbourhood and street catalog report; null when no catalog source was given.</param>
public sealed record AddressReferenceImportResult(
    int CountriesInserted,
    int CitiesInserted,
    int DistrictsInserted,
    int TotalCities,
    AddressCatalogReport? Catalog);
