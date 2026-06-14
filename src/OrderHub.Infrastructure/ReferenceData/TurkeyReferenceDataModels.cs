using System.Text.Json.Serialization;

namespace OrderHub.Infrastructure.ReferenceData;

internal sealed class TurkeyReferenceDataFile
{
    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; } = "TR";

    [JsonPropertyName("cities")]
    public List<TurkeyReferenceCityData> Cities { get; set; } = [];
}

internal sealed class TurkeyReferenceCityData
{
    [JsonPropertyName("plateCode")]
    public string PlateCode { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("phoneAreaCode")]
    public string? PhoneAreaCode { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("districts")]
    public List<string> Districts { get; set; } = [];
}
