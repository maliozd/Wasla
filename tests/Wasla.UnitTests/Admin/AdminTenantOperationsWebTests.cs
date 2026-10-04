using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Behavioral tests of the Tenant Operations Center over real HTTP: authorization, tenant isolation, no tenant-database
/// fan-out, sanitized failures, list state in links, and the rendered output never carrying a secret.
/// </summary>
public sealed partial class AdminTenantOperationsWebTests : IAsyncLifetime
{
    private readonly CentralTestDatabase _central = new();
    private readonly RecordingTenantDbFactory _tenants = new();
    private AdminWebHost _host = null!;

    private Guid _tenantA;
    private Guid _tenantB;

    public async ValueTask InitializeAsync()
    {
        var now = DateTime.UtcNow;
        var a = _central.AddTenant("alpha-kebap", name: "Alpha Kebap", planCode: "Pro");
        var b = _central.AddTenant("bravo-sushi", name: "Bravo Sushi", planCode: "Starter");
        _tenantA = a.Id;
        _tenantB = b.Id;
        _central.AddRegistration("alpha-kebap", PendingRegistrationStatus.Provisioned, a.Id);
        _central.AddDevice(a.Id, "Alpha kitchen PC", true, now.AddSeconds(-5));
        _central.AddDevice(b.Id, "Bravo kitchen PC", true, now.AddHours(-3));

        _tenants.CreateTenantDatabase(a.Id);
        _tenants.Seed(a.Id, db => TenantSeed.HealthyTenant(db, now));
        _tenants.CreateTenantDatabase(b.Id);
        _tenants.Seed(b.Id, db =>
        {
            TenantSeed.HealthyTenant(db, now, TenantOperationalMode.Setup);
            db.PlatformConnections.Local.Single().StoreId = "BRAVO-STORE-777";
            db.Orders.Add(TenantSeed.Order(now, OrderStatus.New, "BRAVO-ORDER-1"));
        });

        _host = await AdminWebHost.StartAsync(_central, _tenants);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
    }

    // Authorization ---------------------------------------------------------------------------------

    [Fact]
    public async Task AnonymousRequests_AreChallengedToTheAdminLogin()
    {
        using var browser = _host.NewBrowser();

        foreach (var path in new[] { "/admin", "/admin/customers", $"/admin/customers/{_tenantA}", "/admin/customers?sort=name" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.Status);
            Assert.Contains("/admin/login", response.Location!.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Alpha Kebap", response.Body, StringComparison.Ordinal);
        }

        Assert.Empty(_tenants.Opened);
    }

    [Fact]
    public async Task TenantUser_EvenWithAForgedRoleClaim_CannotReachAdminPages()
    {
        using var browser = _host.NewBrowser();
        await browser.SignInTenantUserAsync(_tenantA);

        foreach (var path in new[] { "/admin", "/admin/customers", $"/admin/customers/{_tenantA}", $"/admin/customers/{_tenantB}" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.Status);
            Assert.Contains("/admin/login", response.Location!.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Kebap", response.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Sushi", response.Body, StringComparison.Ordinal);
        }

        Assert.Empty(_tenants.Opened);
    }

    // Overview and list: CentralDb only -------------------------------------------------------------

    [Fact]
    public async Task OverviewAndList_NeverOpenATenantDatabase_AndUseAFixedQueryBudget()
    {
        for (var i = 0; i < 30; i++)
            _central.AddTenant($"extra-{i:00}");
        using var browser = await _host.SignedInAdminAsync();

        _central.Counter.Reset();
        var overview = await browser.GetAsync("/admin");
        var overviewQueries = _central.Counter.Count;
        _central.Counter.Reset();
        var list = await browser.GetAsync("/admin/customers?size=50");
        var listQueries = _central.Counter.Count;

        Assert.Equal(HttpStatusCode.OK, overview.Status);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Empty(_tenants.Opened);
        // Overview: 4 aggregate queries + the existing attention list. List: count + page + device counts.
        Assert.Equal(5, overviewQueries);
        Assert.Equal(3, listQueries);
        Assert.Contains("Alpha Kebap", Html(list), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_StaysUsable_WhenATenantDatabaseIsDown()
    {
        _tenants.Override(_tenantA, _ => throw new InvalidOperationException("down"));
        using var browser = await _host.SignedInAdminAsync();

        var list = await browser.GetAsync("/admin/customers");

        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Contains("Alpha Kebap", Html(list), StringComparison.Ordinal);
        Assert.Contains("Bravo Sushi", Html(list), StringComparison.Ordinal);
    }

    // Sorting, filters and pagination in links --------------------------------------------------------

    [Fact]
    public async Task SortHeaders_KeepSearchFiltersAndPageSize_AndResetThePage()
    {
        using var browser = await _host.SignedInAdminAsync();

        var page = await browser.GetAsync("/admin/customers?q=a&status=active&plan=pro&migration=succeeded&size=50&sort=name&dir=asc&page=1");
        var html = page.Body;

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var headers = SortHeaders().Matches(html).Select(m => (AriaSort: m.Groups[1].Value, Href: WebUtility.HtmlDecode(m.Groups[2].Value))).ToList();
        Assert.Equal(5, headers.Count);
        foreach (var (_, href) in headers)
        {
            Assert.Contains("q=a", href, StringComparison.Ordinal);
            Assert.Contains("status=active", href, StringComparison.Ordinal);
            Assert.Contains("plan=Pro", href, StringComparison.Ordinal);
            Assert.Contains("migration=succeeded", href, StringComparison.Ordinal);
            Assert.Contains("size=50", href, StringComparison.Ordinal);
            Assert.DoesNotContain("page=", href, StringComparison.Ordinal);
        }

        var name = headers.Single(h => h.Href.Contains("sort=name", StringComparison.Ordinal));
        Assert.Equal("ascending", name.AriaSort);
        Assert.Contains("dir=desc", name.Href, StringComparison.Ordinal);
        Assert.All(headers.Where(h => h != name), h => Assert.Equal(string.Empty, h.AriaSort));
        Assert.Equal(1, Regex.Count(html, "aria-sort=\""));

        // The filter form keeps the sort, and each control has a label.
        Assert.Contains("name=\"sort\" value=\"name\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"dir\" value=\"asc\"", html, StringComparison.Ordinal);
        foreach (var id in new[] { "tenantSearch", "tenantStatus", "tenantMigration", "tenantPlan", "tenantPageSize" })
            Assert.Contains($"<label for=\"{id}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pagination_KeepsSortAndFilters()
    {
        for (var i = 0; i < 25; i++)
            _central.AddTenant($"zz-{i:00}", name: $"Zulu {i:00}");
        using var browser = await _host.SignedInAdminAsync();

        var page = await browser.GetAsync("/admin/customers?q=zulu&sort=name&dir=desc&size=10&page=2");
        var html = Html(page);
        var links = PageLinks().Matches(page.Body).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();

        Assert.Contains("Zulu 14", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Zulu 24", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", page.Body, StringComparison.Ordinal);
        Assert.NotEmpty(links);
        Assert.All(links, href =>
        {
            Assert.Contains("q=zulu", href, StringComparison.Ordinal);
            Assert.Contains("sort=name", href, StringComparison.Ordinal);
            Assert.Contains("dir=desc", href, StringComparison.Ordinal);
            Assert.Contains("size=10", href, StringComparison.Ordinal);
        });
        Assert.Contains(links, href => href.Contains("page=3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostileListParameters_FallBackToDefaults()
    {
        using var browser = await _host.SignedInAdminAsync();

        var page = await browser.GetAsync("/admin/customers?sort=EncryptedConnectionString&dir=sideways&size=100000&page=-5&status=1&plan=%27%3B--");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("name=\"sort\" value=\"created\"", page.Body, StringComparison.Ordinal);
        Assert.Contains("Alpha Kebap", Html(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStates_DistinguishNoMatchesFromNoTenants()
    {
        using var browser = await _host.SignedInAdminAsync();

        var page = await browser.GetAsync("/admin/customers?q=does-not-exist");

        Assert.Contains(Resource("", "Admin.Ops.List.NoMatches"), Html(page), StringComparison.Ordinal);
        Assert.Contains(Resource("", "Admin.Ops.List.ClearFilters"), Html(page), StringComparison.Ordinal);
    }

    // Detail: one tenant, isolated --------------------------------------------------------------------

    [Fact]
    public async Task Detail_ReadsOnlyTheSelectedTenant_AndShowsOnlyItsData()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");

        var page = await browser.GetAsync($"/admin/customers/{_tenantA}");
        var html = Html(page);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Equal([_tenantA], _tenants.Opened);
        Assert.Contains("Alpha Kebap", html, StringComparison.Ordinal);
        Assert.Contains("STORE-1001", html, StringComparison.Ordinal);
        Assert.Contains("Alpha kitchen PC", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Bravo", html, StringComparison.Ordinal);
        Assert.DoesNotContain("BRAVO-STORE-777", html, StringComparison.Ordinal);
        Assert.Contains(Resource(".en-US", "Admin.Ops.Database.Reachable"), html, StringComparison.Ordinal);
        Assert.Contains(Resource(".en-US", "Admin.Ops.Migrations.Current"), html, StringComparison.Ordinal);
        Assert.Contains(Resource(".en-US", "Admin.Ops.Mode.Live"), html, StringComparison.Ordinal);
        Assert.Contains(Resource(".en-US", "Admin.Ops.PrintBridge.Online"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_ShowsSetupModeForASetupTenant()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");

        var html = Html(await browser.GetAsync($"/admin/customers/{_tenantB}"));

        Assert.Contains(Resource(".en-US", "Admin.Ops.Mode.Setup"), html, StringComparison.Ordinal);
        Assert.Contains(Resource(".en-US", "Admin.Ops.Guidance.TenantInSetup"), html, StringComparison.Ordinal);
        Assert.Contains(Resource(".en-US", "Admin.Ops.PrintBridge.Offline"), html, StringComparison.Ordinal);
        Assert.Contains("BRAVO-STORE-777", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_ShowsMigrationsBehind()
    {
        var behind = _central.AddTenant("behind", name: "Behind Grill");
        _tenants.CreateTenantDatabase(behind.Id, expected => expected.Take(expected.Count - 1));
        using var browser = await _host.SignedInAdminAsync("en-US");

        var html = Html(await browser.GetAsync($"/admin/customers/{behind.Id}"));

        Assert.Contains(Resource(".en-US", "Admin.Ops.Migrations.Pending"), html, StringComparison.Ordinal);
        Assert.Contains(Resource(".en-US", "Admin.Ops.Guidance.MigrationsPending"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownIds_AndRegistrationIds_AreNotFound()
    {
        var registration = _central.AddRegistration("orphan", PendingRegistrationStatus.PaymentSucceeded);
        using var browser = await _host.SignedInAdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/admin/customers/{Guid.NewGuid()}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/admin/customers/{registration.Id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/admin/customers/not-a-guid")).Status);
        Assert.Empty(_tenants.Opened);
    }

    [Fact]
    public async Task UnavailableTenantDatabase_RendersASanitizedPanel()
    {
        var down = _central.AddTenant("down", name: "Down Diner");
        var missing = Path.Combine(Path.GetTempPath(), $"wasla-missing-{Guid.NewGuid():N}", "x.db");
        _tenants.Override(down.Id, _ => Task.FromResult(new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlite($"Data Source={missing};Mode=ReadOnly;Pooling=False").Options)));
        using var browser = await _host.SignedInAdminAsync("en-US");

        var page = await browser.GetAsync($"/admin/customers/{down.Id}");
        var html = Html(page);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("Down Diner", html, StringComparison.Ordinal);
        Assert.True(html.Contains(Resource(".en-US", "Admin.Ops.Database.Unreachable"), StringComparison.Ordinal), Regex.Match(html, @"opsDatabaseHeading[\s\S]{0,1500}").Value);
        Assert.Contains(Resource(".en-US", "Admin.Ops.Database.UnavailablePanelTitle"), html, StringComparison.Ordinal);
        Assert.Contains(Resource(".en-US", "Admin.Ops.Guidance.DatabaseUnreachable"), html, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteException", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-missing", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TenantWithoutConnectionDetails_IsNotConfigured_AndItsDatabaseIsNotOpened()
    {
        var half = _central.AddTenant("half", name: "Half Provisioned", encryptedConnectionString: string.Empty);
        using var browser = await _host.SignedInAdminAsync("en-US");

        var html = Html(await browser.GetAsync($"/admin/customers/{half.Id}"));

        Assert.Contains(Resource(".en-US", "Admin.Ops.Database.NotConfigured"), html, StringComparison.Ordinal);
        Assert.Empty(_tenants.Opened);
    }

    [Fact]
    public async Task SlowTenantDatabase_DoesNotHoldThePage()
    {
        await using var host = await AdminWebHost.StartAsync(_central, _tenants, healthTimeout: TimeSpan.FromMilliseconds(400));
        _tenants.Override(_tenantA, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        });
        using var browser = await host.SignedInAdminAsync("en-US");
        var started = System.Diagnostics.Stopwatch.StartNew();

        var page = await browser.GetAsync($"/admin/customers/{_tenantA}");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains(Resource(".en-US", "Admin.Ops.Database.TimedOut"), Html(page), StringComparison.Ordinal);
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task CentralDatabaseFailure_RendersTheLocalizedErrorPanel()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        _central.Dispose(); // The next open creates an empty database without tables.

        foreach (var path in new[] { "/admin", "/admin/customers", $"/admin/customers/{_tenantA}" })
        {
            var page = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, page.Status);
            Assert.Contains(Resource(".en-US", "Admin.Ops.LoadError.Title"), Html(page), StringComparison.Ordinal);
            Assert.DoesNotContain("no such table", page.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exception", page.Body, StringComparison.Ordinal);
        }

        Assert.Empty(_tenants.Opened);
    }

    // Secrets ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RenderedAdminPages_NeverContainCredentialsTokensOrConnectionStrings()
    {
        var failed = _central.AddTenant("failed-migration", name: "Failed Migration", lastMigrationResult: SecretMarkers.MigrationFailureText);
        _central.AddDevice(failed.Id, "Failed PC", true, null);
        _tenants.Override(failed.Id, _ => throw new InvalidOperationException(SecretMarkers.ConnectionString));
        // A paid registration without a tenant is rendered in the overview's attention list.
        _central.AddRegistration("attention-pizza", PendingRegistrationStatus.PaymentSucceeded, paymentSucceededAt: DateTime.UtcNow.AddHours(-1));
        using var browser = await _host.SignedInAdminAsync();

        // CentralDb SQL behind the overview and list never touches secret or contact columns.
        _central.Counter.Reset();
        var overview = await browser.GetAsync("/admin");
        await browser.GetAsync("/admin/customers");
        Assert.Contains("Registration attention-pizza", Html(overview), StringComparison.Ordinal);
        Assert.All(_central.Counter.Commands, sql =>
        {
            foreach (var column in new[] { "PasswordHash", "EncryptedConnectionString", "TokenHash", "OwnerEmail", "OwnerPhone", "LastIpAddress", "SimulatedPaymentReference" })
                Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
        });

        var pages = new List<string>();
        foreach (var path in new[]
                 {
                     "/admin",
                     "/admin/customers",
                     "/admin/customers?migration=failed",
                     $"/admin/customers/{_tenantA}",
                     $"/admin/customers/{_tenantB}",
                     $"/admin/customers/{failed.Id}"
                 })
        {
            var page = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.Status);
            pages.Add(page.Body);
            pages.Add(WebUtility.HtmlDecode(page.Body));
        }

        foreach (var body in pages)
        {
            foreach (var secret in SecretMarkers.All)
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("owner-pii@example.test", body, StringComparison.Ordinal);
            Assert.DoesNotContain("attention-pizza@example.test", body, StringComparison.Ordinal);
            Assert.DoesNotContain("KITCHEN-PC", body, StringComparison.Ordinal);
            Assert.DoesNotContain("InstallationId", body, StringComparison.Ordinal);
        }
    }

    // Localization and RTL -----------------------------------------------------------------------------

    [Theory]
    [InlineData("ar-SA", ".ar-SA", "rtl")]
    [InlineData("ru-RU", ".ru-RU", "ltr")]
    [InlineData("tr-TR", ".tr-TR", "ltr")]
    public async Task Pages_AreLocalized_AndArabicIsRightToLeft(string culture, string suffix, string direction)
    {
        using var browser = await _host.SignedInAdminAsync(culture);

        foreach (var (path, key) in new[]
                 {
                     ("/admin", "Admin.Ops.OverviewTitle"),
                     ("/admin/customers", "Admin.Ops.List.Subtitle"),
                     ($"/admin/customers/{_tenantA}", "Admin.Ops.Section.Guidance")
                 })
        {
            var page = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.Contains($"dir=\"{direction}\"", page.Body, StringComparison.Ordinal);
            Assert.Contains(Resource(suffix, key), Html(page), StringComparison.Ordinal);
        }
    }

    // Helpers ------------------------------------------------------------------------------------------

    private static string Html(AdminResponse response) => WebUtility.HtmlDecode(response.Body);

    private static string Resource(string suffix, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource{suffix}.resx"))
            .Root!
            .Elements("data")
            .Single(e => e.Attribute("name")!.Value == key)
            .Element("value")!.Value;

    [GeneratedRegex("<th scope=\"col\"[^>]*?(?:aria-sort=\"([a-z]+)\")?>\\s*<a class=\"wasla-admin-sort\" href=\"([^\"]+)\"")]
    private static partial Regex SortHeaders();

    [GeneratedRegex("<a class=\"page-link\" href=\"([^\"]+)\"")]
    private static partial Regex PageLinks();
}
