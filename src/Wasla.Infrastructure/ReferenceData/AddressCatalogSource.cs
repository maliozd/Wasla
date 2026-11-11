using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wasla.Infrastructure.ReferenceData;

/// <summary>
/// The pinned source of the neighbourhood and street catalog (WAS-214): which dataset and commit, and the size and
/// SHA-256 of every file the importer reads. The importer reads no file the manifest does not list and nothing at all
/// until every listed file matches.
/// </summary>
public sealed class AddressCatalogSourceManifest
{
    private const string EmbeddedResourceName = "Wasla.Infrastructure.ReferenceData.turkey-address-catalog-source.json";

    [JsonPropertyName("dataset")] public string Dataset { get; init; } = string.Empty;
    [JsonPropertyName("repository")] public string Repository { get; init; } = string.Empty;
    [JsonPropertyName("commit")] public string Commit { get; init; } = string.Empty;
    [JsonPropertyName("builtAtUtc")] public string BuiltAtUtc { get; init; } = string.Empty;
    [JsonPropertyName("upstream")] public string Upstream { get; init; } = string.Empty;
    [JsonPropertyName("declaredCounts")] public AddressCatalogDeclaredCounts DeclaredCounts { get; init; } = new();
    [JsonPropertyName("files")] public List<AddressCatalogSourceFile> Files { get; init; } = [];

    /// <summary>The manifest of the pinned turkey-geo-api snapshot, embedded in this assembly.</summary>
    public static AddressCatalogSourceManifest Pinned()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedResourceName}' was not found.");
        return JsonSerializer.Deserialize<AddressCatalogSourceManifest>(stream)
            ?? throw new InvalidOperationException("The address catalog source manifest is empty.");
    }
}

public sealed class AddressCatalogDeclaredCounts
{
    [JsonPropertyName("provinces")] public int Provinces { get; init; }
    [JsonPropertyName("districts")] public int Districts { get; init; }
    [JsonPropertyName("neighborhoods")] public int Neighborhoods { get; init; }
    [JsonPropertyName("streets")] public int Streets { get; init; }
}

public sealed class AddressCatalogSourceFile
{
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
}

/// <summary>The source directory does not hold exactly the pinned files.</summary>
public sealed class AddressCatalogSourceException(string message) : Exception(message);

/// <summary>Reads the verified source directory (the <c>data/jsonl</c> folder of the pinned commit).</summary>
public sealed class AddressCatalogSource
{
    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.Strict };

    private readonly string _root;
    private readonly HashSet<string> _verified;

    private AddressCatalogSource(string root, AddressCatalogSourceManifest manifest, HashSet<string> verified)
    {
        _root = root;
        Manifest = manifest;
        _verified = verified;
    }

    public AddressCatalogSourceManifest Manifest { get; }

    /// <summary>Checks the size and SHA-256 of every manifest file before anything is read.</summary>
    /// <exception cref="AddressCatalogSourceException">A file is missing, unlisted paths are named, or a file differs.</exception>
    public static async Task<AddressCatalogSource> OpenAsync(string directory, AddressCatalogSourceManifest manifest, CancellationToken ct)
    {
        var root = System.IO.Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            throw new AddressCatalogSourceException($"The source directory '{root}' does not exist.");
        if (manifest.Files.Count == 0)
            throw new AddressCatalogSourceException("The source manifest lists no files.");

        var problems = new List<string>();
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var full = Resolve(root, file.Path);
            if (full is null)
            {
                problems.Add($"{file.Path}: not a relative path inside the source directory");
                continue;
            }

            var info = new FileInfo(full);
            if (!info.Exists)
            {
                problems.Add($"{file.Path}: missing");
                continue;
            }

            if (info.Length != file.Bytes)
            {
                problems.Add($"{file.Path}: {info.Length} bytes, expected {file.Bytes}");
                continue;
            }

            await using var stream = info.OpenRead();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{file.Path}: SHA-256 {hash}, expected {file.Sha256}");
                continue;
            }

            verified.Add(file.Path);
        }

        if (problems.Count > 0)
        {
            throw new AddressCatalogSourceException(
                $"The source directory does not match the pinned {manifest.Dataset} at {manifest.Commit} ({problems.Count} file(s)): "
                + string.Join("; ", problems.Take(10))
                + (problems.Count > 10 ? "; …" : string.Empty));
        }

        return new AddressCatalogSource(root, manifest, verified);
    }

    public IReadOnlyList<SourceRecord<SourceProvince>> Provinces() => Read<SourceProvince>("provinces.jsonl").ToList();

    public IEnumerable<SourceRecord<SourceDistrict>> Districts(long provinceId) => Read<SourceDistrict>($"province-{provinceId}/districts.jsonl");

    public IEnumerable<SourceRecord<SourceNeighborhood>> Neighborhoods(long provinceId) => Read<SourceNeighborhood>($"province-{provinceId}/neighborhoods.jsonl");

    public IEnumerable<SourceRecord<SourceStreet>> Streets(long provinceId) => Read<SourceStreet>($"province-{provinceId}/streets.jsonl");

    /// <summary>Each non-empty line as a record, or as the reason it could not be read.</summary>
    private IEnumerable<SourceRecord<T>> Read<T>(string path) where T : class
    {
        if (!_verified.Contains(path))
            throw new AddressCatalogSourceException($"{path} is not a verified file of the pinned source.");

        var number = 0;
        foreach (var line in File.ReadLines(Resolve(_root, path)!, new UTF8Encoding(false, throwOnInvalidBytes: false)))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            T? value = null;
            string? problem = null;
            try
            {
                value = JsonSerializer.Deserialize<T>(line, Json);
                if (value is null)
                    problem = "empty record";
            }
            catch (JsonException ex)
            {
                problem = "malformed JSON: " + ex.Message;
            }

            yield return new SourceRecord<T>(path, number, value, problem);
        }
    }

    private static string? Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative))
            return null;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        return full.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : null;
    }
}

public sealed record SourceRecord<T>(string File, int Line, T? Value, string? Problem) where T : class;

public sealed class SourceProvince
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public sealed class SourceDistrict
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("province_id")] public long ProvinceId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public sealed class SourceNeighborhood
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("province_id")] public long ProvinceId { get; init; }
    [JsonPropertyName("district_id")] public long DistrictId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("full_official_name")] public string? FullOfficialName { get; init; }
}

public sealed class SourceStreet
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("province_id")] public long ProvinceId { get; init; }
    [JsonPropertyName("district_id")] public long DistrictId { get; init; }
    [JsonPropertyName("neighborhood_id")] public long NeighborhoodId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("full_official_name")] public string? FullOfficialName { get; init; }
    [JsonPropertyName("type_code")] public int? TypeCode { get; init; }
}
