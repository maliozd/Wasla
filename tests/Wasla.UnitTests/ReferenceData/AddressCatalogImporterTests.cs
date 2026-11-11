using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.ReferenceData;
using static Wasla.UnitTests.ReferenceData.AddressCatalogScenario;

namespace Wasla.UnitTests.ReferenceData;

/// <summary>
/// WAS-214: the catalog import against an in-memory SQLite CentralDb. These prove the import's own behaviour (what is
/// written, repeated and resumed); <see cref="AddressCatalogImporterSqlServerTests"/> repeats the essentials on SQL
/// Server, whose collation and unique indexes SQLite cannot stand for.
/// </summary>
public sealed class AddressCatalogImporterTests : IAsyncLifetime
{
    private const int Plate = 34;
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly AddressCatalogTestSource _source = Source(Plate);
    private AddressCatalogSourceManifest _manifest = null!;
    private int _cityId;
    private int _camlikId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(Ct);
        await using var db = Open();
        await db.Database.EnsureCreatedAsync(Ct);
        (_cityId, _camlikId, _) = await SeedCityAsync(db, Plate, Ct);
        _manifest = _source.Write();
    }

    public async ValueTask DisposeAsync()
    {
        _source.Dispose();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task TheImport_WritesTheSourceIdsAsExternalCodes_WithTurkishNamesIntact()
    {
        var report = await ImportAsync();

        Assert.Equal(ImportedNeighborhoods, report.Neighborhoods.Insert);
        Assert.Equal(ImportedStreets, report.Streets.Insert);
        await using var db = Open();
        var rows = await RowsAsync(db, _cityId, Ct);
        Assert.Equal(
            ["GÖZTEPE", "KARAAĞAÇ KÖYÜ", "KESMETEPE BELDESİ, FATİH MAHALLESİ", "ÇİFTEHAVUZLAR", "ŞAMBAYAT BELDESİ, FATİH MAHALLESİ"],
            rows.Neighborhoods.Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.All(rows.Neighborhoods, n => Assert.True(n.IsActive));
        var ciftehavuzlar = Assert.Single(rows.Neighborhoods, n => n.ExternalCode == Id(Plate, 101).ToString());
        Assert.Equal(_camlikId, ciftehavuzlar.DistrictId);
        Assert.Equal(
            ["BAĞDAT Cadde", "İSKELE Sokak", "ÖRNEK ÜÇ Bulvar", "ŞİFA Sokak"],
            rows.Streets.Where(s => s.NeighborhoodId == ciftehavuzlar.Id).OrderBy(s => s.SortOrder).Select(s => $"{s.Name} {s.StreetType}"));
        Assert.Contains(rows.Streets, s => s.Name == "KARAAĞAÇ" && s.StreetType == "Köy Sokağı");
        Assert.DoesNotContain(rows.Streets, s => s.Name is "BARIŞ" or "BARİŞ" or "-" or "YOL");
    }

    [Fact]
    public async Task ARepeatedImport_AddsNothing_AndKeepsEveryRowAsItWas()
    {
        await ImportAsync();
        await using var before = Open();
        var first = await RowsAsync(before, _cityId, Ct);

        var again = await ImportAsync();

        Assert.Equal(0, again.Neighborhoods.Insert + again.Neighborhoods.Fill + again.Streets.Insert + again.Streets.Fill);
        Assert.Equal(ImportedNeighborhoods, again.Neighborhoods.Unchanged);
        Assert.Equal(ImportedStreets, again.Streets.Unchanged);
        await using var after = Open();
        var second = await RowsAsync(after, _cityId, Ct);
        Assert.Equal(first.Neighborhoods, second.Neighborhoods);
        Assert.Equal(first.Streets, second.Streets);
    }

    [Fact]
    public async Task ADryRun_PlansTheImport_AndWritesNothing_NotEvenCitiesOrTheCountry()
    {
        await using var db = Open();
        var cities = await db.Cities.CountAsync(Ct);

        var result = await EntryPoint(db, _manifest).ImportAsync(new AddressCatalogImportOptions(_source.Root, DryRun: true), Ct);

        Assert.Equal("dry-run", result.Catalog!.Mode);
        Assert.Equal(ImportedNeighborhoods, result.Catalog.Neighborhoods.Insert);
        Assert.Equal(ImportedStreets, result.Catalog.Streets.Insert);
        Assert.Equal(0, await db.Neighborhoods.CountAsync(Ct));
        Assert.Equal(0, await db.Streets.CountAsync(Ct));
        Assert.Equal(0, await db.Countries.CountAsync(Ct));
        Assert.Equal(cities, await db.Cities.CountAsync(Ct));
    }

    [Fact]
    public async Task AnImportStoppedBetweenBatches_IsCompletedByTheNextRun_WithTheSameRowsAsAnUninterruptedOne()
    {
        // Batches of 2: three neighbourhood saves, then five street saves. The fifth save is refused.
        await using (var stopped = Open(new FailingSave(5)))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Importer(stopped, _manifest).RunAsync(new AddressCatalogImportOptions(_source.Root, DryRun: false, BatchSize: 2), Ct));
            Assert.Equal("The test stopped the import here.", error.Message);
        }

        await using (var partial = Open())
        {
            var rows = await RowsAsync(partial, _cityId, Ct);
            Assert.Equal(ImportedNeighborhoods, rows.Neighborhoods.Count);
            Assert.Equal(2, rows.Streets.Count);
        }

        var resumed = await ImportAsync(batchSize: 2);
        Assert.Equal(ImportedNeighborhoods, resumed.Neighborhoods.Unchanged);
        Assert.Equal(2, resumed.Streets.Unchanged);
        Assert.Equal(ImportedStreets - 2, resumed.Streets.Insert);

        await using var db = Open();
        var content = Content(await RowsAsync(db, _cityId, Ct));
        Assert.Equal(content, await UninterruptedContentAsync());
    }

    [Fact]
    public async Task ExistingRows_KeepTheirIdsAndNames_AndAManualStreetWithoutType_IsLeftAlone()
    {
        int neighborhoodId, streetId, manualId;
        await using (var db = Open())
        {
            var gozTepe = new Neighborhood { DistrictId = _camlikId, Name = "Göztepe", SortOrder = 9, IsActive = true };
            db.Neighborhoods.Add(gozTepe);
            await db.SaveChangesAsync(Ct);
            var street = new Street { NeighborhoodId = gozTepe.Id, Name = "1453", StreetType = "Sokak", SortOrder = 4, IsActive = true };
            var manual = new Street { NeighborhoodId = gozTepe.Id, Name = "Elle Girilen", StreetType = null, SortOrder = 5, IsActive = true };
            db.Streets.AddRange(street, manual);
            await db.SaveChangesAsync(Ct);
            (neighborhoodId, streetId, manualId) = (gozTepe.Id, street.Id, manual.Id);
        }

        var report = await ImportAsync();

        Assert.Equal(1, report.Neighborhoods.Fill);
        Assert.Equal(1, report.Streets.Fill);
        await using var check = Open();
        var rows = await RowsAsync(check, _cityId, Ct);
        var kept = Assert.Single(rows.Neighborhoods, n => n.Id == neighborhoodId);
        Assert.Equal(("Göztepe", Id(Plate, 102).ToString(), 9), (kept.Name, kept.ExternalCode, kept.SortOrder));
        Assert.Single(rows.Neighborhoods, n => n.DistrictId == _camlikId && n.Name.Equals("GÖZTEPE", StringComparison.OrdinalIgnoreCase));
        var filled = Assert.Single(rows.Streets, s => s.Id == streetId);
        Assert.Equal(("1453", "Sokak", Id(Plate, 508).ToString(), neighborhoodId), (filled.Name, filled.StreetType, filled.ExternalCode, filled.NeighborhoodId));
        var untouched = Assert.Single(rows.Streets, s => s.Id == manualId);
        Assert.Equal(("Elle Girilen", (string?)null, (string?)null), (untouched.Name, untouched.StreetType, untouched.ExternalCode));
        Assert.Equal(ImportedStreets + 1, rows.Streets.Count);
    }

    [Fact]
    public async Task ASourceThatNoLongerMatchesItsManifest_WritesNothing()
    {
        await File.AppendAllTextAsync(Path.Combine(_source.Root, "province-34", "streets.jsonl"), "\n", Ct);

        await Assert.ThrowsAsync<AddressCatalogSourceException>(() => ImportAsync());

        await using var db = Open();
        Assert.Equal(0, await db.Neighborhoods.CountAsync(Ct));
        Assert.Equal(0, await db.Streets.CountAsync(Ct));
    }

    // The command's own entry point seeds the country, cities and districts too, so a bad source must stop it first.
    [Theory]
    [InlineData("missing folder")]
    [InlineData("missing file")]
    [InlineData("changed bytes")]
    public async Task TheEntryPoint_WithABadSource_ChangesNoTable_NotEvenTheCountryCitiesOrDistricts(string damage)
    {
        await SeedRowsAnImportWouldChangeAsync();
        var directory = _source.Root;
        var streets = Path.Combine(_source.Root, "province-34", "streets.jsonl");
        switch (damage)
        {
            case "missing folder":
                directory = _source.Root + "-missing";
                break;
            case "missing file":
                File.Delete(streets);
                break;
            default:
                var bytes = await File.ReadAllBytesAsync(streets, Ct); // same size, so only the SHA-256 can tell
                bytes[^3] ^= 0x01;
                await File.WriteAllBytesAsync(streets, bytes, Ct);
                break;
        }

        var before = await ReferenceTablesAsync();

        await using (var db = Open())
        {
            await Assert.ThrowsAsync<AddressCatalogSourceException>(() =>
                EntryPoint(db, _manifest).ImportAsync(new AddressCatalogImportOptions(directory, DryRun: false), Ct));
        }

        Assert.Equal(before, await ReferenceTablesAsync());
    }

    // The same rows and entry point with a good source: the import does change them, so the test above can fail.
    [Fact]
    public async Task TheEntryPoint_WithAGoodSource_SeedsTheReferenceRows_ThenImportsTheCatalog()
    {
        await SeedRowsAnImportWouldChangeAsync();
        using var source = new AddressCatalogTestSource()
            .AddProvince(34, "İSTANBUL")
            .AddDistrict(34, 1421, "KADIKÖY")
            .AddNeighborhood(34, 1421, 40001, "CAFERAĞA")
            .AddStreet(34, 1421, 40001, 900001, "MODA", typeCode: 3);
        var manifest = source.Write();

        await using var db = Open();
        var result = await EntryPoint(db, manifest).ImportAsync(new AddressCatalogImportOptions(source.Root, DryRun: false), Ct);

        Assert.Equal((1, 1), (result.Catalog!.Neighborhoods.Insert, result.Catalog.Streets.Insert));
        db.ChangeTracker.Clear();
        Assert.Equal(("Türkiye", true), await db.Countries.Where(c => c.Code == "TR").Select(c => new ValueTuple<string, bool>(c.Name, c.IsActive)).SingleAsync(Ct));
        Assert.Equal("Adana", await db.Cities.Where(c => c.PlateCode == "01").Select(c => c.Name).SingleAsync(Ct));
        Assert.True(await db.Districts.Where(d => d.Name == "Seyhan").Select(d => d.IsActive).SingleAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private CentralDbContext Open(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    private static AddressReferenceDataImporter EntryPoint(CentralDbContext db, AddressCatalogSourceManifest manifest) =>
        new(db,
            new TurkeyReferenceDataSeeder(db, NullLogger<TurkeyReferenceDataSeeder>.Instance),
            Importer(db, manifest),
            NullLogger<AddressReferenceDataImporter>.Instance);

    /// <summary>A country, city and district whose values the reference seed overwrites, and a catalog row without a source id.</summary>
    private async Task SeedRowsAnImportWouldChangeAsync()
    {
        await using var db = Open();
        db.Countries.Add(new Country { Code = "TR", Name = "Eski Ülke Adı", IsActive = false });
        var adana = new City { CountryCode = "TR", Name = "Eski Adana", PlateCode = "01", PhoneAreaCode = "000", SortOrder = 99, IsActive = false };
        var goztepe = new Neighborhood { DistrictId = _camlikId, Name = "Göztepe", SortOrder = 9, IsActive = false };
        db.Cities.Add(adana);
        db.Neighborhoods.Add(goztepe);
        await db.SaveChangesAsync(Ct);
        db.Districts.Add(new District { CityId = adana.Id, Name = "Seyhan", SortOrder = 77, IsActive = false });
        db.Streets.Add(new Street { NeighborhoodId = goztepe.Id, Name = "1453", StreetType = "Sokak", SortOrder = 4, IsActive = false });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Every column of the five reference tables, in id order.</summary>
    private async Task<IReadOnlyList<string>> ReferenceTablesAsync()
    {
        await using var db = Open();
        var countries = await db.Countries.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.Code, c.Name, c.IsActive }).ToListAsync(Ct);
        var cities = await db.Cities.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.CountryCode, c.Name, c.PlateCode, c.PhoneAreaCode, c.SortOrder, c.IsActive }).ToListAsync(Ct);
        var districts = await db.Districts.AsNoTracking().OrderBy(d => d.Id).Select(d => new { d.Id, d.CityId, d.Name, d.SortOrder, d.IsActive }).ToListAsync(Ct);
        var neighborhoods = await db.Neighborhoods.AsNoTracking().OrderBy(n => n.Id).Select(n => new { n.Id, n.DistrictId, n.Name, n.ExternalCode, n.SortOrder, n.IsActive }).ToListAsync(Ct);
        var streets = await db.Streets.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.NeighborhoodId, s.Name, s.StreetType, s.ExternalCode, s.SortOrder, s.IsActive }).ToListAsync(Ct);
        return
        [
            .. countries.Select(r => "Country " + r),
            .. cities.Select(r => "City " + r),
            .. districts.Select(r => "District " + r),
            .. neighborhoods.Select(r => "Neighborhood " + r),
            .. streets.Select(r => "Street " + r)
        ];
    }

    private async Task<AddressCatalogReport> ImportAsync(int batchSize = 1000)
    {
        await using var db = Open();
        return await Importer(db, _manifest).RunAsync(new AddressCatalogImportOptions(_source.Root, DryRun: false, batchSize), Ct);
    }

    /// <summary>The same source imported in one go into a fresh database.</summary>
    private async Task<IReadOnlyList<string>> UninterruptedContentAsync()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(Ct);
        await using var db = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(Ct);
        var (cityId, _, _) = await SeedCityAsync(db, Plate, Ct);
        await Importer(db, _manifest).RunAsync(new AddressCatalogImportOptions(_source.Root, DryRun: false), Ct);
        return Content(await RowsAsync(db, cityId, Ct));
    }
}
