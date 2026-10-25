using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Removing a platform connection through the real tenant application (<see cref="TenantSessionWebHost"/>: real tenant
/// resolution for two tenant hosts, cookie scheme, role policies and antiforgery, the real controller, Razor views and
/// Infrastructure services over one SQLite database per tenant). Every row, in every state, has Remove beside Edit and its
/// own confirmation naming the provider and Store ID; only the confirmation's POST removes, only for an Owner of the
/// tenant that owns the connection, and only with an antiforgery token. A repeated or foreign submission changes nothing
/// and says so with the same message. Stored credentials never reach the page or the log.
/// </summary>
public sealed class PlatformConnectionRemovalWebTests : IAsyncLifetime
{
    private const string StoredKey = "stored-apikey-sentinel-3e8b1f42";
    private const string StoredSecret = "stored-apisecret-sentinel-a9c47d10";

    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantSessionWebHost.StartAsync(configuration: new Dictionary<string, string?>
    {
        ["Logging:LogLevel:Default"] = "Trace"
    });

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task EveryRow_HasRemoveBesideEdit_AndAConfirmationNamingItsProviderAndStore()
    {
        var active = Seed(_host.AlphaId, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        var circuitOpen = Seed(_host.AlphaId, FoodPlatform.TrendyolYemek, "TY-STORE-2", c =>
        {
            c.ConsecutiveFailures = 5;
            c.CircuitOpenUntil = DateTime.UtcNow.AddMinutes(5);
        });
        var inactive = Seed(_host.AlphaId, FoodPlatform.GetirYemek, "GY-STORE-3", c => c.IsActive = false);
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);

        var html = await HtmlAsync(owner, "/platform-connections");

        foreach (var (id, platform, store) in new[]
                 {
                     (active, FoodPlatform.Yemeksepeti, "YS-STORE-1"),
                     (circuitOpen, FoodPlatform.TrendyolYemek, "TY-STORE-2"),
                     (inactive, FoodPlatform.GetirYemek, "GY-STORE-3")
                 })
        {
            var actions = Between(html, $"href=\"/platform-connections/{id}/edit\"", "</td>");
            Assert.Contains($"data-bs-target=\"#pc-remove-{id}\"", actions);
            Assert.Contains($">{Resource("en-US", "PlatformConnections.Remove")}</span>", actions);

            var dialog = Between(html, $"id=\"pc-remove-{id}\"", "</form>");
            Assert.Contains($"aria-labelledby=\"pc-remove-{id}-title\"", dialog);
            Assert.Contains(WebUtility.HtmlEncode(Resource("en-US", "PlatformConnections.RemoveConfirmTitle")), dialog);
            Assert.Contains($"<dd>{platform}</dd>", dialog);
            Assert.Contains($"<dd dir=\"auto\">{store}</dd>", dialog);
            Assert.Contains(WebUtility.HtmlEncode(Resource("en-US", "PlatformConnections.RemoveConfirmStopsSync")), dialog);
            Assert.Contains($">{Resource("en-US", "Common.Cancel")}</button>", dialog);
            Assert.Contains($"action=\"/platform-connections/{id}/remove\"", dialog);
            Assert.Contains("name=\"__RequestVerificationToken\"", dialog);
            // The dialog posts nothing but its token: the connection is the id in its own action.
            Assert.Single(Regex.Matches(dialog[dialog.IndexOf("<form", StringComparison.Ordinal)..], "<input\\b"));
        }

        AssertNoCredential(html);
    }

    [Fact]
    public async Task Owner_RemovesTheConnection_KeepsTheOthers_AndTheListConfirmsIt()
    {
        var removed = Seed(_host.AlphaId, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        var kept = Seed(_host.AlphaId, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        var otherTenant = Seed(_host.BetaId, FoodPlatform.TrendyolYemek, "TY-BETA-1");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);

        var response = await PostRemoveAsync(owner, removed);

        AssertRedirectsTo(response, "/platform-connections");
        Assert.Equal([kept], ConnectionIds(_host.AlphaId));
        Assert.Equal([otherTenant], ConnectionIds(_host.BetaId));
        var page = await HtmlAsync(owner, "/platform-connections");
        Assert.Contains(JavaScriptEncoder.Default.Encode(Resource("en-US", "PlatformConnections.Removed")), page);
        Assert.DoesNotContain($"/platform-connections/{removed}/", page);
        Assert.DoesNotContain("TY-STORE-1", page);
        AssertNoCredential(page);
        foreach (var sentinel in new[] { StoredKey, StoredSecret })
            Assert.DoesNotContain(_host.Logs.Entries, e => e.Contains(sentinel[..20], StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedSubmission_ChangesNothing_AndSaysTheConnectionNoLongerExists()
    {
        var removed = Seed(_host.AlphaId, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        var kept = Seed(_host.AlphaId, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);
        var token = await owner.AntiforgeryTokenAsync("/platform-connections");

        AssertRedirectsTo(await PostRemoveAsync(owner, removed, token), "/platform-connections");
        var repeated = await PostRemoveAsync(owner, removed, token);

        AssertRedirectsTo(repeated, "/platform-connections");
        Assert.Equal([kept], ConnectionIds(_host.AlphaId));
        var page = await HtmlAsync(owner, "/platform-connections");
        Assert.Contains(JavaScriptEncoder.Default.Encode(Resource("en-US", "PlatformConnections.AlreadyRemoved")), page);
    }

    [Fact]
    public async Task WithoutAnAntiforgeryToken_NothingIsRemoved()
    {
        var id = Seed(_host.AlphaId, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);

        var response = await owner.PostFormAsync($"/platform-connections/{id}/remove", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([id], ConnectionIds(_host.AlphaId));
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task OtherRoles_CannotRemove(UserRole role)
    {
        var id = Seed(_host.AlphaId, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        _host.SeedUser(_host.AlphaId, "staff@alpha.test", role);
        using var staff = _host.Client(TenantSessionWebHost.AlphaHost);
        await staff.LoginAsync("staff@alpha.test");
        // A valid token for this session, so only the role decides.
        var token = await staff.AntiforgeryTokenAsync("/orders/live-display");

        AssertRedirectsTo(await PostRemoveAsync(staff, id, token), "/auth/access-denied");

        Assert.Equal([id], ConnectionIds(_host.AlphaId));
    }

    [Fact]
    public async Task AnotherTenantsOwner_CannotRemoveTheConnection_AndLearnsNothingAboutIt()
    {
        var id = Seed(_host.AlphaId, FoodPlatform.TrendyolYemek, "TY-ALPHA-1");
        using var beta = await OwnerAsync(_host.BetaId, TenantSessionWebHost.BetaHost, "owner@beta.test");

        var foreign = await PostRemoveAsync(beta, id);
        var foreignPage = await HtmlAsync(beta, "/platform-connections");
        var unknown = await PostRemoveAsync(beta, Guid.NewGuid());
        var unknownPage = await HtmlAsync(beta, "/platform-connections");

        // The same answer as for an id that exists nowhere.
        AssertRedirectsTo(foreign, "/platform-connections");
        AssertRedirectsTo(unknown, "/platform-connections");
        var alreadyRemoved = JavaScriptEncoder.Default.Encode(Resource("en-US", "PlatformConnections.AlreadyRemoved"));
        Assert.Contains(alreadyRemoved, foreignPage);
        Assert.Contains(alreadyRemoved, unknownPage);
        Assert.DoesNotContain("TY-ALPHA-1", foreignPage);
        Assert.Equal([id], ConnectionIds(_host.AlphaId));

        // An alpha Owner's session replayed to the beta host is not a beta session.
        using var alpha = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);
        var alphaToken = await alpha.AntiforgeryTokenAsync("/platform-connections");
        using var replay = alpha.ReplayTo(_host.BaseAddress, TenantSessionWebHost.BetaHost);
        AssertRedirectsTo(await PostRemoveAsync(replay, id, alphaToken), "/auth/login");
        Assert.Equal([id], ConnectionIds(_host.AlphaId));
    }

    private async Task<TenantSessionClient> OwnerAsync(Guid tenantId, string hostName, string email = "owner@alpha.test")
    {
        _host.SeedUser(tenantId, email, UserRole.Owner);
        var client = _host.Client(hostName);
        client.Cookies[CookieRequestCultureProvider.DefaultCookieName] =
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("en-US")));
        await client.LoginAsync(email);
        return client;
    }

    private static async Task<HttpResponseMessage> PostRemoveAsync(TenantSessionClient client, Guid id, string? token = null)
    {
        token ??= await client.AntiforgeryTokenAsync("/platform-connections");
        return await client.PostFormAsync($"/platform-connections/{id}/remove", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        });
    }

    private Guid Seed(Guid tenantId, FoodPlatform platform, string storeId, Action<PlatformConnection>? change = null)
    {
        var connection = new PlatformConnection
        {
            Platform = platform,
            StoreId = storeId,
            EncryptedApiKey = StoredKey,
            EncryptedApiSecret = StoredSecret,
            EncryptionKeyVersion = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddDays(-3),
            UpdatedAt = DateTime.UtcNow.AddDays(-3)
        };
        change?.Invoke(connection);
        _host.Tenants.Seed(tenantId, db => db.PlatformConnections.Add(connection));
        return connection.Id;
    }

    private List<Guid> ConnectionIds(Guid tenantId)
    {
        using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlite(_host.Tenants.ConnectionStringFor(tenantId)).Options);
        return db.PlatformConnections.Select(p => p.Id).ToList();
    }

    private static string Between(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} is not on the page.");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"{end} does not follow {start}.");
        return html[from..to];
    }

    private static void AssertNoCredential(string body)
    {
        foreach (var credential in new[] { StoredKey, StoredSecret })
        {
            foreach (var form in new[] { credential, credential[..16], credential[^16..] })
                Assert.False(body.Contains(form, StringComparison.Ordinal), $"The response contains a credential ({form.Length} characters of it).");
        }
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
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";
}
