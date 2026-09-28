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
using Wasla.Web.Security;

namespace Wasla.UnitTests.Demos;

/// <summary>
/// The Live Screen polls every few seconds. Only users who may run demo actions can own a demo,
/// so the per-poll demo lookup must not run for anyone else.
/// </summary>
public sealed class LiveDataDemoLookupTests
{
    private static readonly ResolvedTenantDto Tenant = new(Guid.NewGuid(), "Demo Tenant", "demo-tenant", "demo-tenant.wasla.local");

    [Fact]
    public async Task UserWithoutOrderManagement_DoesNotTriggerTheDemoLookup()
    {
        var demos = new CountingDemos();
        var controller = CreateController(demos, canManageOrders: false);

        var result = await controller.LiveData(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, demos.LookupCount);
    }

    [Fact]
    public async Task UserWithOrderManagement_StillLooksUpTheirOwnDemo()
    {
        var demos = new CountingDemos();
        var userId = Guid.NewGuid();
        var controller = CreateController(demos, canManageOrders: true, userId);

        var result = await controller.LiveData(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, demos.LookupCount);
        Assert.Equal(userId, demos.LastUserId);
    }

    private static OrdersController CreateController(CountingDemos demos, bool canManageOrders, Guid? userId = null)
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
            authorization: new PolicyResult(canManageOrders),
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
    }

    private sealed class PolicyResult : IAuthorizationService
    {
        private readonly bool _canManageOrders;

        public PolicyResult(bool canManageOrders) => _canManageOrders = canManageOrders;

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(policyName == TenantPolicies.CanManageOrders && _canManageOrders
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

        public Task<GuidedDemoSessionState> StartAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GuidedDemoActionResult> ApplyActionAsync(Guid tenantId, Guid userId, Guid sessionId, string action,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task FinishActiveAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
