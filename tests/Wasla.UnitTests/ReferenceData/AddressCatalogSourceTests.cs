using Wasla.Infrastructure.ReferenceData;

namespace Wasla.UnitTests.ReferenceData;

/// <summary>WAS-214: the catalog source is read only when every pinned file matches its manifest entry.</summary>
public sealed class AddressCatalogSourceTests : IDisposable
{
    private readonly AddressCatalogTestSource _source = new AddressCatalogTestSource()
        .AddProvince(34, "İSTANBUL")
        .AddDistrict(34, 1421, "KADIKÖY")
        .AddNeighborhood(34, 1421, 40001, "CAFERAĞA")
        .AddStreet(34, 1421, 40001, 900001, "MODA", typeCode: 3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _source.Dispose();

    [Fact]
    public void ThePinnedManifest_NamesTheCommit_AndEveryFileTheImporterReads()
    {
        var manifest = AddressCatalogSourceManifest.Pinned();

        Assert.Equal("https://github.com/onurusluca/turkey-geo-api", manifest.Repository);
        Assert.Equal("e44ca84316a8b3d6ff3d9b898b806b1c3d853449", manifest.Commit);
        Assert.Equal(81, manifest.DeclaredCounts.Provinces);
        Assert.Equal(973, manifest.DeclaredCounts.Districts);
        Assert.Equal(73398, manifest.DeclaredCounts.Neighborhoods);
        Assert.Equal(1276922, manifest.DeclaredCounts.Streets);
        var paths = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(2 + 81 * 3, paths.Count);
        Assert.Contains("provinces.jsonl", paths);
        for (var plate = 1; plate <= 81; plate++)
        {
            Assert.Contains($"province-{plate}/districts.jsonl", paths);
            Assert.Contains($"province-{plate}/neighborhoods.jsonl", paths);
            Assert.Contains($"province-{plate}/streets.jsonl", paths);
        }

        Assert.All(manifest.Files, f =>
        {
            Assert.True(f.Bytes > 0);
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
        });
    }

    [Fact]
    public async Task AMatchingFolder_IsRead_RecordByRecord_WithTurkishTextIntact()
    {
        var source = await AddressCatalogSource.OpenAsync(_source.Root, _source.Write(), Ct);

        var province = Assert.Single(source.Provinces());
        Assert.Equal("İSTANBUL", province.Value!.Name);
        var neighborhood = Assert.Single(source.Neighborhoods(34));
        Assert.Equal("CAFERAĞA", neighborhood.Value!.Name);
        var street = Assert.Single(source.Streets(34));
        Assert.Equal(900001, street.Value!.Id);
        Assert.Equal(3, street.Value.TypeCode);
        Assert.Null(street.Problem);
    }

    [Fact]
    public async Task AChangedFile_StopsEverything_BeforeAnyRecordIsRead()
    {
        var manifest = _source.Write();
        await File.AppendAllTextAsync(Path.Combine(_source.Root, "province-34", "streets.jsonl"), "{\"id\":1}\n", Ct);

        var error = await Assert.ThrowsAsync<AddressCatalogSourceException>(() => AddressCatalogSource.OpenAsync(_source.Root, manifest, Ct));
        Assert.Contains("province-34/streets.jsonl", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWithTheSameSizeButOtherBytes_IsRefusedByItsSha256()
    {
        var manifest = _source.Write();
        var path = Path.Combine(_source.Root, "province-34", "neighborhoods.jsonl");
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        bytes[^3] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes, Ct);

        var error = await Assert.ThrowsAsync<AddressCatalogSourceException>(() => AddressCatalogSource.OpenAsync(_source.Root, manifest, Ct));
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingFile_OrAPathOutsideTheFolder_IsRefused()
    {
        var manifest = _source.Write();
        File.Delete(Path.Combine(_source.Root, "province-34", "districts.jsonl"));
        var escaping = new AddressCatalogSourceManifest
        {
            Files = [.. manifest.Files, new AddressCatalogSourceFile { Path = "../outside.jsonl", Bytes = 1, Sha256 = new string('0', 64) }]
        };

        var error = await Assert.ThrowsAsync<AddressCatalogSourceException>(() => AddressCatalogSource.OpenAsync(_source.Root, escaping, Ct));
        Assert.Contains("province-34/districts.jsonl: missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("../outside.jsonl: not a relative path inside the source directory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileTheManifestDoesNotList_IsNeverRead()
    {
        var manifest = _source.Write();
        var withoutStreets = new AddressCatalogSourceManifest { Files = manifest.Files.Where(f => f.Path != "province-34/streets.jsonl").ToList() };
        var source = await AddressCatalogSource.OpenAsync(_source.Root, withoutStreets, Ct);

        Assert.Throws<AddressCatalogSourceException>(() => source.Streets(34).ToList());
    }

    [Fact]
    public async Task AMalformedLine_IsReported_WithItsFileAndLine()
    {
        _source.AddRawStreetLine(34, "{\"id\": \"not a number\"");
        var source = await AddressCatalogSource.OpenAsync(_source.Root, _source.Write(), Ct);

        var lines = source.Streets(34).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Null(lines[0].Problem);
        Assert.Equal("province-34/streets.jsonl", lines[1].File);
        Assert.Equal(2, lines[1].Line);
        Assert.StartsWith("malformed JSON", lines[1].Problem, StringComparison.Ordinal);
    }
}
