using System.Net;
using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Security;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// WAS-37. Central Admin sessions are revalidated against CentralDb on every request through the real cookie
/// registration and the real <c>/admin/login</c> form: a cookie issued before a deactivation, deletion or password change
/// no longer reaches an Admin page, even when replayed, remembered or due for sliding renewal. Validation reads CentralDb
/// only. The CLI password resets that replace the stamp are driven end to end in <c>CliResetPasswordTests</c>.
/// </summary>
public sealed class CentralAdminSessionRevalidationTests : IAsyncLifetime
{
    private const string SecondAdminEmail = "second-admin@wasla.test";
    private const string SecondAdminPassword = "Second-Test-Password-42";
    private const string NewPassword = "Rotated-Test-Password-43";

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

    /// <summary>Every page behind the Central Admin scheme, plus the authenticated logout.</summary>
    private string[] ProtectedPages =>
    [
        "/admin",
        "/admin/customers",
        $"/admin/customers/{_tenantId}",
        "/admin/pending-registrations"
    ];

    // Valid, unchanged accounts -------------------------------------------------------------------------

    [Fact]
    public async Task UnchangedAccount_KeepsAccessToEveryAdminPage_AcrossRequestsAndSessions()
    {
        var stampBefore = Stamp(AdminWebHost.AdminEmail);
        using var first = await _host.SignedInAdminAsync();
        using var second = await _host.SignedInAdminAsync();

        foreach (var browser in new[] { first, second })
        {
            foreach (var path in ProtectedPages)
            {
                var page = await browser.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Empty(AdminCookieHeaders(page));
            }
        }

        // Signing in again records the login but revokes nothing.
        Assert.Equal(stampBefore, Stamp(AdminWebHost.AdminEmail));
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/admin")).Status);
    }

    // Deactivation during an active session -------------------------------------------------------------

    [Fact]
    public async Task Deactivation_RejectsTheActiveSessionOnItsNextRequest_AndSignsItOut()
    {
        using var browser = await _host.SignedInAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).Status);
        var issued = browser.CentralAdminCookie!.Value;

        // The only way to disable an admin today is a direct CentralDb write; it is not a credential change.
        ExecuteSql("UPDATE \"CentralAdminUsers\" SET \"IsActive\" = 0 WHERE \"NormalizedEmail\" = {0}", AdminWebHost.AdminEmail.ToUpperInvariant());

        AssertSignedOut(await browser.GetAsync("/admin"));
        Assert.Null(browser.CentralAdminCookie);

        // The rejection is server-side: a copy of the cookie kept elsewhere is rejected on every Admin page.
        foreach (var path in ProtectedPages)
        {
            using var replay = _host.NewBrowser();
            replay.UseCentralAdminCookie(issued);
            AssertSignedOut(await replay.GetAsync(path));
        }

        // The login page no longer treats the revoked cookie as signed in.
        using var loginReplay = _host.NewBrowser();
        loginReplay.UseCentralAdminCookie(issued);
        var login = await loginReplay.GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.Contains("__RequestVerificationToken", login.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingAndReenablingWithANewStamp_KeepsCookiesFromBeforeRevoked()
    {
        using var browser = await _host.SignedInAdminAsync();
        var issued = browser.CentralAdminCookie!.Value;

        // The documented disable and enable statements. The cookie is never presented while the account is disabled,
        // so only the replaced stamp can reject it afterwards.
        ExecuteSql("UPDATE \"CentralAdminUsers\" SET \"IsActive\" = 0, \"SecurityStamp\" = {0} WHERE \"NormalizedEmail\" = {1}",
            Guid.NewGuid().ToString().ToUpperInvariant(), AdminWebHost.AdminEmail.ToUpperInvariant());
        ExecuteSql("UPDATE \"CentralAdminUsers\" SET \"IsActive\" = 1 WHERE \"NormalizedEmail\" = {0}", AdminWebHost.AdminEmail.ToUpperInvariant());

        AssertSignedOut(await browser.GetAsync("/admin"));
        using var replay = _host.NewBrowser();
        replay.UseCentralAdminCookie(issued);
        AssertSignedOut(await replay.GetAsync("/admin"));

        using var fresh = await _host.SignedInAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/admin")).Status);
    }

    // Password change during an active session ----------------------------------------------------------

    [Fact]
    public async Task PasswordChange_RevokesSessionsIssuedBeforeIt_AndANewLoginWorks()
    {
        using var browser = await _host.SignedInAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).Status);
        var issued = browser.CentralAdminCookie!.Value;

        ChangePassword(AdminWebHost.AdminEmail, NewPassword);

        AssertSignedOut(await browser.GetAsync("/admin"));
        foreach (var path in ProtectedPages)
        {
            using var replay = _host.NewBrowser();
            replay.UseCentralAdminCookie(issued);
            AssertSignedOut(await replay.GetAsync(path));
        }

        using var fresh = _host.NewBrowser();
        await fresh.SignInAdminAsync(AdminWebHost.AdminEmail, NewPassword);
        foreach (var path in ProtectedPages)
            Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync(path)).Status);
    }

    [Fact]
    public async Task OneAccountChange_RevokesEverySessionOfThatAccount_AndNoOtherAccount()
    {
        using var desk = await _host.SignedInAdminAsync();
        using var laptop = _host.NewBrowser();
        await laptop.SignInAdminAsync(AdminWebHost.AdminEmail, AdminWebHost.AdminPassword, rememberMe: true);
        using var colleague = _host.NewBrowser();
        await colleague.SignInAdminAsync(SecondAdminEmail, SecondAdminPassword);
        foreach (var browser in new[] { desk, laptop, colleague })
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).Status);

        ChangePassword(AdminWebHost.AdminEmail, NewPassword);

        AssertSignedOut(await desk.GetAsync("/admin"));
        AssertSignedOut(await laptop.GetAsync("/admin"));
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync("/admin")).Status);
    }

    // Missing accounts -----------------------------------------------------------------------------------

    [Fact]
    public async Task DeletedAccount_IsRejected()
    {
        using var browser = await _host.SignedInAdminAsync();

        using (var db = _central.CreateContext())
        {
            db.CentralAdminUsers.Remove(Admin(db, AdminWebHost.AdminEmail));
            db.SaveChanges();
        }

        AssertSignedOut(await browser.GetAsync("/admin"));
    }

    // Legacy, missing and malformed session claims ----------------------------------------------------------

    [Fact]
    public async Task ACookieWithTheCurrentIdAndStamp_IsAccepted()
    {
        // Control for the theory below: the test issuer produces cookies the real validation accepts.
        using var browser = _host.NewBrowser();
        browser.UseCentralAdminCookie(_host.ProtectCentralAdminCookie(CentralAdminSessionClaims.Create(
            AdminId(AdminWebHost.AdminEmail), AdminWebHost.AdminEmail, "Ops", Stamp(AdminWebHost.AdminEmail))));

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).Status);
    }

    [Theory]
    [InlineData("pre-stamp cookie")]
    [InlineData("malformed stamp")]
    [InlineData("empty stamp")]
    [InlineData("uppercase stamp")]
    [InlineData("repeated stamp")]
    [InlineData("missing admin id")]
    [InlineData("malformed admin id")]
    [InlineData("empty admin id")]
    [InlineData("repeated admin id")]
    [InlineData("missing role")]
    [InlineData("other role")]
    public async Task LegacyMissingOrMalformedSessionClaims_AreRejectedBeforeCentralDbIsRead(string variant)
    {
        var adminId = AdminId(AdminWebHost.AdminEmail).ToString();
        var stamp = Stamp(AdminWebHost.AdminEmail).ToString();
        var claims = variant switch
        {
            // Exactly the claims a login issued before WAS-37 put in the cookie.
            "pre-stamp cookie" => Claims(adminId, stamp: null),
            "malformed stamp" => Claims(adminId, "not-a-stamp"),
            "empty stamp" => Claims(adminId, Guid.Empty.ToString()),
            "uppercase stamp" => Claims(adminId, stamp.ToUpperInvariant()),
            "repeated stamp" => [.. Claims(adminId, stamp), new Claim(WaslaAuthContracts.CentralAdminSecurityStampClaim, stamp)],
            "missing admin id" => Claims(adminId: null, stamp),
            "malformed admin id" => Claims("ops", stamp),
            "empty admin id" => Claims(Guid.Empty.ToString(), stamp),
            "repeated admin id" => [.. Claims(adminId, stamp), new Claim(ClaimTypes.NameIdentifier, AdminId(SecondAdminEmail).ToString())],
            "missing role" => Claims(adminId, stamp, role: null),
            "other role" => Claims(adminId, stamp, role: "Owner"),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        using var browser = _host.NewBrowser();
        browser.UseCentralAdminCookie(_host.ProtectCentralAdminCookie(claims));

        _central.Counter.Reset();
        AssertSignedOut(await browser.GetAsync("/admin"));
        Assert.DoesNotContain(_central.Counter.Commands, IsSessionValidation);
    }

    [Theory]
    [InlineData("unknown stamp")]
    [InlineData("another admin's stamp")]
    [InlineData("unknown admin id")]
    public async Task WellFormedClaimsThatDoNotMatchTheAccount_AreRejected(string variant)
    {
        var adminId = AdminId(AdminWebHost.AdminEmail).ToString();
        var claims = variant switch
        {
            "unknown stamp" => Claims(adminId, Guid.NewGuid().ToString()),
            "another admin's stamp" => Claims(adminId, Stamp(SecondAdminEmail).ToString()),
            "unknown admin id" => Claims(Guid.NewGuid().ToString(), Stamp(AdminWebHost.AdminEmail).ToString()),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        using var browser = _host.NewBrowser();
        browser.UseCentralAdminCookie(_host.ProtectCentralAdminCookie(claims));

        AssertSignedOut(await browser.GetAsync("/admin"));
    }

    // Remember-me and sliding expiration --------------------------------------------------------------------

    [Fact]
    public async Task RememberMeSession_IsPersistent_AndIsRevokedLikeAnyOther()
    {
        using var deactivated = _host.NewBrowser();
        await deactivated.SignInAdminAsync(AdminWebHost.AdminEmail, AdminWebHost.AdminPassword, rememberMe: true);
        Assert.True(deactivated.CentralAdminCookie!.Expires > DateTime.Now.AddHours(20), "Remember-me must issue a persistent cookie.");
        using var reset = _host.NewBrowser();
        await reset.SignInAdminAsync(SecondAdminEmail, SecondAdminPassword, rememberMe: true);
        Assert.Equal(HttpStatusCode.OK, (await deactivated.GetAsync("/admin")).Status);
        Assert.Equal(HttpStatusCode.OK, (await reset.GetAsync("/admin")).Status);

        ExecuteSql("UPDATE \"CentralAdminUsers\" SET \"IsActive\" = 0 WHERE \"NormalizedEmail\" = {0}", AdminWebHost.AdminEmail.ToUpperInvariant());
        ChangePassword(SecondAdminEmail, NewPassword);

        AssertSignedOut(await deactivated.GetAsync("/admin"));
        AssertSignedOut(await reset.GetAsync("/admin"));
    }

    [Fact]
    public async Task SlidingRenewal_ReissuesValidSessions_ButNeverRenewsARevokedOne()
    {
        using var browser = await _host.SignedInAdminAsync();
        var original = browser.CentralAdminCookie!.Value;

        // Five hours into an eight-hour session, a valid request is renewed.
        _clock.Advance(TimeSpan.FromHours(5));
        var renewal = await browser.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, renewal.Status);
        Assert.Contains(AdminCookieHeaders(renewal), header => !IsDeletion(header));
        var renewed = browser.CentralAdminCookie!.Value;
        Assert.NotEqual(original, renewed);

        ChangePassword(AdminWebHost.AdminEmail, NewPassword);

        // The pre-renewal cookie is unexpired and due for renewal; it is rejected rather than renewed.
        using var replay = _host.NewBrowser();
        replay.UseCentralAdminCookie(original);
        AssertSignedOut(await replay.GetAsync("/admin"));

        // The renewed cookie carries the same revoked stamp; when it is itself due for renewal it is only deleted.
        _clock.Advance(TimeSpan.FromHours(7));
        AssertSignedOut(await browser.GetAsync("/admin"));
    }

    [Fact]
    public async Task SlidingRenewal_OfALegacyCookie_NeverHappens()
    {
        // A pre-WAS-37 cookie that is due for renewal is deleted, not re-issued with a fresh lifetime.
        using var browser = _host.NewBrowser();
        browser.UseCentralAdminCookie(_host.ProtectCentralAdminCookie(Claims(AdminId(AdminWebHost.AdminEmail).ToString(), stamp: null)));
        _clock.Advance(TimeSpan.FromHours(5));

        AssertSignedOut(await browser.GetAsync("/admin"));
    }

    // CentralDb only, no tenant fan-out ------------------------------------------------------------------------

    [Fact]
    public async Task Revalidation_ReadsCentralDbOncePerRequest_WithoutCredentials_AndNeverOpensATenantDatabase()
    {
        // A tenant database that cannot be opened: the Admin overview and the validation must not need it.
        _tenants.Override(_tenantId, _ => throw new InvalidOperationException("Tenant database must not be opened."));
        using var browser = await _host.SignedInAdminAsync();

        for (var i = 0; i < 3; i++)
        {
            _central.Counter.Reset();
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).Status);
            var validation = Assert.Single(_central.Counter.Commands, IsSessionValidation);
            Assert.DoesNotContain("\"PasswordHash\"", validation, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Email\"", validation, StringComparison.Ordinal);
        }

        ExecuteSql("UPDATE \"CentralAdminUsers\" SET \"IsActive\" = 0 WHERE \"NormalizedEmail\" = {0}", AdminWebHost.AdminEmail.ToUpperInvariant());
        _central.Counter.Reset();
        AssertSignedOut(await browser.GetAsync("/admin"));
        Assert.True(IsSessionValidation(Assert.Single(_central.Counter.Commands)));

        Assert.Empty(_tenants.Opened);
        Assert.Equal(0, _tenants.Counter.Count);
    }

    // Validation that cannot complete ----------------------------------------------------------------------

    [Fact]
    public async Task UnreadableAccountState_FailsClosed_WithoutAcceptingRenewingOrDeletingTheSession()
    {
        using var browser = await _host.SignedInAdminAsync("en-US");
        var issued = browser.CentralAdminCookie!.Value;
        _clock.Advance(TimeSpan.FromHours(5)); // due for sliding renewal
        RenameTable("CentralAdminUsers", "CentralAdminUsers_Unavailable");

        foreach (var path in ProtectedPages.Append("/admin/login"))
        {
            var failed = await browser.GetAsync(path);

            // The global error page (Program.cs's exception handling), with nothing from Admin and no SQL detail.
            Assert.Equal(HttpStatusCode.InternalServerError, failed.Status);
            Assert.Contains(Resource(".en-US", "Errors.UnexpectedTitle"), WebUtility.HtmlDecode(failed.Body), StringComparison.Ordinal);
            Assert.DoesNotContain("Alpha Kebap", failed.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("__RequestVerificationToken", failed.Body, StringComparison.Ordinal);
            foreach (var detail in new[] { "no such table", "CentralAdminUsers", "SQLite", "Exception", "SecurityStamp", "Data Source", AdminWebHost.AdminEmail })
                Assert.DoesNotContain(detail, failed.Body, StringComparison.OrdinalIgnoreCase);

            // The cookie is neither renewed nor deleted.
            Assert.Empty(AdminCookieHeaders(failed));
            Assert.Equal(issued, browser.CentralAdminCookie!.Value);
        }

        Assert.Empty(_tenants.Opened);

        // An outage does not sign the admin out: once CentralDb answers, the same session is validated and renewed.
        RenameTable("CentralAdminUsers_Unavailable", "CentralAdminUsers");
        var recovered = await browser.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, recovered.Status);
        Assert.NotEqual(issued, browser.CentralAdminCookie!.Value);
    }

    // Tenant authentication is unchanged ----------------------------------------------------------------------

    [Fact]
    public async Task TenantSession_IsNotPassedThroughCentralAdminRevalidation()
    {
        using var browser = _host.NewBrowser();
        await browser.SignInTenantUserAsync(_tenantId);

        _central.Counter.Reset();
        var page = await browser.GetAsync("/admin");

        Assert.Equal(HttpStatusCode.Redirect, page.Status);
        Assert.DoesNotContain(_central.Counter.Commands, IsSessionValidation);
        Assert.NotNull(browser.Cookies.SingleOrDefault(cookie => cookie.Name == TenantAuthCookieNames.Active));
    }

    [Fact]
    public async Task RejectedCentralAdminSession_LeavesTheTenantCookieAlone()
    {
        using var browser = await _host.SignedInAdminAsync();
        await browser.SignInTenantUserAsync(_tenantId);

        ChangePassword(AdminWebHost.AdminEmail, NewPassword);
        var page = await browser.GetAsync("/admin");

        AssertSignedOut(page);
        Assert.DoesNotContain(page.SetCookies!, header => header.StartsWith(TenantAuthCookieNames.Active + "=", StringComparison.Ordinal));
        Assert.NotNull(browser.Cookies.SingleOrDefault(cookie => cookie.Name == TenantAuthCookieNames.Active));
    }

    // Helpers -----------------------------------------------------------------------------------------------

    private static bool IsSessionValidation(string sql) => sql.Contains("FROM \"CentralAdminUsers\"", StringComparison.Ordinal);

    private static string Resource(string suffix, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource{suffix}.resx"))
            .Root!
            .Elements("data")
            .Single(e => e.Attribute("name")!.Value == key)
            .Element("value")!.Value;

    private static IReadOnlyList<string> AdminCookieHeaders(AdminResponse response) =>
        response.SetCookies!.Where(header => header.StartsWith(CentralAdminAuthCookieNames.Active + "=", StringComparison.Ordinal)).ToList();

    private static bool IsDeletion(string header) =>
        header.StartsWith(CentralAdminAuthCookieNames.Active + "=;", StringComparison.Ordinal)
        && header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);

    /// <summary>Challenged to the Admin login, the Central Admin cookie deleted, and no renewed ticket written.</summary>
    private static void AssertSignedOut(AdminResponse response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.Status);
        Assert.StartsWith("/admin/login", response.Location!.IsAbsoluteUri ? response.Location.PathAndQuery : response.Location.OriginalString, StringComparison.Ordinal);
        var headers = AdminCookieHeaders(response);
        Assert.NotEmpty(headers);
        Assert.All(headers, header => Assert.True(IsDeletion(header), header));
    }

    /// <summary>The claims of a Central Admin cookie; a null argument omits that claim.</summary>
    private static List<Claim> Claims(string? adminId, string? stamp, string? role = CentralAdminSessionClaims.Role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, AdminWebHost.AdminEmail),
            new(ClaimTypes.Name, "Ops")
        };
        if (adminId is not null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, adminId));
        if (role is not null)
            claims.Add(new Claim(ClaimTypes.Role, role));
        if (stamp is not null)
            claims.Add(new Claim(WaslaAuthContracts.CentralAdminSecurityStampClaim, stamp));
        return claims;
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

    /// <summary>The write the CLI password resets perform: a new hash and a new stamp.</summary>
    private void ChangePassword(string email, string password)
    {
        using var db = _central.CreateContext();
        var admin = Admin(db, email);
        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 4);
        admin.SecurityStamp = Guid.NewGuid();
        admin.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    private void ExecuteSql(string sql, params object[] parameters)
    {
        using var db = _central.CreateContext();
        db.Database.ExecuteSqlRaw(sql, parameters);
    }

    private void RenameTable(string from, string to)
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

    /// <summary>The Central Admin cookie handler's clock. It starts at the real time, so cookies issued now are current.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
