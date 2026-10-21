using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Security.RateLimiting;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Rate limiting of the Api's anonymous credential check, <c>POST /api/auth/validate</c> (WAS-90, WAS-94 review O-2).
/// Every test serves the real Api pipeline and registrations in-process (<see cref="TenantSessionApiHost"/>) next to the
/// Web host whose SQLite tenant databases it uses, with its own limits, so no partition is shared between tests. The
/// client connects from 127.0.0.1; forwarded addresses are documentation ranges. Users and passwords are fake.
/// </summary>
public sealed class ApiCredentialRateLimitTests : IAsyncLifetime
{
    private const string ValidatePath = "/api/auth/validate";

    private readonly CredentialCheckProbe _probe = new();
    private DirectoryInfo _keys = null!;
    private TenantSessionWebHost _web = null!;
    private TenantSessionApiHost _api = null!;
    private AppUser _alphaOwner = null!;
    private AppUser _betaOwner = null!;

    public ValueTask InitializeAsync()
    {
        _keys = Directory.CreateTempSubdirectory("wasla-rate-limit-keys-");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();
        if (_web is not null)
            await _web.DisposeAsync();
        try { _keys.Delete(recursive: true); } catch (IOException) { }
    }

    private async Task StartAsync(Dictionary<string, string?> configuration)
    {
        _web = await TenantSessionWebHost.StartAsync(_keys);
        _api = await TenantSessionApiHost.StartAsync(_web, _keys, configuration, _probe.Register);
        _alphaOwner = _web.SeedUser(_web.AlphaId, "owner@alpha.test", UserRole.Owner);
        _betaOwner = _web.SeedUser(_web.BetaId, "owner@beta.test", UserRole.Owner);
    }

    // --- Below and at the limit ---------------------------------------------------------------------

    [Fact]
    public async Task BelowTheLimit_AnswersAreUnchanged_AndTheFirstAttemptOverItGets429WithoutAnyCredentialWork()
    {
        await StartAsync(RateLimitTestConfig.Limits(3));
        using var client = _api.Client(TenantSessionWebHost.AlphaHost);

        Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(client, _alphaOwner.Email, TenantSessionWebHost.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ValidateAsync(client, _alphaOwner.Email, "Wrong-Password-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ValidateAsync(client, "nobody@alpha.test", "Wrong-Password-1")).StatusCode);
        Assert.Equal(3, _probe.TenantChecks);
        var opened = _web.Tenants.Opened.Count;

        // Correct credentials do not get past the limit either.
        var limited = await ValidateAsync(client, _alphaOwner.Email, TenantSessionWebHost.Password);

        await AssertApiRejectionAsync(limited, maxRetryAfterSeconds: 300);
        Assert.Equal(3, _probe.TenantChecks);
        Assert.Equal(opened, _web.Tenants.Opened.Count);
    }

    [Fact]
    public async Task RejectedAttempt_WithASessionCookie_DoesNotOpenTheTenantDatabase()
    {
        // A cookie would make authentication read the tenant database; the limiter runs before authentication.
        await StartAsync(RateLimitTestConfig.Limits(1));
        using var web = _web.Client(TenantSessionWebHost.AlphaHost);
        await web.LoginAsync(_alphaOwner.Email);
        using var api = web.ReplayTo(_api.BaseAddress, TenantSessionWebHost.AlphaHost);
        Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(api, _alphaOwner.Email, TenantSessionWebHost.Password)).StatusCode);
        var opened = _web.Tenants.Opened.Count;

        var limited = await ValidateAsync(api, _alphaOwner.Email, TenantSessionWebHost.Password);

        await AssertApiRejectionAsync(limited, maxRetryAfterSeconds: 300);
        Assert.Equal(opened, _web.Tenants.Opened.Count);
        Assert.Equal(1, _probe.TenantChecks);
    }

    [Fact]
    public async Task ConcurrentAttempts_NeverGetMorePermitsThanTheLimit()
    {
        const int permitLimit = 5;
        const int attempts = 30;
        await StartAsync(RateLimitTestConfig.Limits(permitLimit));
        // Permitted attempts stay inside the credential check until every other attempt has been answered.
        _probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = Enumerable.Range(0, attempts).Select(_ => _api.Client(TenantSessionWebHost.AlphaHost)).ToList();

        try
        {
            var requests = clients.Select(client => ValidateAsync(client, _alphaOwner.Email, TenantSessionWebHost.Password)).ToList();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (requests.Count(request => request.IsCompleted) < attempts - permitLimit)
                await Task.Delay(20, timeout.Token);

            var answered = requests.Where(request => request.IsCompleted).Select(request => request.Result).ToList();
            Assert.All(answered, response => Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode));
            Assert.Equal(permitLimit, _probe.TenantChecks);

            _probe.Gate.SetResult();
            var responses = await Task.WhenAll(requests);
            Assert.Equal(permitLimit, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(attempts - permitLimit, responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests));
            Assert.Equal(permitLimit, _probe.TenantChecks);
        }
        finally
        {
            _probe.Gate.TrySetResult();
            clients.ForEach(client => client.Dispose());
        }
    }

    [Fact]
    public async Task WhenTheWindowEnds_TheClientCanTryAgain()
    {
        // No clock seam in System.Threading.RateLimiting: a two-second window with bounded real-time waits. Which window an
        // attempt falls into is not assumed, so a slow machine cannot turn this into a timing race.
        await StartAsync(RateLimitTestConfig.Limits(1, "00:00:02"));
        using var client = _api.Client(TenantSessionWebHost.AlphaHost);
        var stopwatch = Stopwatch.StartNew();

        // Attempt until one is rejected: normally the second, but a window may end between two slow attempts.
        var permitted = 0;
        HttpResponseMessage limited;
        while (true)
        {
            limited = await ValidateAsync(client, _alphaOwner.Email, "Wrong-Password-1");
            if (limited.StatusCode == HttpStatusCode.TooManyRequests || stopwatch.Elapsed > TimeSpan.FromSeconds(10))
                break;
            Assert.Equal(HttpStatusCode.Unauthorized, limited.StatusCode);
            permitted++;
        }

        Assert.InRange(RateLimitTestConfig.RetryAfterSeconds(limited), 1, 2);

        // Then wait for the window to end: the next attempt is checked again.
        HttpResponseMessage retry;
        stopwatch.Restart();
        do
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            retry = await ValidateAsync(client, _alphaOwner.Email, TenantSessionWebHost.Password);
        }
        while (retry.StatusCode == HttpStatusCode.TooManyRequests && stopwatch.Elapsed < TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        // Exactly the attempts that were not rejected reached the credential check.
        Assert.Equal(permitted + 1, _probe.TenantChecks);
    }

    // --- Partitions ---------------------------------------------------------------------------------

    [Fact]
    public async Task EachTenant_HasItsOwnAllowance()
    {
        await StartAsync(RateLimitTestConfig.Limits(2));
        using var alpha = _api.Client(TenantSessionWebHost.AlphaHost);
        using var beta = _api.Client(TenantSessionWebHost.BetaHost);

        await ValidateAsync(alpha, _alphaOwner.Email, "Wrong-Password-1");
        await ValidateAsync(alpha, _alphaOwner.Email, "Wrong-Password-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ValidateAsync(alpha, _alphaOwner.Email, TenantSessionWebHost.Password)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ValidateAsync(beta, _betaOwner.Email, "Wrong-Password-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(beta, _betaOwner.Email, TenantSessionWebHost.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ValidateAsync(beta, _betaOwner.Email, TenantSessionWebHost.Password)).StatusCode);
    }

    // "": no trusted proxies at all; otherwise a trusted proxy that is not the connecting peer.
    [Theory]
    [InlineData("")]
    [InlineData("192.0.2.1")]
    public async Task ForwardedFor_FromAPeerThatIsNotATrustedProxy_DoesNotChangeThePartition(string trustedProxy)
    {
        var configuration = RateLimitTestConfig.Limits(2);
        if (trustedProxy.Length > 0)
            configuration.With("TrustedProxies:KnownProxies:0", trustedProxy);
        await StartAsync(configuration);
        using var client = _api.Client(TenantSessionWebHost.AlphaHost);

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 5; i++)
        {
            var response = await ValidateAsync(client, _alphaOwner.Email, "Wrong-Password-1",
                RateLimitTestConfig.ForwardedFor($"198.51.100.{i}"));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests },
            statuses);
        Assert.Equal(2, _probe.TenantChecks);
    }

    [Fact]
    public async Task ThroughATrustedProxy_EachForwardedClientNetwork_HasItsOwnPartition()
    {
        await StartAsync(RateLimitTestConfig.Limits(1).TrustingLoopbackProxy());
        using var client = _api.Client(TenantSessionWebHost.AlphaHost);

        async Task<HttpStatusCode> AttemptFrom(string forwardedFor) =>
            (await ValidateAsync(client, _alphaOwner.Email, "Wrong-Password-1", RateLimitTestConfig.ForwardedFor(forwardedFor))).StatusCode;

        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptFrom("198.51.100.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptFrom("198.51.100.10"));
        // Only the entry the trusted proxy appended counts; a value the client put in front of it is ignored.
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptFrom("203.0.113.77, 198.51.100.10"));
        // The IPv4-mapped IPv6 form is the same client.
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptFrom("::ffff:198.51.100.10"));
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptFrom("198.51.100.11"));

        // IPv6 clients are limited per /64.
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptFrom("2001:db8:1:2::1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptFrom("2001:db8:1:2:ffff:ffff:ffff:fffe"));
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptFrom("2001:db8:1:3::1"));

        // The proxy's own address (no forwarded value) is a partition of its own.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ValidateAsync(client, _alphaOwner.Email, "Wrong-Password-1")).StatusCode);
        Assert.Equal(5, _probe.TenantChecks);
    }

    [Fact]
    public async Task UnknownHost_IsStillAnswered404_NeverConsumesATenantsAllowance_AndOpensNoDatabase()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        using var unknown = _api.Client("unknown.app.wasla.local");

        for (var i = 0; i < 4; i++)
        {
            var response = await ValidateAsync(unknown, _alphaOwner.Email, TenantSessionWebHost.Password);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Tenant not found", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Empty(_web.Tenants.Opened);
        using var alpha = _api.Client(TenantSessionWebHost.AlphaHost);
        Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(alpha, _alphaOwner.Email, TenantSessionWebHost.Password)).StatusCode);
    }

    // --- Everything else is unchanged ---------------------------------------------------------------

    [Fact]
    public async Task ExhaustedCredentialCheck_DoesNotAffectSessionsDeviceTokensRootOrHealth()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
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

        using var web = _web.Client(TenantSessionWebHost.AlphaHost);
        await web.LoginAsync(_alphaOwner.Email);
        using var session = web.ReplayTo(_api.BaseAddress, TenantSessionWebHost.AlphaHost);
        using var anonymous = _api.Client(TenantSessionWebHost.AlphaHost);
        await ValidateAsync(anonymous, _alphaOwner.Email, "Wrong-Password-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ValidateAsync(anonymous, _alphaOwner.Email, "Wrong-Password-1")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(HttpMethod.Get, "/api/print-bridge/health", null, [new("X-PrintBridge-Token", rawToken)])).StatusCode);
        var noToken = await anonymous.GetAsync("/api/print-bridge/health");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal("print_bridge_token_required", noToken.Headers.GetValues("X-PrintBridge-Error-Code").Single());
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task TenantDatabaseUnavailable_BelowTheLimit_StillFailsWith500_AndTheAttemptCounts()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        _web.Tenants.Override(_web.AlphaId, _ => throw new SimulatedDbException());
        using var client = _api.Client(TenantSessionWebHost.AlphaHost);

        var failed = await ValidateAsync(client, _alphaOwner.Email, TenantSessionWebHost.Password);
        var limited = await ValidateAsync(client, _alphaOwner.Email, TenantSessionWebHost.Password);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await AssertApiRejectionAsync(limited, maxRetryAfterSeconds: 300);
        Assert.Equal(1, _probe.TenantChecks);
    }

    [Fact]
    public async Task Rejection_IsLoggedOncePerWindow_WithoutCredentialsAddressOrPartitionKey()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        using var client = _api.Client(TenantSessionWebHost.AlphaHost);
        await ValidateAsync(client, _alphaOwner.Email, "Secret-Attempt-1");
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.TooManyRequests, (await ValidateAsync(client, _alphaOwner.Email, "Secret-Attempt-1")).StatusCode);

        var entries = _api.Logs.Entries;
        var warning = Assert.Single(entries, entry => entry.StartsWith($"Warning {AuthenticationRateLimiting.LoggerCategory}:", StringComparison.Ordinal));
        Assert.Contains($"Policy={AuthenticationRateLimitPolicies.ApiCredentialValidation}", warning, StringComparison.Ordinal);
        Assert.Contains($"TenantId={_web.AlphaId}", warning, StringComparison.Ordinal);
        Assert.Matches("ClientTag=[0-9a-f]{12} ", warning);
        Assert.Contains("RetryAfterSeconds=", warning, StringComparison.Ordinal);
        foreach (var entry in entries)
        {
            Assert.DoesNotContain(_alphaOwner.Email, entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Secret-Attempt-1", entry, StringComparison.Ordinal);
            Assert.DoesNotContain(_alphaOwner.PasswordHash, entry, StringComparison.Ordinal);
            Assert.DoesNotContain($"{_web.AlphaId:N}|", entry, StringComparison.Ordinal);
        }

        // The host logs its own listening address at startup; the rate-limit log never names the client address.
        Assert.All(
            entries.Where(entry => entry.Contains(AuthenticationRateLimiting.LoggerCategory, StringComparison.Ordinal)),
            entry => Assert.DoesNotContain("127.0.0.1", entry, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedResponse_AddsNoCorsHeaders_AndPreflightIsStillNotAnswered()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        using var client = _api.Client(TenantSessionWebHost.AlphaHost);
        var origin = new KeyValuePair<string, string>("Origin", "http://beta.app.wasla.local");
        await ValidateAsync(client, _alphaOwner.Email, "Wrong-Password-1", origin);

        var limited = await ValidateAsync(client, _alphaOwner.Email, "Wrong-Password-1", origin);
        var preflight = await client.SendAsync(HttpMethod.Options, ValidatePath, null,
        [
            origin,
            new("Access-Control-Request-Method", "POST"),
            new("Access-Control-Request-Headers", "content-type")
        ]);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.False(limited.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> ValidateAsync(
        TenantSessionClient client,
        string email,
        string password,
        params KeyValuePair<string, string>[] headers) =>
        client.SendAsync(HttpMethod.Post, ValidatePath, JsonContent.Create(new { email, password }), headers);

    private static async Task AssertApiRejectionAsync(HttpResponseMessage response, int maxRetryAfterSeconds)
    {
        var retryAfter = RateLimitTestConfig.RetryAfterSeconds(response);
        Assert.InRange(retryAfter, 1, maxRetryAfterSeconds);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var property = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("error", property.Name);
        Assert.Equal("too_many_requests", property.Value.GetString());
    }

    private sealed class SimulatedDbException() : DbException("Simulated tenant database outage (test).");
}
