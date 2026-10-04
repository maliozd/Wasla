using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Demos;
using Wasla.Domain.Enums;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.Demos;

/// <summary>
/// The Live Screen polls every few seconds. Order training is Owner-only, so only an Owner can own a practice order,
/// and the per-poll lookups must not run for anyone else.
/// </summary>
public sealed class LiveDataDemoLookupTests
{
    private static readonly ResolvedTenantDto Tenant = new(Guid.NewGuid(), "Demo Tenant", "demo-tenant", "demo-tenant.wasla.local");

    [Fact]
    public async Task ANonOwner_DoesNotTriggerTheDemoLookup()
    {
        var demos = new CountingDemos();
        var controller = CreateController(demos, isOwner: false);

        var result = await controller.LiveData(NoTraining(), new FakeTenantModes(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, demos.LookupCount);
    }

    [Fact]
    public async Task AnOwner_StillLooksUpTheirOwnDemo()
    {
        var demos = new CountingDemos();
        var userId = Guid.NewGuid();
        var controller = CreateController(demos, isOwner: true, userId);

        var result = await controller.LiveData(NoTraining(), new FakeTenantModes(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, demos.LookupCount);
        Assert.Equal(userId, demos.LastUserId);
    }

    /// <summary>Guided setup with nobody training and a live tenant: the normal snapshot.</summary>
    private static GuidedSetupCoordinator NoTraining() =>
        new(new FakeGuidedSetup(), new FixedNavigation(new TenantNavigationPermissions(true, true, true, true, true, true, true, true, true, true, true)),
            new FakeSetupStatus(), new FakeDemos(), new FakePrintBridgeDevices(), new CapturingLogger());

    private static OrdersController CreateController(CountingDemos demos, bool isOwner, Guid? userId = null)
    {
        var controller = new OrdersController(
            new FixedTenant(),
            new EmptySnapshot(),
            actions: null!,
            orderSyncSettings: null!,
            orderSettings: null!,
            receiptCreation: null!,
            manualPrint: null!,
            demos: demos,
            authorization: new PolicyResult(isOwner),
            orderSettingsValidator: null!,
            logger: NullLogger<OrdersController>.Instance,
            localizer: null!);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString())],
            "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private sealed class FixedTenant : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant => Tenant;
    }

    private sealed class EmptySnapshot : IOrderReadService
    {
        public Task<LiveScreenSnapshotResult> GetLiveScreenSnapshotAsync(Guid customerId, CancellationToken ct) =>
            Task.FromResult(new LiveScreenSnapshotResult(DateTime.UtcNow, [], 0, 0));

        public Task<OrderListResult> GetListAsync(Guid customerId, FoodPlatform? platform, OrderStatus? status,
            DateTime? startDateUtc, DateTime? endDateUtc, string? sortBy, string? sortDirection, int page, int pageSize,
            string? search, CancellationToken ct, bool includeLineItems = false) =>
            throw new NotSupportedException();

        public Task<OrderDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> CountReceivedSinceAsync(Guid customerId, DateTime sinceUtc, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class PolicyResult : IAuthorizationService
    {
        private readonly bool _isOwner;

        public PolicyResult(bool isOwner) => _isOwner = isOwner;

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(policyName == TenantPolicies.TenantOwner && _isOwner
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            throw new NotSupportedException();
    }

    private sealed class CountingDemos : IGuidedDemoService
    {
        public int LookupCount { get; private set; }
        public Guid? LastUserId { get; private set; }

        public Task<GuidedDemoSessionState?> GetForLiveScreenAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            LookupCount++;
            LastUserId = userId;
            return Task.FromResult<GuidedDemoSessionState?>(null);
        }

        public Task<GuidedDemoSessionState?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GuidedDemoSummary?> GetLatestAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GuidedDemoSessionState> StartAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GuidedDemoActionResult> ApplyActionAsync(Guid tenantId, Guid userId, Guid sessionId, string action,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task FinishActiveAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
