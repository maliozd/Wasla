using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Plans;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Signup;

namespace Wasla.UnitTests.Routing;

/// <summary>
/// WAS-66: the host contract end to end with the production domains (<c>wasla.tr</c>, <c>app.wasla.tr</c>,
/// <c>{slug}.app.wasla.tr</c>), served by the real tenant-resolution middleware, controllers, views and resources in the
/// Production environment (<see cref="SignupWebHost"/>) over an in-memory CentralDb. Every request names its host in the
/// Host header; no DNS or hosts file is involved, and all data is fake.
/// </summary>
public sealed partial class WaslaHostRoutingTests : IClassFixture<WaslaHostRoutingTests.Host>
{
    private const string Marketing = "wasla.tr";
    private const string Application = "app.wasla.tr";

    private readonly Host _host;

    public WaslaHostRoutingTests(Host host)
    {
        _host = host;
    }

    // --- Marketing host --------------------------------------------------------------------------------

    [Theory]
    [InlineData("wasla.tr")]
    [InlineData("WWW.Wasla.TR")]
    public async Task TheMarketingHost_RendersTheHomepage_WithItsApexCanonicalAndTheApplicationSignIn(string host)
    {
        using var browser = _host.Web.NewBrowser();

        var page = await browser.GetAsync("/", host);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("<link rel=\"canonical\" href=\"http://wasla.tr/\" />", page.Body);
        var links = Links(page.Body);
        Assert.Equal(2, links.Count(l => l == "http://app.wasla.tr/auth/login"));
        Assert.DoesNotContain(links, l => l.Contains("/auth/login", StringComparison.Ordinal) && !l.StartsWith("http://app.wasla.tr/", StringComparison.Ordinal));
        Assert.Contains("/signup", links);
        Assert.Contains("/pos", links);
    }

    [Fact]
    public async Task TheMarketingHost_KeepsThePosPageAndSignup()
    {
        using var browser = _host.Web.NewBrowser();

        var pos = await browser.GetAsync("/pos", Marketing);
        var signup = await browser.GetAsync("/signup", Marketing);

        Assert.Equal(HttpStatusCode.OK, pos.Status);
        Assert.Contains("<link rel=\"canonical\" href=\"http://wasla.tr/pos\" />", pos.Body);
        Assert.Equal(HttpStatusCode.OK, signup.Status);
        Assert.Contains("__RequestVerificationToken", signup.Body);
        // The tenant address the visitor is choosing is under the application domain.
        Assert.Contains(">.app.wasla.tr</span>", signup.Body);
        Assert.Contains("id=\"signupBaseDomain\" value=\"app.wasla.tr\"", signup.Body);
    }

    [Fact]
    public async Task SignInOnTheMarketingHost_AsksForTheTenantAddress()
    {
        using var browser = _host.Web.NewBrowser();

        var response = await browser.GetAsync("/auth/login", Marketing);

        Assert.Equal(HttpStatusCode.Redirect, response.Status);
        Assert.Equal("/tenant-address-required?returnUrl=%2Fauth%2Flogin", response.Location);
    }

    // --- Application host ------------------------------------------------------------------------------

    [Theory]
    [InlineData("/auth/login", "/tenant-address-required?returnUrl=%2Fauth%2Flogin")]
    [InlineData("/", "/tenant-address-required")]
    [InlineData("/orders", "/tenant-address-required?returnUrl=%2Forders")]
    public async Task TheApplicationHost_IsNotATenant_AndLeadsToTheTenantAddressPage(string path, string location)
    {
        using var browser = _host.Web.NewBrowser();

        var response = await browser.GetAsync(path, Application);

        Assert.Equal(HttpStatusCode.Redirect, response.Status);
        Assert.Equal(location, response.Location);
    }

    [Fact]
    public async Task TheTenantAddressPage_ShowsAnExampleUnderTheApplicationDomain_AndSignupOnTheMarketingHost()
    {
        using var browser = _host.Web.NewBrowser();

        var page = await browser.GetAsync("/tenant-address-required", "App.Wasla.TR");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("bahce-tadi.app.wasla.tr", page.Body);
        Assert.Contains("href=\"http://wasla.tr/signup\"", page.Body);
    }

    [Fact]
    public async Task ForwardedHostHeaders_DoNotChooseATenant()
    {
        await SeedTenantAsync("forwarded", isActive: true);
        using var browser = _host.Web.NewBrowser();

        var response = await browser.SendWithHeadersAsync("/auth/login", Application, ("X-Forwarded-Host", "forwarded.app.wasla.tr"));

        Assert.Equal(HttpStatusCode.Redirect, response.Status);
        Assert.Equal("/tenant-address-required?returnUrl=%2Fauth%2Flogin", response.Location);
    }

    // --- Tenant hosts ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("nobody.app.wasla.tr")]
    [InlineData("NOBODY.App.Wasla.TR")]
    public async Task AnUnknownTenantAddress_GetsTheTenantNotFoundPage(string host)
    {
        using var browser = _host.Web.NewBrowser();

        var redirect = await browser.GetAsync("/auth/login", host);
        Assert.Equal(HttpStatusCode.Redirect, redirect.Status);
        Assert.Equal($"/tenant-not-found?host={Uri.EscapeDataString(host)}", redirect.Location);

        var page = await browser.GetAsync(redirect.Location!, host);
        Assert.Equal(HttpStatusCode.NotFound, page.Status);
        Assert.Contains("href=\"http://wasla.tr/signup\"", page.Body);
    }

    [Theory]
    // Reserved, extra-label, invalid and look-alike hosts, the former {slug}.wasla.tr shape and arbitrary hosts.
    [InlineData("admin.app.wasla.tr")]
    [InlineData("www.app.wasla.tr")]
    [InlineData("api.app.wasla.tr")]
    [InlineData("a.known.app.wasla.tr")]
    [InlineData("-known.app.wasla.tr")]
    [InlineData("known.app.wasla.tr.evil.example")]
    [InlineData("app.wasla.tr.evil.example")]
    [InlineData("known.wasla.tr")]
    [InlineData("evil.example")]
    public async Task EveryOtherHost_FailsClosed_LikeAnUnknownTenantAddress(string host)
    {
        await SeedTenantAsync("known", isActive: true);
        using var browser = _host.Web.NewBrowser();

        var redirect = await browser.GetAsync("/auth/login", host);
        Assert.Equal(HttpStatusCode.Redirect, redirect.Status);
        Assert.Equal($"/tenant-not-found?host={Uri.EscapeDataString(host)}", redirect.Location);

        var page = await browser.GetAsync(redirect.Location!, host);
        Assert.Equal(HttpStatusCode.NotFound, page.Status);
        Assert.DoesNotContain("href=\"http://known.app.wasla.tr", page.Body);
    }

    [Fact]
    public async Task AnInactiveTenant_IsAnUnknownTenantAddress()
    {
        await SeedTenantAsync("closed", isActive: false);
        using var browser = _host.Web.NewBrowser();

        var redirect = await browser.GetAsync("/auth/login", "closed.app.wasla.tr");

        Assert.Equal("/tenant-not-found?host=closed.app.wasla.tr", redirect.Location);
    }

    // --- Signup and provisioning -----------------------------------------------------------------------

    [Fact]
    public async Task Signup_BuildsTheNestedTenantAddress_AndTheTenantHostLeadsToItsStatusUntilProvisioned()
    {
        using var browser = _host.Web.NewBrowser();
        var token = await browser.GetAntiforgeryTokenAsync("/signup", Marketing);
        var slug = $"pide{SentinelApplicant.NewToken()}".ToLowerInvariant();

        var submitted = await browser.PostFormAsync("/signup", SignupForm(token, slug), Marketing);

        Assert.Equal(HttpStatusCode.Redirect, submitted.Status);
        Assert.StartsWith("/signup/pending/", submitted.Location);
        var registrationId = Guid.Parse(submitted.Location!["/signup/pending/".Length..]);
        await using (var db = NewDb())
        {
            var stored = await db.PendingRegistrations.AsNoTracking().SingleAsync(r => r.Id == registrationId, TestContext.Current.CancellationToken);
            Assert.Equal($"{slug}.app.wasla.tr", stored.PrimaryDomain);
        }

        var pending = await browser.GetAsync(submitted.Location!, Marketing);
        Assert.Equal(HttpStatusCode.OK, pending.Status);
        Assert.Contains($"href=\"http://{slug}.app.wasla.tr\"", pending.Body);
        Assert.Contains("href=\"http://wasla.tr/\"", pending.Body);

        // The tenant host of a registration that is not provisioned shows its status page.
        var tenantHost = await browser.GetAsync("/", $"{slug}.app.wasla.tr");
        Assert.Equal(HttpStatusCode.Redirect, tenantHost.Status);
        Assert.Equal(submitted.Location, tenantHost.Location);

        // Once provisioned, the status page offers the panel's sign-in on the nested tenant host.
        await using (var db = NewDb())
        {
            var stored = await db.PendingRegistrations.SingleAsync(r => r.Id == registrationId, TestContext.Current.CancellationToken);
            stored.Status = PendingRegistrationStatus.Provisioned;
            stored.PaymentSucceededAtUtc = DateTime.UtcNow;
            stored.ProvisionedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var provisioned = await browser.GetAsync(submitted.Location!, Marketing);
        Assert.Contains($"href=\"http://{slug}.app.wasla.tr/auth/login\"", provisioned.Body);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:8443")]
    [InlineData("app.wasla.tr")]
    public async Task StatusPageUrls_ComeFromTheRegistrationAndConfiguration_NeverFromTheHostHeader(string hostile)
    {
        using var browser = _host.Web.NewBrowser();
        var token = await browser.GetAntiforgeryTokenAsync("/signup", Marketing);
        var slug = $"kebap{SentinelApplicant.NewToken()}".ToLowerInvariant();
        var submitted = await browser.PostFormAsync("/signup", SignupForm(token, slug), Marketing);
        Assert.Equal(HttpStatusCode.Redirect, submitted.Status);

        var page = await browser.SendWithHeadersAsync(submitted.Location!, hostile, ("X-Forwarded-Host", "attacker.example"));

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains($"href=\"http://{slug}.app.wasla.tr\"", page.Body);
        Assert.Contains("href=\"http://wasla.tr/\"", page.Body);
        Assert.DoesNotContain("evil.example", page.Body);
        Assert.DoesNotContain("attacker.example", page.Body);
        Assert.DoesNotContain(":8443", page.Body);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("app")]
    [InlineData("www")]
    [InlineData("api")]
    [InlineData("docs")]
    public async Task Signup_RefusesReservedNames_AsUnavailableAddresses(string slug)
    {
        using var browser = _host.Web.NewBrowser();
        var token = await browser.GetAntiforgeryTokenAsync("/signup", Marketing);

        var refused = await browser.PostFormAsync("/signup", SignupForm(token, slug), Marketing);

        Assert.Equal(HttpStatusCode.OK, refused.Status);
        Assert.Null(refused.Location);
        await using var db = NewDb();
        Assert.False(await db.PendingRegistrations.AnyAsync(r => r.Slug == slug, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("admin.app.wasla.tr")]
    public async Task TheTenantNotFoundPage_OnlyLooksUpTenantAddresses(string requested)
    {
        using var browser = _host.Web.NewBrowser();

        var page = await browser.GetAsync($"/tenant-not-found?host={Uri.EscapeDataString(requested)}", Marketing);

        Assert.Equal(HttpStatusCode.NotFound, page.Status);
    }

    // --- Helpers -----------------------------------------------------------------------------------

    private async Task SeedTenantAsync(string slug, bool isActive)
    {
        await using var db = NewDb();
        if (await db.Tenants.AnyAsync(t => t.Slug == slug, TestContext.Current.CancellationToken))
            return;

        db.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"Restaurant {slug}",
            Slug = slug,
            PrimaryDomain = $"{slug}.app.wasla.tr",
            DatabaseName = $"Wasla_Test_{slug}",
            EncryptedConnectionString = "not-a-real-secret",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private CentralDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_host.Connection).Options);

    private static List<KeyValuePair<string, string>> SignupForm(string antiforgeryToken, string slug)
    {
        var token = SentinelApplicant.NewToken();
        return
        [
            new("__RequestVerificationToken", antiforgeryToken),
            new("PlanCode", WaslaPlanCodes.Starter),
            new("BillingPeriod", "Monthly"),
            new("BusinessName", $"Bizname{token}"),
            new("SelectedBusinessTypeCodes", "burger"),
            new("BusinessPhoneType", "Mobile"),
            new("BusinessPhone", SentinelApplicant.NewPhone()),
            new("Slug", slug),
            new("Country", "Germany"),
            new("CityId", "1"),
            new("DistrictId", "1"),
            new("City", "Berlin"),
            new("District", "Mitte"),
            new("StreetAddress", $"Streetq{token}"),
            new("OwnerFullName", $"Ownerq{token}"),
            new("OwnerEmail", $"owner.{token}@sentinel.test"),
            new("Password", $"Secret-pass-{token}"),
            new("ConfirmPassword", $"Secret-pass-{token}")
        ];
    }

    private static List<string> Links(string html) =>
        LinkHref().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups["href"].Value)).ToList();

    [GeneratedRegex("<a [^>]*href=\"(?<href>[^\"]*)\"")]
    private static partial Regex LinkHref();

    public sealed class Host : IAsyncLifetime
    {
        private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-host-routing-keys-").FullName;

        public SqliteConnection Connection { get; } = new("Data Source=:memory:");

        internal SignupWebHost Web { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            Connection.Open();
            await SignupWebHost.CreateCentralDbAsync(Connection);
            Web = await SignupWebHost.StartAsync(Connection, _keyRing, configuration: new Dictionary<string, string?>
            {
                ["Wasla:TenantOnboarding:MarketingBaseDomain"] = Marketing,
                ["Wasla:TenantOnboarding:ApplicationBaseDomain"] = Application
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Web.DisposeAsync();
            Connection.Dispose();
            try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
        }
    }
}
