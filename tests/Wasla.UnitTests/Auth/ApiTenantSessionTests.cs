using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Wasla.Api tenant sessions and role parity (WAS-94, audit F-09, WAS-89 review R-1). Web and Api are served in-process
/// with one shared Data Protection key ring, as an operator may configure them. Every stale-session test signs in through
/// the real Web login form, keeps that cookie, changes the account the way an Owner, the password reset or the CLI does,
/// and then presents the old cookie to the Api with the tenant's Host header.
/// </summary>
public sealed class ApiTenantSessionTests : IAsyncLifetime
{
    private const string NewPassword = "New-Correct-Horse-7";

    private DirectoryInfo _keys = null!;
    private TenantSessionWebHost _web = null!;
    private TenantSessionApiHost _api = null!;
    private AppUser _owner = null!;
    private AppUser _otherOwner = null!;
    private Guid _connectionId;

    public async ValueTask InitializeAsync()
    {
        _keys = Directory.CreateTempSubdirectory("wasla-shared-keys-");
        _web = await TenantSessionWebHost.StartAsync(_keys);
        _api = await TenantSessionApiHost.StartAsync(_web, _keys);
        _owner = _web.SeedUser(_web.AlphaId, "owner@alpha.test", UserRole.Owner);
        _otherOwner = _web.SeedUser(_web.AlphaId, "other-owner@alpha.test", UserRole.Owner);
        var connection = new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = "STORE-1",
            EncryptedApiKey = "fake-encrypted:key",
            EncryptedApiSecret = "fake-encrypted:secret",
            IsActive = true
        };
        _web.Tenants.Seed(_web.AlphaId, db => db.PlatformConnections.Add(connection));
        _connectionId = connection.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _web.DisposeAsync();
        try { _keys.Delete(recursive: true); } catch (IOException) { }
    }

    // --- The shared cookie --------------------------------------------------------------------------

    [Fact]
    public async Task WebLoginCookie_IsAcceptedByApi_ForTheSameUser()
    {
        using var web = await WebLoginAsync(_owner);
        using var api = ToApi(web);

        var response = await api.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(_owner.Id, body.RootElement.GetProperty("id").GetGuid());
    }

    // --- Stale sessions -----------------------------------------------------------------------------

    [Fact]
    public async Task DeactivatedOwner_OldWebCookie_IsRejectedByApi()
    {
        using var web = await WebLoginAsync(_owner);
        using var admin = await WebLoginAsync(_otherOwner);
        await ToggleAsync(admin, _owner.Id, "deactivate");

        await AssertStaleCookieRejectedAsync(web);
    }

    [Fact]
    public async Task InactiveUserWithUnchangedStamp_OldWebCookie_IsRejectedByApi()
    {
        using var web = await WebLoginAsync(_owner);
        _web.UpdateUser(_web.AlphaId, _owner.Id, user => user.IsActive = false);

        await AssertStaleCookieRejectedAsync(web);
    }

    [Fact]
    public async Task DeletedUser_OldWebCookie_IsRejectedByApi()
    {
        using var web = await WebLoginAsync(_owner);
        _web.DeleteUser(_web.AlphaId, _owner.Id);

        await AssertStaleCookieRejectedAsync(web);
    }

    [Fact]
    public async Task DemotedOwner_OldOwnerCookie_CannotCallOwnerWritesOnApi()
    {
        using var web = await WebLoginAsync(_owner);
        using var admin = await WebLoginAsync(_otherOwner);
        await EditAsync(admin, _owner, UserRole.Manager, isActive: true);

        await AssertStaleCookieRejectedAsync(web);
    }

    [Fact]
    public async Task RoleChangedDirectlyInTheDatabase_OldCookie_IsRejectedByApi()
    {
        using var web = await WebLoginAsync(_owner);
        _web.UpdateUser(_web.AlphaId, _owner.Id, user => user.Role = UserRole.Viewer);

        await AssertStaleCookieRejectedAsync(web);
    }

    [Fact]
    public async Task PasswordChangedByAnotherOwner_OldWebCookie_IsRejectedByApi()
    {
        using var web = await WebLoginAsync(_owner);
        using var admin = await WebLoginAsync(_otherOwner);
        await EditAsync(admin, _owner, UserRole.Owner, isActive: true, NewPassword);

        await AssertStaleCookieRejectedAsync(web);
    }

    [Fact]
    public async Task WebPasswordReset_OldWebCookie_IsRejectedByApi()
    {
        using var web = await WebLoginAsync(_owner);
        var rawToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        _web.Tenants.Seed(_web.AlphaId, db => db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = _owner.Id,
            TokenHash = TenantPasswordResetService.HashToken(rawToken),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
        }));

        // The reset link, completed through the real Web form by an anonymous browser.
        using var browser = _web.Client(TenantSessionWebHost.AlphaHost);
        var token = await browser.AntiforgeryTokenAsync($"/auth/reset-password?token={rawToken}");
        var reset = await browser.PostFormAsync("/auth/reset-password",
        [
            new("Token", rawToken),
            new("NewPassword", NewPassword),
            new("ConfirmPassword", NewPassword),
            new("__RequestVerificationToken", token)
        ]);
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.NotEqual(_owner.SecurityStamp, _web.ReadUser(_web.AlphaId, _owner.Id)!.SecurityStamp);

        await AssertStaleCookieRejectedAsync(web);
    }

    [Fact]
    public async Task CliPasswordReset_OldWebCookie_IsRejectedByApi()
    {
        using var web = await WebLoginAsync(_owner);
        await using (var central = _web.Central.CreateContext())
        {
            var code = await CliPasswordReset.ExecuteAsync(
                central,
                "tenant",
                _owner.Email,
                "alpha",
                dryRun: false,
                openTenant: (_, _) => Task.FromResult(new TenantDbContext(
                    new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(_web.Tenants.ConnectionStringFor(_web.AlphaId)).Options)),
                new FixedPasswordReader(NewPassword),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, code);
        }

        Assert.NotEqual(_owner.SecurityStamp, _web.ReadUser(_web.AlphaId, _owner.Id)!.SecurityStamp);
        await AssertStaleCookieRejectedAsync(web);
    }

    [Fact]
    public async Task StaleSession_IsNotRenewedByApi_ButAValidOneIs()
    {
        // Issued five days ago: sliding renewal is due after half of the seven-day lifetime.
        using var valid = _api.Client(TenantSessionWebHost.AlphaHost);
        valid.Cookies[TenantAuthCookieNames.Active] = _api.ProtectTenantCookie(
            SessionClaims(_owner, UserRole.Owner), DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(2));
        var original = valid.AuthCookie;

        Assert.Equal(HttpStatusCode.OK, (await valid.GetAsync("/api/auth/me")).StatusCode);
        Assert.NotNull(valid.AuthCookie);
        Assert.NotEqual(original, valid.AuthCookie);

        _web.UpdateUser(_web.AlphaId, _owner.Id, user => user.Role = UserRole.Manager);
        using var stale = _api.Client(TenantSessionWebHost.AlphaHost);
        stale.Cookies[TenantAuthCookieNames.Active] = original!;

        AssertUnauthorized(await stale.GetAsync("/api/auth/me"));
        AssertSessionDeleted(stale);
    }

    // --- Another tenant -----------------------------------------------------------------------------

    [Fact]
    public async Task AlphaCookie_ReplayedOnBetaApiHost_IsRejectedWithoutOpeningBetaDatabase()
    {
        // Tenant B has an active Owner with the same user id and the same security stamp as the alpha session.
        _web.SeedUser(_web.BetaId, "owner@beta.test", UserRole.Owner, id: _owner.Id);
        _web.UpdateUser(_web.BetaId, _owner.Id, user => user.SecurityStamp = _owner.SecurityStamp);
        using var web = await WebLoginAsync(_owner);
        using var beta = web.ReplayTo(_api.BaseAddress, TenantSessionWebHost.BetaHost);
        var openedBefore = _web.Tenants.Opened.Count(id => id == _web.BetaId);

        var read = await beta.GetAsync("/api/branches");

        AssertUnauthorized(read);
        AssertSessionDeleted(beta);
        Assert.Equal(openedBefore, _web.Tenants.Opened.Count(id => id == _web.BetaId));
        // The alpha session itself is unaffected.
        using var alpha = ToApi(web);
        Assert.Equal(HttpStatusCode.OK, (await alpha.GetAsync("/api/branches")).StatusCode);
    }

    // --- Cookies the login flow does not produce ----------------------------------------------------

    public static TheoryData<string> MalformedSessions =>
    [
        "no-user-id", "malformed-user-id", "empty-user-id", "no-stamp", "empty-stamp", "malformed-stamp",
        "no-tenant-id", "malformed-tenant-id", "empty-tenant-id", "no-role", "unknown-role", "undefined-role-number",
        "user-id-claims-disagree"
    ];

    [Theory]
    [MemberData(nameof(MalformedSessions))]
    public async Task CookieWithMissingOrMalformedClaims_IsRejectedByApiWithoutOpeningADatabase(string defect)
    {
        var claims = SessionClaims(_owner, UserRole.Owner);
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
            case "empty-tenant-id": Replace("TenantId", Guid.Empty.ToString()); break;
            case "no-role": Replace(ClaimTypes.Role, null); Replace("Role", null); break;
            case "unknown-role": Replace(ClaimTypes.Role, "Administrator"); Replace("Role", "Administrator"); break;
            case "undefined-role-number": Replace(ClaimTypes.Role, "99"); Replace("Role", "99"); break;
            case "user-id-claims-disagree": Replace("UserId", _otherOwner.Id.ToString()); break;
        }

        using var client = _api.Client(TenantSessionWebHost.AlphaHost);
        client.Cookies[TenantAuthCookieNames.Active] = _api.ProtectTenantCookie(claims, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));
        var openedBefore = _web.Tenants.Opened.Count;

        var response = await client.GetAsync("/api/auth/me");

        AssertUnauthorized(response);
        AssertSessionDeleted(client);
        Assert.Equal(openedBefore, _web.Tenants.Opened.Count);
    }

    [Fact]
    public async Task ForgedCookieWithCurrentClaims_IsAcceptedByApi()
    {
        // The malformed cases above differ from this working session in one claim each.
        using var client = _api.Client(TenantSessionWebHost.AlphaHost);
        client.Cookies[TenantAuthCookieNames.Active] = _api.ProtectTenantCookie(
            SessionClaims(_owner, UserRole.Owner), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // --- Tenant database unavailable ----------------------------------------------------------------

    [Fact]
    public async Task TenantDatabaseFailure_FailsClosedWithoutDetails_AndRunsNoController()
    {
        using var web = await WebLoginAsync(_owner);
        using var api = ToApi(web);
        var cookie = api.AuthCookie;
        var branchesBefore = CountBranches();
        _web.Tenants.Override(_web.AlphaId, _ => throw new SimulatedDbException());

        var read = await api.GetAsync("/api/branches");
        var write = await PostJsonAsync(api, "/api/branches", new { name = "Branch", address = "Street 1", isActive = true });

        foreach (var response in new[] { read, write })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("tenant_session_unavailable", body, StringComparison.Ordinal);
            Assert.DoesNotContain(SimulatedDbException.Secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(SimulatedDbException), body, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(_api.Logs.Entries, e => e.Contains(SimulatedDbException.Secret, StringComparison.Ordinal));
        Assert.Contains(_api.Logs.Entries, e => e.Contains("Tenant session could not be validated", StringComparison.Ordinal)
                                                && e.Contains(nameof(SimulatedDbException), StringComparison.Ordinal));
        // Neither accepted, renewed nor deleted: the same cookie is checked again on the next request.
        Assert.Equal(cookie, api.AuthCookie);

        _web.Tenants.Override(_web.AlphaId, _ => Task.FromResult(new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(_web.Tenants.ConnectionStringFor(_web.AlphaId)).Options)));
        Assert.Equal(branchesBefore, CountBranches());
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/branches")).StatusCode);
    }

    // --- Status codes, not redirects ----------------------------------------------------------------

    [Fact]
    public async Task AnonymousAndForbiddenRequests_GetApiStatusCodes_NotRedirects()
    {
        using var anonymous = _api.Client(TenantSessionWebHost.AlphaHost);
        var unauthenticated = await anonymous.GetAsync("/api/branches");

        var manager = _web.SeedUser(_web.AlphaId, "manager@alpha.test", UserRole.Manager);
        using var managerWeb = await WebLoginAsync(manager);
        using var managerApi = ToApi(managerWeb);
        var forbidden = await managerApi.GetAsync("/api/branches");

        AssertUnauthorized(unauthenticated);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Null(forbidden.Headers.Location);
        Assert.NotEqual("text/html", forbidden.Content.Headers.ContentType?.MediaType);
        // A current session that lacks the role is not signed out.
        Assert.NotNull(managerApi.AuthCookie);
        Assert.DoesNotContain(managerApi.LastSetCookies, c => c.StartsWith(TenantAuthCookieNames.Active + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PathsWithoutTenantResolution_NeitherUseNorDeleteTheSession()
    {
        using var web = await WebLoginAsync(_owner);
        _web.UpdateUser(_web.AlphaId, _owner.Id, user => user.IsActive = false);
        using var api = ToApi(web);
        var cookie = api.AuthCookie;

        var health = await api.GetAsync("/health/live");
        var printBridge = await api.GetAsync("/api/print-bridge/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        // Print Bridge authenticates with its device token only; a tenant cookie is not a substitute.
        Assert.Equal(HttpStatusCode.Unauthorized, printBridge.StatusCode);
        Assert.Equal("print_bridge_token_required", printBridge.Headers.GetValues("X-PrintBridge-Error-Code").Single());
        Assert.Equal(cookie, api.AuthCookie);
    }

    [Fact]
    public async Task AnonymousEndpoints_StayReachable_WithoutASessionAndBesideAStaleOne()
    {
        using var anonymous = _api.Client(TenantSessionWebHost.AlphaHost);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostJsonAsync(anonymous, "/api/auth/validate",
            new { email = _otherOwner.Email, password = TenantSessionWebHost.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostJsonAsync(anonymous, "/api/auth/validate",
            new { email = _otherOwner.Email, password = "wrong-password" })).StatusCode);

        // A stale session does not block an anonymous endpoint: the session is rejected and deleted, the endpoint runs.
        using var web = await WebLoginAsync(_owner);
        _web.UpdateUser(_web.AlphaId, _owner.Id, user => user.IsActive = false);
        using var stale = ToApi(web);
        Assert.Equal(HttpStatusCode.OK, (await PostJsonAsync(stale, "/api/auth/validate",
            new { email = _otherOwner.Email, password = TenantSessionWebHost.Password })).StatusCode);
        AssertSessionDeleted(stale);
    }

    [Fact]
    public async Task PrintBridgeDeviceToken_StillAuthenticatesItsRoutes_AndATenantCookieIsNotUsedThere()
    {
        var rawToken = PrintBridgeTokenHasher.GenerateRawToken();
        await using (var central = _web.Central.CreateContext())
        {
            central.PrintBridgeDevices.Add(new PrintBridgeDevice
            {
                TenantId = _web.AlphaId,
                Name = "Kitchen printer",
                TokenHash = PrintBridgeTokenHasher.HashToken(rawToken),
                IsActive = true
            });
            await central.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // A stale Owner session rides along. Print Bridge routes skip tenant resolution: the cookie is neither used
        // nor deleted, and the device token alone decides.
        using var web = await WebLoginAsync(_owner);
        _web.UpdateUser(_web.AlphaId, _owner.Id, user => user.IsActive = false);
        using var device = ToApi(web);
        var cookie = device.AuthCookie;

        var health = await device.SendAsync(HttpMethod.Get, "/api/print-bridge/health", null, [new("X-PrintBridge-Token", rawToken)]);
        var pending = await device.SendAsync(HttpMethod.Get, "/api/print-bridge/jobs/pending", null, [new("X-PrintBridge-Token", rawToken)]);
        var wrongToken = await device.SendAsync(HttpMethod.Get, "/api/print-bridge/health", null,
            [new("X-PrintBridge-Token", PrintBridgeTokenHasher.GenerateRawToken())]);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        Assert.Equal(cookie, device.AuthCookie);
    }

    // --- Role parity with Web -----------------------------------------------------------------------

    public static TheoryData<UserRole> Roles =>
    [
        UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer,
#pragma warning disable CS0618 // Obsolete Staff: a stored role that no Web policy admits.
        UserRole.Staff
#pragma warning restore CS0618
    ];

    /// <summary>
    /// Every Api endpoint with a session that passed the Web login for each role. The expected answer follows the Web
    /// policy of the same operation: settings (branches, platform connections) are <c>CanManageTenantSettings</c>
    /// (Owner), the dashboard is <c>CanViewReports</c> (Owner, Manager, Viewer), orders are <c>CanViewOrders</c> (every
    /// role), and <c>/api/auth/me</c> answers any current tenant user with an assignable role.
    /// </summary>
    [Theory]
    [MemberData(nameof(Roles))]
    public async Task RoleMatrix_MatchesWebPolicies_AndDeniedWritesChangeNothing(UserRole role)
    {
        var user = role == UserRole.Owner ? _owner : _web.SeedUser(_web.AlphaId, $"{role.ToString().ToLowerInvariant()}@alpha.test", role);
        var order = TenantSeed.Order(DateTime.UtcNow.AddMinutes(-5), OrderStatus.New, "API-1");
        _web.Tenants.Seed(_web.AlphaId, db => db.Orders.Add(order));
        using var web = await WebLoginAsync(user);
        using var api = ToApi(web);

        var isOwner = role == UserRole.Owner;
#pragma warning disable CS0618
        var assignable = role != UserRole.Staff;
#pragma warning restore CS0618
        var reports = role is UserRole.Owner or UserRole.Manager or UserRole.Viewer;
        var failures = new List<string>();
        async Task Expect(string name, Task<HttpResponseMessage> call, HttpStatusCode allowed, bool isAllowed)
        {
            var actual = (await call).StatusCode;
            var expected = isAllowed ? allowed : HttpStatusCode.Forbidden;
            if (actual != expected)
                failures.Add($"{name}: expected {(int)expected}, got {(int)actual}");
        }

        // The dashboard sums decimal amounts, which SQLite cannot aggregate (the test databases answer 500 even for
        // an Owner; SQL Server can). Only the authorization outcome is checked there: admitted means neither 401 nor 403.
        async Task ExpectAdmitted(string name, Task<HttpResponseMessage> call, bool isAllowed)
        {
            var actual = (await call).StatusCode;
            var admitted = actual is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            if (admitted != isAllowed || (!isAllowed && actual != HttpStatusCode.Forbidden))
                failures.Add($"{name}: expected {(isAllowed ? "admitted" : "403")}, got {(int)actual}");
        }

        await Expect("GET /api/auth/me", api.GetAsync("/api/auth/me"), HttpStatusCode.OK, assignable);
        await Expect("GET /api/orders", api.GetAsync("/api/orders"), HttpStatusCode.OK, assignable);
        await Expect("GET /api/orders/{id}", api.GetAsync($"/api/orders/{order.Id}"), HttpStatusCode.OK, assignable);
        await ExpectAdmitted("GET /api/dashboard/summary", api.GetAsync("/api/dashboard/summary"), reports);
        await ExpectAdmitted("GET /api/dashboard/today", api.GetAsync("/api/dashboard/today"), reports);
        await Expect("GET /api/branches", api.GetAsync("/api/branches"), HttpStatusCode.OK, isOwner);
        await Expect("GET /api/platform-connections", api.GetAsync("/api/platform-connections"), HttpStatusCode.OK, isOwner);

        var branches = CountBranches();
        var connections = CountConnections();
        await Expect("POST /api/branches",
            PostJsonAsync(api, "/api/branches", new { name = "New branch", address = "Street 2", isActive = true }),
            HttpStatusCode.Created, isOwner);
        await Expect("POST /api/platform-connections",
            PostJsonAsync(api, "/api/platform-connections", new { platform = (int)FoodPlatform.GetirYemek, storeId = "STORE-2", apiKey = "fake-key", apiSecret = "fake-secret" }),
            HttpStatusCode.Created, isOwner);
        await Expect("PATCH /api/platform-connections/{id}/active",
            api.SendAsync(HttpMethod.Patch, $"/api/platform-connections/{_connectionId}/active", JsonContent.Create(new { isActive = false })),
            HttpStatusCode.OK, isOwner);

        Assert.True(failures.Count == 0, $"{role}: " + string.Join("; ", failures));
        Assert.Equal(isOwner ? branches + 1 : branches, CountBranches());
        Assert.Equal(isOwner ? connections + 1 : connections, CountConnections());
        Assert.Equal(!isOwner, IsConnectionActive(_connectionId));
        // A session that lacks a role is still a current session: it is refused, not signed out.
        Assert.NotNull(api.AuthCookie);
    }

    [Fact]
    public async Task BranchCreate_AppliesTheSameValidationAsWeb()
    {
        using var web = await WebLoginAsync(_owner);
        using var api = ToApi(web);
        var before = CountBranches();

        // Web requires an address (CreateBranchCommandValidator).
        var response = await PostJsonAsync(api, "/api/branches", new { name = "No address", isActive = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, CountBranches());
    }

    // --- Cross-site requests ------------------------------------------------------------------------

    /// <summary>
    /// A cross-site page can make a browser send only "simple" requests without a CORS preflight: GET, HEAD or POST
    /// with a form or text body. The Api's writes are JSON-only (PATCH always needs a preflight), and the Api answers no
    /// preflight, so such a request is refused before the action runs even with a valid Owner session attached.
    /// </summary>
    [Fact]
    public async Task OwnerSession_SimpleCrossSiteRequestBodies_CannotMutate()
    {
        using var web = await WebLoginAsync(_owner);
        using var api = ToApi(web);
        var branches = CountBranches();
        var connections = CountConnections();
        const string branchJson = "{\"name\":\"Csrf\",\"address\":\"Street 3\",\"isActive\":true}";
        const string connectionJson = "{\"platform\":2,\"storeId\":\"CSRF\",\"apiKey\":\"k\",\"apiSecret\":\"s\"}";

        var responses = new[]
        {
            await api.SendAsync(HttpMethod.Post, "/api/branches", new StringContent(branchJson, Encoding.UTF8, "text/plain")),
            await api.SendAsync(HttpMethod.Post, "/api/branches", new FormUrlEncodedContent([new("name", "Csrf"), new("address", "Street 3")])),
            await api.SendAsync(HttpMethod.Post, "/api/branches", new MultipartFormDataContent { { new StringContent("Csrf"), "name" } }),
            await api.SendAsync(HttpMethod.Post, "/api/platform-connections", new StringContent(connectionJson, Encoding.UTF8, "text/plain")),
            await api.SendAsync(HttpMethod.Post, "/api/platform-connections", new FormUrlEncodedContent([new("platform", "2"), new("storeId", "CSRF")]))
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode));
        Assert.Equal(branches, CountBranches());
        Assert.Equal(connections, CountConnections());

        var preflightResponse = await api.SendAsync(HttpMethod.Options, $"/api/platform-connections/{_connectionId}/active", null,
        [
            new("Origin", "https://evil.example"),
            new("Access-Control-Request-Method", "PATCH"),
            new("Access-Control-Request-Headers", "content-type")
        ]);
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private async Task AssertStaleCookieRejectedAsync(TenantSessionClient web)
    {
        var branches = CountBranches();
        var connections = CountConnections();

        using (var read = ToApi(web))
        {
            AssertUnauthorized(await read.GetAsync("/api/branches"));
            AssertSessionDeleted(read);
        }

        using (var me = ToApi(web))
            AssertUnauthorized(await me.GetAsync("/api/auth/me"));

        // The attacker keeps the cookie value and replays it for each write.
        using (var write = ToApi(web))
            AssertUnauthorized(await PostJsonAsync(write, "/api/platform-connections",
                new { platform = (int)FoodPlatform.GetirYemek, storeId = "STORE-2", apiKey = "fake-key", apiSecret = "fake-secret" }));
        using (var toggle = ToApi(web))
            AssertUnauthorized(await toggle.SendAsync(HttpMethod.Patch, $"/api/platform-connections/{_connectionId}/active",
                JsonContent.Create(new { isActive = false })));
        using (var branch = ToApi(web))
            AssertUnauthorized(await PostJsonAsync(branch, "/api/branches", new { name = "Stale", address = "Street 9", isActive = true }));

        Assert.Equal(branches, CountBranches());
        Assert.Equal(connections, CountConnections());
        Assert.True(IsConnectionActive(_connectionId));
    }

    private static void AssertUnauthorized(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    private static void AssertSessionDeleted(TenantSessionClient client)
    {
        Assert.Null(client.AuthCookie);
        Assert.Contains(client.LastSetCookies, c => c.StartsWith(TenantAuthCookieNames.Active + "=;", StringComparison.Ordinal));
    }

    private async Task<TenantSessionClient> WebLoginAsync(AppUser user)
    {
        var client = _web.Client(TenantSessionWebHost.AlphaHost);
        await client.LoginAsync(user.Email);
        return client;
    }

    /// <summary>The Web session's cookies, presented to the Api on the same tenant host.</summary>
    private TenantSessionClient ToApi(TenantSessionClient web) => web.ReplayTo(_api.BaseAddress, web.Host);

    private static Task<HttpResponseMessage> PostJsonAsync(TenantSessionClient client, string path, object body) =>
        client.SendAsync(HttpMethod.Post, path, JsonContent.Create(body));

    private List<Claim> SessionClaims(AppUser user, UserRole role) =>
    [
        .. TenantSessionClaims.Create(_web.AlphaId, user.Id, role, user.SecurityStamp),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Name, user.FullName)
    ];

    private TenantDbContext AlphaDb() => new(
        new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(_web.Tenants.ConnectionStringFor(_web.AlphaId)).Options);

    private int CountBranches()
    {
        using var db = AlphaDb();
        return db.Branches.Count();
    }

    private int CountConnections()
    {
        using var db = AlphaDb();
        return db.PlatformConnections.Count();
    }

    private bool IsConnectionActive(Guid id)
    {
        using var db = AlphaDb();
        return db.PlatformConnections.AsNoTracking().Single(c => c.Id == id).IsActive;
    }

    private static async Task EditAsync(TenantSessionClient owner, AppUser target, UserRole role, bool isActive, string? newPassword = null)
    {
        var token = await owner.AntiforgeryTokenAsync($"/settings/users/{target.Id}/edit");
        var response = await owner.PostFormAsync($"/settings/users/{target.Id}/edit",
        [
            new("Id", target.Id.ToString()),
            new("Email", target.Email),
            new("FullName", target.FullName),
            new("Role", role.ToString()),
            new("IsActive", isActive ? "true" : "false"),
            new("NewPassword", newPassword ?? string.Empty),
            new("ConfirmPassword", newPassword ?? string.Empty),
            new("__RequestVerificationToken", token)
        ]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/settings/users/{target.Id}", response.Headers.Location!.OriginalString);
    }

    private static async Task ToggleAsync(TenantSessionClient owner, Guid targetId, string action)
    {
        var token = await owner.AntiforgeryTokenAsync($"/settings/users/{targetId}");
        var response = await owner.PostFormAsync($"/settings/users/{targetId}/{action}", [new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class SimulatedDbException() : DbException(Secret)
    {
        public const string Secret = "Server=tcp:secret-sql.example;Password=CONNSTR-SECRET-94b1";
    }

    private sealed class FixedPasswordReader(string password) : ICliPasswordReader
    {
        public CliSecretPrompt ReadSecret(string prompt) => new(true, password);
    }
}
