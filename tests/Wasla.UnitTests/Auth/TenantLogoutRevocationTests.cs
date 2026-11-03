using System.Data.Common;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Security;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.Web.Security;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Tenant logout ends the server-side login session it logs out (WAS-114). Every test drives the tenant application over
/// HTTP: the real login form and welcome link, the real cookie scheme with its per-request session check, the tenant
/// resolved from the Host header, and the real logout action with its antiforgery check, over SQLite tenant databases.
/// Cookies are kept in memory only: no assertion message or test output contains a cookie, header, password or token.
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

    [Fact]
    public async Task RevocationFailure_DoesNotReportSuccess_AndTheSessionStaysUntilALaterLogout()
    {
        using var browser = await SignedInAsync(_owner);
        var copied = browser.AuthCookie!;
        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        FailAlphaCommands(command => command.CommandText.Contains("UPDATE \"TenantSessions\"", StringComparison.Ordinal));

        var failed = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(browser.AuthCookie is not null, "A failed logout must not delete the browser's cookie.");
        await AssertRevealsNothingAsync(failed, "Tenant session could not be revoked");
        Assert.Null(Assert.Single(_host.ReadSessions(_host.AlphaId)).RevokedAtUtc);
        // Nothing claimed the copy was invalidated, and it was not.
        using var replay = CookieClient(TenantSessionWebHost.AlphaHost, copied);
        Assert.Equal(HttpStatusCode.OK, (await replay.GetAsync("/settings/users")).StatusCode);

        UsePlainAlphaDatabase();
        await LogoutAsync(browser, token);
        AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
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

    private void FailAlphaCommands(Func<DbCommand, bool> match) =>
        _host.Tenants.Override(_host.AlphaId, _ => Task.FromResult(new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlite(_host.Tenants.ConnectionStringFor(_host.AlphaId))
                .AddInterceptors(new FailingCommands(match))
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
