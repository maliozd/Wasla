using System.Data.Common;
using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Entities.Customer;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Tenant sessions after the account changes (WAS-89, audit F-02). Every test drives the tenant application over HTTP:
/// real login form, real cookie scheme, tenant resolved from the Host header, Program.cs role policies and the real
/// user-management controller and service over SQLite tenant databases. The attacker is an Owner who signed in, was then
/// deactivated, demoted, deleted or had the password changed by another Owner, and keeps the old cookie.
/// </summary>
public sealed class TenantSessionRevalidationTests : IAsyncLifetime
{
    private TenantSessionWebHost _host = null!;
    private AppUser _attacker = null!;
    private AppUser _victim = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TenantSessionWebHost.StartAsync();
        _attacker = _host.SeedUser(_host.AlphaId, "attacker@alpha.test", UserRole.Owner);
        _victim = _host.SeedUser(_host.AlphaId, "victim@alpha.test", UserRole.Owner);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    // --- The reported takeover --------------------------------------------------------------------

    [Fact]
    public async Task DeactivatedOwner_OldCookie_CannotReactivateItselfOrDeactivateAnotherOwner()
    {
        using var attacker = await SignedInAsync(_attacker);
        var attackerToken = await attacker.AntiforgeryTokenAsync($"/settings/users/{_attacker.Id}/edit");
        using var victim = await SignedInAsync(_victim);

        await DeactivateAsync(victim, _attacker.Id);
        Assert.False(_host.ReadUser(_host.AlphaId, _attacker.Id)!.IsActive);

        var list = await attacker.GetAsync("/settings/users");
        var restore = await attacker.PostFormAsync($"/settings/users/{_attacker.Id}/edit",
            EditForm(_attacker, UserRole.Owner, isActive: true, attackerToken));
        var takeover = await attacker.PostFormAsync($"/settings/users/{_victim.Id}/deactivate",
            [new("__RequestVerificationToken", attackerToken)]);

        Assert.False(_host.ReadUser(_host.AlphaId, _attacker.Id)!.IsActive);
        Assert.True(_host.ReadUser(_host.AlphaId, _victim.Id)!.IsActive);
        AssertRedirectsToLogin(list);
        AssertRedirectsToLogin(restore);
        AssertRedirectsToLogin(takeover);
    }

    [Fact]
    public async Task DemotedOwner_OldCookie_CannotRestoreOwnerRoleOrDemoteAnotherOwner()
    {
        using var attacker = await SignedInAsync(_attacker);
        var attackerToken = await attacker.AntiforgeryTokenAsync($"/settings/users/{_attacker.Id}/edit");
        using var victim = await SignedInAsync(_victim);

        await EditAsync(victim, _attacker, UserRole.Manager, isActive: true);
        Assert.Equal(UserRole.Manager, _host.ReadUser(_host.AlphaId, _attacker.Id)!.Role);

        var list = await attacker.GetAsync("/settings/users");
        var restore = await attacker.PostFormAsync($"/settings/users/{_attacker.Id}/edit",
            EditForm(_attacker, UserRole.Owner, isActive: true, attackerToken));
        var takeover = await attacker.PostFormAsync($"/settings/users/{_victim.Id}/edit",
            EditForm(_victim, UserRole.Viewer, isActive: true, attackerToken));

        Assert.Equal(UserRole.Manager, _host.ReadUser(_host.AlphaId, _attacker.Id)!.Role);
        Assert.Equal(UserRole.Owner, _host.ReadUser(_host.AlphaId, _victim.Id)!.Role);
        AssertRedirectsToLogin(list);
        AssertRedirectsToLogin(restore);
        AssertRedirectsToLogin(takeover);
    }

    [Fact]
    public async Task DeletedUser_OldCookie_IsRejected()
    {
        using var attacker = await SignedInAsync(_attacker);
        var attackerToken = await attacker.AntiforgeryTokenAsync($"/settings/users/{_victim.Id}/edit");

        _host.DeleteUser(_host.AlphaId, _attacker.Id);

        var list = await attacker.GetAsync("/settings/users");
        var takeover = await attacker.PostFormAsync($"/settings/users/{_victim.Id}/deactivate",
            [new("__RequestVerificationToken", attackerToken)]);

        Assert.True(_host.ReadUser(_host.AlphaId, _victim.Id)!.IsActive);
        AssertRedirectsToLogin(list);
        AssertRedirectsToLogin(takeover);
    }

    [Fact]
    public async Task PasswordChangedByAnotherOwner_OldCookie_IsRejected()
    {
        using var attacker = await SignedInAsync(_attacker);
        using var victim = await SignedInAsync(_victim);

        await EditAsync(victim, _attacker, UserRole.Owner, isActive: true, newPassword: "Another-Horse-27");

        AssertRedirectsToLogin(await attacker.GetAsync("/settings/users"));
    }

    [Fact]
    public async Task ReactivatedUser_OldCookie_StaysRevoked()
    {
        using var attacker = await SignedInAsync(_attacker);
        using var victim = await SignedInAsync(_victim);

        await DeactivateAsync(victim, _attacker.Id);
        await ActivateAsync(victim, _attacker.Id);
        Assert.True(_host.ReadUser(_host.AlphaId, _attacker.Id)!.IsActive);

        AssertRedirectsToLogin(await attacker.GetAsync("/settings/users"));

        // A new password login works again.
        using var again = await SignedInAsync(_attacker);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/settings/users")).StatusCode);
    }

    [Fact]
    public async Task RoleChangedDirectlyInTheDatabase_OldCookieRoleIsNotUsable()
    {
        using var attacker = await SignedInAsync(_attacker);

        // An operator edit that changes only the role column (no other account change).
        _host.UpdateUser(_host.AlphaId, _attacker.Id, user => user.Role = UserRole.Viewer);

        AssertRedirectsToLogin(await attacker.GetAsync("/settings/users"));
    }

    // --- Sessions that must keep working ----------------------------------------------------------

    [Fact]
    public async Task ActiveOwnerWithUnchangedRole_StaysAuthorized()
    {
        using var owner = await SignedInAsync(_attacker);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/settings/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/settings/users/{_victim.Id}")).StatusCode);
    }

    [Fact]
    public async Task ManagerSession_DoesNotSatisfyOwnerPolicy()
    {
        var manager = _host.SeedUser(_host.AlphaId, "manager@alpha.test", UserRole.Manager);
        using var client = await SignedInAsync(manager);

        var response = await client.GetAsync("/settings/users");

        AssertRedirectsTo(response, "/auth/access-denied");
        Assert.NotNull(client.AuthCookie);
    }

    [Fact]
    public async Task LoginAndLogout_StillWork()
    {
        using var owner = await SignedInAsync(_attacker);
        var token = await owner.AntiforgeryTokenAsync("/settings/users/create");

        var logout = await owner.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/auth/login", logout.Headers.Location!.OriginalString);
        Assert.Null(owner.AuthCookie);
        AssertRedirectsToLogin(await owner.GetAsync("/settings/users"));
    }

    // --- Another tenant ---------------------------------------------------------------------------

    [Fact]
    public async Task AlphaCookie_ReplayedOnBetaHost_IsRejectedWithoutOpeningBetaDatabase()
    {
        // Tenant B has an active Owner with the same user id and the same security stamp as the alpha session.
        _host.SeedUser(_host.BetaId, "attacker@beta.test", UserRole.Owner, id: _attacker.Id);
        _host.UpdateUser(_host.BetaId, _attacker.Id, user => user.SecurityStamp = _attacker.SecurityStamp);
        using var alpha = await SignedInAsync(_attacker);
        using var beta = alpha.ReplayTo(_host.BaseAddress, TenantSessionWebHost.BetaHost);
        var openedBefore = _host.Tenants.Opened.Count(id => id == _host.BetaId);

        var users = await beta.GetAsync("/settings/users");
        var accessDenied = await beta.GetAsync("/auth/access-denied");

        AssertRedirectsToLogin(users);
        AssertRedirectsToLogin(accessDenied);
        Assert.Equal(openedBefore, _host.Tenants.Opened.Count(id => id == _host.BetaId));
        Assert.Null(beta.AuthCookie);
        // The alpha session itself is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await alpha.GetAsync("/settings/users")).StatusCode);
    }

    // --- Cookies the login flow does not produce --------------------------------------------------

    public static TheoryData<string> MalformedSessions =>
    [
        "no-user-id", "malformed-user-id", "empty-user-id", "no-stamp", "empty-stamp", "malformed-stamp",
        "no-tenant-id", "malformed-tenant-id", "no-role", "unknown-role", "undefined-role-number", "user-id-claims-disagree"
    ];

    [Theory]
    [MemberData(nameof(MalformedSessions))]
    public async Task CookieWithMissingOrMalformedClaims_IsRejectedAndSignedOut(string defect)
    {
        var claims = OwnerClaims(_attacker);
        void Replace(string type, string? value)
        {
            claims.RemoveAll(c => c.Type == type);
            if (value is not null)
                claims.Add(new Claim(type, value));
        }

        switch (defect)
        {
            case "no-user-id": Replace(ClaimTypes.NameIdentifier, null); Replace("UserId", null); break;
            case "malformed-user-id": Replace(ClaimTypes.NameIdentifier, "not-a-guid"); break;
            case "empty-user-id": Replace(ClaimTypes.NameIdentifier, Guid.Empty.ToString()); break;
            // A cookie issued before security stamps existed.
            case "no-stamp": Replace(Wasla.Application.Security.WaslaAuthContracts.TenantSecurityStampClaim, null); break;
            case "empty-stamp": Replace(Wasla.Application.Security.WaslaAuthContracts.TenantSecurityStampClaim, Guid.Empty.ToString()); break;
            case "malformed-stamp": Replace(Wasla.Application.Security.WaslaAuthContracts.TenantSecurityStampClaim, "stamp"); break;
            case "no-tenant-id": Replace("TenantId", null); break;
            case "malformed-tenant-id": Replace("TenantId", "alpha"); break;
            case "no-role": Replace(ClaimTypes.Role, null); Replace("Role", null); break;
            case "unknown-role": Replace(ClaimTypes.Role, "Administrator"); break;
            case "undefined-role-number": Replace(ClaimTypes.Role, "99"); break;
            case "user-id-claims-disagree": Replace("UserId", _victim.Id.ToString()); break;
        }

        using var client = ForgedClient(claims, DateTimeOffset.UtcNow);
        var openedBefore = _host.Tenants.Opened.Count;

        var response = await client.GetAsync("/settings/users");

        AssertRedirectsToLogin(response);
        Assert.Null(client.AuthCookie);
        Assert.Equal(openedBefore, _host.Tenants.Opened.Count);
    }

    [Fact]
    public async Task ForgedCookieWithCurrentClaims_StillWorks()
    {
        // The same claims as a login issues: the malformed cases above differ from a working session in one claim.
        using var client = ForgedClient(OwnerClaims(_attacker), DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings/users")).StatusCode);
    }

    // --- Sliding renewal --------------------------------------------------------------------------

    [Fact]
    public async Task SlidingRenewal_ReissuesAValidSession()
    {
        // Issued five days ago: renewal is due after half of the seven-day lifetime.
        using var client = ForgedClient(OwnerClaims(_attacker), DateTimeOffset.UtcNow.AddDays(-5));
        var original = client.AuthCookie;

        var response = await client.GetAsync("/settings/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(client.AuthCookie);
        Assert.NotEqual(original, client.AuthCookie);
    }

    [Fact]
    public async Task SlidingRenewal_DoesNotReissueAStaleSession()
    {
        using var victim = await SignedInAsync(_victim);
        await EditAsync(victim, _attacker, UserRole.Manager, isActive: true);

        // An Owner session issued five days ago (renewal is due after half of the seven-day lifetime).
        using var stale = _host.Client(TenantSessionWebHost.AlphaHost);
        stale.Cookies[TenantAuthCookieNames.Active] = _host.ProtectTenantCookie(
            OwnerClaims(_attacker),
            DateTimeOffset.UtcNow.AddDays(-5),
            DateTimeOffset.UtcNow.AddDays(2));

        var response = await stale.GetAsync("/settings/users");

        AssertRedirectsToLogin(response);
        Assert.Null(stale.AuthCookie);
        Assert.Single(stale.LastSetCookies, c => c.StartsWith(TenantAuthCookieNames.Active + "=;", StringComparison.Ordinal));
    }

    // --- Requests without a resolved tenant -------------------------------------------------------

    [Theory]
    [InlineData("/tenant-not-found?host=x.wasla.local")]
    [InlineData("/culture?culture=en-US&returnUrl=%2F")]
    public async Task PathWithoutTenantResolution_NeitherAcceptsNorDeletesTheSession(string path)
    {
        using var owner = await SignedInAsync(_attacker);
        var cookie = owner.AuthCookie;

        await owner.GetAsync(path);

        Assert.DoesNotContain(owner.LastSetCookies, c => c.StartsWith(TenantAuthCookieNames.Active + "=", StringComparison.Ordinal));
        Assert.Equal(cookie, owner.AuthCookie);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/settings/users")).StatusCode);
    }

    // --- Tenant database unavailable --------------------------------------------------------------

    [Fact]
    public async Task TenantDatabaseFailure_FailsClosedWithoutDetailsAndKeepsTheSession()
    {
        using var owner = await SignedInAsync(_attacker);
        var cookie = owner.AuthCookie;
        _host.Tenants.Override(_host.AlphaId, _ => throw new SimulatedDbException());

        var read = await owner.GetAsync("/settings/users");
        var write = await owner.PostFormAsync($"/settings/users/{_victim.Id}/deactivate", [new("__RequestVerificationToken", "x")]);

        Assert.Equal(HttpStatusCode.InternalServerError, read.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, write.StatusCode);
        var body = await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(SimulatedDbException.Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.Logs.Entries, e => e.Contains(SimulatedDbException.Secret, StringComparison.Ordinal));
        Assert.Contains(_host.Logs.Entries, e => e.Contains("Tenant session could not be validated", StringComparison.Ordinal)
                                                 && e.Contains(nameof(SimulatedDbException), StringComparison.Ordinal));
        Assert.Equal(cookie, owner.AuthCookie);

        _host.Tenants.Override(_host.AlphaId, _ => Task.FromResult(new Wasla.Infrastructure.Persistence.Tenant.TenantDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Wasla.Infrastructure.Persistence.Tenant.TenantDbContext>()
                .UseSqlite(_host.Tenants.ConnectionStringFor(_host.AlphaId)).Options)));
        Assert.True(_host.ReadUser(_host.AlphaId, _victim.Id)!.IsActive);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/settings/users")).StatusCode);
    }

    // --- Redirects --------------------------------------------------------------------------------

    [Fact]
    public async Task RejectedSessionOnLogout_DoesNotRedirectOffHost()
    {
        using var attacker = await SignedInAsync(_attacker);
        var token = await attacker.AntiforgeryTokenAsync("/settings/users/create");
        _host.UpdateUser(_host.AlphaId, _attacker.Id, user => user.IsActive = false);

        var response = await attacker.PostFormAsync(
            "/auth/logout?ReturnUrl=https%3A%2F%2Fevil.example%2F",
            [new("__RequestVerificationToken", token)]);

        AssertRedirectsToLogin(response);
        Assert.DoesNotContain("evil.example", response.Headers.Location!.Host, StringComparison.Ordinal);
        Assert.Null(attacker.AuthCookie);
    }

    [Fact]
    public async Task RejectedSession_LoginPageRendersWithoutLoop()
    {
        using var attacker = await SignedInAsync(_attacker);
        _host.UpdateUser(_host.AlphaId, _attacker.Id, user => user.IsActive = false);

        var protectedPage = await attacker.GetAsync("/settings/users");
        var loginPage = await attacker.GetAsync("/auth/login");

        AssertRedirectsToLogin(protectedPage);
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
    }

    // --- The Owner's own account and legitimate management ---------------------------------------

    [Fact]
    public async Task Owner_CannotDeactivateThemselves()
    {
        using var owner = await SignedInAsync(_attacker);

        await ToggleAsync(owner, _attacker.Id, "deactivate");

        var row = _host.ReadUser(_host.AlphaId, _attacker.Id)!;
        Assert.Equal((true, _attacker.SecurityStamp), (row.IsActive, row.SecurityStamp));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/settings/users")).StatusCode);
    }

    [Theory]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Owner, false)]
    public async Task Owner_CannotChangeOwnRoleOrActiveState_AndSeesALocalizedReason(UserRole role, bool isActive)
    {
        using var owner = await SignedInAsync(_attacker);
        var token = await owner.AntiforgeryTokenAsync($"/settings/users/{_attacker.Id}/edit");

        var response = await owner.PostFormAsync($"/settings/users/{_attacker.Id}/edit", EditForm(_attacker, role, isActive, token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("You cannot change your own role or active status.", body, StringComparison.Ordinal);
        var row = _host.ReadUser(_host.AlphaId, _attacker.Id)!;
        Assert.Equal((UserRole.Owner, true, _attacker.SecurityStamp), (row.Role, row.IsActive, row.SecurityStamp));
    }

    [Fact]
    public async Task Owner_ChangingOwnPassword_EndsTheOldSession_AndTheNewPasswordSignsIn()
    {
        using var owner = await SignedInAsync(_attacker);

        await EditAsync(owner, _attacker, UserRole.Owner, isActive: true, newPassword: "Another-Horse-27");

        AssertRedirectsToLogin(await owner.GetAsync("/settings/users"));
        using var again = _host.Client(TenantSessionWebHost.AlphaHost);
        await again.LoginAsync(_attacker.Email, "Another-Horse-27");
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/settings/users")).StatusCode);
    }

    [Fact]
    public async Task Owner_ManagesAnotherUser_AndThatUsersSessionEnds()
    {
        var manager = _host.SeedUser(_host.AlphaId, "manager@alpha.test", UserRole.Manager);
        using var managerClient = await SignedInAsync(manager);
        // A page every signed-in tenant user may open.
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync("/auth/access-denied")).StatusCode);
        using var owner = await SignedInAsync(_attacker);

        await EditAsync(owner, manager, UserRole.Kitchen, isActive: true);

        Assert.Equal(UserRole.Kitchen, _host.ReadUser(_host.AlphaId, manager.Id)!.Role);
        AssertRedirectsToLogin(await managerClient.GetAsync("/auth/access-denied"));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/settings/users")).StatusCode);
    }

    [Fact]
    public async Task SoleOwner_RemainsTheActiveOwner()
    {
        _host.DeleteUser(_host.AlphaId, _victim.Id);
        using var owner = await SignedInAsync(_attacker);
        var token = await owner.AntiforgeryTokenAsync($"/settings/users/{_attacker.Id}/edit");

        await owner.PostFormAsync($"/settings/users/{_attacker.Id}/edit", EditForm(_attacker, UserRole.Manager, isActive: true, token));
        await ToggleAsync(owner, _attacker.Id, "deactivate");

        var row = _host.ReadUser(_host.AlphaId, _attacker.Id)!;
        Assert.Equal((UserRole.Owner, true), (row.Role, row.IsActive));
    }

    // --- Signup welcome link ----------------------------------------------------------------------

    [Fact]
    public async Task WelcomeLink_SignsInWithTheCurrentRoleAndRefusesAnInactiveUser()
    {
        using var scope = _host.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ISignupCompletionTokenService>();
        var demotedLink = tokens.CreateToken(new SignupCompletionPayload(_host.AlphaId, _attacker.Id, _attacker.Email, _attacker.FullName, UserRole.Owner));
        var inactiveLink = tokens.CreateToken(new SignupCompletionPayload(_host.AlphaId, _victim.Id, _victim.Email, _victim.FullName, UserRole.Owner));
        _host.UpdateUser(_host.AlphaId, _attacker.Id, user => user.Role = UserRole.Manager);
        _host.UpdateUser(_host.AlphaId, _victim.Id, user => user.IsActive = false);

        using var demoted = _host.Client(TenantSessionWebHost.AlphaHost);
        var signedIn = await demoted.GetAsync($"/auth/welcome?token={Uri.EscapeDataString(demotedLink)}");
        using var inactive = _host.Client(TenantSessionWebHost.AlphaHost);
        var refused = await inactive.GetAsync($"/auth/welcome?token={Uri.EscapeDataString(inactiveLink)}");

        AssertRedirectsTo(signedIn, "/dashboard");
        AssertRedirectsTo(await demoted.GetAsync("/settings/users"), "/auth/access-denied");
        AssertRedirectsToLogin(refused);
        Assert.Null(inactive.AuthCookie);
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private TenantSessionClient ForgedClient(IEnumerable<Claim> claims, DateTimeOffset issuedUtc)
    {
        var client = _host.Client(TenantSessionWebHost.AlphaHost);
        client.Cookies[TenantAuthCookieNames.Active] = _host.ProtectTenantCookie(claims, issuedUtc, issuedUtc.AddDays(7));
        return client;
    }

    private sealed class SimulatedDbException() : DbException(Secret)
    {
        public const string Secret = "Server=tcp:secret-sql.example;Password=CONNSTR-SECRET-7f3a";
    }

    private async Task<TenantSessionClient> SignedInAsync(AppUser user, string host = TenantSessionWebHost.AlphaHost)
    {
        var client = _host.Client(host);
        await client.LoginAsync(user.Email);
        return client;
    }

    /// <summary>The claims a login issued for this Owner, with the security stamp the user was seeded with.</summary>
    private List<Claim> OwnerClaims(AppUser user) =>
    [
        .. TenantSessionClaims.Create(_host.AlphaId, user.Id, UserRole.Owner, user.SecurityStamp),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Name, user.FullName)
    ];

    private static async Task EditAsync(TenantSessionClient owner, AppUser target, UserRole role, bool isActive, string? newPassword = null)
    {
        var token = await owner.AntiforgeryTokenAsync($"/settings/users/{target.Id}/edit");
        var response = await owner.PostFormAsync($"/settings/users/{target.Id}/edit",
            EditForm(target, role, isActive, token, newPassword));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/settings/users/{target.Id}", response.Headers.Location!.OriginalString);
    }

    private static Task DeactivateAsync(TenantSessionClient owner, Guid targetId) => ToggleAsync(owner, targetId, "deactivate");

    private static Task ActivateAsync(TenantSessionClient owner, Guid targetId) => ToggleAsync(owner, targetId, "activate");

    private static async Task ToggleAsync(TenantSessionClient owner, Guid targetId, string action)
    {
        var token = await owner.AntiforgeryTokenAsync($"/settings/users/{targetId}");
        var response = await owner.PostFormAsync($"/settings/users/{targetId}/{action}", [new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/settings/users/{targetId}", response.Headers.Location!.OriginalString);
    }

    private static List<KeyValuePair<string, string>> EditForm(AppUser user, UserRole role, bool isActive, string token, string? newPassword = null) =>
    [
        new("Id", user.Id.ToString()),
        new("Email", user.Email),
        new("FullName", user.FullName),
        new("Role", role.ToString()),
        new("IsActive", isActive ? "true" : "false"),
        new("NewPassword", newPassword ?? string.Empty),
        new("ConfirmPassword", newPassword ?? string.Empty),
        new("__RequestVerificationToken", token)
    ];

    private static void AssertRedirectsToLogin(HttpResponseMessage response) => AssertRedirectsTo(response, "/auth/login");

    /// <summary>
    /// The cookie handler answers a challenge with an absolute URL on the request's own host; controllers answer
    /// with a local path. Either way the redirect stays on the tenant host.
    /// </summary>
    private static void AssertRedirectsTo(HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        if (location.IsAbsoluteUri)
        {
            Assert.Equal(response.RequestMessage!.Headers.Host, location.Authority);
            location = new Uri(location.PathAndQuery, UriKind.Relative);
        }

        Assert.StartsWith(path, location.OriginalString, StringComparison.Ordinal);
    }
}
