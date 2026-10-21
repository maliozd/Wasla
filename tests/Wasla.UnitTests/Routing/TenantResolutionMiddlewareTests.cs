using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.Checkout;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Tenant;
using Wasla.Web.Middleware;

namespace Wasla.UnitTests.Routing;

public sealed class TenantResolutionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenActiveTenantExists_ContinuesTenantFlow()
    {
        var tenantId = Guid.NewGuid();
        var resolver = new FakeTenantResolver(new ResolvedTenantDto(
            tenantId,
            "Sushim",
            "sushim",
            "sushim.app.wasla.local"));
        var pendingRegistrations = new FakePendingRegistrationService();
        var nextCalled = false;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("sushim.app.wasla.local");

        await InvokeAsync(middleware, context, resolver, pendingRegistrations);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Location.ToString()));
        Assert.Equal(tenantId, Assert.IsType<ResolvedTenantDto>(context.Items["CurrentTenant"]).Id);
        Assert.False(pendingRegistrations.WasCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenPendingRegistrationExists_RedirectsToRegistrationStatus()
    {
        var registrationId = Guid.NewGuid();
        var pendingRegistrations = new FakePendingRegistrationService(
            CreateSummary(registrationId, PendingRegistrationStatus.AwaitingPayment));
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("hasan-ustanin-yeri.app.wasla.local");

        await InvokeAsync(middleware, context, new FakeTenantResolver(null), pendingRegistrations);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal($"/signup/pending/{registrationId}", context.Response.Headers.Location.ToString());
        Assert.Equal("hasan-ustanin-yeri.app.wasla.local", pendingRegistrations.LastLookupHost);
    }

    [Fact]
    public async Task InvokeAsync_WhenNoTenantOrPendingRegistration_RedirectsToGenericUnknownTenantPage()
    {
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("unknown.app.wasla.local");

        await InvokeAsync(
            middleware,
            context,
            new FakeTenantResolver(null),
            new FakePendingRegistrationService());

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/tenant-not-found?host=unknown.app.wasla.local", context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.Cancelled)]
    [InlineData(PendingRegistrationStatus.Expired)]
    public async Task InvokeAsync_WhenRegistrationIsNotActive_RedirectsToGenericUnknownTenantPage(
        PendingRegistrationStatus status)
    {
        var pendingRegistrations = new FakePendingRegistrationService(CreateSummary(Guid.NewGuid(), status));
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("inactive.app.wasla.local");

        await InvokeAsync(middleware, context, new FakeTenantResolver(null), pendingRegistrations);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/tenant-not-found?host=inactive.app.wasla.local", context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.PaymentSucceeded)]
    [InlineData(PendingRegistrationStatus.Provisioned)]
    public async Task InvokeAsync_WhenRegistrationStatusHasStatusPage_RedirectsToRegistrationStatus(
        PendingRegistrationStatus status)
    {
        var registrationId = Guid.NewGuid();
        var pendingRegistrations = new FakePendingRegistrationService(CreateSummary(registrationId, status));
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("sushim.app.wasla.local");

        await InvokeAsync(middleware, context, new FakeTenantResolver(null), pendingRegistrations);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal($"/signup/pending/{registrationId}", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task InvokeAsync_PassesTheNormalizedHostToPendingRegistrationLookup()
    {
        var registrationId = Guid.NewGuid();
        var pendingRegistrations = new FakePendingRegistrationService(
            CreateSummary(registrationId, PendingRegistrationStatus.PaymentSucceeded));
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("HASAN-USTANIN-YERI.app.wasla.local.");

        await InvokeAsync(middleware, context, new FakeTenantResolver(null), pendingRegistrations);

        Assert.Equal($"/signup/pending/{registrationId}", context.Response.Headers.Location.ToString());
        Assert.Equal("hasan-ustanin-yeri.app.wasla.local", pendingRegistrations.LastLookupHost);
    }

    [Theory]
    [InlineData("sushim.app.wasla.local")]
    [InlineData("SUSHIM.App.Wasla.Local")]
    [InlineData("sushim.app.wasla.local.")]
    public async Task InvokeAsync_EverySpellingOfATenantHost_ResolvesThroughOneNormalizedHostAndCacheKey(string host)
    {
        var tenant = new ResolvedTenantDto(Guid.NewGuid(), "Sushim", "sushim", "sushim.app.wasla.local");
        var resolver = new FakeTenantResolver(tenant);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var context = CreateContext(host);
        context.Request.Host = new HostString(host, 7200);

        await InvokeAsync(new TenantResolutionMiddleware(_ => Task.CompletedTask), context, resolver, new FakePendingRegistrationService(), cache);

        Assert.Equal(new[] { "sushim.app.wasla.local" }, resolver.LookedUpHosts);
        Assert.Same(tenant, context.Items["CurrentTenant"]);
        Assert.True(cache.TryGetValue("tenant:sushim.app.wasla.local", out _));
    }

    [Fact]
    public async Task InvokeAsync_ACachedTenant_IsOnlyServedForItsOwnHost()
    {
        var alpha = new ResolvedTenantDto(Guid.NewGuid(), "Alpha", "alpha", "alpha.app.wasla.local");
        var beta = new ResolvedTenantDto(Guid.NewGuid(), "Beta", "beta", "beta.app.wasla.local");
        var resolver = new FakeTenantResolver(beta);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("tenant:alpha.app.wasla.local", alpha);
        var context = CreateContext("beta.app.wasla.local");

        await InvokeAsync(new TenantResolutionMiddleware(_ => Task.CompletedTask), context, resolver, new FakePendingRegistrationService(), cache);

        Assert.Same(beta, context.Items["CurrentTenant"]);
        Assert.Equal(new[] { "beta.app.wasla.local" }, resolver.LookedUpHosts);
    }

    [Theory]
    [InlineData("/auth/login", "/tenant-address-required?returnUrl=%2Fauth%2Flogin")]
    [InlineData("/", "/tenant-address-required")]
    [InlineData("/orders", "/tenant-address-required?returnUrl=%2Forders")]
    [InlineData("/pos", "/tenant-address-required?returnUrl=%2Fpos")]
    public async Task InvokeAsync_TheApplicationHost_IsNotATenant_AndAsksForTheTenantAddress(string path, string location)
    {
        var nextCalled = false;
        var context = CreateContext("APP.wasla.local");
        context.Request.Path = path;

        await InvokeAsync(
            new TenantResolutionMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }),
            context,
            new ThrowingTenantResolver(),
            new FakePendingRegistrationService());

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(location, context.Response.Headers.Location.ToString());
        Assert.False(context.Items.ContainsKey("CurrentTenant"));
    }

    [Theory]
    [InlineData("/tenant-address-required")]
    [InlineData("/admin/auth/login")]
    public async Task InvokeAsync_TheApplicationHost_StillServesTheTenantAddressPageAndCentralAdmin(string path)
    {
        var nextCalled = false;
        var context = CreateContext("app.wasla.local");
        context.Request.Path = path;

        await InvokeAsync(
            new TenantResolutionMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }),
            context,
            new ThrowingTenantResolver(),
            new FakePendingRegistrationService());

        Assert.True(nextCalled);
        Assert.False(context.Items.ContainsKey("CurrentTenant"));
    }

    [Theory]
    [InlineData("www.app.wasla.local")]
    [InlineData("admin.app.wasla.local")]
    [InlineData("api.app.wasla.local")]
    [InlineData("app.app.wasla.local")]
    [InlineData("nested.sushim.app.wasla.local")]
    [InlineData("sushim.app.wasla.local.evil.example")]
    [InlineData("app.wasla.local.evil.example")]
    [InlineData("sushim.wasla.local")]
    [InlineData("evil.example")]
    [InlineData("-bad.app.wasla.local")]
    public async Task InvokeAsync_AnInvalidHost_GetsTheUnknownTenantPage_WithoutAnyLookup(string host)
    {
        var nextCalled = false;
        var pending = new FakePendingRegistrationService();
        var context = CreateContext(host);

        await InvokeAsync(
            new TenantResolutionMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }),
            context,
            new ThrowingTenantResolver(),
            pending);

        Assert.False(nextCalled);
        Assert.False(pending.WasCalled);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal($"/tenant-not-found?host={Uri.EscapeDataString(host)}", context.Response.Headers.Location.ToString());
        Assert.False(context.Items.ContainsKey("CurrentTenant"));
    }

    [Fact]
    public async Task InvokeAsync_IgnoresForwardedHost()
    {
        var context = CreateContext("app.wasla.local");
        context.Request.Headers["X-Forwarded-Host"] = "sushim.app.wasla.local";

        await InvokeAsync(
            new TenantResolutionMiddleware(_ => Task.CompletedTask),
            context,
            new ThrowingTenantResolver(),
            new FakePendingRegistrationService());

        Assert.Equal("/tenant-address-required?returnUrl=%2Fauth%2Flogin", context.Response.Headers.Location.ToString());
        Assert.False(context.Items.ContainsKey("CurrentTenant"));
    }

    [Theory]
    [InlineData("wasla.local", "/")]
    [InlineData("WWW.wasla.local", "/pos")]
    [InlineData("localhost", "/")]
    public async Task InvokeAsync_TheMarketingHost_ServesItsPagesWithoutTenantResolution(string host, string path)
    {
        var nextCalled = false;
        var context = CreateContext(host);
        context.Request.Path = path;

        await InvokeAsync(
            new TenantResolutionMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }),
            context,
            new ThrowingTenantResolver(),
            new FakePendingRegistrationService());

        Assert.True(nextCalled);
        Assert.False(context.Items.ContainsKey("CurrentTenant"));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/error")]
    public async Task InvokeAsync_DiagnosticPaths_SkipTenantResolution(string path)
    {
        var called = false;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("sushim.app.wasla.local");
        context.Request.Path = path;

        await InvokeAsync(middleware, context, new ThrowingTenantResolver(), new FakePendingRegistrationService());

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    // --- Host and path policy (WAS-66) ---------------------------------------------------------------
    // Outcomes: "next" continues without a tenant, "tenant" continues with the resolved tenant, "address" redirects to
    // the tenant address page, "notfound" to the unknown-tenant page, "marketing" to the same path on the marketing host,
    // "404" is a plain status for machine clients.

    private const string Marketing = "wasla.local";
    private const string App = "app.wasla.local";
    private const string Tenant = "known.app.wasla.local";
    private const string Registration = "/signup/pending/0f8fad5b-d9cb-469f-a165-70867728950e";

    public static TheoryData<string, string, string> HostAndPathPolicy()
    {
        var data = new TheoryData<string, string, string>();
        void Row(string path, string marketing, string app, string tenant, string invalid)
        {
            data.Add(Marketing, path, marketing);
            data.Add("WWW.wasla.local", path, marketing);
            data.Add(App, path, app);
            data.Add(Tenant, path, tenant);
            foreach (var host in new[] { "evil.example", "known.app.wasla.local.evil.example", "admin.app.wasla.local", "a.known.app.wasla.local", "known.wasla.local" })
                data.Add(host, path, invalid);
        }

        // Pages of the marketing site and the tenant application.
        Row("/", "next", "address", "tenant", "notfound");
        Row("/pos", "next", "address", "tenant", "notfound");
        Row("/auth/login", "address", "address", "tenant", "notfound");
        Row("/orders", "next", "address", "tenant", "notfound");
        Row("/signupx", "next", "address", "tenant", "notfound");
        Row("/administrator", "next", "address", "tenant", "notfound");
        // Signup and checkout belong to the marketing host.
        Row("/signup", "next", "marketing", "marketing", "notfound");
        Row("/signup/", "next", "marketing", "marketing", "notfound");
        Row("/Signup", "next", "marketing", "marketing", "notfound");
        Row("/signup/districts", "next", "marketing", "marketing", "notfound");
        Row("/checkout", "next", "marketing", "marketing", "notfound");
        Row("/checkout/review/0f8fad5b-d9cb-469f-a165-70867728950e", "next", "marketing", "marketing", "notfound");
        // A pending tenant host's status page stays on that host; the application entry sends it to the marketing host.
        Row(Registration, "next", "marketing", "next", "notfound");
        Row("/signup/pending/not-a-guid", "next", "marketing", "marketing", "notfound");
        // Central Admin and the token-authenticated Print Bridge API: every valid Wasla host, as before.
        Row("/admin", "next", "next", "next", "notfound");
        Row("/admin/auth/login", "next", "next", "next", "notfound");
        Row("/api/print-bridge/jobs/claim", "next", "next", "next", "404");
        // The tenant address page: the marketing host and the application entry.
        Row("/tenant-address-required", "next", "next", "tenant", "notfound");
        Row("/customer-access-required", "next", "next", "tenant", "notfound");
        // Every host: failure pages, probes, culture and static assets, so nothing loops.
        foreach (var path in new[] { "/tenant-not-found", "/error", "/health/live", "/health/ready", "/culture/set", "/css/site.css", "/js/x.js", "/lib/x.js", "/images/x.svg", "/favicon.ico" })
            Row(path, "next", "next", "next", "next");
        return data;
    }

    [Theory]
    [MemberData(nameof(HostAndPathPolicy))]
    public async Task InvokeAsync_FollowsTheHostAndPathPolicy(string host, string path, string outcome)
    {
        var tenant = new ResolvedTenantDto(Guid.NewGuid(), "Known", "known", Tenant);
        var resolver = new FakeTenantResolver(tenant);
        var pending = new FakePendingRegistrationService();
        var nextCalled = false;
        var context = CreateContext(host);
        context.Request.Scheme = "https";
        var query = path.IndexOf('?');
        context.Request.Path = query < 0 ? path : path[..query];

        await InvokeAsync(
            new TenantResolutionMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }),
            context,
            resolver,
            pending);

        var location = context.Response.Headers.Location.ToString();
        var label = $"{host}{path}";
        switch (outcome)
        {
            case "next":
                Assert.True(nextCalled, label);
                Assert.False(context.Items.ContainsKey("CurrentTenant"), label);
                Assert.Empty(resolver.LookedUpHosts);
                break;
            case "tenant":
                Assert.True(nextCalled, label);
                Assert.Same(tenant, context.Items["CurrentTenant"]);
                break;
            case "address":
                Assert.False(nextCalled, label);
                Assert.StartsWith("/tenant-address-required", location);
                break;
            case "notfound":
                Assert.False(nextCalled, label);
                Assert.Equal($"/tenant-not-found?host={Uri.EscapeDataString(host)}", location);
                Assert.Empty(resolver.LookedUpHosts);
                Assert.False(pending.WasCalled, label);
                break;
            case "marketing":
                Assert.False(nextCalled, label);
                Assert.Equal($"https://wasla.local{context.Request.Path.ToUriComponent()}", location);
                Assert.Empty(resolver.LookedUpHosts);
                break;
            case "404":
                Assert.False(nextCalled, label);
                Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
                Assert.True(string.IsNullOrEmpty(location), label);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    [Fact]
    public async Task InvokeAsync_MovingSignupToTheMarketingHost_KeepsThePathAndQuery_AndNeverTheRequestHostOrPort()
    {
        var context = CreateContext(Tenant);
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(Tenant, 8443);
        context.Request.Path = "/signup";
        context.Request.QueryString = new QueryString("?plan=Pro");
        context.Request.Headers["X-Forwarded-Host"] = "evil.example";

        await InvokeAsync(new TenantResolutionMiddleware(_ => Task.CompletedTask), context, new ThrowingTenantResolver(), new FakePendingRegistrationService());

        Assert.Equal("https://wasla.local/signup?plan=Pro", context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/tenant-not-found")]
    [InlineData("/css/site.css")]
    public async Task InvokeAsync_WithoutAHost_StillServesTheAnyHostPaths(string path)
    {
        var nextCalled = false;
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await InvokeAsync(
            new TenantResolutionMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }),
            context,
            new ThrowingTenantResolver(),
            new FakePendingRegistrationService());

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("/signup")]
    [InlineData("/admin")]
    [InlineData("/")]
    public async Task InvokeAsync_WithoutAHost_RefusesEverythingElse(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await InvokeAsync(new TenantResolutionMiddleware(_ => Task.CompletedTask), context, new ThrowingTenantResolver(), new FakePendingRegistrationService());

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = "/auth/login";
        return context;
    }

    private static async Task InvokeAsync(
        TenantResolutionMiddleware middleware,
        HttpContext context,
        ITenantResolver resolver,
        IPendingRegistrationService pendingRegistrations)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await InvokeAsync(middleware, context, resolver, pendingRegistrations, cache);
    }

    private static async Task InvokeAsync(
        TenantResolutionMiddleware middleware,
        HttpContext context,
        ITenantResolver resolver,
        IPendingRegistrationService pendingRegistrations,
        IMemoryCache cache)
    {
        using var misses = new TenantHostMissCache(Options.Create(new TenantHostMissCacheOptions()));
        await middleware.InvokeAsync(
            context,
            cache,
            resolver,
            pendingRegistrations,
            misses,
            Options.Create(new TenantOnboardingOptions { MarketingBaseDomain = "wasla.local", ApplicationBaseDomain = "app.wasla.local" }),
            new Wasla.UnitTests.DevelopmentTools.TestHostEnvironment("Production"),
            NullLogger<TenantResolutionMiddleware>.Instance);
    }

    private static PendingRegistrationSummary CreateSummary(Guid id, PendingRegistrationStatus status) =>
        new(
            id,
            "Sushim",
            "sushim.app.wasla.local",
            "starter",
            "Monthly",
            status,
            "5424848618",
            null,
            "Owner",
            "owner@example.invalid",
            null,
            DateTime.UtcNow,
            status is PendingRegistrationStatus.PaymentSucceeded or PendingRegistrationStatus.Provisioned
                ? DateTime.UtcNow
                : null,
            status == PendingRegistrationStatus.Provisioned ? DateTime.UtcNow : null);

    private sealed class ThrowingTenantResolver : ITenantResolver
    {
        public Task<ResolvedTenantDto?> ResolveByHostAsync(string host, CancellationToken ct) =>
            throw new InvalidOperationException("Tenant resolution should not run.");
    }

    private sealed class FakeTenantResolver(ResolvedTenantDto? tenant) : ITenantResolver
    {
        public List<string> LookedUpHosts { get; } = new();

        public Task<ResolvedTenantDto?> ResolveByHostAsync(string host, CancellationToken ct)
        {
            LookedUpHosts.Add(host);
            return Task.FromResult(tenant);
        }
    }

    private sealed class FakePendingRegistrationService(PendingRegistrationSummary? pending = null)
        : IPendingRegistrationService
    {
        public bool WasCalled { get; private set; }

        public string? LastLookupHost { get; private set; }

        public Task<PendingRegistrationSummary?> GetActiveByPrimaryDomainAsync(string primaryDomain, CancellationToken ct)
        {
            WasCalled = true;
            LastLookupHost = primaryDomain;
            return Task.FromResult(IsActive(pending) ? pending : null);
        }

        public Task<bool> IsSlugAvailableAsync(string slug, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<PendingRegistrationResult> SubmitAsync(PendingRegistrationRequest request, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<PendingRegistrationSummary?> GetSummaryAsync(Guid registrationId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<PendingRegistrationCheckoutDetails?> GetCheckoutDetailsAsync(Guid registrationId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<CheckoutSimulationResult> SimulatePaymentSuccessAsync(Guid registrationId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<CheckoutSimulationResult> SimulatePaymentFailedAsync(Guid registrationId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<CheckoutSimulationResult> CancelRegistrationAsync(Guid registrationId, CancellationToken ct) =>
            throw new NotImplementedException();

        private static bool IsActive(PendingRegistrationSummary? summary) =>
            summary is not null
            && summary.Status is not PendingRegistrationStatus.PaymentFailed
            && summary.Status is not PendingRegistrationStatus.Cancelled
            && summary.Status is not PendingRegistrationStatus.Expired;
    }
}
