using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// WAS-131 through the real tenant application (<see cref="TenantSessionWebHost"/>: real tenant resolution for two tenant
/// hosts, the real cookie scheme, role policies and antiforgery, the real controller, Razor views and Infrastructure
/// services over one SQLite database per tenant). Platform credentials are masked inputs that never carry a value: a
/// stored credential is never sent to the browser, and a credential typed into a form that fails is never sent back,
/// whichever path the failure takes (model binding, data annotations, the create validator, the service). A blank edit
/// field keeps the stored value, a new value replaces only its own credential and only when the whole edit succeeds, and
/// only an Owner of the tenant that owns the connection reaches any of it. Every credential here is a synthetic sentinel;
/// the host's secret manager is a passthrough fake, so the stored "ciphertext" contains the sentinel itself and any
/// disclosure of the stored value shows up as the sentinel.
/// </summary>
public sealed class PlatformConnectionCredentialWebTests : IAsyncLifetime
{
    private const string StoredKey = "stored-apikey-sentinel-4c1e9a7d";
    private const string StoredSecret = "stored-apisecret-sentinel-8b2f6e3a";
    private const string TypedKey = "typed-apikey-sentinel-51d7c0b9";
    private const string TypedSecret = "typed-apisecret-sentinel-e93a2f64";

    private static readonly Regex InputTag = new("<input\\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ButtonTag = new("<button\\b[^>]*>", RegexOptions.Compiled);

    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantSessionWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Create_RendersMaskedEmptyCredentialInputs_WithAnAccessibleRevealControlPerField()
    {
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);

        var html = await HtmlAsync(owner, "/platform-connections/create");

        foreach (var (field, showKey) in new[] { ("ApiKey", "PlatformConnections.ShowApiKey"), ("ApiSecret", "PlatformConnections.ShowApiSecret") })
        {
            var input = Input(html, field);
            Assert.Contains("type=\"password\"", input);
            Assert.DoesNotContain(" value=", input);
            Assert.Contains("autocomplete=\"off\"", input);
            Assert.Contains("spellcheck=\"false\"", input);

            var toggle = Toggle(html, field);
            Assert.Contains("type=\"button\"", toggle);
            Assert.Contains("aria-pressed=\"false\"", toggle);
            Assert.Contains($"aria-label=\"{WebUtility.HtmlEncode(Resource("en-US", showKey))}\"", toggle);
        }

        // Nothing typed yet, so nothing to enter again.
        Assert.DoesNotContain(Resource("en-US", "PlatformConnections.CredentialsReenter"), html);
    }

    public static TheoryData<string, string> InvalidCreates() => new()
    {
        // Model binding and data annotations: the required StoreId is missing.
        { "missing store id", "StoreId=" },
        // Data annotations: an invalid executor e-mail.
        { "invalid executor e-mail", "ExecutorEmail=not-an-email" },
        // The create validator: an API key over its 500 characters, built around the sentinel.
        { "API key too long", "ApiKey=" + TypedKey + new string('k', 500) },
        // The service: the platform is already connected.
        { "platform already connected", "Platform=TrendyolYemek" }
    };

    [Theory]
    [MemberData(nameof(InvalidCreates))]
    public async Task Create_ThatFails_NeverSendsTheTypedCredentialsBack_AndKeepsTheOtherFields(string reason, string change)
    {
        Seed(_host.AlphaId, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);
        var form = CreateForm("YS-STORE-77", TypedKey, TypedSecret);
        Apply(form, change);
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync("/platform-connections/create");

        var response = await owner.PostFormAsync("/platform-connections/create", form);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{reason}: {response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        AssertNoCredential(html, TypedKey, TypedSecret);
        Assert.DoesNotContain(" value=", Input(html, "ApiKey"));
        Assert.DoesNotContain(" value=", Input(html, "ApiSecret"));
        Assert.Contains(Resource("en-US", "PlatformConnections.CredentialsReenter"), html);
        // The non-secret fields come back as typed.
        if (!change.StartsWith("StoreId=", StringComparison.Ordinal))
            Assert.Contains("value=\"YS-STORE-77\"", Input(html, "StoreId"));
        Assert.Contains("value=\"5501\"", Input(html, "SupplierId"));
        Assert.Equal(1, ConnectionCount(_host.AlphaId));
    }

    [Fact]
    public async Task Create_StoresBothCredentialsExactlyAsTyped()
    {
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);
        // Characters a careless encoder or trimmer would change.
        const string key = "typed key+/=<&>\"'-ab12";
        const string secret = "typed-secret_%20;ö-cd34";
        var form = CreateForm("YS-STORE-77", key, secret);
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync("/platform-connections/create");

        AssertRedirectsTo(await owner.PostFormAsync("/platform-connections/create", form), "/platform-connections");

        var stored = Read(_host.AlphaId, FoodPlatform.Yemeksepeti);
        Assert.Equal(key, stored.EncryptedApiKey);
        Assert.Equal(secret, stored.EncryptedApiSecret);
        Assert.Equal("YS-STORE-77", stored.StoreId);
    }

    [Fact]
    public async Task Edit_NeverRendersTheStoredCredentials_AndSaysOnlyThatTheyAreSaved()
    {
        var id = Seed(_host.AlphaId, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);

        var html = await HtmlAsync(owner, $"/platform-connections/{id}/edit");

        AssertNoCredential(html, StoredKey, StoredSecret);
        foreach (var field in new[] { "ApiKey", "ApiSecret" })
        {
            var input = Input(html, field);
            Assert.Contains("type=\"password\"", input);
            Assert.DoesNotContain(" value=", input);
            Assert.Contains("autocomplete=\"off\"", input);
            Assert.Contains("aria-pressed=\"false\"", Toggle(html, field));
        }

        Assert.Equal(2, Occurrences(html, Resource("en-US", "PlatformConnections.CredentialSaved")));
        // One sentence after the other, as text.
        Assert.Equal(2, Occurrences(html, Resource("en-US", "PlatformConnections.CredentialSaved") + " " + Resource("en-US", "PlatformConnections.ApiLeaveEmptyHelp")));
        Assert.DoesNotContain(Resource("en-US", "PlatformConnections.CredentialMissing"), html);
        Assert.DoesNotContain(Resource("en-US", "PlatformConnections.CredentialsReenter"), html);
    }

    [Fact]
    public async Task Edit_WithBlankCredentials_ChangesOnlyTheOtherFields()
    {
        var id = Seed(_host.AlphaId, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);
        var before = Read(_host.AlphaId, FoodPlatform.TrendyolYemek);

        // One credential posted empty, the other not posted at all.
        var form = EditForm(FoodPlatform.TrendyolYemek, "TY-STORE-2", apiKey: "", apiSecret: null);
        form["IsActive"] = "false";
        form["SupplierId"] = "7700";
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync($"/platform-connections/{id}/edit");

        AssertRedirectsTo(await owner.PostFormAsync($"/platform-connections/{id}/edit", form), "/platform-connections");

        var after = Read(_host.AlphaId, FoodPlatform.TrendyolYemek);
        Assert.Equal("TY-STORE-2", after.StoreId);
        Assert.Equal("7700", after.SupplierId);
        Assert.False(after.IsActive);
        Assert.Equal(StoredKey, after.EncryptedApiKey);
        Assert.Equal(StoredSecret, after.EncryptedApiSecret);
        Assert.Equal(before.EncryptionKeyVersion, after.EncryptionKeyVersion);
    }

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("ApiSecret")]
    public async Task Edit_WithOneNewCredential_ReplacesOnlyThatCredential(string field)
    {
        var id = Seed(_host.AlphaId, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);
        var form = EditForm(FoodPlatform.Yemeksepeti, "YS-STORE-1",
            apiKey: field == "ApiKey" ? TypedKey : "", apiSecret: field == "ApiSecret" ? TypedSecret : "");
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync($"/platform-connections/{id}/edit");

        AssertRedirectsTo(await owner.PostFormAsync($"/platform-connections/{id}/edit", form), "/platform-connections");

        var after = Read(_host.AlphaId, FoodPlatform.Yemeksepeti);
        Assert.Equal(field == "ApiKey" ? TypedKey : StoredKey, after.EncryptedApiKey);
        Assert.Equal(field == "ApiSecret" ? TypedSecret : StoredSecret, after.EncryptedApiSecret);
        // And the next edit page still shows neither.
        AssertNoCredential(await HtmlAsync(owner, $"/platform-connections/{id}/edit"), TypedKey, TypedSecret, StoredKey, StoredSecret);
    }

    public static TheoryData<string, string> InvalidEdits() => new()
    {
        // Data annotations: the required StoreId is missing.
        { "missing store id", "StoreId=" },
        // Data annotations: an invalid executor e-mail.
        { "invalid executor e-mail", "ExecutorEmail=not-an-email" },
        // Data annotations: a replacement over the 500 characters the create validator allows.
        { "API secret too long", "ApiSecret=" + TypedSecret + new string('s', 500) },
        // The service: the platform of a connection cannot change.
        { "different platform", "Platform=GetirYemek" }
    };

    [Theory]
    [MemberData(nameof(InvalidEdits))]
    public async Task Edit_ThatFails_KeepsTheStoredCredentials_AndNeverSendsTheTypedOnesBack(string reason, string change)
    {
        var id = Seed(_host.AlphaId, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);
        var before = Read(_host.AlphaId, FoodPlatform.TrendyolYemek);
        var form = EditForm(FoodPlatform.TrendyolYemek, "TY-STORE-2", TypedKey, TypedSecret);
        form["SupplierId"] = "7700";
        Apply(form, change);
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync($"/platform-connections/{id}/edit");

        var response = await owner.PostFormAsync($"/platform-connections/{id}/edit", form);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{reason}: {response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        AssertNoCredential(html, TypedKey, TypedSecret, StoredKey, StoredSecret);
        Assert.DoesNotContain(" value=", Input(html, "ApiKey"));
        Assert.DoesNotContain(" value=", Input(html, "ApiSecret"));
        Assert.Contains(Resource("en-US", "PlatformConnections.CredentialsReenter"), html);
        Assert.Equal(2, Occurrences(html, Resource("en-US", "PlatformConnections.CredentialSaved")));
        Assert.Contains("value=\"7700\"", Input(html, "SupplierId"));

        var after = Read(_host.AlphaId, FoodPlatform.TrendyolYemek);
        Assert.Equal(StoredKey, after.EncryptedApiKey);
        Assert.Equal(StoredSecret, after.EncryptedApiSecret);
        Assert.Equal(before.StoreId, after.StoreId);
        Assert.Equal(before.SupplierId, after.SupplierId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Edit_OfAConnectionWithoutAStoredSecret_RequiresOne_AndKeepsTheRowUnchanged()
    {
        // No create path leaves a credential empty; this row stands in for one written before validation existed.
        var id = Seed(_host.AlphaId, FoodPlatform.Yemeksepeti, "YS-STORE-1", storedSecret: "");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);

        var page = await HtmlAsync(owner, $"/platform-connections/{id}/edit");
        Assert.Equal(1, Occurrences(page, Resource("en-US", "PlatformConnections.CredentialSaved")));
        Assert.Equal(1, Occurrences(page, Resource("en-US", "PlatformConnections.CredentialMissing")));

        var form = EditForm(FoodPlatform.Yemeksepeti, "YS-STORE-2", TypedKey, apiSecret: "");
        form["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync($"/platform-connections/{id}/edit");
        var response = await owner.PostFormAsync($"/platform-connections/{id}/edit", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(Resource("en-US", "Validation.ApiSecretRequired"), html);
        AssertNoCredential(html, TypedKey, StoredKey);
        var after = Read(_host.AlphaId, FoodPlatform.Yemeksepeti);
        Assert.Equal(StoredKey, after.EncryptedApiKey);
        Assert.Equal("", after.EncryptedApiSecret);
        Assert.Equal("YS-STORE-1", after.StoreId);
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task OtherRoles_CannotOpenOrSubmitEitherForm(UserRole role)
    {
        var id = Seed(_host.AlphaId, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        _host.SeedUser(_host.AlphaId, "staff@alpha.test", role);
        using var staff = _host.Client(TenantSessionWebHost.AlphaHost);
        await staff.LoginAsync("staff@alpha.test");
        // A valid token for this session, so only the role decides.
        var token = await staff.AntiforgeryTokenAsync("/orders/live-display");

        AssertRedirectsTo(await staff.GetAsync("/platform-connections/create"), "/auth/access-denied");
        AssertRedirectsTo(await staff.GetAsync($"/platform-connections/{id}/edit"), "/auth/access-denied");

        var create = CreateForm("YS-STORE-77", TypedKey, TypedSecret);
        create["Platform"] = nameof(FoodPlatform.GetirYemek);
        create["__RequestVerificationToken"] = token;
        AssertRedirectsTo(await staff.PostFormAsync("/platform-connections/create", create), "/auth/access-denied");

        var edit = EditForm(FoodPlatform.Yemeksepeti, "YS-STORE-2", TypedKey, TypedSecret);
        edit["__RequestVerificationToken"] = token;
        AssertRedirectsTo(await staff.PostFormAsync($"/platform-connections/{id}/edit", edit), "/auth/access-denied");

        Assert.Equal(1, ConnectionCount(_host.AlphaId));
        var after = Read(_host.AlphaId, FoodPlatform.Yemeksepeti);
        Assert.Equal(("YS-STORE-1", StoredKey, StoredSecret), (after.StoreId, after.EncryptedApiKey, after.EncryptedApiSecret));
    }

    [Fact]
    public async Task AnOwner_CannotSubmitEitherFormWithoutAnAntiforgeryToken()
    {
        var id = Seed(_host.AlphaId, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        using var owner = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);

        var create = CreateForm("GY-STORE-77", TypedKey, TypedSecret);
        create["Platform"] = nameof(FoodPlatform.GetirYemek);
        var created = await owner.PostFormAsync("/platform-connections/create", create);
        var edited = await owner.PostFormAsync($"/platform-connections/{id}/edit", EditForm(FoodPlatform.Yemeksepeti, "YS-STORE-2", TypedKey, TypedSecret));

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, edited.StatusCode);
        AssertNoCredential(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TypedKey, TypedSecret);
        AssertNoCredential(await edited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TypedKey, TypedSecret);
        Assert.Equal(1, ConnectionCount(_host.AlphaId));
        var after = Read(_host.AlphaId, FoodPlatform.Yemeksepeti);
        Assert.Equal(("YS-STORE-1", StoredKey, StoredSecret), (after.StoreId, after.EncryptedApiKey, after.EncryptedApiSecret));
    }

    [Fact]
    public async Task AnotherTenantsOwner_CannotOpenOrChangeTheConnection()
    {
        var id = Seed(_host.AlphaId, FoodPlatform.Yemeksepeti, "YS-ALPHA-1");
        using var beta = await OwnerAsync(_host.BetaId, TenantSessionWebHost.BetaHost, "owner@beta.test");
        var token = await beta.AntiforgeryTokenAsync("/platform-connections/create");

        var page = await beta.GetAsync($"/platform-connections/{id}/edit");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);

        var form = EditForm(FoodPlatform.Yemeksepeti, "YS-BETA-9", TypedKey, TypedSecret);
        form["__RequestVerificationToken"] = token;
        var posted = await beta.PostFormAsync($"/platform-connections/{id}/edit", form);
        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);
        var body = await posted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        AssertNoCredential(body, TypedKey, TypedSecret, StoredKey, StoredSecret);
        Assert.DoesNotContain("YS-ALPHA-1", body);

        // An alpha Owner's session replayed to the beta host is not a beta session.
        using var alpha = await OwnerAsync(_host.AlphaId, TenantSessionWebHost.AlphaHost);
        var alphaToken = await alpha.AntiforgeryTokenAsync($"/platform-connections/{id}/edit");
        using var replay = alpha.ReplayTo(_host.BaseAddress, TenantSessionWebHost.BetaHost);
        form["__RequestVerificationToken"] = alphaToken;
        AssertRedirectsTo(await replay.PostFormAsync($"/platform-connections/{id}/edit", form), "/auth/login");

        var after = Read(_host.AlphaId, FoodPlatform.Yemeksepeti);
        Assert.Equal(("YS-ALPHA-1", StoredKey, StoredSecret), (after.StoreId, after.EncryptedApiKey, after.EncryptedApiSecret));
        Assert.Equal(0, ConnectionCount(_host.BetaId));
    }

    [Fact]
    public async Task NoLogEntry_CarriesACredential_AtTraceLevel()
    {
        await using var host = await TenantSessionWebHost.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace"
        });
        var id = SeedIn(host, host.AlphaId, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        host.SeedUser(host.AlphaId, "owner@alpha.test", UserRole.Owner);
        using var owner = host.Client(TenantSessionWebHost.AlphaHost);
        await owner.LoginAsync("owner@alpha.test");

        // A failed create, a failed edit, a successful replacement and a successful create.
        var badCreate = CreateForm("", TypedKey, TypedSecret);
        badCreate["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync("/platform-connections/create");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostFormAsync("/platform-connections/create", badCreate)).StatusCode);
        var badEdit = EditForm(FoodPlatform.Yemeksepeti, "", TypedKey, TypedSecret);
        badEdit["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync($"/platform-connections/{id}/edit");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostFormAsync($"/platform-connections/{id}/edit", badEdit)).StatusCode);
        var goodEdit = EditForm(FoodPlatform.Yemeksepeti, "YS-STORE-2", TypedKey, TypedSecret);
        goodEdit["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync($"/platform-connections/{id}/edit");
        AssertRedirectsTo(await owner.PostFormAsync($"/platform-connections/{id}/edit", goodEdit), "/platform-connections");
        var goodCreate = CreateForm("GY-STORE-3", TypedKey + "-2", TypedSecret + "-2");
        goodCreate["Platform"] = nameof(FoodPlatform.GetirYemek);
        goodCreate["__RequestVerificationToken"] = await owner.AntiforgeryTokenAsync("/platform-connections/create");
        AssertRedirectsTo(await owner.PostFormAsync("/platform-connections/create", goodCreate), "/platform-connections");

        var entries = host.Logs.Entries;
        Assert.Contains(entries, e => e.Contains("/platform-connections", StringComparison.Ordinal));
        foreach (var sentinel in new[] { TypedKey, TypedSecret, StoredKey, StoredSecret })
            Assert.DoesNotContain(entries, e => e.Contains(sentinel[..20], StringComparison.Ordinal));
    }

    private async Task<TenantSessionClient> OwnerAsync(Guid tenantId, string hostName, string email = "owner@alpha.test")
    {
        _host.SeedUser(tenantId, email, UserRole.Owner);
        var client = _host.Client(hostName);
        await client.LoginAsync(email);
        return client;
    }

    private Guid Seed(Guid tenantId, FoodPlatform platform, string storeId, string storedSecret = StoredSecret) =>
        SeedIn(_host, tenantId, platform, storeId, storedSecret);

    private static Guid SeedIn(TenantSessionWebHost host, Guid tenantId, FoodPlatform platform, string storeId, string storedSecret = StoredSecret)
    {
        var connection = new PlatformConnection
        {
            Platform = platform,
            StoreId = storeId,
            SupplierId = platform == FoodPlatform.TrendyolYemek ? "5501" : null,
            EncryptedApiKey = StoredKey,
            EncryptedApiSecret = storedSecret,
            EncryptionKeyVersion = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddDays(-3),
            UpdatedAt = DateTime.UtcNow.AddDays(-3)
        };
        host.Tenants.Seed(tenantId, db => db.PlatformConnections.Add(connection));
        return connection.Id;
    }

    private PlatformConnection Read(Guid tenantId, FoodPlatform platform)
    {
        using var db = TenantDb(tenantId);
        return db.PlatformConnections.AsNoTracking().Single(p => p.Platform == platform);
    }

    private int ConnectionCount(Guid tenantId)
    {
        using var db = TenantDb(tenantId);
        return db.PlatformConnections.Count();
    }

    private TenantDbContext TenantDb(Guid tenantId) =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(_host.Tenants.ConnectionStringFor(tenantId)).Options);

    private static Dictionary<string, string> CreateForm(string storeId, string apiKey, string apiSecret) => new()
    {
        ["Platform"] = nameof(FoodPlatform.Yemeksepeti),
        ["StoreId"] = storeId,
        ["SupplierId"] = "5501",
        ["ExecutorEmail"] = "ops@alpha.test",
        ["ApiKey"] = apiKey,
        ["ApiSecret"] = apiSecret,
        ["IsActive"] = "true"
    };

    private static Dictionary<string, string> EditForm(FoodPlatform platform, string storeId, string? apiKey, string? apiSecret)
    {
        var form = new Dictionary<string, string>
        {
            ["Platform"] = platform.ToString(),
            ["StoreId"] = storeId,
            ["IsActive"] = "true"
        };
        if (apiKey is not null)
            form["ApiKey"] = apiKey;
        if (apiSecret is not null)
            form["ApiSecret"] = apiSecret;
        return form;
    }

    private static void Apply(Dictionary<string, string> form, string change)
    {
        var separator = change.IndexOf('=');
        form[change[..separator]] = change[(separator + 1)..];
    }

    /// <summary>
    /// Neither the value nor a distinctive part of it, raw or in any encoding a page could carry it in (HTML attribute or
    /// text, JavaScript string, URL).
    /// </summary>
    private static void AssertNoCredential(string body, params string[] credentials)
    {
        foreach (var credential in credentials)
        {
            foreach (var form in new[]
                     {
                         credential, credential[..16], credential[^16..], WebUtility.HtmlEncode(credential),
                         JavaScriptEncoder.Default.Encode(credential), Uri.EscapeDataString(credential)
                     })
            {
                Assert.False(body.Contains(form, StringComparison.Ordinal), $"The response contains a credential ({form.Length} characters of it).");
            }
        }
    }

    private static string Input(string html, string name)
    {
        var inputs = InputTag.Matches(html).Select(m => m.Value).Where(tag => tag.Contains($"name=\"{name}\"", StringComparison.Ordinal)).ToList();
        Assert.True(inputs.Count == 1, $"Expected one {name} input, found {inputs.Count}.");
        return inputs[0];
    }

    private static string Toggle(string html, string field)
    {
        var toggles = ButtonTag.Matches(html).Select(m => m.Value)
            .Where(tag => tag.Contains("data-credential-toggle", StringComparison.Ordinal) && tag.Contains($"aria-controls=\"{field}\"", StringComparison.Ordinal))
            .ToList();
        Assert.True(toggles.Count == 1, $"Expected one reveal control for {field}, found {toggles.Count}.");
        return toggles[0];
    }

    private static int Occurrences(string html, string text) => Regex.Matches(html, Regex.Escape(WebUtility.HtmlEncode(text))).Count;

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
