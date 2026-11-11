using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.ReferenceData;

namespace Wasla.UnitTests.ReferenceData;

/// <summary>
/// One invented province for the importer tests (WAS-214), shared by the SQLite and SQL Server classes. Its city is
/// "Deneme {plate}" with the districts Çamlık and Güneşli; the source holds every outcome the importer must handle.
/// </summary>
internal static class AddressCatalogScenario
{
    /// <summary>Source neighbourhoods 7: 5 imported (one village, two renamed by their official names), 2 quarantined.</summary>
    public const int ImportedNeighborhoods = 5;

    /// <summary>Source streets 14: 10 imported, 2 quarantined (BARIŞ/BARİŞ), 1 rejected ("-"), 1 under a quarantined parent.</summary>
    public const int ImportedStreets = 10;

    public static string CityName(int plate) => $"Deneme {plate}";

    public static long Id(int plate, int n) => plate * 1000L + n;

    public static AddressCatalogTestSource Source(int plate)
    {
        long A = Id(plate, 1), B = Id(plate, 2);
        long N(int n) => Id(plate, 100 + n);
        long S(int n) => Id(plate, 500 + n);
        return new AddressCatalogTestSource()
            .AddProvince(plate, $"DENEME {plate}")
            .AddDistrict(plate, A, "ÇAMLIK")
            .AddDistrict(plate, B, "GÜNEŞLİ")
            .AddNeighborhood(plate, A, N(1), "ÇİFTEHAVUZLAR")
            .AddNeighborhood(plate, A, N(2), "GÖZTEPE")
            .AddNeighborhood(plate, B, N(3), null, "KARAAĞAÇ KÖYÜ")
            .AddNeighborhood(plate, B, N(4), "FATİH", "KESMETEPE BELDESİ, FATİH MAHALLESİ")
            .AddNeighborhood(plate, B, N(5), "FATİH", "ŞAMBAYAT BELDESİ, FATİH MAHALLESİ")
            .AddNeighborhood(plate, B, N(6), "YAHYALI", "OSMANİYE KÖYÜ, YAHYALI MEVKİİ")
            .AddNeighborhood(plate, B, N(7), "YAHYALI", "OSMANİYE KÖYÜ, YAHYALI MEVKİİ")
            .AddStreet(plate, A, N(1), S(1), "BAĞDAT", 3)
            .AddStreet(plate, A, N(1), S(2), "İSKELE", 4)
            .AddStreet(plate, A, N(1), S(3), "ŞİFA", 4)
            .AddStreet(plate, A, N(1), S(4), "ÖRNEK ÜÇ", 2)
            .AddStreet(plate, A, N(1), S(5), "BARIŞ", 3)
            .AddStreet(plate, A, N(1), S(6), "BARİŞ", 3)
            .AddStreet(plate, A, N(1), S(7), "-", 4)
            .AddStreet(plate, A, N(2), S(8), "1453", 4)
            .AddStreet(plate, A, N(2), S(9), "CUMHURİYET", 1)
            .AddStreet(plate, A, N(2), S(10), "1498 KÜME EVLER", 5)
            .AddStreet(plate, B, N(3), S(11), "KARAAĞAÇ", 0)
            .AddStreet(plate, B, N(4), S(12), "CUMHURİYET", 3)
            .AddStreet(plate, B, N(5), S(13), "CUMHURİYET", 3)
            .AddStreet(plate, B, N(6), S(14), "YOL", 4);
    }

    /// <summary>The city with the plate code and its two districts, as the Turkish reference seed would add them.</summary>
    public static async Task<(int CityId, int CamlikId, int GunesliId)> SeedCityAsync(CentralDbContext db, int plate, CancellationToken ct)
    {
        var city = new City { CountryCode = "TR", Name = CityName(plate), PlateCode = plate.ToString("00"), SortOrder = plate, IsActive = true };
        db.Cities.Add(city);
        await db.SaveChangesAsync(ct);
        var camlik = new District { CityId = city.Id, Name = "Çamlık", SortOrder = 1, IsActive = true };
        var gunesli = new District { CityId = city.Id, Name = "Güneşli", SortOrder = 2, IsActive = true };
        db.Districts.AddRange(camlik, gunesli);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return (city.Id, camlik.Id, gunesli.Id);
    }

    public static AddressCatalogImporter Importer(CentralDbContext db, AddressCatalogSourceManifest manifest) =>
        new(db, NullLogger<AddressCatalogImporter>.Instance, manifest);

    /// <summary>Every neighbourhood and street of the city, by value, with the ids that point at each other.</summary>
    public static async Task<CatalogRows> RowsAsync(CentralDbContext db, int cityId, CancellationToken ct)
    {
        var neighborhoods = await db.Neighborhoods.AsNoTracking()
            .Where(n => n.District.CityId == cityId)
            .OrderBy(n => n.Id)
            .Select(n => new NeighborhoodRow(n.Id, n.DistrictId, n.Name, n.ExternalCode, n.SortOrder, n.IsActive))
            .ToListAsync(ct);
        var streets = await db.Streets.AsNoTracking()
            .Where(s => s.Neighborhood.District.CityId == cityId)
            .OrderBy(s => s.Id)
            .Select(s => new StreetRow(s.Id, s.NeighborhoodId, s.Name, s.StreetType, s.ExternalCode, s.SortOrder, s.IsActive))
            .ToListAsync(ct);
        return new CatalogRows(neighborhoods, streets);
    }

    /// <summary>The rows without their generated ids, so two imports into different databases can be compared.</summary>
    public static IReadOnlyList<string> Content(CatalogRows rows)
    {
        var codes = rows.Neighborhoods.ToDictionary(n => n.Id, n => n.ExternalCode ?? "(manual " + n.Name + ")");
        return rows.Neighborhoods.Select(n => $"N {n.ExternalCode} {n.Name} order {n.SortOrder} active {n.IsActive}")
            .Concat(rows.Streets.Select(s => $"S {s.ExternalCode} {s.Name} {s.StreetType} under {codes[s.NeighborhoodId]} order {s.SortOrder} active {s.IsActive}"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public sealed record CatalogRows(IReadOnlyList<NeighborhoodRow> Neighborhoods, IReadOnlyList<StreetRow> Streets);

    public sealed record NeighborhoodRow(int Id, int DistrictId, string Name, string? ExternalCode, int SortOrder, bool IsActive);

    public sealed record StreetRow(int Id, int NeighborhoodId, string Name, string? StreetType, string? ExternalCode, int SortOrder, bool IsActive);

    /// <summary>Fails the <paramref name="failingSave"/>-th SaveChanges, as a stopped process or a lost connection would.</summary>
    public sealed class FailingSave(int failingSave) : SaveChangesInterceptor
    {
        private int _saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (++_saves == failingSave)
                throw new InvalidOperationException("The test stopped the import here.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
