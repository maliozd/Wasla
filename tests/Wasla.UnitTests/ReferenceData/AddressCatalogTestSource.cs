using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wasla.Infrastructure.ReferenceData;

namespace Wasla.UnitTests.ReferenceData;

/// <summary>
/// A synthetic source folder in the layout of turkey-geo-api's <c>data/jsonl</c> (WAS-214), with a manifest of its own
/// files. The records are invented; only their shape follows the pinned dataset.
/// </summary>
internal sealed class AddressCatalogTestSource : IDisposable
{
    private static readonly Dictionary<int, string> Types = new()
    {
        [0] = "Köy Sokağı", [1] = "Meydan", [2] = "Bulvar", [3] = "Cadde", [4] = "Sokak", [5] = "Küme Evler"
    };

    private readonly List<string> _provinces = [];
    private readonly SortedDictionary<long, Province> _byProvince = new();

    public AddressCatalogTestSource()
    {
        Root = Path.Combine(Path.GetTempPath(), "wasla-was214-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public AddressCatalogTestSource AddProvince(long id, string name)
    {
        _provinces.Add(Line(new { id, name, full_official_name = name }));
        _byProvince[id] = new Province();
        return this;
    }

    public AddressCatalogTestSource AddDistrict(long province, long id, string name)
    {
        _byProvince[province].Districts.Add(Line(new { id, province_id = province, name, full_official_name = name }));
        return this;
    }

    /// <param name="official">Defaults to "<paramref name="name"/> MAHALLESİ".</param>
    public AddressCatalogTestSource AddNeighborhood(long province, long district, long id, string? name, string? official = null)
    {
        _byProvince[province].Neighborhoods.Add(Line(new
        {
            district_id = district,
            full_official_name = official ?? name + " MAHALLESİ",
            id,
            name,
            province_id = province
        }));
        return this;
    }

    /// <param name="official">Defaults to "<paramref name="name"/> (label of <paramref name="typeCode"/>)".</param>
    public AddressCatalogTestSource AddStreet(long province, long district, long neighborhood, long id, string? name, int? typeCode = 4, string? official = null)
    {
        _byProvince[province].Streets.Add(Line(new
        {
            district_id = district,
            full_official_name = official ?? $"{name} ({(typeCode is { } t && Types.TryGetValue(t, out var label) ? label : "?")})",
            id,
            name,
            neighborhood_id = neighborhood,
            province_id = province,
            type_code = typeCode
        }));
        return this;
    }

    /// <summary>A line written as it is, for malformed input.</summary>
    public AddressCatalogTestSource AddRawStreetLine(long province, string line)
    {
        _byProvince[province].Streets.Add(line);
        return this;
    }

    /// <summary>Writes every file and returns a manifest that matches them.</summary>
    public AddressCatalogSourceManifest Write()
    {
        var files = new List<AddressCatalogSourceFile>
        {
            Save("meta.json", "{\"built_at_utc\":\"2026-11-10T00:00:00.000Z\"}\n"),
            Save("provinces.jsonl", string.Concat(_provinces.Select(l => l + "\n")))
        };
        foreach (var (id, province) in _byProvince)
        {
            files.Add(Save($"province-{id}/districts.jsonl", string.Concat(province.Districts.Select(l => l + "\n"))));
            files.Add(Save($"province-{id}/neighborhoods.jsonl", string.Concat(province.Neighborhoods.Select(l => l + "\n"))));
            files.Add(Save($"province-{id}/streets.jsonl", string.Concat(province.Streets.Select(l => l + "\n"))));
        }

        return new AddressCatalogSourceManifest
        {
            Dataset = "synthetic test catalog",
            Repository = "https://example.test/catalog",
            Commit = "0000000000000000000000000000000000000000",
            Upstream = "invented records",
            Files = files
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private AddressCatalogSourceFile Save(string relative, string content)
    {
        var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        File.WriteAllBytes(full, bytes);
        return new AddressCatalogSourceFile { Path = relative, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    }

    // Raw UTF-8, as the pinned files are written, not \u escapes.
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Line(object value) => JsonSerializer.Serialize(value, Json);

    private sealed class Province
    {
        public List<string> Districts { get; } = [];
        public List<string> Neighborhoods { get; } = [];
        public List<string> Streets { get; } = [];
    }
}
