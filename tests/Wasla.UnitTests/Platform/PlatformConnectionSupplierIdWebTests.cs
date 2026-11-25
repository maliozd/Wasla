using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The SupplierId limit through the real tenant application (<see cref="TenantSessionWebHost"/>: the real controller,
/// Razor views, create validator and service over SQLite). The column holds 64 characters. Both forms limit the input to
/// that, a create or edit of 64 characters is stored, and one of 65 is refused with the localized message and changes
/// nothing. SQLite does not enforce the column length, so a refused value that got through would be stored here.
/// </summary>
public sealed class PlatformConnectionSupplierIdWebTests : IAsyncLifetime
{
    private static readonly string SupplierId64 = new('5', 64);
    private static readonly string SupplierId65 = new('5', 65);
    private static readonly Regex InputTag = new("<input\\b[^>]*>", RegexOptions.Compiled);

    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantSessionWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task BothForms_LimitTheSupplierIdInputTo64Characters()
    {
        var id = Seed();
        using var owner = await OwnerAsync();

        Assert.Contains("maxlength=\"64\"", Input(await HtmlAsync(owner, "/platform-connections/create"), "SupplierId"));
        Assert.Contains("maxlength=\"64\"", Input(await HtmlAsync(owner, $"/platform-connections/{id}/edit"), "SupplierId"));
    }

    [Fact]
    public async Task Create_StoresASupplierIdOf64Characters()
    {
        using var owner = await OwnerAsync();
        var form = CreateForm(SupplierId64);
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync("/platform-connections/create");

        AssertRedirectsTo(await owner.PostFormAsync("/platform-connections/create", form), "/platform-connections");

        Assert.Equal(SupplierId64, Read().SupplierId);
    }

    [Fact]
    public async Task Create_RefusesASupplierIdOf65Characters_WithTheLocalizedMessage()
    {
        using var owner = await OwnerAsync();
        var form = CreateForm(SupplierId65);
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync("/platform-connections/create");

        var response = await owner.PostFormAsync("/platform-connections/create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(WebUtility.HtmlEncode(Resource("en-US", "Validation.SupplierIdMaxLength")), html);
        Assert.Equal(0, ConnectionCount());
    }

    [Fact]
    public async Task Edit_StoresASupplierIdOf64Characters()
    {
        var id = Seed();
        using var owner = await OwnerAsync();
        var form = EditForm(SupplierId64);
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync($"/platform-connections/{id}/edit");

        AssertRedirectsTo(await owner.PostFormAsync($"/platform-connections/{id}/edit", form), "/platform-connections");

        var after = Read();
        Assert.Equal(SupplierId64, after.SupplierId);
        Assert.Equal("TY-STORE-2", after.StoreId);
    }

    [Fact]
    public async Task Edit_RefusesASupplierIdOf65Characters_WithTheLocalizedMessage_AndChangesNoField()
    {
        var id = Seed();
        using var owner = await OwnerAsync();
        var before = Read();
        // Every other field changes too, so a partial write would show.
        var form = EditForm(SupplierId65);
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync($"/platform-connections/{id}/edit");

        var response = await owner.PostFormAsync($"/platform-connections/{id}/edit", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(WebUtility.HtmlEncode(Resource("en-US", "Validation.SupplierIdMaxLength")), html);
        Assert.Equivalent(before, Read(), strict: true);
    }

    [Theory]
    [InlineData("SharedResource.resx")]
    [InlineData("SharedResource.tr-TR.resx")]
    [InlineData("SharedResource.en-US.resx")]
    [InlineData("SharedResource.ar-SA.resx")]
    [InlineData("SharedResource.ru-RU.resx")]
    public void SupplierIdMaxLengthMessage_NamesTheLimitOf64(string file)
    {
        var message = ResourceIn(file, "Validation.SupplierIdMaxLength");

        Assert.NotNull(message);
        Assert.Matches(@"(?<!\d)64(?!\d)", message);
    }

    private Guid Seed()
    {
        var connection = new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = "TY-STORE-1",
            SupplierId = "5501",
            ExecutorEmail = "ops@alpha.test",
            EncryptedApiKey = "stored-key",
            EncryptedApiSecret = "stored-secret",
            EncryptionKeyVersion = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddDays(-3),
            UpdatedAt = DateTime.UtcNow.AddDays(-3)
        };
        _host.Tenants.Seed(_host.AlphaId, db => db.PlatformConnections.Add(connection));
        return connection.Id;
    }

    private async Task<TenantSessionClient> OwnerAsync()
    {
        _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
        var client = _host.Client(TenantSessionWebHost.AlphaHost);
        await client.LoginAsync("owner@alpha.test");
        return client;
    }

    private static Dictionary<string, string> CreateForm(string supplierId) => new()
    {
        ["Platform"] = nameof(FoodPlatform.TrendyolYemek),
        ["StoreId"] = "TY-STORE-1",
        ["SupplierId"] = supplierId,
        ["ExecutorEmail"] = "ops@alpha.test",
        ["ApiKey"] = "typed-key",
        ["ApiSecret"] = "typed-secret",
        ["IsActive"] = "true"
    };

    private static Dictionary<string, string> EditForm(string supplierId) => new()
    {
        ["Platform"] = nameof(FoodPlatform.TrendyolYemek),
        ["StoreId"] = "TY-STORE-2",
        ["SupplierId"] = supplierId,
        ["ExecutorEmail"] = "new@alpha.test",
        ["ApiKey"] = "typed-key",
        ["ApiSecret"] = "typed-secret",
        ["IsActive"] = "false"
    };

    private PlatformConnection Read()
    {
        using var db = TenantDb();
        return db.PlatformConnections.AsNoTracking().Single();
    }

    private int ConnectionCount()
    {
        using var db = TenantDb();
        return db.PlatformConnections.Count();
    }

    private TenantDbContext TenantDb() =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(_host.Tenants.ConnectionStringFor(_host.AlphaId)).Options);

    private static string Input(string html, string name)
    {
        var inputs = InputTag.Matches(html).Select(m => m.Value).Where(tag => tag.Contains($"name=\"{name}\"", StringComparison.Ordinal)).ToList();
        Assert.True(inputs.Count == 1, $"Expected one {name} input, found {inputs.Count}.");
        return inputs[0];
    }

    private static async Task<string> HtmlAsync(TenantSessionClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static void AssertRedirectsTo(HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        var actual = location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
        Assert.True(actual == path || actual.StartsWith(path + "?", StringComparison.Ordinal), $"Redirected to {actual}, not {path}.");
    }

    private static string Resource(string culture, string key) =>
        ResourceIn($"SharedResource.{culture}.resx", key) ?? $"[missing {key} in {culture}]";

    private static string? ResourceIn(string file, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", file))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault();
}
