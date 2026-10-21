using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.Checkout;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Tenant;
using Wasla.UnitTests.Auth;
using WebTenantResolutionMiddleware = Wasla.Web.Middleware.TenantResolutionMiddleware;

namespace Wasla.UnitTests.Routing;

/// <summary>
/// Unknown tenant hosts (WAS-90): a host without a tenant costs at most one CentralDb lookup per host per TTL, concurrent
/// requests for it share one lookup, the cache is bounded in size, and an entry only ever answers for the exact host that
/// produced it. Covered on the cache itself, through the real Web tenant-resolution middleware with counting fakes, and
/// through the real Api pipeline over SQLite.
/// </summary>
public sealed class TenantHostMissCacheTests
{
    private static readonly ResolvedTenantDto Alpha = new(Guid.NewGuid(), "Alpha", "alpha", "alpha.app.wasla.local");

    // --- The cache ------------------------------------------------------------------------------------

    [Fact]
    public async Task RepeatedMisses_AreLookedUpOnce()
    {
        using var cache = Cache();
        var lookups = 0;

        for (var i = 0; i < 10; i++)
        {
            var result = await cache.GetOrLookupAsync("unknown.app.wasla.local", () =>
            {
                Interlocked.Increment(ref lookups);
                return Task.FromResult(TenantHostLookup.NotFound);
            }, TestContext.Current.CancellationToken);
            Assert.True(result.IsMiss);
        }

        Assert.Equal(1, lookups);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneHost_ShareOneLookup()
    {
        using var cache = Cache();
        var lookups = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var requests = Enumerable.Range(0, 50).Select(_ => Task.Run(() => cache.GetOrLookupAsync("unknown.app.wasla.local", async () =>
        {
            Interlocked.Increment(ref lookups);
            started.TrySetResult();
            await release.Task;
            return TenantHostLookup.NotFound;
        }, TestContext.Current.CancellationToken))).ToList();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        release.SetResult();
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, lookups);
        Assert.All(results, result => Assert.True(result.IsMiss));
    }

    [Fact]
    public async Task AMiss_ExpiresAfterTheTtl()
    {
        var clock = new ManualClock();
        using var cache = Cache(ttl: TimeSpan.FromSeconds(60), clock: clock);
        var lookups = 0;
        Task<TenantHostLookup> Lookup()
        {
            Interlocked.Increment(ref lookups);
            return Task.FromResult(TenantHostLookup.NotFound);
        }

        await cache.GetOrLookupAsync("new.app.wasla.local", Lookup, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(59));
        await cache.GetOrLookupAsync("new.app.wasla.local", Lookup, TestContext.Current.CancellationToken);
        Assert.Equal(1, lookups);

        // A tenant provisioned for the host meanwhile is found once the entry expires.
        clock.Advance(TimeSpan.FromSeconds(2));
        var found = await cache.GetOrLookupAsync("new.app.wasla.local",
            () => Task.FromResult(new TenantHostLookup(Alpha)), TestContext.Current.CancellationToken);

        Assert.Same(Alpha, found.Tenant);
    }

    [Fact]
    public async Task ManyRandomHosts_NeverGrowTheCacheBeyondItsBound()
    {
        using var cache = Cache(maxEntries: 5);
        var lookups = 0;

        for (var i = 0; i < 200; i++)
        {
            var result = await cache.GetOrLookupAsync($"random-{i}.app.wasla.local", () =>
            {
                Interlocked.Increment(ref lookups);
                return Task.FromResult(TenantHostLookup.NotFound);
            }, TestContext.Current.CancellationToken);
            Assert.True(result.IsMiss);
            Assert.InRange(cache.Count, 0, 5);
        }

        // Every host was still answered correctly (looked up once each).
        Assert.Equal(200, lookups);
    }

    [Fact]
    public async Task TenantsAndFailures_AreNotKeptAsMisses()
    {
        using var cache = Cache();
        var lookups = 0;

        await cache.GetOrLookupAsync("alpha.app.wasla.local", () =>
        {
            Interlocked.Increment(ref lookups);
            return Task.FromResult(new TenantHostLookup(Alpha));
        }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrLookupAsync("down.app.wasla.local",
            () => throw new InvalidOperationException("Simulated CentralDb failure (test)."), TestContext.Current.CancellationToken));
        var afterFailure = await cache.GetOrLookupAsync("down.app.wasla.local", () =>
        {
            Interlocked.Increment(ref lookups);
            return Task.FromResult(new TenantHostLookup(Alpha));
        }, TestContext.Current.CancellationToken);

        Assert.Same(Alpha, afterFailure.Tenant);
        Assert.Equal(2, lookups);
        Assert.Equal(0, cache.Count);
    }

    [Theory]
    [InlineData("Alpha.app.WASLA.local", "alpha.app.wasla.local")]
    [InlineData("  alpha.app.wasla.local ", "alpha.app.wasla.local")]
    [InlineData("alpha.app.wasla.local.", "alpha.app.wasla.local.")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Key_IsTheHostAsTheResolverLooksItUp(string host, string? expected)
    {
        Assert.Equal(expected, TenantHostMissCache.Key(host));
    }

    [Theory]
    [InlineData("TenantResolution:MissCache:Ttl", "00:00:00")]
    [InlineData("TenantResolution:MissCache:Ttl", "00:30:00")]
    [InlineData("TenantResolution:MissCache:MaxEntries", "0")]
    [InlineData("TenantResolution:MissCache:MaxEntries", "-5")]
    public void InvalidSettings_FailValidation(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
        using var provider = Wasla.Infrastructure.DependencyInjection.TenantHostMissCacheRegistration
            .AddWaslaTenantHostMissCache(new ServiceCollection(), configuration)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TenantHostMissCacheOptions>>().Value);
    }

    [Fact]
    public void Defaults_AreAShortTtlAndABoundedSize()
    {
        var options = new TenantHostMissCacheOptions();

        Assert.Equal(TimeSpan.FromSeconds(60), options.Ttl);
        Assert.Equal(10_000, options.MaxEntries);
    }

    // --- Through the Web middleware ------------------------------------------------------------------

    [Fact]
    public async Task WebMiddleware_RepeatedAndConcurrentUnknownHostRequests_QueryCentralDbOnce()
    {
        var resolver = new CountingResolver(Alpha);
        var pending = new CountingPendingRegistrations();
        using var web = new WebMiddlewareHarness(resolver, pending);
        resolver.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var concurrent = Enumerable.Range(0, 20).Select(_ => Task.Run(() => web.InvokeAsync("unknown.app.wasla.local"))).ToList();
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        resolver.Gate.SetResult();
        var contexts = (await Task.WhenAll(concurrent)).ToList();
        for (var i = 0; i < 10; i++)
            contexts.Add(await web.InvokeAsync("unknown.app.wasla.local"));

        Assert.Equal(1, resolver.Lookups("unknown.app.wasla.local"));
        Assert.Equal(1, pending.Lookups);
        Assert.All(contexts, context => Assert.Equal("/tenant-not-found?host=unknown.app.wasla.local", context.Response.Headers.Location.ToString()));
    }

    [Fact]
    public async Task WebMiddleware_APendingRegistrationHost_IsAlsoLookedUpOnce()
    {
        var registrationId = Guid.NewGuid();
        var resolver = new CountingResolver(Alpha);
        var pending = new CountingPendingRegistrations(registrationId);
        using var web = new WebMiddlewareHarness(resolver, pending);

        for (var i = 0; i < 5; i++)
        {
            var context = await web.InvokeAsync("sushim.app.wasla.local");
            Assert.Equal($"/signup/pending/{registrationId}", context.Response.Headers.Location.ToString());
        }

        Assert.Equal(1, resolver.Lookups("sushim.app.wasla.local"));
        Assert.Equal(1, pending.Lookups);
    }

    [Fact]
    public async Task WebMiddleware_AMissNeverAnswersForAnotherHostOrTheTenant()
    {
        var resolver = new CountingResolver(Alpha);
        using var web = new WebMiddlewareHarness(resolver, new CountingPendingRegistrations());

        // Misses for another tenant host and a forwarded host header pointing at the tenant; a look-alike host is refused
        // before any lookup (WAS-66). Case, a trailing dot and a port are the tenant's own host.
        var lookAlike = await web.InvokeAsync("alpha.app.wasla.local.evil.example");
        var otherTenant = await web.InvokeAsync("beta.app.wasla.local");
        var forwarded = await web.InvokeAsync("evil.app.wasla.local", forwardedHost: "alpha.app.wasla.local");
        var tenant = await web.InvokeAsync("alpha.app.wasla.local");
        var upperCase = await web.InvokeAsync("ALPHA.app.WASLA.LOCAL");
        var trailingDot = await web.InvokeAsync("alpha.app.wasla.local.");
        var withPort = await web.InvokeAsync("alpha.app.wasla.local", port: 8443);

        Assert.StartsWith("/tenant-not-found", lookAlike.Response.Headers.Location.ToString());
        Assert.StartsWith("/tenant-not-found", otherTenant.Response.Headers.Location.ToString());
        Assert.Equal("/tenant-not-found?host=evil.app.wasla.local", forwarded.Response.Headers.Location.ToString());
        foreach (var context in new[] { tenant, upperCase, trailingDot, withPort })
        {
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Same(Alpha, context.Items["CurrentTenant"]);
        }

        Assert.Equal(2, web.Misses.Count);
    }

    // --- Through the Api pipeline --------------------------------------------------------------------

    [Fact]
    public async Task ApiPipeline_UnknownHostRequests_QueryCentralDbOnce_AndTenantsStillResolve()
    {
        var keys = Directory.CreateTempSubdirectory("wasla-miss-cache-keys-");
        var lookups = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        await using var web = await TenantSessionWebHost.StartAsync(keys);
        await using var api = await TenantSessionApiHost.StartAsync(web, keys, configureServices: services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(ITenantResolver));
            services.Remove(real);
            services.AddScoped(real.ImplementationType!);
            services.AddScoped<ITenantResolver>(provider =>
                new RecordingResolver((ITenantResolver)provider.GetRequiredService(real.ImplementationType!), lookups));
        });
        try
        {
            var concurrent = Enumerable.Range(0, 20).Select(async _ =>
            {
                using var client = api.Client("unknown.app.wasla.local");
                return (await client.GetAsync("/api/auth/me")).StatusCode;
            }).ToList();
            var statuses = (await Task.WhenAll(concurrent)).ToList();
            using (var client = api.Client("unknown.app.wasla.local"))
            {
                for (var i = 0; i < 10; i++)
                    statuses.Add((await client.SendAsync(HttpMethod.Post, "/api/auth/validate",
                        JsonContent.Create(new { email = "a@b.test", password = "x" }))).StatusCode);
            }

            using var alpha = api.Client(TenantSessionWebHost.AlphaHost);
            var tenantResponse = await alpha.GetAsync("/api/auth/me");

            Assert.All(statuses, status => Assert.Equal(HttpStatusCode.NotFound, status));
            Assert.Equal(1, lookups.GetValueOrDefault("unknown.app.wasla.local"));
            Assert.Equal(HttpStatusCode.Unauthorized, tenantResponse.StatusCode);
            Assert.Equal(1, lookups.GetValueOrDefault(TenantSessionWebHost.AlphaHost));
        }
        finally
        {
            try { keys.Delete(recursive: true); } catch (IOException) { }
        }
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static TenantHostMissCache Cache(TimeSpan? ttl = null, int maxEntries = 100, ISystemClock? clock = null) =>
        new(Options.Create(new TenantHostMissCacheOptions { Ttl = ttl ?? TimeSpan.FromSeconds(60), MaxEntries = maxEntries }), clock);

    private sealed class ManualClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>The real Web middleware with one shared tenant cache and miss cache, as in a running process.</summary>
    private sealed class WebMiddlewareHarness(ITenantResolver resolver, IPendingRegistrationService pending) : IDisposable
    {
        private readonly MemoryCache _tenants = new(new MemoryCacheOptions());

        public TenantHostMissCache Misses { get; } = Cache();

        public async Task<HttpContext> InvokeAsync(string host, int? port = null, string? forwardedHost = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Host = port is null ? new HostString(host) : new HostString(host, port.Value);
            context.Request.Path = "/auth/login";
            if (forwardedHost is not null)
                context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
            var middleware = new WebTenantResolutionMiddleware(_ => Task.CompletedTask);
            await middleware.InvokeAsync(
                context,
                _tenants,
                resolver,
                pending,
                Misses,
                Options.Create(new TenantOnboardingOptions { MarketingBaseDomain = "wasla.local", ApplicationBaseDomain = "app.wasla.local" }),
                new Wasla.UnitTests.DevelopmentTools.TestHostEnvironment("Production"),
                NullLogger<WebTenantResolutionMiddleware>.Instance);
            return context;
        }

        public void Dispose()
        {
            _tenants.Dispose();
            Misses.Dispose();
        }
    }

    /// <summary>Resolves only <paramref name="tenant"/>'s domain, with the real resolver's normalization.</summary>
    private sealed class CountingResolver(ResolvedTenantDto tenant) : ITenantResolver
    {
        private readonly ConcurrentDictionary<string, int> _lookups = new(StringComparer.Ordinal);

        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Lookups(string host) => _lookups.GetValueOrDefault(host.Trim().ToLowerInvariant());

        public async Task<ResolvedTenantDto?> ResolveByHostAsync(string host, CancellationToken ct)
        {
            var normalized = host.Trim().ToLowerInvariant();
            _lookups.AddOrUpdate(normalized, 1, (_, count) => count + 1);
            Started.TrySetResult();
            if (Gate is not null)
                await Gate.Task;
            return normalized == tenant.PrimaryDomain ? tenant : null;
        }
    }

    private sealed class RecordingResolver(ITenantResolver inner, ConcurrentDictionary<string, int> lookups) : ITenantResolver
    {
        public Task<ResolvedTenantDto?> ResolveByHostAsync(string host, CancellationToken ct)
        {
            lookups.AddOrUpdate(host.Trim().ToLowerInvariant(), 1, (_, count) => count + 1);
            return inner.ResolveByHostAsync(host, ct);
        }
    }

    private sealed class CountingPendingRegistrations(Guid? registrationId = null) : IPendingRegistrationService
    {
        private int _lookups;

        public int Lookups => Volatile.Read(ref _lookups);

        public Task<PendingRegistrationSummary?> GetActiveByPrimaryDomainAsync(string primaryDomain, CancellationToken ct)
        {
            Interlocked.Increment(ref _lookups);
            return Task.FromResult(registrationId is Guid id
                ? new PendingRegistrationSummary(id, "Sushim", primaryDomain, "starter", "Monthly", PendingRegistrationStatus.AwaitingPayment,
                    "5550000000", null, "Owner", "owner@example.test", null, DateTime.UtcNow, null, null)
                : null);
        }

        public Task<bool> IsSlugAvailableAsync(string slug, CancellationToken ct) => throw new NotSupportedException();

        public Task<PendingRegistrationResult> SubmitAsync(PendingRegistrationRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<PendingRegistrationSummary?> GetSummaryAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<PendingRegistrationCheckoutDetails?> GetCheckoutDetailsAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<CheckoutSimulationResult> SimulatePaymentSuccessAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<CheckoutSimulationResult> SimulatePaymentFailedAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<CheckoutSimulationResult> CancelRegistrationAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
}
