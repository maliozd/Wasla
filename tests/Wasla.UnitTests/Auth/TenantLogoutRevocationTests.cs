using System.Data.Common;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Security;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.Web.Security;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Tenant logout ends the server-side login session it logs out (WAS-114), and a logout that cannot complete says so
/// and can be tried again (WAS-186). Every test drives the tenant application over HTTP: the real login form and
/// welcome link, the real cookie scheme with its per-request session check, the tenant resolved from the Host header,
/// the real logout page and logout action with its antiforgery check, and the logout failure middleware, over SQLite
/// tenant databases. Cookies are kept in memory only: no assertion message or test output contains a cookie, header,
/// password or token.
/// </summary>
public sealed class TenantLogoutRevocationTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private TenantSessionWebHost _host = null!;
    private AppUser _owner = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await TenantSessionWebHost.StartAsync();
        _owner = _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    // --- The reported replay ----------------------------------------------------------------------

    [Fact]
    public async Task CookieCopiedBeforeLogout_IsRejectedAfterLogout()
    {
        using var browser = _host.Client(TenantSessionWebHost.AlphaHost);
        await browser.LoginAsync(_owner.Email);
        var copied = browser.AuthCookie!;
        var sessionId = SessionIdOf(copied);
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");

        var logout = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/auth/login", logout.Headers.Location!.OriginalString);
        AssertSignedOut(browser);
        var revoked = Assert.Single(_host.ReadSessions(_host.AlphaId));
        Assert.Equal((sessionId, _owner.Id), (revoked.Id, revoked.UserId));
        Assert.NotNull(revoked.RevokedAtUtc);

        using var replay = CookieClient(TenantSessionWebHost.AlphaHost, copied);
        var response = await replay.GetAsync("/settings/users");

        AssertRedirectsToLogin(response);
        AssertSignedOut(replay);
        AssertRejectionLogged(TenantSessionState.SessionRevoked);
        // The rejection wrote nothing: still one session, revoked at the same time.
        Assert.Equal(revoked.RevokedAtUtc, Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
        AssertLogsHold(copied, sessionId.ToString());
    }

    // --- Several sessions, users and tenants ------------------------------------------------------

    [Fact]
    public async Task Logout_EndsOnlyItsOwnSession_AnotherSessionOfTheUserAndAFreshLoginKeepWorking()
    {
        using var first = await SignedInAsync(_owner);
        using var second = await SignedInAsync(_owner);
        var firstCookie = first.AuthCookie!;

        await LogoutAsync(first);

        using var firstReplay = CookieClient(TenantSessionWebHost.AlphaHost, firstCookie);
        AssertRedirectsToLogin(await firstReplay.GetAsync("/settings/users"));
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/settings/users")).StatusCode);
        using var third = await SignedInAsync(_owner);
        Assert.Equal(HttpStatusCode.OK, (await third.GetAsync("/settings/users")).StatusCode);

        var sessions = _host.ReadSessions(_host.AlphaId);
        Assert.Equal(3, sessions.Select(s => s.Id).Distinct().Count());
        Assert.Equal([SessionIdOf(firstCookie)], sessions.Where(s => s.RevokedAtUtc is not null).Select(s => s.Id));
    }

    [Fact]
    public async Task OtherUsersAndTheOtherTenant_AreUnaffectedByALogout()
    {
        var manager = _host.SeedUser(_host.AlphaId, "manager@alpha.test", UserRole.Manager);
        var betaOwner = _host.SeedUser(_host.BetaId, "owner@beta.test", UserRole.Owner);
        using var owner = await SignedInAsync(_owner);
        using var alphaManager = await SignedInAsync(manager);
        using var beta = await SignedInAsync(betaOwner, TenantSessionWebHost.BetaHost);

        await LogoutAsync(owner);

        Assert.Equal(HttpStatusCode.OK, (await alphaManager.GetAsync("/auth/access-denied")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await beta.GetAsync("/settings/users")).StatusCode);
        Assert.All(_host.ReadSessions(_host.AlphaId).Where(s => s.UserId == manager.Id), s => Assert.Null(s.RevokedAtUtc));
        Assert.Null(Assert.Single(_host.ReadSessions(_host.BetaId)).RevokedAtUtc);
    }

    [Fact]
    public async Task CookieReplayedOnAnotherTenantsHost_NeitherAuthenticatesNorEndsTheSession()
    {
        using var alpha = await SignedInAsync(_owner);
        var token = await alpha.AntiforgeryTokenAsync("/settings/users/create");
        using var beta = alpha.ReplayTo(_host.BaseAddress, TenantSessionWebHost.BetaHost);
        var betaOpened = _host.Tenants.Opened.Count(id => id == _host.BetaId);

        var page = await beta.GetAsync("/settings/users");
        using var betaLogout = alpha.ReplayTo(_host.BaseAddress, TenantSessionWebHost.BetaHost);
        var logout = await betaLogout.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        AssertRedirectsToLogin(page);
        AssertSignedOut(beta);
        // The logout was challenged as anonymous on that host, not performed.
        AssertRedirectsToLogin(logout);
        Assert.Contains("ReturnUrl=", logout.Headers.Location!.Query, StringComparison.Ordinal);
        Assert.Equal(betaOpened, _host.Tenants.Opened.Count(id => id == _host.BetaId));
        // The signed-out cookie on the wrong host did not end the session on its own host.
        Assert.Null(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await alpha.GetAsync("/settings/users")).StatusCode);
    }

    [Fact]
    public async Task SameUserAndSessionIdsInAnotherTenantDatabase_DoNotCrossTenants()
    {
        // Tenant B has the same user id and stamp, and a stored session with the same id as the alpha login's.
        _host.SeedUser(_host.BetaId, "owner@beta.test", UserRole.Owner, id: _owner.Id);
        _host.UpdateUser(_host.BetaId, _owner.Id, user => user.SecurityStamp = _owner.SecurityStamp);
        using var alpha = await SignedInAsync(_owner);
        var alphaCookie = alpha.AuthCookie!;
        var sessionId = SessionIdOf(alphaCookie);
        _host.SeedSession(_host.BetaId, _owner.Id, id: sessionId);
        using var beta = CookieClient(TenantSessionWebHost.BetaHost, _host.ProtectTenantCookie(
            SessionClaims(_host.BetaId, _owner, sessionId), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7)));
        Assert.Equal(HttpStatusCode.OK, (await beta.GetAsync("/settings/users")).StatusCode);

        await LogoutAsync(alpha);

        Assert.NotNull(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
        Assert.Null(Assert.Single(_host.ReadSessions(_host.BetaId)).RevokedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await beta.GetAsync("/settings/users")).StatusCode);
        using var alphaReplay = CookieClient(TenantSessionWebHost.AlphaHost, alphaCookie);
        AssertRedirectsToLogin(await alphaReplay.GetAsync("/settings/users"));
    }

    [Fact]
    public async Task SessionIdOfAnotherUser_IsRejected_AndThatUsersSessionIsUnaffected()
    {
        var manager = _host.SeedUser(_host.AlphaId, "manager@alpha.test", UserRole.Manager);
        using var managerClient = await SignedInAsync(manager);
        var managerSession = SessionIdOf(managerClient.AuthCookie!);

        // The Owner's own claims, naming the Manager's stored, active session.
        using var forged = CookieClient(TenantSessionWebHost.AlphaHost, _host.ProtectTenantCookie(
            SessionClaims(_host.AlphaId, _owner, managerSession), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7)));

        AssertRedirectsToLogin(await forged.GetAsync("/settings/users"));
        AssertSignedOut(forged);
        AssertRejectionLogged(TenantSessionState.SessionNotFound);
        Assert.Null(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync("/auth/access-denied")).StatusCode);
    }

    // --- Sign-in paths and lifetimes --------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordLogin_StoresADistinctSession_InBothRememberMeModes_AndLogoutEndsIt(bool rememberMe)
    {
        using var first = _host.Client(TenantSessionWebHost.AlphaHost);
        await first.LoginAsync(_owner.Email, rememberMe: rememberMe);
        var issued = AuthSetCookie(first);
        using var second = _host.Client(TenantSessionWebHost.AlphaHost);
        await second.LoginAsync(_owner.Email, rememberMe: rememberMe);

        // The cookie contract is unchanged: a persistent 14-day cookie with remember-me, a browser-session cookie without.
        Assert.Equal(rememberMe, issued.Expires is not null);
        var ticket = _host.UnprotectTenantCookie(first.AuthCookie!)!;
        Assert.Equal(rememberMe, ticket.Properties.IsPersistent);
        var lifetime = ticket.Properties.ExpiresUtc!.Value - ticket.Properties.IssuedUtc!.Value;
        Assert.Equal(rememberMe ? AuthCookiePersistence.TenantPersistentDuration : TimeSpan.FromDays(7), lifetime);
        Assert.True(issued.HttpOnly, "The tenant cookie must stay HttpOnly.");
        Assert.True(issued.Domain.Length == 0, "The tenant cookie must stay host-only.");

        var ids = new[] { SessionIdOf(first.AuthCookie!), SessionIdOf(second.AuthCookie!) };
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(ids.Order(), _host.ReadSessions(_host.AlphaId).Select(s => s.Id).Order());
        Assert.All(_host.ReadSessions(_host.AlphaId), s => Assert.Equal(_owner.Id, s.UserId));

        var firstCookie = first.AuthCookie!;
        await LogoutAsync(first);
        using var replay = CookieClient(TenantSessionWebHost.AlphaHost, firstCookie);
        AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/settings/users")).StatusCode);
    }

    [Fact]
    public async Task WelcomeLink_StoresASession_ThatLogoutEnds()
    {
        using var scope = _host.Services.CreateScope();
        var link = scope.ServiceProvider.GetRequiredService<ISignupCompletionTokenService>()
            .CreateToken(new SignupCompletionPayload(_host.AlphaId, _owner.Id, _owner.Email, _owner.FullName, UserRole.Owner));
        using var welcomed = _host.Client(TenantSessionWebHost.AlphaHost);

        var signedIn = await welcomed.GetAsync($"/auth/welcome?token={Uri.EscapeDataString(link)}");

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/dashboard", signedIn.Headers.Location!.OriginalString);
        var copied = welcomed.AuthCookie!;
        var stored = Assert.Single(_host.ReadSessions(_host.AlphaId));
        Assert.Equal((SessionIdOf(copied), _owner.Id), (stored.Id, stored.UserId));
        Assert.Equal(HttpStatusCode.OK, (await welcomed.GetAsync("/settings/users")).StatusCode);

        await LogoutAsync(welcomed);

        using var replay = CookieClient(TenantSessionWebHost.AlphaHost, copied);
        AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
    }

    [Fact]
    public async Task SlidingRenewal_KeepsTheSession_AndLogoutRejectsTheOriginalAndTheRenewedCookie()
    {
        using var browser = await SignedInAsync(_owner);
        var original = browser.AuthCookie!;
        var sessionId = SessionIdOf(original);
        // The same login's claims, issued five days ago: renewal is due after half of the seven-day lifetime.
        using var aged = CookieClient(TenantSessionWebHost.AlphaHost, Aged(original, TimeSpan.FromDays(5)));
        var agedCookie = aged.AuthCookie!;

        Assert.Equal(HttpStatusCode.OK, (await aged.GetAsync("/settings/users")).StatusCode);
        var renewed = aged.AuthCookie!;
        Assert.False(string.Equals(agedCookie, renewed, StringComparison.Ordinal), "A renewal-due session must be re-issued.");
        Assert.Equal(sessionId, SessionIdOf(renewed));
        Assert.Single(_host.ReadSessions(_host.AlphaId));

        await LogoutAsync(browser);

        foreach (var copy in new[] { original, agedCookie, renewed })
        {
            using var replay = CookieClient(TenantSessionWebHost.AlphaHost, copy);
            AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
            AssertSignedOut(replay);
        }

        Assert.Single(_host.ReadSessions(_host.AlphaId));
    }

    [Fact]
    public async Task ExpiredTicket_IsRejected_ThoughItsSessionIsStoredAndNotRevoked()
    {
        var sessionId = _host.SeedSession(_host.AlphaId, _owner.Id);
        var claims = SessionClaims(_host.AlphaId, _owner, sessionId);
        var now = DateTimeOffset.UtcNow;
        using var current = CookieClient(TenantSessionWebHost.AlphaHost, _host.ProtectTenantCookie(claims, now, now.AddDays(7)));
        // Past the seven-day default and past the fourteen-day remember-me lifetime.
        using var expiredDefault = CookieClient(TenantSessionWebHost.AlphaHost, _host.ProtectTenantCookie(claims, now.AddDays(-8), now.AddDays(-1)));
        using var expiredPersistent = CookieClient(TenantSessionWebHost.AlphaHost, _host.ProtectTenantCookie(claims, now.AddDays(-15), now.AddDays(-1)));

        AssertRedirectsToLogin(await expiredDefault.GetAsync("/settings/users"));
        AssertRedirectsToLogin(await expiredPersistent.GetAsync("/settings/users"));
        // The same session in a ticket that has not expired is accepted, so the expiry alone rejected the others.
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/settings/users")).StatusCode);
        var stored = Assert.Single(_host.ReadSessions(_host.AlphaId));
        Assert.Equal(sessionId, stored.Id);
        Assert.Null(stored.RevokedAtUtc);
    }

    // --- Cookies that name no valid session -------------------------------------------------------

    public static TheoryData<string> InvalidSessionIds =>
        ["missing", "empty", "malformed", "uppercase", "duplicate", "unknown", "revoked"];

    [Theory]
    [MemberData(nameof(InvalidSessionIds))]
    public async Task CookieNamingNoActiveStoredSession_IsRejected_AndNothingIsWritten(string defect)
    {
        var active = _host.SeedSession(_host.AlphaId, _owner.Id);
        var revokedAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        var revoked = _host.SeedSession(_host.AlphaId, _owner.Id, revokedAtUtc: revokedAt);
        var claims = SessionClaims(_host.AlphaId, _owner, active);
        void Replace(string? value)
        {
            claims.RemoveAll(c => c.Type == WaslaAuthContracts.TenantSessionIdClaim);
            if (value is not null)
                claims.Add(new Claim(WaslaAuthContracts.TenantSessionIdClaim, value));
        }

        switch (defect)
        {
            case "missing": Replace(null); break;
            case "empty": Replace(Guid.Empty.ToString()); break;
            case "malformed": Replace("session"); break;
            case "uppercase": Replace(active.ToString().ToUpperInvariant()); break;
            case "duplicate": claims.Add(new Claim(WaslaAuthContracts.TenantSessionIdClaim, active.ToString())); break;
            case "unknown": Replace(Guid.NewGuid().ToString()); break;
            case "revoked": Replace(revoked.ToString()); break;
        }

        var before = _host.ReadSessions(_host.AlphaId);
        using var client = CookieClient(TenantSessionWebHost.AlphaHost,
            _host.ProtectTenantCookie(claims, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7)));

        AssertRedirectsToLogin(await client.GetAsync("/settings/users"));
        AssertSignedOut(client);

        // No session was created, reactivated or changed.
        var after = _host.ReadSessions(_host.AlphaId);
        Assert.Equal(before.Select(s => (s.Id, s.UserId, s.RevokedAtUtc)), after.Select(s => (s.Id, s.UserId, s.RevokedAtUtc)));
        Assert.Equal(revokedAt, after.Single(s => s.Id == revoked).RevokedAtUtc);
        if (defect == "unknown")
            AssertRejectionLogged(TenantSessionState.SessionNotFound);
        if (defect == "revoked")
            AssertRejectionLogged(TenantSessionState.SessionRevoked);
    }

    // --- Logout requests --------------------------------------------------------------------------

    public static TheoryData<string> RefusedLogouts => ["get", "no-token", "invalid-token", "other-session-token"];

    [Theory]
    [MemberData(nameof(RefusedLogouts))]
    public async Task LogoutWithoutAValidAntiforgeryToken_OrByGet_DoesNotEndTheSession(string kind)
    {
        using var owner = await SignedInAsync(_owner);
        using var other = await SignedInAsync(_host.SeedUser(_host.AlphaId, "other@alpha.test", UserRole.Owner));
        var otherToken = await other.AntiforgeryTokenAsync("/settings/users/create");

        var response = kind switch
        {
            "get" => await owner.GetAsync("/auth/logout"),
            "no-token" => await owner.PostFormAsync("/auth/logout", []),
            "invalid-token" => await owner.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", "not-a-token")]),
            _ => await owner.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", otherToken)])
        };

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(owner.AuthCookie is not null, "A refused logout must leave the browser's cookie in place.");
        Assert.All(_host.ReadSessions(_host.AlphaId), s => Assert.Null(s.RevokedAtUtc));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/settings/users")).StatusCode);
    }

    [Fact]
    public async Task RepeatedLogoutWithACopiedCookie_IsChallenged_AndKeepsTheFirstRevocation()
    {
        using var browser = await SignedInAsync(_owner);
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        using var copy = browser.ReplayTo(_host.BaseAddress, TenantSessionWebHost.AlphaHost);
        await LogoutAsync(browser, token);
        var first = Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc;

        var again = await copy.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        AssertRedirectsToLogin(again);
        Assert.Contains("ReturnUrl=", again.Headers.Location!.Query, StringComparison.Ordinal);
        AssertSignedOut(copy);
        Assert.Equal(first, Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
    }

    // --- Storage failures -------------------------------------------------------------------------

    [Fact]
    public async Task SessionCreationFailure_IssuesNoCookie_AndRevealsNothing()
    {
        FailAlphaCommands(command => command.CommandText.Contains("INSERT INTO \"TenantSessions\"", StringComparison.Ordinal));
        using var client = _host.Client(TenantSessionWebHost.AlphaHost);
        var token = await client.AntiforgeryTokenAsync("/auth/login");

        var response = await client.PostFormAsync("/auth/login",
        [
            new("Email", _owner.Email),
            new("Password", TenantSessionWebHost.Password),
            new("RememberMe", "true"),
            new("__RequestVerificationToken", token)
        ]);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertSignedOut(client);
        // SaveChanges wraps the failed INSERT; only the outer exception type is logged.
        await AssertRevealsNothingAsync(response, "Tenant session could not be created", nameof(DbUpdateException));
        UsePlainAlphaDatabase();
        Assert.Empty(_host.ReadSessions(_host.AlphaId));
        Assert.Null(_host.ReadUser(_host.AlphaId, _owner.Id)!.LastLoginAt);

        // Once the database works again, the same credentials sign in.
        using var again = await SignedInAsync(_owner);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/settings/users")).StatusCode);
    }

    /// <summary>
    /// The revocation cannot be stored (WAS-186): the logout failure page, not a success redirect, and the cookie stays.
    /// Its "Try again" is the logout page, a GET that ends no session; posting from it fails the same way while storage
    /// fails, and logs out once storage works again. Only then is the copied cookie rejected. Another session of the user
    /// and the other tenant are untouched throughout.
    /// </summary>
    [Fact]
    public async Task RevocationFailure_ShowsTheFailurePage_KeepsTheCookie_AndTryAgainLogsOutOnceStorageWorks()
    {
        var betaOwner = _host.SeedUser(_host.BetaId, "owner@beta.test", UserRole.Owner);
        using var browser = await SignedInAsync(_owner);
        using var otherDevice = await SignedInAsync(_owner);
        using var beta = await SignedInAsync(betaOwner, TenantSessionWebHost.BetaHost);
        var copied = browser.AuthCookie!;
        var sessionId = SessionIdOf(copied);
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        FailAlphaCommands(command => command.CommandText.Contains("UPDATE \"TenantSessions\"", StringComparison.Ordinal));

        var failed = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        await AssertLogoutFailurePageAsync(failed, browser);
        Assert.True(string.Equals(copied, browser.AuthCookie, StringComparison.Ordinal), "A failed logout must keep the browser's cookie.");
        await AssertRevealsNothingAsync(failed, "Tenant session could not be revoked");
        await AssertBodyHoldsAsync(failed, copied, sessionId.ToString());
        AssertLogsHold(copied, sessionId.ToString());
        Assert.All(_host.ReadSessions(_host.AlphaId), s => Assert.Null(s.RevokedAtUtc));
        // Nothing claimed the copy was invalidated, and it was not.
        using var replay = CookieClient(TenantSessionWebHost.AlphaHost, copied);
        Assert.Equal(HttpStatusCode.OK, (await replay.GetAsync("/settings/users")).StatusCode);

        // Try again while storage still fails: the logout page opens (the session can be checked) and its form fails alike.
        await AssertLogoutFailurePageAsync(await TryLogoutAgainAsync(browser), browser);
        Assert.All(_host.ReadSessions(_host.AlphaId), s => Assert.Null(s.RevokedAtUtc));

        UsePlainAlphaDatabase();
        var retried = await TryLogoutAgainAsync(browser);

        Assert.Equal(HttpStatusCode.Redirect, retried.StatusCode);
        Assert.Equal("/auth/login", retried.Headers.Location!.OriginalString);
        AssertSignedOut(browser);
        Assert.Equal([sessionId], _host.ReadSessions(_host.AlphaId).Where(s => s.RevokedAtUtc is not null).Select(s => s.Id));
        AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/settings/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await beta.GetAsync("/settings/users")).StatusCode);
        Assert.Null(Assert.Single(_host.ReadSessions(_host.BetaId)).RevokedAtUtc);
    }

    /// <summary>
    /// The revocation was stored, but the database reported a failure afterwards: the outcome is unknown to the server,
    /// so the page is the same and claims neither outcome. "Try again" finds the session already ended: the cookie scheme
    /// signs the browser out and sends it to the login page, which shows its form. Signing in from there does not
    /// return to logout, so nothing loops. The first revocation time is kept.
    /// </summary>
    [Fact]
    public async Task RevocationStoredButReportedAsFailed_TryAgainLandsOnTheLoginPage_WithoutALoop()
    {
        using var browser = await SignedInAsync(_owner);
        using var otherDevice = await SignedInAsync(_owner);
        var copied = browser.AuthCookie!;
        var sessionId = SessionIdOf(copied);
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        InterceptAlphaCommands(new FailingAfterExecution(command => command.CommandText.Contains("UPDATE \"TenantSessions\"", StringComparison.Ordinal)));

        var failed = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        await AssertLogoutFailurePageAsync(failed, browser);
        Assert.True(string.Equals(copied, browser.AuthCookie, StringComparison.Ordinal), "An unconfirmed logout must keep the browser's cookie.");
        var revokedAt = _host.ReadSessions(_host.AlphaId).Single(s => s.Id == sessionId).RevokedAtUtc;
        Assert.NotNull(revokedAt);

        var retry = await browser.GetAsync("/auth/logout");

        AssertRedirectsToLogin(retry);
        AssertSignedOut(browser);
        AssertRejectionLogged(TenantSessionState.SessionRevoked);
        var login = await browser.GetAsync(retry.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True((await login.Content.ReadAsStringAsync(Ct)).Contains("<form method=\"post\" action=\"/auth/login\">", StringComparison.Ordinal),
            "The retry should end on the login form.");
        // The login page carries the logout path as its ReturnUrl; signing in goes to the landing page instead.
        var signIn = await browser.PostFormAsync("/auth/login",
        [
            new("Email", _owner.Email),
            new("Password", TenantSessionWebHost.Password),
            new("ReturnUrl", "/auth/logout"),
            new("__RequestVerificationToken", await browser.AntiforgeryTokenAsync("/auth/login"))
        ]);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/dashboard", signIn.Headers.Location!.OriginalString);

        Assert.Equal(revokedAt, _host.ReadSessions(_host.AlphaId).Single(s => s.Id == sessionId).RevokedAtUtc);
        using var replay = CookieClient(TenantSessionWebHost.AlphaHost, copied);
        AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/settings/users")).StatusCode);
    }

    public static TheoryData<string> SessionCheckFailures => ["session-read", "database-unreachable"];

    /// <summary>
    /// The session cannot be checked before the logout action (WAS-186). The request is not authenticated, so the
    /// action does not run: with only the check's read failing, the revocation's UPDATE would have worked, and the
    /// session stays unrevoked. The failure page renders even when the tenant database cannot be opened at all, and
    /// its "Try again" gets the same page while that lasts. Once the database works, the logout page logs out.
    /// </summary>
    [Theory]
    [MemberData(nameof(SessionCheckFailures))]
    public async Task SessionCheckFailureBeforeLogout_ShowsTheFailurePage_AndDoesNotRunTheLogout(string failure)
    {
        using var browser = await SignedInAsync(_owner);
        var copied = browser.AuthCookie!;
        var sessionId = SessionIdOf(copied);
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        if (failure == "session-read")
        {
            FailAlphaCommands(command => command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                                         && command.CommandText.Contains("\"TenantSessions\"", StringComparison.Ordinal));
        }
        else
        {
            _host.Tenants.Override(_host.AlphaId, _ => Task.FromException<TenantDbContext>(new SimulatedDbException()));
        }

        var failed = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        await AssertLogoutFailurePageAsync(failed, browser);
        Assert.True(string.Equals(copied, browser.AuthCookie, StringComparison.Ordinal), "An unchecked session must be neither accepted nor deleted.");
        await AssertRevealsNothingAsync(failed, "Tenant session could not be validated");
        await AssertBodyHoldsAsync(failed, copied, sessionId.ToString());
        AssertLogsHold(copied, sessionId.ToString());
        await AssertLogoutFailurePageAsync(await browser.GetAsync("/auth/logout"), browser);

        UsePlainAlphaDatabase();
        Assert.Null(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
        var retried = await TryLogoutAgainAsync(browser);

        Assert.Equal(HttpStatusCode.Redirect, retried.StatusCode);
        Assert.Equal("/auth/login", retried.Headers.Location!.OriginalString);
        AssertSignedOut(browser);
        Assert.NotNull(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
    }

    public static TheoryData<string, bool> RenewalDueFailedLogouts => new()
    {
        { "revocation-fails", false },
        { "revocation-fails", true },
        { "revocation-stored-then-fails", false },
        { "revocation-stored-then-fails", true },
        { "session-check-fails", false },
        { "session-check-fails", true }
    };

    /// <summary>
    /// A logout that cannot complete for a session whose sliding renewal is due, with and without remember-me: the
    /// failure page neither renews nor deletes the cookie. The antiforgery token comes from a page of the fresh session,
    /// which is not due for renewal; the cookie is aged afterwards, so the logout under test is the first request that
    /// finds renewal due. Afterwards the logout page, an ordinary authenticated page, renews the due cookie like any page,
    /// which shows renewal was due, and its form logs out. A revocation that was stored is found by the retry instead, which
    /// is signed out and sent to the login page. Every copy of the cookie is then rejected; the other device keeps its
    /// session.
    /// </summary>
    [Theory]
    [MemberData(nameof(RenewalDueFailedLogouts))]
    public async Task RenewalDueSession_LogoutThatCannotComplete_NeitherRenewsNorDeletesTheCookie(string failure, bool rememberMe)
    {
        using var browser = _host.Client(TenantSessionWebHost.AlphaHost);
        await browser.LoginAsync(_owner.Email, rememberMe: rememberMe);
        using var otherDevice = await SignedInAsync(_owner);
        var original = browser.AuthCookie!;
        var sessionId = SessionIdOf(original);
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        Assert.True(string.Equals(original, browser.AuthCookie, StringComparison.Ordinal), "A fresh session must not be renewed.");
        var due = RenewalDue(original);
        browser.Cookies[TenantAuthCookieNames.Active] = due;
        switch (failure)
        {
            case "revocation-fails":
                FailAlphaCommands(command => command.CommandText.Contains("UPDATE \"TenantSessions\"", StringComparison.Ordinal));
                break;
            case "revocation-stored-then-fails":
                InterceptAlphaCommands(new FailingAfterExecution(command => command.CommandText.Contains("UPDATE \"TenantSessions\"", StringComparison.Ordinal)));
                break;
            default:
                FailAlphaCommands(command => command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                                             && command.CommandText.Contains("\"TenantSessions\"", StringComparison.Ordinal));
                break;
        }

        var failed = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        await AssertLogoutFailurePageAsync(failed, browser);
        Assert.True(string.Equals(due, browser.AuthCookie, StringComparison.Ordinal), "A logout that cannot complete must neither renew nor delete the cookie.");
        var stored = failure == "revocation-stored-then-fails";
        Assert.Equal(stored, _host.ReadSessions(_host.AlphaId).Single(s => s.Id == sessionId).RevokedAtUtc is not null);

        UsePlainAlphaDatabase();
        var copies = new List<string> { original, due };
        if (stored)
        {
            AssertRedirectsToLogin(await browser.GetAsync("/auth/logout"));
            AssertSignedOut(browser);
        }
        else
        {
            var retryToken = await browser.AntiforgeryTokenAsync("/auth/logout");
            var renewed = browser.AuthCookie!;
            Assert.False(string.Equals(due, renewed, StringComparison.Ordinal), "The logout page renews a due cookie like any page.");
            Assert.Equal(sessionId, SessionIdOf(renewed));
            Assert.Null(_host.ReadSessions(_host.AlphaId).Single(s => s.Id == sessionId).RevokedAtUtc);
            copies.Add(renewed);

            var retried = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", retryToken)]);

            Assert.Equal(HttpStatusCode.Redirect, retried.StatusCode);
            Assert.Equal("/auth/login", retried.Headers.Location!.OriginalString);
            AssertSignedOut(browser);
        }

        Assert.Equal([sessionId], _host.ReadSessions(_host.AlphaId).Where(s => s.RevokedAtUtc is not null).Select(s => s.Id));
        foreach (var copy in copies)
        {
            using var replay = CookieClient(TenantSessionWebHost.AlphaHost, copy);
            AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
            AssertSignedOut(replay);
        }

        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/settings/users")).StatusCode);
    }

    /// <summary>
    /// The logout page: the logout form for the signed-in session, with its antiforgery token. Opening it revokes and
    /// deletes nothing and leaves a fresh cookie as it is. It is an ordinary authenticated page, so a cookie whose sliding
    /// renewal is due is renewed as on any page, for the same session. Without a session it is challenged like any page.
    /// </summary>
    [Fact]
    public async Task LogoutPage_ShowsTheLogoutForm_AndOpeningItEndsNoSession()
    {
        using var browser = await SignedInAsync(_owner);
        var cookie = browser.AuthCookie!;
        var sessionId = SessionIdOf(cookie);

        var page = await browser.GetAsync("/auth/logout");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(Ct);
        Assert.True(html.Contains("<form method=\"post\" action=\"/auth/logout\">", StringComparison.Ordinal), "The logout form is missing.");
        Assert.True(html.Contains("name=\"__RequestVerificationToken\"", StringComparison.Ordinal), "The logout form has no antiforgery token.");
        Assert.True(html.Contains("type=\"submit\" data-logout-submit>Logout</button>", StringComparison.Ordinal), "The logout button is missing.");
        Assert.False(html.Contains("data-logout-failed", StringComparison.Ordinal), "The logout page must not report a failure.");
        Assert.True(string.Equals(cookie, browser.AuthCookie, StringComparison.Ordinal), "Opening the logout page must not change a fresh cookie.");
        Assert.Null(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/settings/users")).StatusCode);

        var due = RenewalDue(cookie);
        browser.Cookies[TenantAuthCookieNames.Active] = due;
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/auth/logout")).StatusCode);
        Assert.False(string.Equals(due, browser.AuthCookie, StringComparison.Ordinal), "The logout page renews a due cookie like any page.");
        Assert.Equal(sessionId, SessionIdOf(browser.AuthCookie!));
        Assert.Null(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);

        using var anonymous = _host.Client(TenantSessionWebHost.AlphaHost);
        AssertRedirectsToLogin(await anonymous.GetAsync("/auth/logout"));
    }

    /// <summary>
    /// A logout that succeeds for a session whose sliding renewal is due, with and without remember-me: the response only
    /// deletes the cookie, with no renewed copy beside it, and every copy is rejected afterwards.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewalDueSession_LogsOut_WithoutARenewedCookie(bool rememberMe)
    {
        using var browser = _host.Client(TenantSessionWebHost.AlphaHost);
        await browser.LoginAsync(_owner.Email, rememberMe: rememberMe);
        var original = browser.AuthCookie!;
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        var due = RenewalDue(original);
        browser.Cookies[TenantAuthCookieNames.Active] = due;

        var logout = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/auth/login", logout.Headers.Location!.OriginalString);
        AssertSignedOut(browser);
        Assert.False(browser.LastSetCookies.Select(header => SetCookieHeaderValue.Parse(header))
                .Any(cookie => cookie.Name.Equals(TenantAuthCookieNames.Active) && cookie.Value.Length > 0),
            "A logout must not re-issue the tenant cookie.");
        Assert.NotNull(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
        foreach (var copy in new[] { original, due })
        {
            using var replay = CookieClient(TenantSessionWebHost.AlphaHost, copy);
            AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
        }
    }

    [Theory]
    [InlineData("tr-TR", "ltr", "Çıkış yap", "Çıkış tamamlanamadı. Oturumunuz açık kalmış olabilir. Lütfen tekrar deneyin.", "Tekrar dene")]
    [InlineData("en-US", "ltr", "Logout", "Logout could not be completed. Your session may still be open. Please try again.", "Try again")]
    [InlineData("ar-SA", "rtl", "تسجيل الخروج", "تعذر إكمال تسجيل الخروج. قد تظل جلستك مفتوحة. يرجى المحاولة مرة أخرى.", "إعادة المحاولة")]
    [InlineData("ru-RU", "ltr", "Выход из системы", "Не удалось завершить выход. Ваш сеанс может оставаться открытым. Повторите попытку.", "Повторить")]
    public async Task LogoutFailurePage_IsLocalized(string culture, string direction, string heading, string message, string tryAgain)
    {
        using var browser = await SignedInAsync(_owner);
        browser.Cookies[CookieRequestCultureProvider.DefaultCookieName] =
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        FailAlphaCommands(command => command.CommandText.Contains("UPDATE \"TenantSessions\"", StringComparison.Ordinal));

        var failed = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        await AssertLogoutFailurePageAsync(failed, browser, message, tryAgain);
        var html = WebUtility.HtmlDecode(await failed.Content.ReadAsStringAsync(Ct));
        Assert.True(html.Contains($"<html lang=\"{culture}\" dir=\"{direction}\">", StringComparison.Ordinal), $"Expected lang {culture} and dir {direction}.");
        Assert.True(html.Contains($"<h1 class=\"h4 mb-3\">{heading}</h1>", StringComparison.Ordinal), "The localized heading is missing.");
    }

    [Fact]
    public async Task SessionReadFailure_FailsClosed_KeepsTheCookie_AndRevealsNothing()
    {
        using var browser = await SignedInAsync(_owner);
        FailAlphaCommands(command => command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                                     && command.CommandText.Contains("\"TenantSessions\"", StringComparison.Ordinal));

        var response = await browser.GetAsync("/settings/users");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(browser.AuthCookie is not null, "An unreadable session must be neither accepted nor deleted.");
        await AssertRevealsNothingAsync(response, "Tenant session could not be validated");
        UsePlainAlphaDatabase();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/settings/users")).StatusCode);
    }

    // --- Renewal racing logout --------------------------------------------------------------------

    /// <summary>
    /// A request authenticated just before logout commits may finish afterwards and still carry a renewed cookie. The
    /// renewal request is held right after its session check passed (a validator decorator released by the test), the
    /// logout commits, and then the renewal completes. The cookie it returns is already unusable.
    /// </summary>
    [Fact]
    public async Task RenewalAuthenticatedBeforeLogout_MayReturnACookie_ThatIsRejectedAfterLogout()
    {
        var gate = new HoldAfterValidation();
        await using var host = await TenantSessionWebHost.StartAsync(configureServices: gate.Register);
        var owner = host.SeedUser(host.AlphaId, "racer@alpha.test", UserRole.Owner);
        using var browser = host.Client(TenantSessionWebHost.AlphaHost);
        await browser.LoginAsync(owner.Email);
        var original = browser.AuthCookie!;
        var sessionId = SessionIdOf(host, original);
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        using var renewer = CookieClient(host, TenantSessionWebHost.AlphaHost, Aged(host, original, TimeSpan.FromDays(5)));
        var aged = renewer.AuthCookie!;

        var renewal = renewer.SendAsync(HttpMethod.Get, "/settings/users", null, [new(HoldAfterValidation.Header, "1")]);
        Assert.Equal(TenantSessionState.Valid, await gate.Validated.WaitAsync(Wait, Ct));
        var logout = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.NotNull(Assert.Single(host.ReadSessions(host.AlphaId)).RevokedAtUtc);
        gate.Release();
        var renewed = await renewal.WaitAsync(Wait, Ct);

        // The request had authenticated before the revocation, so it completes and re-issues the same session.
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var renewedCookie = renewer.AuthCookie!;
        Assert.False(string.Equals(aged, renewedCookie, StringComparison.Ordinal), "The held request must have renewed the cookie.");
        Assert.Equal(sessionId, SessionIdOf(host, renewedCookie));

        foreach (var copy in new[] { original, aged, renewedCookie })
        {
            using var replay = CookieClient(host, TenantSessionWebHost.AlphaHost, copy);
            AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
            AssertSignedOut(replay);
        }

        Assert.Single(host.ReadSessions(host.AlphaId));
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private async Task<TenantSessionClient> SignedInAsync(AppUser user, string host = TenantSessionWebHost.AlphaHost)
    {
        var client = _host.Client(host);
        await client.LoginAsync(user.Email);
        return client;
    }

    /// <summary>Normal logout with a token from a page of the same session; it must succeed.</summary>
    private static async Task LogoutAsync(TenantSessionClient client, string? token = null)
    {
        token ??= await client.AntiforgeryTokenAsync("/auth/access-denied");
        var response = await client.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/auth/login", response.Headers.Location!.OriginalString);
        AssertSignedOut(client);
    }

    /// <summary>
    /// The failure page's "Try again", as a browser follows it: open the logout page (it must render its form) and post
    /// its form, with the token the page issued for this session.
    /// </summary>
    private static async Task<HttpResponseMessage> TryLogoutAgainAsync(TenantSessionClient client)
    {
        var token = await client.AntiforgeryTokenAsync("/auth/logout");
        return await client.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);
    }

    /// <summary>
    /// The logout failure page (WAS-186): HTTP 503, not cached, no redirect, no change to the tenant cookie, the localized
    /// alert and a "Try again" link to the logout page, and no form of its own.
    /// </summary>
    private static async Task AssertLogoutFailurePageAsync(
        HttpResponseMessage response,
        TenantSessionClient client,
        string message = "Logout could not be completed. Your session may still be open. Please try again.",
        string tryAgain = "Try again")
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore == true, "The failure page must not be cached.");
        Assert.False(client.LastSetCookies.Any(header => SetCookieHeaderValue.Parse(header).Name.Equals(TenantAuthCookieNames.Active)),
            "The failure page must neither delete nor renew the tenant cookie.");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(html.Contains($"role=\"alert\" data-logout-failed>{message}</div>", StringComparison.Ordinal), "The localized failure alert is missing.");
        Assert.True(html.Contains($"href=\"/auth/logout\" data-logout-retry>{tryAgain}</a>", StringComparison.Ordinal), "The Try again link is missing.");
        Assert.False(html.Contains("<form", StringComparison.Ordinal), "The failure page must not post anything itself.");
    }

    /// <summary>The response body contains none of the values (cookies, session ids).</summary>
    private static async Task AssertBodyHoldsAsync(HttpResponseMessage response, params string[] values)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        foreach (var value in values)
            Assert.False(body.Contains(value, StringComparison.OrdinalIgnoreCase), "The page contains a cookie or session id.");
    }

    private TenantSessionClient CookieClient(string host, string cookie) => CookieClient(_host, host, cookie);

    /// <summary>An independent client holding only the given tenant cookie, as a copy taken from another device.</summary>
    private static TenantSessionClient CookieClient(TenantSessionWebHost server, string host, string cookie)
    {
        var client = server.Client(host);
        client.Cookies[TenantAuthCookieNames.Active] = cookie;
        return client;
    }

    private Guid SessionIdOf(string cookie) => SessionIdOf(_host, cookie);

    private static Guid SessionIdOf(TenantSessionWebHost server, string cookie)
    {
        var ticket = server.UnprotectTenantCookie(cookie);
        Assert.True(ticket is not null, "The cookie is not a tenant ticket of this host.");
        Assert.True(TenantSessionClaims.TryRead(ticket.Principal, out var session), "The cookie does not carry valid session claims.");
        return session.SessionId;
    }

    private string Aged(string cookie, TimeSpan age) => Aged(_host, cookie, age);

    /// <summary>The same ticket's claims, protected as if issued <paramref name="age"/> ago with the default lifetime.</summary>
    private static string Aged(TenantSessionWebHost server, string cookie, TimeSpan age)
    {
        var ticket = server.UnprotectTenantCookie(cookie)!;
        var issued = DateTimeOffset.UtcNow - age;
        return server.ProtectTenantCookie(ticket.Principal.Claims, issued, issued.AddDays(7));
    }

    /// <summary>
    /// The same login's ticket, with its claims, lifetime and remember-me choice, protected as if issued 70 % of its
    /// lifetime ago: valid, and due for sliding renewal, since more of its lifetime has passed than remains.
    /// </summary>
    private string RenewalDue(string cookie)
    {
        var ticket = _host.UnprotectTenantCookie(cookie)!;
        var lifetime = ticket.Properties.ExpiresUtc!.Value - ticket.Properties.IssuedUtc!.Value;
        var issued = DateTimeOffset.UtcNow - lifetime * 0.7;
        return _host.ProtectTenantCookie(ticket.Principal.Claims, issued, issued + lifetime, ticket.Properties.IsPersistent);
    }

    private static List<Claim> SessionClaims(Guid tenantId, AppUser user, Guid sessionId) =>
    [
        .. TenantSessionClaims.Create(tenantId, user.Id, UserRole.Owner, user.SecurityStamp, sessionId),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Name, user.FullName)
    ];

    private static SetCookieHeaderValue AuthSetCookie(TenantSessionClient client)
    {
        var cookies = client.LastSetCookies
            .Select(header => SetCookieHeaderValue.Parse(header))
            .Where(cookie => cookie.Name.Equals(TenantAuthCookieNames.Active) && cookie.Value.Length > 0)
            .ToList();
        Assert.True(cookies.Count == 1, $"Expected one issued tenant cookie, found {cookies.Count}.");
        return cookies[0];
    }

    private void FailAlphaCommands(Func<DbCommand, bool> match) => InterceptAlphaCommands(new FailingCommands(match));

    private void InterceptAlphaCommands(IInterceptor interceptor) =>
        _host.Tenants.Override(_host.AlphaId, _ => Task.FromResult(new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlite(_host.Tenants.ConnectionStringFor(_host.AlphaId))
                .AddInterceptors(interceptor)
                .Options)));

    private void UsePlainAlphaDatabase() =>
        _host.Tenants.Override(_host.AlphaId, _ => Task.FromResult(new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(_host.Tenants.ConnectionStringFor(_host.AlphaId)).Options)));

    private async Task AssertRevealsNothingAsync(
        HttpResponseMessage response,
        string expectedLog,
        string exceptionType = nameof(SimulatedDbException))
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.False(body.Contains(SimulatedDbException.Secret, StringComparison.Ordinal), "The response revealed the database error.");
        Assert.False(_host.Logs.Entries.Any(e => e.Contains(SimulatedDbException.Secret, StringComparison.Ordinal)), "A log revealed the database error.");
        Assert.True(_host.Logs.Entries.Any(e => e.Contains(expectedLog, StringComparison.Ordinal)
                                                && e.Contains($"({exceptionType}).", StringComparison.Ordinal)),
            $"Expected the log line \"{expectedLog}\" with exception type {exceptionType}.");
    }

    private void AssertRejectionLogged(TenantSessionState reason) =>
        Assert.True(_host.Logs.Entries.Any(e => e.Contains($"Tenant session rejected: {reason}.", StringComparison.Ordinal)),
            $"Expected a rejection logged as {reason}.");

    /// <summary>No log line contains any of the values (cookies, session ids).</summary>
    private void AssertLogsHold(params string[] values)
    {
        foreach (var value in values)
        {
            Assert.False(_host.Logs.Entries.Any(e => e.Contains(value, StringComparison.OrdinalIgnoreCase)),
                "A log line contains a cookie or session id.");
        }
    }

    private static void AssertSignedOut(TenantSessionClient client) =>
        Assert.True(client.AuthCookie is null, "The tenant cookie should have been deleted on this host.");

    /// <summary>
    /// The cookie handler answers a challenge with an absolute URL on the request's own host; controllers answer
    /// with a local path. Either way the redirect stays on the tenant host and goes to the login page.
    /// </summary>
    private static void AssertRedirectsToLogin(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        if (location.IsAbsoluteUri)
        {
            Assert.Equal(response.RequestMessage!.Headers.Host, location.Authority);
            location = new Uri(location.PathAndQuery, UriKind.Relative);
        }

        Assert.StartsWith("/auth/login", location.OriginalString, StringComparison.Ordinal);
    }

    private sealed class SimulatedDbException() : DbException(Secret)
    {
        public const string Secret = "Server=tcp:secret-sql.example;Password=CONNSTR-SECRET-114";
    }

    /// <summary>Fails every command <paramref name="match"/> accepts before it reaches the database.</summary>
    private sealed class FailingCommands(Func<DbCommand, bool> match) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            match(command) ? throw new SimulatedDbException() : result;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            match(command) ? throw new SimulatedDbException() : ValueTask.FromResult(result);

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) =>
            match(command) ? throw new SimulatedDbException() : result;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            match(command) ? throw new SimulatedDbException() : ValueTask.FromResult(result);

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result) =>
            match(command) ? throw new SimulatedDbException() : result;

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) =>
            match(command) ? throw new SimulatedDbException() : ValueTask.FromResult(result);
    }

    /// <summary>
    /// Lets every command <paramref name="match"/> accepts run and commit, then fails it: the database stored the change,
    /// the caller sees an error.
    /// </summary>
    private sealed class FailingAfterExecution(Func<DbCommand, bool> match) : DbCommandInterceptor
    {
        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) =>
            match(command) ? throw new SimulatedDbException() : result;

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            match(command) ? throw new SimulatedDbException() : ValueTask.FromResult(result);
    }

    /// <summary>
    /// Wraps the real validator. A request carrying <see cref="Header"/> is held right after its session check, with the
    /// result reported to the test, until the test releases it; every other request passes straight through.
    /// </summary>
    private sealed class HoldAfterValidation
    {
        public const string Header = "X-Test-Hold-After-Validation";

        private readonly TaskCompletionSource<TenantSessionState> _validated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TenantSessionState> Validated => _validated.Task;

        public void Release() => _release.TrySetResult();

        public void Register(IServiceCollection services)
        {
            services.RemoveAll<ITenantSessionValidator>();
            services.AddScoped<TenantSessionValidator>();
            services.AddScoped<ITenantSessionValidator>(sp => new Validator(
                sp.GetRequiredService<TenantSessionValidator>(), sp.GetRequiredService<IHttpContextAccessor>(), this));
        }

        private sealed class Validator(TenantSessionValidator inner, IHttpContextAccessor http, HoldAfterValidation gate) : ITenantSessionValidator
        {
            public async Task<TenantSessionState> ValidateAsync(Guid? resolvedTenantId, ClaimsPrincipal? principal, CancellationToken ct)
            {
                var state = await inner.ValidateAsync(resolvedTenantId, principal, ct);
                if (http.HttpContext?.Request.Headers.ContainsKey(Header) == true)
                {
                    gate._validated.TrySetResult(state);
                    await gate._release.Task.WaitAsync(ct);
                }

                return state;
            }
        }
    }
}
