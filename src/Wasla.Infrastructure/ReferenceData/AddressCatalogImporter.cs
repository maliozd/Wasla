using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.ReferenceData;

/// <summary>How to run the neighbourhood and street catalog import.</summary>
/// <param name="SourceDirectory">The <c>data/jsonl</c> folder of the pinned turkey-geo-api commit.</param>
/// <param name="DryRun">Validate and plan against CentralDb without writing anything.</param>
/// <param name="BatchSize">Rows per SaveChanges, and so per transaction.</param>
public sealed record AddressCatalogImportOptions(string SourceDirectory, bool DryRun, int BatchSize = 1000);

/// <summary>
/// Loads the pinned Türkiye neighbourhood and street catalog into CentralDb (WAS-214). The source files are verified
/// against the manifest first; then each province is planned against the city's current rows (three queries) and, unless
/// it is a dry run, written in batches of <see cref="AddressCatalogImportOptions.BatchSize"/> rows, each batch one
/// SaveChanges and one transaction, with the change tracker cleared after it. A stopped run leaves only whole batches,
/// and the next run finds them by their ExternalCode and goes on with the rest, so repeating the import is safe. Nothing
/// is deleted, deactivated, renamed or re-keyed.
/// </summary>
public sealed class AddressCatalogImporter
{
    private readonly CentralDbContext _central;
    private readonly ILogger<AddressCatalogImporter> _logger;
    private readonly AddressCatalogSourceManifest _manifest;

    public AddressCatalogImporter(CentralDbContext central, ILogger<AddressCatalogImporter> logger, AddressCatalogSourceManifest? manifest = null)
    {
        _central = central;
        _logger = logger;
        _manifest = manifest ?? AddressCatalogSourceManifest.Pinned();
    }

    public async Task<AddressCatalogReport> RunAsync(AddressCatalogImportOptions options, CancellationToken ct)
    {
        if (options.BatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "The batch size must be at least 1.");

        var source = await AddressCatalogSource.OpenAsync(options.SourceDirectory, _manifest, ct);
        var report = new AddressCatalogReport
        {
            Mode = options.DryRun ? "dry-run" : "import",
            Dataset = _manifest.Dataset,
            Repository = _manifest.Repository,
            Commit = _manifest.Commit,
            Upstream = _manifest.Upstream,
            FilesVerified = _manifest.Files.Count,
            DeclaredCounts = _manifest.DeclaredCounts,
            DatabaseCollation = await CollationAsync(ct)
        };

        var provinces = source.Provinces();
        var (duplicateNeighborhoods, duplicateStreets) = FindDuplicateIds(source, provinces, ct);

        var seenProvinces = new HashSet<long>();
        foreach (var record in provinces.OrderBy(p => p.Value?.Id ?? long.MaxValue))
        {
            ct.ThrowIfCancellationRequested();
            if (record.Problem is not null || record.Value is not { Id: > 0 } province || !seenProvinces.Add(province.Id))
            {
                report.Add(report.Rejected, new AddressCatalogEntry
                {
                    Level = "province",
                    SourceId = record.Value?.Id,
                    Name = $"{record.File}:{record.Line}",
                    Reason = record.Problem ?? (record.Value is { Id: > 0 } ? "the province appears more than once; only its first line is used" : "no id")
                });
                continue;
            }

            var provinceSource = new AddressCatalogProvinceSource(
                province,
                source.Districts(province.Id).ToList(),
                source.Neighborhoods(province.Id).ToList(),
                source.Streets(province.Id).ToList());
            var city = await LoadCityAsync(province, ct);
            var plan = AddressCatalogPlanner.Plan(provinceSource, city, duplicateNeighborhoods, duplicateStreets, report);
            if (!options.DryRun)
                await ApplyAsync(plan, options.BatchSize, ct);

            _logger.LogInformation(
                "Address catalog province {Province}: {NeighborhoodInserts} neighbourhood and {StreetInserts} street inserts, {Fills} ExternalCode fills{DryRun}.",
                province.Id,
                plan.NeighborhoodInserts.Count,
                plan.StreetInserts.Count,
                plan.NeighborhoodFills.Count + plan.StreetFills.Count,
                options.DryRun ? " planned (dry run)" : string.Empty);
        }

        return report;
    }

    /// <summary>The CentralDb rows of the city with the province's plate code, when its name agrees.</summary>
    private async Task<AddressCatalogCity?> LoadCityAsync(SourceProvince province, CancellationToken ct)
    {
        var plate = province.Id.ToString("00", CultureInfo.InvariantCulture);
        var city = await _central.Cities.AsNoTracking()
            .Where(c => c.CountryCode == "TR" && c.PlateCode == plate)
            .Select(c => new { c.Id, c.Name })
            .FirstOrDefaultAsync(ct);
        if (city is null
            || AddressCatalogText.TurkishKey(AddressCatalogText.Normalize(city.Name)) != AddressCatalogText.TurkishKey(AddressCatalogText.Normalize(province.Name)))
            return null;

        var districts = await _central.Districts.AsNoTracking()
            .Where(d => d.CityId == city.Id)
            .Select(d => new AddressCatalogExistingDistrict(d.Id, d.Name))
            .ToListAsync(ct);
        var neighborhoods = await _central.Neighborhoods.AsNoTracking()
            .Where(n => n.District.CityId == city.Id)
            .Select(n => new AddressCatalogExistingNeighborhood(n.Id, n.DistrictId, n.Name, n.ExternalCode))
            .ToListAsync(ct);
        var streets = await _central.Streets.AsNoTracking()
            .Where(s => s.Neighborhood.District.CityId == city.Id)
            .Select(s => new AddressCatalogExistingStreet(s.Id, s.NeighborhoodId, s.Name, s.StreetType, s.ExternalCode))
            .ToListAsync(ct);
        return new AddressCatalogCity(city.Id, city.Name, districts, neighborhoods, streets);
    }

    private async Task ApplyAsync(AddressCatalogProvincePlan plan, int batchSize, CancellationToken ct)
    {
        var inserted = new Dictionary<long, int>();
        foreach (var batch in plan.NeighborhoodInserts.Chunk(batchSize))
        {
            var rows = batch.Select(p => new Neighborhood
            {
                DistrictId = p.DistrictId,
                Name = p.Name,
                ExternalCode = Code(p.SourceId),
                SortOrder = p.SortOrder,
                IsActive = true
            }).ToList();
            _central.Neighborhoods.AddRange(rows);
            await SaveBatchAsync(ct);
            for (var i = 0; i < batch.Length; i++)
                inserted[batch[i].SourceId] = rows[i].Id;
        }

        foreach (var batch in plan.NeighborhoodFills.Chunk(batchSize))
        {
            foreach (var (row, code) in batch)
            {
                var stub = new Neighborhood { Id = row.Id, DistrictId = row.DistrictId, Name = row.Name };
                _central.Neighborhoods.Attach(stub);
                stub.ExternalCode = code;
            }

            await SaveBatchAsync(ct);
        }

        foreach (var batch in plan.StreetInserts.Chunk(batchSize))
        {
            _central.Streets.AddRange(batch.Select(p => new Street
            {
                NeighborhoodId = p.NeighborhoodId ?? inserted[p.ParentSourceId],
                Name = p.Name,
                StreetType = p.StreetType,
                ExternalCode = Code(p.SourceId),
                SortOrder = p.SortOrder,
                IsActive = true
            }));
            await SaveBatchAsync(ct);
        }

        foreach (var batch in plan.StreetFills.Chunk(batchSize))
        {
            foreach (var (row, code) in batch)
            {
                var stub = new Street { Id = row.Id, NeighborhoodId = row.NeighborhoodId, Name = row.Name, StreetType = row.StreetType };
                _central.Streets.Attach(stub);
                stub.ExternalCode = code;
            }

            await SaveBatchAsync(ct);
        }
    }

    private async Task SaveBatchAsync(CancellationToken ct)
    {
        await _central.SaveChangesAsync(ct);
        _central.ChangeTracker.Clear();
    }

    /// <summary>Source ids that appear more than once in the whole source: none of their records is imported.</summary>
    private static (HashSet<long> Neighborhoods, HashSet<long> Streets) FindDuplicateIds(
        AddressCatalogSource source, IReadOnlyList<SourceRecord<SourceProvince>> provinces, CancellationToken ct)
    {
        var seenNeighborhoods = new HashSet<long>();
        var seenStreets = new HashSet<long>();
        var duplicateNeighborhoods = new HashSet<long>();
        var duplicateStreets = new HashSet<long>();
        foreach (var province in provinces.Select(p => p.Value).OfType<SourceProvince>().Where(p => p.Id > 0))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var record in source.Neighborhoods(province.Id))
            {
                if (record.Value is { Id: > 0 } n && !seenNeighborhoods.Add(n.Id))
                    duplicateNeighborhoods.Add(n.Id);
            }

            foreach (var record in source.Streets(province.Id))
            {
                if (record.Value is { Id: > 0 } s && !seenStreets.Add(s.Id))
                    duplicateStreets.Add(s.Id);
            }
        }

        return (duplicateNeighborhoods, duplicateStreets);
    }

    private async Task<string?> CollationAsync(CancellationToken ct) =>
        _central.Database.IsSqlServer()
            ? await _central.Database
                .SqlQueryRaw<string>("SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')) AS [Value]")
                .SingleOrDefaultAsync(ct)
            : null;

    private static string Code(long sourceId) => sourceId.ToString(CultureInfo.InvariantCulture);
}
