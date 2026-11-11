using System.Globalization;

namespace Wasla.Infrastructure.ReferenceData;

/// <summary>The CentralDb rows of one city the plan is made against.</summary>
public sealed record AddressCatalogCity(
    int Id,
    string Name,
    IReadOnlyList<AddressCatalogExistingDistrict> Districts,
    IReadOnlyList<AddressCatalogExistingNeighborhood> Neighborhoods,
    IReadOnlyList<AddressCatalogExistingStreet> Streets);

public sealed record AddressCatalogExistingDistrict(int Id, string Name);

public sealed record AddressCatalogExistingNeighborhood(int Id, int DistrictId, string Name, string? ExternalCode);

public sealed record AddressCatalogExistingStreet(int Id, int NeighborhoodId, string Name, string? StreetType, string? ExternalCode);

/// <summary>The source records of one province.</summary>
public sealed record AddressCatalogProvinceSource(
    SourceProvince Province,
    IReadOnlyList<SourceRecord<SourceDistrict>> Districts,
    IReadOnlyList<SourceRecord<SourceNeighborhood>> Neighborhoods,
    IReadOnlyList<SourceRecord<SourceStreet>> Streets);

public sealed record PlannedNeighborhood(long SourceId, int DistrictId, string Name, int SortOrder);

/// <summary>A new street under an existing neighbourhood (<see cref="NeighborhoodId"/>) or a planned one.</summary>
public sealed record PlannedStreet(long SourceId, int? NeighborhoodId, long ParentSourceId, string Name, string StreetType, int SortOrder);

public sealed class AddressCatalogProvincePlan
{
    public List<PlannedNeighborhood> NeighborhoodInserts { get; } = [];
    public List<(AddressCatalogExistingNeighborhood Row, string ExternalCode)> NeighborhoodFills { get; } = [];
    public List<PlannedStreet> StreetInserts { get; } = [];
    public List<(AddressCatalogExistingStreet Row, string ExternalCode)> StreetFills { get; } = [];
}

/// <summary>
/// Decides, without touching a database, what happens to every neighbourhood and street record of one province
/// (WAS-214). Names are compared with <see cref="AddressCatalogText.MatchKey"/>, so two records the database could
/// consider equal never both reach it. Existing rows keep their id, name and every other value; one without an
/// ExternalCode gets the source id when the names are equal under Turkish case rules. The source id is written only
/// as <c>ExternalCode</c>, never as a key.
/// </summary>
public static class AddressCatalogPlanner
{
    private static readonly StringComparer TurkishOrder = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), CompareOptions.None);

    public static AddressCatalogProvincePlan Plan(
        AddressCatalogProvinceSource source,
        AddressCatalogCity? city,
        IReadOnlySet<long> duplicateNeighborhoodIds,
        IReadOnlySet<long> duplicateStreetIds,
        AddressCatalogReport report)
    {
        var plan = new AddressCatalogProvincePlan();
        var province = source.Province;
        var provinceName = AddressCatalogText.Normalize(province.Name);
        report.SourceProvinces++;
        if (city is not null)
            report.MatchedProvinces++;

        // --- Districts: matched to CentralDb by their Turkish upper-case name within the city.
        var waslaDistricts = (city?.Districts ?? [])
            .GroupBy(d => AddressCatalogText.TurkishKey(AddressCatalogText.Normalize(d.Name)))
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First() : null);
        var districts = new Dictionary<long, AddressCatalogDistrict>();
        foreach (var record in source.Districts)
        {
            var district = record.Value;
            if (record.Problem is not null || district is null || district.Id <= 0 || district.ProvinceId != province.Id)
            {
                report.Add(report.Rejected, new AddressCatalogEntry
                {
                    Level = "district",
                    ProvinceId = province.Id,
                    SourceId = district?.Id,
                    Reason = record.Problem ?? (district is null || district.Id <= 0 ? "no id" : "district of another province"),
                    Name = $"{record.File}:{record.Line}"
                });
                continue;
            }

            var name = AddressCatalogText.Normalize(district.Name);
            waslaDistricts.TryGetValue(AddressCatalogText.TurkishKey(name), out var match);
            report.SourceDistricts++;
            if (match is not null)
                report.MatchedDistricts++;
            var coverage = new AddressCatalogDistrict
            {
                ProvinceId = province.Id,
                Province = provinceName,
                SourceDistrictId = district.Id,
                District = name,
                WaslaDistrictId = match?.Id
            };
            districts[district.Id] = coverage;
            report.Districts.Add(coverage);
            if (match is null)
            {
                report.Add(report.ParentsNotImported, new AddressCatalogEntry
                {
                    Level = "district",
                    ProvinceId = province.Id,
                    SourceId = district.Id,
                    Name = name,
                    Reason = city is null
                        ? "no CentralDb city has this plate code and name"
                        : "the district is not in CentralDb"
                });
            }
        }

        // --- Neighbourhoods.
        var sourceNeighborhoods = new Dictionary<long, SourceNeighborhood>();
        var candidates = new List<NeighborhoodCandidate>();
        foreach (var record in source.Neighborhoods)
        {
            var value = record.Value;
            if (record.Problem is not null || value is null || value.Id <= 0)
            {
                report.Neighborhoods.Source++;
                report.Neighborhoods.Rejected++;
                report.Add(report.Rejected, new AddressCatalogEntry
                {
                    Level = "neighborhood",
                    ProvinceId = province.Id,
                    SourceId = value?.Id,
                    Name = $"{record.File}:{record.Line}",
                    Reason = record.Problem ?? "no id"
                });
                continue;
            }

            sourceNeighborhoods.TryAdd(value.Id, value);
            if (!districts.TryGetValue(value.DistrictId, out var coverage) || value.ProvinceId != province.Id)
            {
                RejectWithoutDistrict(report, report.Neighborhoods, "neighborhood", province.Id, value.Id, value.Name,
                    value.ProvinceId != province.Id ? "neighbourhood of another province" : $"district {value.DistrictId} is not in the source province");
                continue;
            }

            coverage.Neighborhoods.Source++;
            if (duplicateNeighborhoodIds.Contains(value.Id))
            {
                Reject(report, coverage.Neighborhoods, "neighborhood", province.Id, value.Id, value.Name, "the source id appears more than once");
                continue;
            }

            if (coverage.WaslaDistrictId is not { } waslaDistrictId)
            {
                coverage.Neighborhoods.ParentNotImported++;
                continue;
            }

            var name = AddressCatalogText.Normalize(value.Name);
            var nameSource = NameSource.Name;
            if (name.Length == 0)
            {
                name = AddressCatalogText.Normalize(value.FullOfficialName);
                nameSource = NameSource.OfficialForMissingName;
            }

            if (AddressCatalogText.Problem(name, AddressCatalogText.NeighborhoodNameMaxLength) is { } problem)
            {
                Reject(report, coverage.Neighborhoods, "neighborhood", province.Id, value.Id, name, problem);
                continue;
            }

            candidates.Add(new NeighborhoodCandidate(value, coverage, waslaDistrictId, name, nameSource));
        }

        // Names shared within a district: every member takes its official full name, which names its village or town.
        foreach (var group in candidates.GroupBy(c => (c.WaslaDistrictId, AddressCatalogText.MatchKey(c.Name))).Where(g => g.Count() > 1))
        {
            foreach (var candidate in group)
            {
                candidate.Name = AddressCatalogText.Normalize(candidate.Source.FullOfficialName);
                candidate.NameSource = NameSource.OfficialForSharedName;
                candidate.Problem = AddressCatalogText.Problem(candidate.Name, AddressCatalogText.NeighborhoodNameMaxLength);
            }
        }

        foreach (var candidate in candidates.Where(c => c.Problem is not null))
            Reject(report, candidate.Coverage.Neighborhoods, "neighborhood", province.Id, candidate.Source.Id, candidate.Name, candidate.Problem!);

        var importable = new List<NeighborhoodCandidate>();
        foreach (var group in candidates.Where(c => c.Problem is null).GroupBy(c => (c.WaslaDistrictId, AddressCatalogText.MatchKey(c.Name))))
        {
            if (group.Count() == 1)
            {
                importable.Add(group.First());
                continue;
            }

            foreach (var candidate in group)
                candidate.Coverage.Neighborhoods.Quarantined++;
            report.Add(new AddressCatalogCollision
            {
                Level = "neighborhood",
                ProvinceId = province.Id,
                District = group.First().Coverage.District,
                SourceIds = group.Select(c => c.Source.Id).Order().ToList(),
                Names = group.Select(c => c.Name).ToList()
            });
        }

        var neighborhoodsByCode = (city?.Neighborhoods ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n.ExternalCode))
            .GroupBy(n => n.ExternalCode!.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var neighborhoodsByName = (city?.Neighborhoods ?? [])
            .GroupBy(n => (n.DistrictId, AddressCatalogText.MatchKey(AddressCatalogText.Normalize(n.Name))))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Where each imported source neighbourhood is, or will be, in CentralDb.
        var parents = new Dictionary<long, int?>();
        var sortOrders = SortOrders(importable, c => c.WaslaDistrictId, c => c.Name, c => string.Empty, c => c.Source.Id);
        foreach (var candidate in importable)
        {
            var code = candidate.Source.Id.ToString(CultureInfo.InvariantCulture);
            var counts = candidate.Coverage.Neighborhoods;
            string? conflict = null;
            if (neighborhoodsByCode.TryGetValue(code, out var withCode))
            {
                if (withCode.Count > 1)
                    conflict = "the source id is on several existing rows";
                else if (withCode[0].DistrictId != candidate.WaslaDistrictId)
                    conflict = $"the source id is on existing row {withCode[0].Id} of another district";
                else
                {
                    counts.Unchanged++;
                    parents[candidate.Source.Id] = withCode[0].Id;
                }
            }
            else if (neighborhoodsByName.TryGetValue((candidate.WaslaDistrictId, AddressCatalogText.MatchKey(candidate.Name)), out var named))
            {
                var row = named[0];
                if (named.Count > 1)
                    conflict = "several existing rows have this name";
                else if (AddressCatalogText.TurkishKey(AddressCatalogText.Normalize(row.Name)) != AddressCatalogText.TurkishKey(candidate.Name))
                    conflict = $"existing row {row.Id} '{row.Name}' differs only in accents or I letters";
                else if (!string.IsNullOrWhiteSpace(row.ExternalCode))
                    conflict = $"existing row {row.Id} has another source id ({row.ExternalCode})";
                else
                {
                    counts.Fill++;
                    plan.NeighborhoodFills.Add((row, code));
                    parents[candidate.Source.Id] = row.Id;
                    report.Add(new AddressCatalogFill
                    {
                        Level = "neighborhood",
                        ProvinceId = province.Id,
                        Province = candidate.Coverage.Province,
                        SourceDistrictId = candidate.Coverage.SourceDistrictId,
                        District = candidate.Coverage.District,
                        WaslaDistrictId = row.DistrictId,
                        WaslaId = row.Id,
                        Name = row.Name,
                        ExternalCode = code,
                        SourceId = candidate.Source.Id,
                        SourceName = candidate.Name
                    });
                }
            }
            else
            {
                counts.Insert++;
                plan.NeighborhoodInserts.Add(new PlannedNeighborhood(candidate.Source.Id, candidate.WaslaDistrictId, candidate.Name, sortOrders[candidate]));
                parents[candidate.Source.Id] = null;
            }

            if (conflict is not null)
            {
                counts.Conflict++;
                report.Add(report.Conflicts, new AddressCatalogEntry
                {
                    Level = "neighborhood", ProvinceId = province.Id, SourceId = candidate.Source.Id, Name = candidate.Name, Reason = conflict
                });
                continue;
            }

            switch (candidate.NameSource)
            {
                case NameSource.Name: report.NeighborhoodNames.Name++; break;
                case NameSource.OfficialForMissingName: report.NeighborhoodNames.OfficialNameForMissingName++; break;
                default: report.NeighborhoodNames.OfficialNameForSharedName++; break;
            }
        }

        // --- Streets.
        var streetCandidates = new List<StreetCandidate>();
        var skippedUnder = new Dictionary<long, int>();
        foreach (var record in source.Streets)
        {
            var value = record.Value;
            if (record.Problem is not null || value is null || value.Id <= 0)
            {
                report.Streets.Source++;
                report.Streets.Rejected++;
                report.Add(report.Rejected, new AddressCatalogEntry
                {
                    Level = "street", ProvinceId = province.Id, SourceId = value?.Id, Name = $"{record.File}:{record.Line}", Reason = record.Problem ?? "no id"
                });
                continue;
            }

            if (!sourceNeighborhoods.TryGetValue(value.NeighborhoodId, out var parent)
                || parent.DistrictId != value.DistrictId
                || value.ProvinceId != province.Id
                || !districts.TryGetValue(parent.DistrictId, out var coverage))
            {
                RejectWithoutDistrict(report, report.Streets, "street", province.Id, value.Id, value.Name,
                    parent is null
                        ? $"neighbourhood {value.NeighborhoodId} is not in the source province"
                        : parent.DistrictId != value.DistrictId || value.ProvinceId != province.Id
                            ? "the street's district or province differs from its neighbourhood's"
                            : $"its neighbourhood's district {parent.DistrictId} is not a readable district of the source province");
                continue;
            }

            coverage.Streets.Source++;
            if (duplicateStreetIds.Contains(value.Id))
            {
                Reject(report, coverage.Streets, "street", province.Id, value.Id, value.Name, "the source id appears more than once");
                continue;
            }

            if (!parents.ContainsKey(parent.Id))
            {
                coverage.Streets.ParentNotImported++;
                skippedUnder[parent.Id] = skippedUnder.GetValueOrDefault(parent.Id) + 1;
                continue;
            }

            if (AddressCatalogText.StreetTypeProblem(value.TypeCode, AddressCatalogText.Normalize(value.FullOfficialName), out var label) is { } typeProblem)
            {
                Reject(report, coverage.Streets, "street", province.Id, value.Id, value.Name, typeProblem);
                continue;
            }

            var name = AddressCatalogText.Normalize(value.Name);
            if (AddressCatalogText.Problem(name, AddressCatalogText.StreetNameMaxLength) is { } problem)
            {
                Reject(report, coverage.Streets, "street", province.Id, value.Id, name, problem);
                continue;
            }

            streetCandidates.Add(new StreetCandidate(value, coverage, parent, name, label!));
        }

        foreach (var (parentId, count) in skippedUnder)
        {
            report.Add(report.ParentsNotImported, new AddressCatalogEntry
            {
                Level = "neighborhood",
                ProvinceId = province.Id,
                SourceId = parentId,
                Name = AddressCatalogText.Normalize(sourceNeighborhoods[parentId].Name ?? sourceNeighborhoods[parentId].FullOfficialName),
                Reason = "the neighbourhood is not imported, so its streets are not",
                Records = count
            });
        }

        var streetImportable = new List<StreetCandidate>();
        foreach (var group in streetCandidates.GroupBy(s => (s.Parent.Id, AddressCatalogText.MatchKey(s.Name), AddressCatalogText.MatchKey(s.StreetType))))
        {
            if (group.Count() == 1)
            {
                streetImportable.Add(group.First());
                continue;
            }

            foreach (var candidate in group)
                candidate.Coverage.Streets.Quarantined++;
            var first = group.First();
            report.Add(new AddressCatalogCollision
            {
                Level = "street",
                ProvinceId = province.Id,
                District = first.Coverage.District,
                Neighborhood = AddressCatalogText.Normalize(first.Parent.Name ?? first.Parent.FullOfficialName),
                StreetType = first.StreetType,
                SourceIds = group.Select(s => s.Source.Id).Order().ToList(),
                Names = group.Select(s => s.Name).ToList()
            });
        }

        var streetsByCode = (city?.Streets ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.ExternalCode))
            .GroupBy(s => s.ExternalCode!.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var streetsByName = (city?.Streets ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.StreetType))
            .GroupBy(s => (s.NeighborhoodId, AddressCatalogText.MatchKey(AddressCatalogText.Normalize(s.Name)), AddressCatalogText.MatchKey(AddressCatalogText.Normalize(s.StreetType))))
            .ToDictionary(g => g.Key, g => g.ToList());
        var existingNeighborhoodNames = (city?.Neighborhoods ?? []).ToDictionary(n => n.Id, n => n.Name);

        var streetOrders = SortOrders(streetImportable, s => s.Parent.Id, s => s.Name, s => s.StreetType, s => s.Source.Id);
        foreach (var candidate in streetImportable)
        {
            var code = candidate.Source.Id.ToString(CultureInfo.InvariantCulture);
            var counts = candidate.Coverage.Streets;
            var parentId = parents[candidate.Parent.Id];
            string? conflict = null;
            if (streetsByCode.TryGetValue(code, out var withCode))
            {
                if (withCode.Count > 1)
                    conflict = "the source id is on several existing rows";
                else if (withCode[0].NeighborhoodId != parentId)
                    conflict = $"the source id is on existing row {withCode[0].Id} of another neighbourhood";
                else
                    counts.Unchanged++;
            }
            else if (parentId is { } existingParent
                     && streetsByName.TryGetValue((existingParent, AddressCatalogText.MatchKey(candidate.Name), AddressCatalogText.MatchKey(candidate.StreetType)), out var named))
            {
                var row = named[0];
                if (named.Count > 1)
                    conflict = "several existing rows have this name and type";
                else if (AddressCatalogText.TurkishKey(AddressCatalogText.Normalize(row.Name)) != AddressCatalogText.TurkishKey(candidate.Name)
                         || AddressCatalogText.TurkishKey(AddressCatalogText.Normalize(row.StreetType)) != AddressCatalogText.TurkishKey(candidate.StreetType))
                    conflict = $"existing row {row.Id} '{row.Name}' differs only in accents or I letters";
                else if (!string.IsNullOrWhiteSpace(row.ExternalCode))
                    conflict = $"existing row {row.Id} has another source id ({row.ExternalCode})";
                else
                {
                    counts.Fill++;
                    plan.StreetFills.Add((row, code));
                    report.Add(new AddressCatalogFill
                    {
                        Level = "street",
                        ProvinceId = province.Id,
                        Province = candidate.Coverage.Province,
                        SourceDistrictId = candidate.Coverage.SourceDistrictId,
                        District = candidate.Coverage.District,
                        WaslaDistrictId = candidate.Coverage.WaslaDistrictId,
                        WaslaNeighborhoodId = row.NeighborhoodId,
                        Neighborhood = existingNeighborhoodNames.GetValueOrDefault(row.NeighborhoodId),
                        SourceNeighborhoodId = candidate.Parent.Id,
                        WaslaId = row.Id,
                        Name = row.Name,
                        StreetType = row.StreetType,
                        ExternalCode = code,
                        SourceId = candidate.Source.Id,
                        SourceName = candidate.Name
                    });
                }
            }
            else
            {
                counts.Insert++;
                plan.StreetInserts.Add(new PlannedStreet(candidate.Source.Id, parentId, candidate.Parent.Id, candidate.Name, candidate.StreetType, streetOrders[candidate]));
            }

            if (conflict is not null)
            {
                counts.Conflict++;
                report.Add(report.Conflicts, new AddressCatalogEntry
                {
                    Level = "street", ProvinceId = province.Id, SourceId = candidate.Source.Id, Name = candidate.Name, Reason = conflict
                });
            }
        }

        foreach (var coverage in districts.Values)
        {
            coverage.Neighborhoods.AddTo(report.Neighborhoods);
            coverage.Streets.AddTo(report.Streets);
        }

        return plan;
    }

    /// <summary>1-based positions in Turkish alphabetical order within each parent, ties broken by source id.</summary>
    private static Dictionary<T, int> SortOrders<T, TParent>(
        IEnumerable<T> items, Func<T, TParent> parent, Func<T, string> name, Func<T, string> type, Func<T, long> id)
        where T : notnull
        where TParent : notnull
    {
        var orders = new Dictionary<T, int>();
        foreach (var group in items.GroupBy(parent))
        {
            var position = 1;
            foreach (var item in group.OrderBy(name, TurkishOrder).ThenBy(type, TurkishOrder).ThenBy(id))
                orders[item] = position++;
        }

        return orders;
    }

    private static void Reject(AddressCatalogReport report, AddressCatalogCounts counts, string level, long provinceId, long sourceId, string? name, string reason)
    {
        counts.Rejected++;
        report.Add(report.Rejected, new AddressCatalogEntry { Level = level, ProvinceId = provinceId, SourceId = sourceId, Name = name, Reason = reason });
    }

    /// <summary>A record that belongs to no district of the province is counted on the totals only.</summary>
    private static void RejectWithoutDistrict(AddressCatalogReport report, AddressCatalogCounts total, string level, long provinceId, long sourceId, string? name, string reason)
    {
        total.Source++;
        total.Rejected++;
        report.Add(report.Rejected, new AddressCatalogEntry { Level = level, ProvinceId = provinceId, SourceId = sourceId, Name = name, Reason = reason });
    }

    private enum NameSource
    {
        Name,
        OfficialForMissingName,
        OfficialForSharedName
    }

    private sealed class NeighborhoodCandidate(SourceNeighborhood source, AddressCatalogDistrict coverage, int waslaDistrictId, string name, NameSource nameSource)
    {
        public SourceNeighborhood Source { get; } = source;
        public AddressCatalogDistrict Coverage { get; } = coverage;
        public int WaslaDistrictId { get; } = waslaDistrictId;
        public string Name { get; set; } = name;
        public NameSource NameSource { get; set; } = nameSource;
        public string? Problem { get; set; }
    }

    private sealed class StreetCandidate(SourceStreet source, AddressCatalogDistrict coverage, SourceNeighborhood parent, string name, string streetType)
    {
        public SourceStreet Source { get; } = source;
        public AddressCatalogDistrict Coverage { get; } = coverage;
        public SourceNeighborhood Parent { get; } = parent;
        public string Name { get; } = name;
        public string StreetType { get; } = streetType;
    }
}
