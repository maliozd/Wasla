using Wasla.Infrastructure.ReferenceData;

namespace Wasla.UnitTests.ReferenceData;

/// <summary>
/// WAS-214: what the catalog plan does with each source record, against invented CentralDb rows. No database is
/// involved; every record must land in exactly one outcome.
/// </summary>
public sealed class AddressCatalogPlannerTests
{
    private const long Province = 34;
    private const long Kadikoy = 1421;
    private const long Uskudar = 1708;
    private const int KadikoyId = 10;
    private const int UskudarId = 11;

    private static readonly IReadOnlySet<long> NoDuplicates = new HashSet<long>();

    [Fact]
    public void PlainNames_AreInserted_WithTheSourceIdAsExternalCode_InTurkishAlphabeticalOrder()
    {
        var (plan, report) = Plan(
            [Nb(3, Kadikoy, "ZÜHTÜPAŞA"), Nb(1, Kadikoy, "CAFERAĞA"), Nb(2, Kadikoy, "ÇİFTEHAVUZLAR")],
            [St(101, Kadikoy, 1, "MODA", 3), St(100, Kadikoy, 1, "BAHARİYE", 3)]);

        Assert.Equal(
            [(1L, KadikoyId, "CAFERAĞA", 1), (2L, KadikoyId, "ÇİFTEHAVUZLAR", 2), (3L, KadikoyId, "ZÜHTÜPAŞA", 3)],
            plan.NeighborhoodInserts.OrderBy(n => n.SourceId).Select(n => (n.SourceId, n.DistrictId, n.Name, n.SortOrder)));
        Assert.Equal(
            [(100L, (int?)null, 1L, "BAHARİYE", "Cadde", 1), (101L, (int?)null, 1L, "MODA", "Cadde", 2)],
            plan.StreetInserts.OrderBy(s => s.SourceId).Select(s => (s.SourceId, s.NeighborhoodId, s.ParentSourceId, s.Name, s.StreetType, s.SortOrder)));
        Assert.Equal(3, report.Neighborhoods.Insert);
        Assert.Equal(2, report.Streets.Insert);
        Assert.Equal(3, report.NeighborhoodNames.Name);
        Assert.Empty(report.Collisions);
        AssertEveryRecordCountedOnce(report);
    }

    [Fact]
    public void AVillageWithoutAName_TakesItsOfficialName()
    {
        var (plan, report) = Plan([Nb(7, Uskudar, null, official: "AKDURAK KÖYÜ")], [St(70, Uskudar, 7, "KARAAĞAÇ", 0)]);

        Assert.Equal("AKDURAK KÖYÜ", Assert.Single(plan.NeighborhoodInserts).Name);
        Assert.Equal("Köy Sokağı", Assert.Single(plan.StreetInserts).StreetType);
        Assert.Equal(1, report.NeighborhoodNames.OfficialNameForMissingName);
        AssertEveryRecordCountedOnce(report);
    }

    [Fact]
    public void NamesSharedInADistrict_TakeTheirOfficialNames_AndNamesStillShared_AreQuarantinedWithTheirStreets()
    {
        var (plan, report) = Plan(
            [
                Nb(1, Uskudar, "FATİH", official: "KESMETEPE BELDESİ, FATİH MAHALLESİ"),
                Nb(2, Uskudar, "FATİH", official: "ŞAMBAYAT BELDESİ, FATİH MAHALLESİ"),
                Nb(3, Uskudar, "YAHYALI", official: "OSMANİYE KÖYÜ, YAHYALI MEVKİİ"),
                Nb(4, Uskudar, "YAHYALI", official: "OSMANİYE KÖYÜ, YAHYALI MEVKİİ"),
                Nb(5, Kadikoy, "FATİH")
            ],
            [St(30, Uskudar, 3, "1. SOKAK", 4), St(31, Uskudar, 4, "2. SOKAK", 4)]);

        Assert.Equal(
            ["FATİH", "KESMETEPE BELDESİ, FATİH MAHALLESİ", "ŞAMBAYAT BELDESİ, FATİH MAHALLESİ"],
            plan.NeighborhoodInserts.Select(n => n.Name).Order(StringComparer.Ordinal));
        var collision = Assert.Single(report.Collisions);
        Assert.Equal("neighborhood", collision.Level);
        Assert.Equal([3L, 4L], collision.SourceIds);
        Assert.Equal(2, report.Neighborhoods.Quarantined);
        Assert.Equal(2, report.NeighborhoodNames.OfficialNameForSharedName);
        Assert.Empty(plan.StreetInserts);
        Assert.Equal(2, report.Streets.ParentNotImported);
        Assert.Equal([3L, 4L], report.ParentsNotImported.Where(p => p.Level == "neighborhood").Select(p => p.SourceId!.Value).Order());
        AssertEveryRecordCountedOnce(report);
    }

    [Fact]
    public void StreetsACollationCouldEquate_AreAllQuarantined_WhileTheSameNameOfAnotherType_IsImported()
    {
        var (plan, report) = Plan(
            [Nb(1, Kadikoy, "MAKSUDİYE")],
            [
                St(10, Kadikoy, 1, "BARIŞ", 3), St(11, Kadikoy, 1, "BARİŞ", 3),
                St(12, Kadikoy, 1, "9", 4), St(13, Kadikoy, 1, "9", 4),
                St(14, Kadikoy, 1, "BARIŞ", 4)
            ]);

        Assert.Equal([14L], plan.StreetInserts.Select(s => s.SourceId));
        Assert.Equal(4, report.Streets.Quarantined);
        Assert.Equal([[10L, 11L], [12L, 13L]], report.Collisions.Select(c => c.SourceIds).OrderBy(ids => ids[0]));
        Assert.All(report.Collisions, c => Assert.Equal("MAKSUDİYE", c.Neighborhood));
        AssertEveryRecordCountedOnce(report);
    }

    [Fact]
    public void StreetsThatFailValidation_AreRejected_WithTheirReason()
    {
        var (plan, report) = Plan(
            [Nb(1, Kadikoy, "CAFERAĞA")],
            [
                St(20, Kadikoy, 1, "-", 4),
                St(21, Kadikoy, 1, "?S?MS?Z", 4),
                St(22, Kadikoy, 1, "MODA", 9),
                St(23, Kadikoy, 1, "MODA", 4, official: "MODA (Cadde)"),
                St(24, Kadikoy, 1, null, 4, official: " (Sokak)"),
                St(25, Kadikoy, 1, "GÜNEŞLİ BAHÇE", 4)
            ]);

        Assert.Equal([25L], plan.StreetInserts.Select(s => s.SourceId));
        Assert.Equal(5, report.Streets.Rejected);
        Assert.Equal(
            new Dictionary<long, string>
            {
                [20] = "no letter or digit",
                [21] = "damaged text (a character was replaced by '?')",
                [22] = "unknown street type code 9",
                [23] = "street type code 4 (Sokak) does not match the official name",
                [24] = "no name"
            },
            report.Rejected.ToDictionary(r => r.SourceId!.Value, r => r.Reason));
        AssertEveryRecordCountedOnce(report);
    }

    [Fact]
    public void AStreetWhoseDistrictDiffersFromItsNeighbourhoods_AndDuplicateIds_AreRejected()
    {
        var duplicates = new HashSet<long> { 40 };
        var (plan, report) = Plan(
            [Nb(1, Kadikoy, "CAFERAĞA"), Nb(2, Kadikoy, "OSMANAĞA")],
            [St(40, Kadikoy, 1, "MODA", 3), St(41, Uskudar, 2, "SÖĞÜTLÜÇEŞME", 3), St(42, Kadikoy, 99, "YOK", 4)],
            duplicateStreets: duplicates);

        Assert.Empty(plan.StreetInserts);
        Assert.Equal(3, report.Streets.Rejected);
        Assert.Contains(report.Rejected, r => r.SourceId == 40 && r.Reason == "the source id appears more than once");
        Assert.Contains(report.Rejected, r => r.SourceId == 41 && r.Reason == "the street's district or province differs from its neighbourhood's");
        Assert.Contains(report.Rejected, r => r.SourceId == 42 && r.Reason == "neighbourhood 99 is not in the source province");
        AssertEveryRecordCountedOnce(report);
    }

    [Fact]
    public void ExistingRows_KeepTheirIdsAndNames_AndOnlyAMissingExternalCodeIsFilled()
    {
        var city = City(
            neighborhoods:
            [
                new(500, KadikoyId, "Caferağa", null),          // same name, Turkish case rules: gets the source id
                new(501, KadikoyId, "OSMANAĞA", "2"),           // already carries the source id: unchanged
                new(502, KadikoyId, "RASİMPAŞA", "777"),        // same name, another source id: conflict
                new(503, KadikoyId, "CAKIR", null),             // differs from ÇAKIR only in accents: conflict
                new(504, UskudarId, "ACIBADEM", "5")            // source id 5 belongs to a Kadıköy record: conflict
            ],
            streets:
            [
                new(900, 500, "Moda", "Cadde", null),           // gets the source id
                new(901, 500, "BAHARİYE", null, null)           // no street type: never matched, left alone
            ]);

        var (plan, report) = Plan(
            [Nb(1, Kadikoy, "CAFERAĞA"), Nb(2, Kadikoy, "OSMANAĞA"), Nb(3, Kadikoy, "RASİMPAŞA"), Nb(4, Kadikoy, "ÇAKIR"), Nb(5, Kadikoy, "ACIBADEM")],
            [St(10, Kadikoy, 1, "MODA", 3), St(11, Kadikoy, 1, "BAHARİYE", 3), St(12, Kadikoy, 3, "NEŞET ÖMER", 4)],
            city: city);

        Assert.Equal([(500, "1")], plan.NeighborhoodFills.Select(f => (f.Row.Id, f.ExternalCode)));
        Assert.Empty(plan.NeighborhoodInserts);
        Assert.Equal(1, report.Neighborhoods.Unchanged);
        Assert.Equal(3, report.Neighborhoods.Conflict);
        Assert.Contains(report.Conflicts, c => c.SourceId == 3 && c.Reason == "existing row 502 has another source id (777)");
        Assert.Contains(report.Conflicts, c => c.SourceId == 4 && c.Reason == "existing row 503 'CAKIR' differs only in accents or I letters");
        Assert.Contains(report.Conflicts, c => c.SourceId == 5 && c.Reason == "the source id is on existing row 504 of another district");

        Assert.Equal([(900, "10")], plan.StreetFills.Select(f => (f.Row.Id, f.ExternalCode)));
        var inserted = Assert.Single(plan.StreetInserts);
        Assert.Equal((11L, (int?)500, "BAHARİYE", "Cadde"), (inserted.SourceId, inserted.NeighborhoodId, inserted.Name, inserted.StreetType));
        Assert.Equal(1, report.Streets.ParentNotImported); // street 12's neighbourhood is in conflict
        AssertEveryRecordCountedOnce(report);
    }

    [Fact]
    public void ADistrictOrCityMissingFromCentralDb_SkipsEverythingBelowIt()
    {
        var withoutUskudar = City(districts: [new(KadikoyId, "Kadıköy")]);
        var (plan, report) = Plan([Nb(1, Kadikoy, "CAFERAĞA"), Nb(2, Uskudar, "ACIBADEM")], [St(10, Uskudar, 2, "TUNA", 4)], city: withoutUskudar);

        Assert.Equal([1L], plan.NeighborhoodInserts.Select(n => n.SourceId));
        Assert.Equal(1, report.Neighborhoods.ParentNotImported);
        Assert.Equal(1, report.Streets.ParentNotImported);
        Assert.Equal(1, report.MatchedDistricts);
        Assert.Contains(report.ParentsNotImported, p => p.Level == "district" && p.SourceId == Uskudar && p.Reason == "the district is not in CentralDb");
        AssertEveryRecordCountedOnce(report);

        var (noCity, noCityReport) = Plan([Nb(1, Kadikoy, "CAFERAĞA")], [St(10, Kadikoy, 1, "MODA", 3)], cityMissing: true);
        Assert.Empty(noCity.NeighborhoodInserts);
        Assert.Equal(0, noCityReport.MatchedProvinces);
        Assert.Equal(1, noCityReport.Neighborhoods.ParentNotImported);
        Assert.Equal(1, noCityReport.Streets.ParentNotImported);
        AssertEveryRecordCountedOnce(noCityReport);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static (AddressCatalogProvincePlan Plan, AddressCatalogReport Report) Plan(
        SourceRecord<SourceNeighborhood>[] neighborhoods,
        SourceRecord<SourceStreet>[] streets,
        AddressCatalogCity? city = null,
        bool cityMissing = false,
        IReadOnlySet<long>? duplicateStreets = null)
    {
        var report = new AddressCatalogReport();
        var source = new AddressCatalogProvinceSource(
            new SourceProvince { Id = Province, Name = "İSTANBUL" },
            [Record(new SourceDistrict { Id = Kadikoy, ProvinceId = Province, Name = "KADIKÖY" }), Record(new SourceDistrict { Id = Uskudar, ProvinceId = Province, Name = "ÜSKÜDAR" })],
            neighborhoods,
            streets);
        var plan = AddressCatalogPlanner.Plan(source, cityMissing ? null : city ?? City(), NoDuplicates, duplicateStreets ?? NoDuplicates, report);
        return (plan, report);
    }

    private static AddressCatalogCity City(
        AddressCatalogExistingDistrict[]? districts = null,
        AddressCatalogExistingNeighborhood[]? neighborhoods = null,
        AddressCatalogExistingStreet[]? streets = null) =>
        new(1, "İstanbul", districts ?? [new(KadikoyId, "Kadıköy"), new(UskudarId, "Üsküdar")], neighborhoods ?? [], streets ?? []);

    private static SourceRecord<SourceNeighborhood> Nb(long id, long district, string? name, string? official = null) =>
        Record(new SourceNeighborhood { Id = id, ProvinceId = Province, DistrictId = district, Name = name, FullOfficialName = official ?? name + " MAHALLESİ" });

    private static SourceRecord<SourceStreet> St(long id, long district, long neighborhood, string? name, int? type, string? official = null) =>
        Record(new SourceStreet
        {
            Id = id,
            ProvinceId = Province,
            DistrictId = district,
            NeighborhoodId = neighborhood,
            Name = name,
            TypeCode = type,
            FullOfficialName = official ?? $"{name} ({(type is { } t && AddressCatalogText.StreetTypes.TryGetValue(t, out var label) ? label : "?")})"
        });

    private static SourceRecord<T> Record<T>(T value) where T : class => new("test.jsonl", 1, value, null);

    private static void AssertEveryRecordCountedOnce(AddressCatalogReport report)
    {
        Assert.Equal(report.Neighborhoods.Source, report.Neighborhoods.Accounted);
        Assert.Equal(report.Streets.Source, report.Streets.Accounted);
        Assert.All(report.Districts, d =>
        {
            Assert.Equal(d.Neighborhoods.Source, d.Neighborhoods.Accounted);
            Assert.Equal(d.Streets.Source, d.Streets.Accounted);
        });
    }
}
