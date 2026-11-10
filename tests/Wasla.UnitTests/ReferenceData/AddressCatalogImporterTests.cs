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
        var importer = new AddressReferenceDataImporter(
            db,
            new TurkeyReferenceDataSeeder(db, NullLogger<TurkeyReferenceDataSeeder>.Instance),
            Importer(db, _manifest),
            NullLogger<AddressReferenceDataImporter>.Instance);

        var result = await importer.ImportAsync(new AddressCatalogImportOptions(_source.Root, DryRun: true), Ct);

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

    // ---------------------------------------------------------------------------------------------------------------

    private CentralDbContext Open(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

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
