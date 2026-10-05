using System.Net;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// WAS-37. Central Admin sessions are revalidated against CentralDb on every request through the real cookie handler:
/// a cookie issued before a deactivation, password change, deletion or re-enable no longer reaches an Admin page, even
/// when replayed, remembered, or renewed by sliding expiration. Validation reads CentralDb only.
/// </summary>
public sealed class CentralAdminSessionRevalidationTests : IAsyncLifetime
{
    private const string AdminPage = "/admin";
    private const string SecondAdminEmail = "second-admin@wasla.test";
    private const string SecondAdminPassword = "Second-Test-Password-42";

    private readonly CentralTestDatabase _central = new();
    private readonly RecordingTenantDbFactory _tenants = new();
    private readonly ManualClock _clock = new();
    private AdminWebHost _host = null!;
    private Guid _tenantId;

    public async ValueTask InitializeAsync()
    {
        // A real tenant database exists, so any fan-out to it would be observable.
        _tenantId = _central.AddTenant("alpha-kebap", name: "Alpha Kebap").Id;
        _tenants.CreateTenantDatabase(_tenantId);
        _tenants.Seed(_tenantId, db => TenantSeed.HealthyTenant(db, DateTime.UtcNow));

        _host = await AdminWebHost.StartAsync(_central, _tenants, centralAdminCookieClock: _clock);
        SeedSecondAdmin();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
    }

    // 1. Enabled admin with a valid session ----------------------------------------------------------

    [Fact]
    public async Task EnabledAdmin_KeepsAccessAcrossRequestsAndConcurrentSessions()
    {
        var stampBefore = Stamp(AdminWebHost.AdminEmail);
        using var first = await _host.SignedInAdminAsync();
        using var second = await _host.SignedInAdminAsync();

        foreach (var browser in new[] { first, second, first, second })
        {
            var page = await browser.GetAsync(AdminPage);
            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.Empty(AdminCookieHeaders(page));
        }

        // Signing in again records the login but revokes nothing.
        Assert.Equal(stampBefore, Stamp(AdminWebHost.AdminEmail));
    }

    // 2. Deactivation during an active session ------------------------------------------------------

    [Fact]
    public async Task Deactivation_RejectsTheActiveSessionOnItsNextRequest_AndDeletesTheCookie()
    {
        using var browser = await _host.SignedInAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(AdminPage)).Status);
        var issued = browser.CentralAdminCookie!.Value;

        SetActive(AdminWebHost.AdminEmail, false);

        AssertSignedOut(await browser.GetAsync(AdminPage));
        Assert.Null(browser.CentralAdminCookie);

        // The rejection is server-side: a copy of the cookie kept elsewhere is rejected too.
        using var replay = _host.NewBrowser();
        replay.UseCentralAdminCookie(issued);
        AssertSignedOut(await replay.GetAsync($"/admin/customers/{_tenantId}"));

        // The login page no longer treats the revoked cookie as signed in.
        using var loginReplay = _host.NewBrowser();
        loginReplay.UseCentralAdminCookie(issued);
        var login = await loginReplay.GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.Contains("__RequestVerificationToken", login.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deactivation_ByDirectSqlWithoutAStampChange_StillRejectsTheSession()
    {
        using var browser = await _host.SignedInAdminAsync();
        var stamp = Stamp(AdminWebHost.AdminEmail);

        using (var db = _central.CreateContext())
            db.Database.ExecuteSqlRaw("UPDATE \"CentralAdminUsers\" SET \"IsActive\" = 0 WHERE \"NormalizedEmail\" = {0}", AdminWebHost.AdminEmail.ToUpperInvariant());

        Assert.Equal(stamp, Stamp(AdminWebHost.AdminEmail));
        AssertSignedOut(await browser.GetAsync(AdminPage));
    }

    // 3. Password change or reset during an active session ------------------------------------------

    [Fact]
    public async Task PasswordChange_RevokesSessionsIssuedBeforeIt_AndTheNewPasswordSignsIn()
    {
        using var browser = await _host.SignedInAdminAsync();
        var issued = browser.CentralAdminCookie!.Value;
        const string newPassword = "Rotated-Test-Password-43";

        ChangePassword(AdminWebHost.AdminEmail, newPassword);

        AssertSignedOut(await browser.GetAsync(AdminPage));
        using var replay = _host.NewBrowser();
        replay.UseCentralAdminCookie(issued);
        AssertSignedOut(await replay.GetAsync(AdminPage));

        using var fresh = _host.NewBrowser();
        await fresh.SignInAdminAsync(AdminWebHost.AdminEmail, newPassword);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync(AdminPage)).Status);
    }

    // 4. Re-enabling after revocation ---------------------------------------------------------------

    [Fact]
    public async Task Reenabling_DoesNotReviveCookiesRevokedByTheDeactivation()
    {
        using var browser = await _host.SignedInAdminAsync();
        var issued = browser.CentralAdminCookie!.Value;

        // The cookie is never presented while the account is disabled, so nothing but the stamp can reject it.
        SetActive(AdminWebHost.AdminEmail, false);
        SetActive(AdminWebHost.AdminEmail, true);

        AssertSignedOut(await browser.GetAsync(AdminPage));
        using var replay = _host.NewBrowser();
        replay.UseCentralAdminCookie(issued);
        AssertSignedOut(await replay.GetAsync(AdminPage));

        using var fresh = await _host.SignedInAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync(AdminPage)).Status);
    }

    [Fact]
    public async Task SupportedDisableAndEnableCommands_KeepTheOriginalCookieRejected()
    {
        using var desk = await _host.SignedInAdminAsync();
        using var laptop = await _host.SignedInAdminAsync();
        var laptopCookie = laptop.CentralAdminCookie!.Value;

        // A dry run, and an enable of an account that is already enabled, change nothing: no session is revoked.
        Assert.Equal(0, await RunStatusCommandAsync(enable: false, dryRun: true));
        Assert.Equal(0, await RunStatusCommandAsync(enable: true));
        Assert.Equal(HttpStatusCode.OK, (await desk.GetAsync(AdminPage)).Status);

        Assert.Equal(0, await RunStatusCommandAsync(enable: false));
        AssertSignedOut(await desk.GetAsync(AdminPage));

        // Enabled again, and the enable repeated: the laptop cookie, never presented while the account was disabled,
        // stays rejected.
        Assert.Equal(0, await RunStatusCommandAsync(enable: true));
        Assert.Equal(0, await RunStatusCommandAsync(enable: true));
        AssertSignedOut(await laptop.GetAsync(AdminPage));
        using var replay = _host.NewBrowser();
        replay.UseCentralAdminCookie(laptopCookie);
        AssertSignedOut(await replay.GetAsync(AdminPage));

        using var fresh = await _host.SignedInAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync(AdminPage)).Status);
    }

    // 5. Deleted accounts and missing or malformed claims ---------------------------------------------

    [Fact]
    public async Task DeletedAccount_IsRejected()
    {
        using var browser = await _host.SignedInAdminAsync();

        using (var db = _central.CreateContext())
        {
            db.CentralAdminUsers.Remove(db.CentralAdminUsers.Single(x => x.NormalizedEmail == AdminWebHost.AdminEmail.ToUpperInvariant()));
            db.SaveChanges();
        }

        AssertSignedOut(await browser.GetAsync(AdminPage));
    }

    [Fact]
    public async Task ACookieWithTheCurrentIdAndStamp_IsAccepted()
    {
        // Control for the theory below: the test issuer itself produces cookies the real validation accepts.
        using var browser = _host.NewBrowser();
        await IssueCookieAsync(browser, $"adminId={AdminId(AdminWebHost.AdminEmail)}&stamp={Stamp(AdminWebHost.AdminEmail)}");

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(AdminPage)).Status);
    }

    [Theory]
    [InlineData("pre-stamp cookie")]
    [InlineData("malformed stamp")]
    [InlineData("empty stamp")]
    [InlineData("unknown stamp")]
    [InlineData("missing admin id")]
    [InlineData("malformed admin id")]
    [InlineData("empty admin id")]
    [InlineData("another admin's id")]
    [InlineData("unknown admin id")]
    public async Task MissingOrMalformedSessionClaims_AreRejected(string variant)
    {
        var adminId = AdminId(AdminWebHost.AdminEmail);
        var stamp = Stamp(AdminWebHost.AdminEmail);
        var query = variant switch
        {
            // Exactly the claims a login issued before this change put in the cookie.
            "pre-stamp cookie" => $"adminId={adminId}",
            "malformed stamp" => $"adminId={adminId}&stamp=not-a-stamp",
            "empty stamp" => $"adminId={adminId}&stamp={Guid.Empty}",
            "unknown stamp" => $"adminId={adminId}&stamp={Guid.NewGuid()}",
            "missing admin id" => $"stamp={stamp}",
            "malformed admin id" => $"adminId=ops&stamp={stamp}",
            "empty admin id" => $"adminId={Guid.Empty}&stamp={stamp}",
            "another admin's id" => $"adminId={AdminId(SecondAdminEmail)}&stamp={stamp}",
            "unknown admin id" => $"adminId={Guid.NewGuid()}&stamp={stamp}",
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        using var browser = _host.NewBrowser();
        await IssueCookieAsync(browser, query);

        AssertSignedOut(await browser.GetAsync(AdminPage));
    }

    // 6. Remember-me and sliding expiration ------------------------------------------------------------

    [Fact]
    public async Task RememberMeSession_IsPersistent_AndIsRevokedLikeAnyOther()
    {
        using var browser = _host.NewBrowser();
        await browser.SignInAdminAsync(AdminWebHost.AdminEmail, AdminWebHost.AdminPassword, rememberMe: true);
        Assert.True(browser.CentralAdminCookie!.Expires > DateTime.Now.AddHours(20), "Remember-me must issue a persistent cookie.");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(AdminPage)).Status);

        SetActive(AdminWebHost.AdminEmail, false);

        AssertSignedOut(await browser.GetAsync(AdminPage));
    }

    [Fact]
    public async Task SlidingRenewal_ReissuesValidSessions_ButNeverRenewsOrRevivesARevokedOne()
    {
        using var browser = await _host.SignedInAdminAsync();
        var original = browser.CentralAdminCookie!.Value;

        // Five hours into an eight-hour session, a valid request is renewed.
        _clock.Advance(TimeSpan.FromHours(5));
        var renewal = await browser.GetAsync(AdminPage);
        Assert.Equal(HttpStatusCode.OK, renewal.Status);
        Assert.Contains(AdminCookieHeaders(renewal), header => !header.StartsWith(CentralAdminAuthCookieNames.Active + "=;", StringComparison.Ordinal));
        var renewed = browser.CentralAdminCookie!.Value;
        Assert.NotEqual(original, renewed);

        ChangePassword(AdminWebHost.AdminEmail, "Rotated-Test-Password-43");

        // The pre-renewal cookie is still unexpired and due for renewal, and is rejected rather than renewed.
        using var replay = _host.NewBrowser();
        replay.UseCentralAdminCookie(original);
        AssertSignedOut(await replay.GetAsync(AdminPage));

        // The renewed cookie carries the same revoked stamp; when it is itself due for renewal it is only deleted.
        _clock.Advance(TimeSpan.FromHours(7));
        AssertSignedOut(await browser.GetAsync(AdminPage));
    }

    // 7. One account change revokes every session of that account ---------------------------------------

    [Fact]
    public async Task OneAccountChange_RevokesEverySessionOfThatAccount_AndNoOtherAccount()
    {
        using var desk = await _host.SignedInAdminAsync();
        using var laptop = _host.NewBrowser();
        await laptop.SignInAdminAsync(AdminWebHost.AdminEmail, AdminWebHost.AdminPassword, rememberMe: true);
        using var colleague = _host.NewBrowser();
        await colleague.SignInAdminAsync(SecondAdminEmail, SecondAdminPassword);
        foreach (var browser in new[] { desk, laptop, colleague })
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(AdminPage)).Status);

        ChangePassword(AdminWebHost.AdminEmail, "Rotated-Test-Password-43");

        AssertSignedOut(await desk.GetAsync(AdminPage));
        AssertSignedOut(await laptop.GetAsync(AdminPage));
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync(AdminPage)).Status);
    }

    // 8. CentralDb only, no tenant fan-out --------------------------------------------------------------

    [Fact]
    public async Task Revalidation_ReadsCentralDbOncePerRequest_AndNeverOpensATenantDatabase()
    {
        // A tenant database that cannot be opened: the Admin overview and the validation must not need it.
        _tenants.Override(_tenantId, _ => throw new InvalidOperationException("Tenant database must not be opened."));
        using var browser = await _host.SignedInAdminAsync();

        for (var i = 0; i < 3; i++)
        {
            _central.Counter.Reset();
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(AdminPage)).Status);
            var validation = Assert.Single(_central.Counter.Commands, IsSessionValidation);
            Assert.DoesNotContain("\"PasswordHash\"", validation, StringComparison.Ordinal);
        }

        SetActive(AdminWebHost.AdminEmail, false);
        _central.Counter.Reset();
        AssertSignedOut(await browser.GetAsync(AdminPage));
        Assert.True(IsSessionValidation(Assert.Single(_central.Counter.Commands)));

        Assert.Empty(_tenants.Opened);
        Assert.Equal(0, _tenants.Counter.Count);
    }

    // Validation that cannot complete -----------------------------------------------------------------

    [Fact]
    public async Task UnreadableAccountState_Answers503_WithoutAcceptingRenewingOrDeletingTheSession()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var issued = browser.CentralAdminCookie!.Value;
        _clock.Advance(TimeSpan.FromHours(5)); // due for sliding renewal
        RenameAdminTable("CentralAdminUsers", "CentralAdminUsers_Unavailable");

        foreach (var path in new[] { AdminPage, $"/admin/customers/{_tenantId}", "/admin/login" })
        {
            var failed = await browser.GetAsync(path);

            // The global error page, as 503, with nothing from Admin and no SQL detail.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.Status);
            Assert.Contains(Resource(".en-US", "Errors.UnexpectedTitle"), WebUtility.HtmlDecode(failed.Body), StringComparison.Ordinal);
            Assert.DoesNotContain("Alpha Kebap", failed.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("wasla-admin", failed.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("__RequestVerificationToken", failed.Body, StringComparison.Ordinal);
            foreach (var detail in new[] { "no such table", "CentralAdminUsers", "SQLite", "Exception", "SecurityStamp", "Data Source" })
                Assert.DoesNotContain(detail, failed.Body, StringComparison.OrdinalIgnoreCase);

            // The cookie is neither renewed nor deleted.
            Assert.Empty(AdminCookieHeaders(failed));
            Assert.Equal(issued, browser.CentralAdminCookie!.Value);
        }

        Assert.Empty(_tenants.Opened);

        // An outage does not sign the admin out: once CentralDb answers, the same session is validated and renewed.
        RenameAdminTable("CentralAdminUsers_Unavailable", "CentralAdminUsers");
        var recovered = await browser.GetAsync(AdminPage);
        Assert.Equal(HttpStatusCode.OK, recovered.Status);
        Assert.NotEqual(issued, browser.CentralAdminCookie!.Value);
    }

    // Tenant authentication is unchanged ---------------------------------------------------------------

    [Fact]
    public async Task TenantSession_IsNotPassedThroughCentralAdminRevalidation()
    {
        using var browser = _host.NewBrowser();
        await browser.SignInTenantUserAsync(_tenantId);

        _central.Counter.Reset();
        var page = await browser.GetAsync(AdminPage);

        Assert.Equal(HttpStatusCode.Redirect, page.Status);
        Assert.DoesNotContain(_central.Counter.Commands, IsSessionValidation);
        Assert.NotNull(browser.Cookies.SingleOrDefault(cookie => cookie.Name == TenantAuthCookieNames.Active));
    }

    // Helpers ----------------------------------------------------------------------------------------

    private static bool IsSessionValidation(string sql) => sql.Contains("FROM \"CentralAdminUsers\"", StringComparison.Ordinal);

    private static string Resource(string suffix, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource{suffix}.resx"))
            .Root!
            .Elements("data")
            .Single(e => e.Attribute("name")!.Value == key)
            .Element("value")!.Value;

    private static IReadOnlyList<string> AdminCookieHeaders(AdminResponse response) =>
        response.SetCookies!.Where(header => header.StartsWith(CentralAdminAuthCookieNames.Active + "=", StringComparison.Ordinal)).ToList();

    /// <summary>Challenged to the Admin login, the cookie deleted, and no renewed ticket written.</summary>
    private static void AssertSignedOut(AdminResponse response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.Status);
        Assert.StartsWith("/admin/login", response.Location!.IsAbsoluteUri ? response.Location.PathAndQuery : response.Location.OriginalString, StringComparison.Ordinal);
        var headers = AdminCookieHeaders(response);
        Assert.NotEmpty(headers);
        Assert.All(headers, header =>
        {
            Assert.StartsWith(CentralAdminAuthCookieNames.Active + "=;", header, StringComparison.Ordinal);
            Assert.Contains("expires=Thu, 01 Jan 1970", header, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task IssueCookieAsync(AdminBrowser browser, string query)
    {
        var issued = await browser.GetAsync($"{AdminWebHost.CentralAdminCookiePath}?{query}");
        Assert.Equal(HttpStatusCode.OK, issued.Status);
        Assert.NotNull(browser.CentralAdminCookie);
    }

    private static CentralAdminUser Admin(CentralDbContext db, string email) =>
        db.CentralAdminUsers.Single(x => x.NormalizedEmail == email.ToUpperInvariant());

    private Guid AdminId(string email)
    {
        using var db = _central.CreateContext();
        return Admin(db, email).Id;
    }

    private Guid Stamp(string email)
    {
        using var db = _central.CreateContext();
        return Admin(db, email).SecurityStamp;
    }

    /// <summary>Changes the account the way any application path does: through CentralDbContext.</summary>
    private void SetActive(string email, bool active)
    {
        using var db = _central.CreateContext();
        Admin(db, email).IsActive = active;
        db.SaveChanges();
    }

    /// <summary>The same write the CLI password resets perform.</summary>
    private void ChangePassword(string email, string password)
    {
        using var db = _central.CreateContext();
        var admin = Admin(db, email);
        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 4);
        admin.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    /// <summary>The supported path: <c>disable-central-admin</c> / <c>enable-central-admin</c>.</summary>
    private async Task<int> RunStatusCommandAsync(bool enable, bool dryRun = false)
    {
        await using var db = _central.CreateContext();
        return await CliCentralAdminStatus.ExecuteAsync(db, AdminWebHost.AdminEmail, enable, dryRun, TextWriter.Null, TestContext.Current.CancellationToken);
    }

    private void RenameAdminTable(string from, string to)
    {
        // Fixed table names from this class, never input.
        var sql = "ALTER TABLE \"" + from + "\" RENAME TO \"" + to + "\";";
        using var db = _central.CreateContext();
        db.Database.ExecuteSqlRaw(sql);
    }

    private void SeedSecondAdmin()
    {
        using var db = _central.CreateContext();
        db.CentralAdminUsers.Add(new CentralAdminUser
        {
            Email = SecondAdminEmail,
            NormalizedEmail = SecondAdminEmail.ToUpperInvariant(),
            DisplayName = "Second",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(SecondAdminPassword, workFactor: 4),
            IsActive = true
        });
        db.SaveChanges();
    }

    /// <summary>The cookie handler's clock. It starts at the real time, so cookies issued now are current.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
