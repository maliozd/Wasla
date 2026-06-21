using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.Checkout;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
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
            "sushim.wasla.local"));
        var pendingRegistrations = new FakePendingRegistrationService();
        var nextCalled = false;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("sushim.wasla.local");

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
        var context = CreateContext("hasan-ustanin-yeri.wasla.local");

        await InvokeAsync(middleware, context, new FakeTenantResolver(null), pendingRegistrations);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal($"/signup/pending/{registrationId}", context.Response.Headers.Location.ToString());
        Assert.Equal("hasan-ustanin-yeri.wasla.local", pendingRegistrations.LastLookupHost);
    }

    [Fact]
    public async Task InvokeAsync_WhenNoTenantOrPendingRegistration_RedirectsToGenericUnknownTenantPage()
    {
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("unknown.wasla.local");

        await InvokeAsync(
            middleware,
            context,
            new FakeTenantResolver(null),
            new FakePendingRegistrationService());

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/tenant-not-found?host=unknown.wasla.local", context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.Cancelled)]
    [InlineData(PendingRegistrationStatus.Expired)]
    public async Task InvokeAsync_WhenRegistrationIsNotActive_RedirectsToGenericUnknownTenantPage(
        PendingRegistrationStatus status)
    {
        var pendingRegistrations = new FakePendingRegistrationService(CreateSummary(Guid.NewGuid(), status));
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("inactive.wasla.local");

        await InvokeAsync(middleware, context, new FakeTenantResolver(null), pendingRegistrations);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/tenant-not-found?host=inactive.wasla.local", context.Response.Headers.Location.ToString());
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
        var context = CreateContext("sushim.wasla.local");

        await InvokeAsync(middleware, context, new FakeTenantResolver(null), pendingRegistrations);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal($"/signup/pending/{registrationId}", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task InvokeAsync_PassesOriginalHostToPendingRegistrationLookup()
    {
        var registrationId = Guid.NewGuid();
        var pendingRegistrations = new FakePendingRegistrationService(
            CreateSummary(registrationId, PendingRegistrationStatus.PaymentSucceeded));
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("HASAN-USTANIN-YERI.wasla.local");

        await InvokeAsync(middleware, context, new FakeTenantResolver(null), pendingRegistrations);

        Assert.Equal($"/signup/pending/{registrationId}", context.Response.Headers.Location.ToString());
        Assert.Equal("HASAN-USTANIN-YERI.wasla.local", pendingRegistrations.LastLookupHost);
    }

    private static DefaultHttpContext CreateContext(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = "/auth/login";
        return context;
    }

    private static Task InvokeAsync(
        TenantResolutionMiddleware middleware,
        HttpContext context,
        ITenantResolver resolver,
        IPendingRegistrationService pendingRegistrations)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        return middleware.InvokeAsync(
            context,
            cache,
            resolver,
            pendingRegistrations,
            Options.Create(new CustomerOnboardingOptions { MarketingBaseDomain = "wasla.local" }),
            NullLogger<TenantResolutionMiddleware>.Instance);
    }

    private static PendingRegistrationSummary CreateSummary(Guid id, PendingRegistrationStatus status) =>
        new(
            id,
            "Sushim",
            "sushim.wasla.local",
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

    private sealed class FakeTenantResolver(ResolvedTenantDto? tenant) : ITenantResolver
    {
        public Task<ResolvedTenantDto?> ResolveByHostAsync(string host, CancellationToken ct) =>
            Task.FromResult(tenant);
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
