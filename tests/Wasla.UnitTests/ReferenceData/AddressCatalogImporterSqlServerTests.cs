using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.ReferenceData;
using Wasla.UnitTests.Billing;
using static Wasla.UnitTests.ReferenceData.AddressCatalogScenario;

namespace Wasla.UnitTests.ReferenceData;

/// <summary>
/// WAS-214: the catalog import on a real SQL Server CentralDb (the throwaway database of
/// <see cref="SqlServerCentralDatabase"/>, migrated with this build's migrations). SQLite cannot show these: nvarchar
/// round trips of Turkish text, the server's collation behind the unique name indexes, and the foreign keys that tie
/// registrations to neighbourhoods and streets. Each test uses its own invented city, so they share the database.
/// </summary>
public sealed class AddressCatalogImporterSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private readonly SqlServerCentralDatabase _db;

    public AddressCatalogImporterSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheImport_AndARepeat_KeepEveryRow_AndTurkishTextSurvivesTheRoundTrip()
    {
        const int plate = 71;
        using var source = Source(plate);
        var manifest = source.Write();
        int cityId;
        await using (var setup = _db.Open())
            (cityId, _, _) = await SeedCityAsync(setup, plate, Ct);

        var first = await ImportAsync(source, manifest);
        await using var afterFirst = _db.Open();
        var rows = await RowsAsync(afterFirst, cityId, Ct);
        var second = await ImportAsync(source, manifest);
        await using var afterSecond = _db.Open();

        Assert.False(string.IsNullOrWhiteSpace(first.DatabaseCollation));
        Assert.Equal(ImportedNeighborhoods, first.Neighborhoods.Insert);
        Assert.Equal(ImportedStreets, first.Streets.Insert);
        Assert.Equal(0, second.Neighborhoods.Insert + second.Streets.Insert + second.Neighborhoods.Fill + second.Streets.Fill);
        var repeated = await RowsAsync(afterSecond, cityId, Ct);
        Assert.Equal(rows.Neighborhoods, repeated.Neighborhoods);
        Assert.Equal(rows.Streets, repeated.Streets);
        Assert.Contains(rows.Neighborhoods, n => n.Name == "ŞAMBAYAT BELDESİ, FATİH MAHALLESİ");
        Assert.Contains(rows.Neighborhoods, n => n.Name == "ÇİFTEHAVUZLAR");
        Assert.Contains(rows.Streets, s => s.Name == "KARAAĞAÇ" && s.StreetType == "Köy Sokağı");
        Assert.Contains(rows.Streets, s => s.Name == "ÖRNEK ÜÇ" && s.StreetType == "Bulvar");
        Assert.Equal(rows.Streets.Count, rows.Streets.Select(s => s.ExternalCode).Distinct().Count());
    }

    [Fact]
    public async Task ARegistrationPointingAtAnExistingNeighbourhoodAndStreet_StillPointsAtTheSameRows()
    {
        const int plate = 72;
        using var source = Source(plate);
        var manifest = source.Write();
        int cityId, neighborhoodId, streetId;
        var registrationId = Guid.NewGuid();
        await using (var setup = _db.Open())
        {
            var (city, camlik, _) = await SeedCityAsync(setup, plate, Ct);
            cityId = city;
            var neighborhood = new Neighborhood { DistrictId = camlik, Name = "Göztepe", SortOrder = 9, IsActive = true };
            setup.Neighborhoods.Add(neighborhood);
            await setup.SaveChangesAsync(Ct);
            var street = new Street { NeighborhoodId = neighborhood.Id, Name = "1453", StreetType = "Sokak", SortOrder = 4, IsActive = true };
            setup.Streets.Add(street);
            await setup.SaveChangesAsync(Ct);
            (neighborhoodId, streetId) = (neighborhood.Id, street.Id);
            setup.PendingRegistrations.Add(Registration(registrationId, cityId, camlik, neighborhoodId, streetId));
            await setup.SaveChangesAsync(Ct);
        }

        var report = await ImportAsync(source, manifest);

        Assert.Equal(1, report.Neighborhoods.Fill);
        Assert.Equal(1, report.Streets.Fill);
        await using var check = _db.Open();
        var registration = await check.PendingRegistrations.AsNoTracking().SingleAsync(r => r.Id == registrationId, Ct);
        Assert.Equal((neighborhoodId, streetId), (registration.NeighborhoodId, registration.StreetId));
        var neighborhoodRow = await check.Neighborhoods.AsNoTracking().SingleAsync(n => n.Id == neighborhoodId, Ct);
        Assert.Equal(("Göztepe", Id(plate, 102).ToString()), (neighborhoodRow.Name, neighborhoodRow.ExternalCode));
        var streetRow = await check.Streets.AsNoTracking().SingleAsync(s => s.Id == streetId, Ct);
        Assert.Equal(("1453", Id(plate, 508).ToString(), neighborhoodId), (streetRow.Name, streetRow.ExternalCode, streetRow.NeighborhoodId));
        var rows = await RowsAsync(check, cityId, Ct);
        Assert.Equal(ImportedNeighborhoods, rows.Neighborhoods.Count);
        Assert.Equal(ImportedStreets, rows.Streets.Count);
    }

    [Fact]
    public async Task NamesTheCollationCouldEquate_NeverReachItsUniqueIndexes()
    {
        const int plate = 73;
        long A = Id(plate, 1), N1 = Id(plate, 101), N2 = Id(plate, 102), N3 = Id(plate, 103);
        using var source = new AddressCatalogTestSource()
            .AddProvince(plate, $"DENEME {plate}")
            .AddDistrict(plate, A, "ÇAMLIK")
            .AddNeighborhood(plate, A, N1, "YENİ", "ORTA KÖYÜ, YENİ MEVKİİ")
            .AddNeighborhood(plate, A, N2, "yeni", "ORTA KÖYÜ, YENİ MEVKİİ")
            .AddNeighborhood(plate, A, N3, "MERKEZ")
            .AddStreet(plate, A, N3, Id(plate, 501), "Moda", 3)
            .AddStreet(plate, A, N3, Id(plate, 502), "MODA", 3)
            .AddStreet(plate, A, N3, Id(plate, 503), "KÂHYA", 4)
            .AddStreet(plate, A, N3, Id(plate, 504), "KAHYA", 4)
            .AddStreet(plate, A, N3, Id(plate, 505), "İNÖNÜ", 4)
            .AddStreet(plate, A, N3, Id(plate, 506), "INÖNÜ", 4)
            .AddStreet(plate, A, N3, Id(plate, 507), "GÜL", 4);
        var manifest = source.Write();
        int cityId;
        await using (var setup = _db.Open())
            (cityId, _, _) = await SeedCityAsync(setup, plate, Ct);

        var report = await ImportAsync(source, manifest);

        Assert.Equal(1, report.Neighborhoods.Insert);
        Assert.Equal(2, report.Neighborhoods.Quarantined);
        Assert.Equal(1, report.Streets.Insert);
        Assert.Equal(6, report.Streets.Quarantined);
        await using var check = _db.Open();
        var rows = await RowsAsync(check, cityId, Ct);
        Assert.Equal(["MERKEZ"], rows.Neighborhoods.Select(n => n.Name));
        Assert.Equal(["GÜL"], rows.Streets.Select(s => s.Name));
    }

    [Fact]
    public async Task AnImportStoppedBetweenBatches_IsCompletedByTheNextRun()
    {
        const int plate = 74;
        using var source = Source(plate);
        var manifest = source.Write();
        int cityId;
        await using (var setup = _db.Open())
            (cityId, _, _) = await SeedCityAsync(setup, plate, Ct);

        await using (var stopped = _db.Open("Stopped", new FailingSave(5)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Importer(stopped, manifest).RunAsync(new AddressCatalogImportOptions(source.Root, DryRun: false, BatchSize: 2), Ct));
        }

        await using (var partial = _db.Open())
        {
            var rows = await RowsAsync(partial, cityId, Ct);
            Assert.Equal((ImportedNeighborhoods, 2), (rows.Neighborhoods.Count, rows.Streets.Count));
        }

        var resumed = await ImportAsync(source, manifest, batchSize: 2);

        Assert.Equal((ImportedNeighborhoods, 2, ImportedStreets - 2), (resumed.Neighborhoods.Unchanged, resumed.Streets.Unchanged, resumed.Streets.Insert));
        await using var check = _db.Open();
        var final = await RowsAsync(check, cityId, Ct);
        Assert.Equal(ImportedNeighborhoods, final.Neighborhoods.Select(n => n.ExternalCode).Distinct().Count());
        Assert.Equal(ImportedStreets, final.Streets.Select(s => s.ExternalCode).Distinct().Count());
        Assert.Equal(ImportedStreets, final.Streets.Count);
    }

    private async Task<AddressCatalogReport> ImportAsync(AddressCatalogTestSource source, AddressCatalogSourceManifest manifest, int batchSize = 1000)
    {
        await using var db = _db.Open("Importer");
        return await Importer(db, manifest).RunAsync(new AddressCatalogImportOptions(source.Root, DryRun: false, batchSize), Ct);
    }

    private static PendingRegistration Registration(Guid id, int cityId, int districtId, int neighborhoodId, int streetId) => new()
    {
        Id = id,
        Status = PendingRegistrationStatus.AwaitingPayment,
        Slug = "was214-" + id.ToString("N")[..8],
        BusinessName = "Deneme Lokantası",
        PrimaryDomain = "was214.app.wasla.local",
        DatabaseName = "Wasla_was214_" + id.ToString("N")[..8],
        BusinessPhone = "+902125550000",
        Country = "TR",
        CityId = cityId,
        DistrictId = districtId,
        NeighborhoodId = neighborhoodId,
        StreetId = streetId,
        City = "Deneme",
        District = "Çamlık",
        Neighborhood = "Göztepe",
        StreetAddress = "1453 Sokak",
        OwnerFullName = "Test Owner",
        OwnerEmail = "owner@was214.example",
        PasswordHash = "fake-hash",
        PlanCode = "starter",
        BillingPeriod = "Monthly",
        CreatedAtUtc = DateTime.UtcNow
    };
}
