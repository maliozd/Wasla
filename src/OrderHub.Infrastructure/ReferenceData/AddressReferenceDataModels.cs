using System.Text.Json.Serialization;

namespace OrderHub.Infrastructure.ReferenceData;

internal sealed class AddressNeighborhoodDataFile
{
    [JsonPropertyName("entries")]
    public List<AddressNeighborhoodDistrictEntry> Entries { get; set; } = [];
}

internal sealed class AddressNeighborhoodDistrictEntry
{
    [JsonPropertyName("cityPlateCode")]
    public string CityPlateCode { get; set; } = string.Empty;

    [JsonPropertyName("districtName")]
    public string DistrictName { get; set; } = string.Empty;

    [JsonPropertyName("neighborhoods")]
    public List<AddressNeighborhoodEntry> Neighborhoods { get; set; } = [];
}

internal sealed class AddressNeighborhoodEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("externalCode")]
    public string? ExternalCode { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }
}

internal sealed class AddressStreetDataFile
{
    [JsonPropertyName("entries")]
    public List<AddressStreetDistrictEntry> Entries { get; set; } = [];
}

internal sealed class AddressStreetDistrictEntry
{
    [JsonPropertyName("cityPlateCode")]
    public string CityPlateCode { get; set; } = string.Empty;

    [JsonPropertyName("districtName")]
    public string DistrictName { get; set; } = string.Empty;

    [JsonPropertyName("neighborhoodName")]
    public string NeighborhoodName { get; set; } = string.Empty;

    [JsonPropertyName("streets")]
    public List<AddressStreetEntry> Streets { get; set; } = [];
}

internal sealed class AddressStreetEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("streetType")]
    public string? StreetType { get; set; }

    [JsonPropertyName("externalCode")]
    public string? ExternalCode { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }
}
