using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wasla.Infrastructure.ReferenceData;

/// <summary>
/// What a neighbourhood and street catalog run found and did, or would do in a dry run (WAS-214). Every source record
/// is counted in exactly one outcome of its level; the lists name the records behind each outcome other than insert
/// and unchanged (fills, collisions, rejects, conflicts, parents not imported), up to <see cref="MaxListedEntries"/>
/// per list (the counts stay exact). A run that fails produces no report.
/// </summary>
public sealed class AddressCatalogReport
{
    public const int MaxListedEntries = 5000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Mode { get; init; } = string.Empty;
    public string Dataset { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public string Upstream { get; init; } = string.Empty;
    public int FilesVerified { get; init; }
    public AddressCatalogDeclaredCounts DeclaredCounts { get; init; } = new();
    public string? DatabaseCollation { get; set; }
    public int SourceProvinces { get; set; }
    public int SourceDistricts { get; set; }
    public int MatchedProvinces { get; set; }
    public int MatchedDistricts { get; set; }
    public AddressCatalogCounts Neighborhoods { get; init; } = new();
    public AddressCatalogCounts Streets { get; init; } = new();

    /// <summary>Where the stored neighbourhood names come from.</summary>
    public AddressCatalogNameSources NeighborhoodNames { get; init; } = new();

    public List<AddressCatalogDistrict> Districts { get; init; } = [];

    /// <summary>The existing rows that get, or got, a source id as their ExternalCode: the Fill outcome.</summary>
    public List<AddressCatalogFill> Fills { get; init; } = [];

    public List<AddressCatalogCollision> Collisions { get; init; } = [];
    public List<AddressCatalogEntry> Rejected { get; init; } = [];
    public List<AddressCatalogEntry> Conflicts { get; init; } = [];
    public List<AddressCatalogEntry> ParentsNotImported { get; init; } = [];
    public bool ListsTruncated { get; set; }

    public void Add(List<AddressCatalogEntry> list, AddressCatalogEntry entry)
    {
        if (list.Count < MaxListedEntries)
            list.Add(entry);
        else
            ListsTruncated = true;
    }

    public void Add(AddressCatalogCollision collision)
    {
        if (Collisions.Count < MaxListedEntries)
            Collisions.Add(collision);
        else
            ListsTruncated = true;
    }

    public void Add(AddressCatalogFill fill)
    {
        if (Fills.Count < MaxListedEntries)
            Fills.Add(fill);
        else
            ListsTruncated = true;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Outcomes of one level. Source equals the sum of the other counts.</summary>
public sealed class AddressCatalogCounts
{
    public int Source { get; set; }

    /// <summary>New rows (planned in a dry run).</summary>
    public int Insert { get; set; }

    /// <summary>Existing rows with the same name and no ExternalCode, which get the source id.</summary>
    public int Fill { get; set; }

    /// <summary>Existing rows that already carry the source id.</summary>
    public int Unchanged { get; set; }

    /// <summary>Records that share a name with another record of the same parent and are not imported.</summary>
    public int Quarantined { get; set; }

    /// <summary>Records that fail validation.</summary>
    public int Rejected { get; set; }

    /// <summary>Records an existing row stands in the way of; the row is left as it is.</summary>
    public int Conflict { get; set; }

    /// <summary>Records whose parent is not imported (missing in CentralDb, rejected, quarantined or in conflict).</summary>
    public int ParentNotImported { get; set; }

    [JsonIgnore]
    public int Accounted => Insert + Fill + Unchanged + Quarantined + Rejected + Conflict + ParentNotImported;

    public void AddTo(AddressCatalogCounts total)
    {
        total.Source += Source;
        total.Insert += Insert;
        total.Fill += Fill;
        total.Unchanged += Unchanged;
        total.Quarantined += Quarantined;
        total.Rejected += Rejected;
        total.Conflict += Conflict;
        total.ParentNotImported += ParentNotImported;
    }
}

public sealed class AddressCatalogNameSources
{
    /// <summary>The source's own name.</summary>
    public int Name { get; set; }

    /// <summary>The official full name, because the source record has no name (village records).</summary>
    public int OfficialNameForMissingName { get; set; }

    /// <summary>The official full name, because another neighbourhood of the district has the same name.</summary>
    public int OfficialNameForSharedName { get; set; }
}

public sealed class AddressCatalogDistrict
{
    public long ProvinceId { get; init; }
    public string Province { get; init; } = string.Empty;
    public long SourceDistrictId { get; init; }
    public string District { get; init; } = string.Empty;
    public int? WaslaDistrictId { get; init; }
    public AddressCatalogCounts Neighborhoods { get; init; } = new();
    public AddressCatalogCounts Streets { get; init; } = new();
}

/// <summary>
/// An existing CentralDb row whose name matched a source record under Turkish case rules and which had no ExternalCode:
/// the import writes <see cref="ExternalCode"/> to it and changes nothing else. <see cref="Name"/> and
/// <see cref="StreetType"/> are the row's own; <see cref="SourceName"/> is the name the source gives.
/// </summary>
public sealed class AddressCatalogFill
{
    /// <summary>"neighborhood" or "street".</summary>
    public string Level { get; init; } = string.Empty;
    public long ProvinceId { get; init; }
    public string Province { get; init; } = string.Empty;
    public long SourceDistrictId { get; init; }
    public string District { get; init; } = string.Empty;
    public int? WaslaDistrictId { get; init; }

    /// <summary>Streets: the existing neighbourhood row the street is under, and its name.</summary>
    public int? WaslaNeighborhoodId { get; init; }
    public string? Neighborhood { get; init; }

    /// <summary>Streets: the source neighbourhood the source street is under.</summary>
    public long? SourceNeighborhoodId { get; init; }

    /// <summary>The existing row's id, which does not change.</summary>
    public int WaslaId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? StreetType { get; init; }

    /// <summary>The ExternalCode the row gets: the source id.</summary>
    public string ExternalCode { get; init; } = string.Empty;
    public long SourceId { get; init; }
    public string SourceName { get; init; } = string.Empty;
}

/// <summary>Records of one parent whose names a collation could treat as equal; none of them is imported.</summary>
public sealed class AddressCatalogCollision
{
    public string Level { get; init; } = string.Empty;
    public long ProvinceId { get; init; }
    public string District { get; init; } = string.Empty;
    public string? Neighborhood { get; init; }
    public string? StreetType { get; init; }
    public List<long> SourceIds { get; init; } = [];
    public List<string> Names { get; init; } = [];
}

public sealed class AddressCatalogEntry
{
    public string Level { get; init; } = string.Empty;
    public long ProvinceId { get; init; }
    public long? SourceId { get; init; }
    public int? WaslaId { get; init; }
    public string? Name { get; init; }
    public string Reason { get; init; } = string.Empty;
    public int? Records { get; init; }
}
