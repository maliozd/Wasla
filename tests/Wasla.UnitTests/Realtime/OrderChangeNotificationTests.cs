using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-24: every order write that can change the Live Screen announces an <see cref="OrderChanged"/> invalidation
/// after it is committed: provider ingestion (insert and update, as the Worker runs it) and every lifecycle action
/// (as Web and the Worker's automatic approval run it). Nothing is announced for an unchanged order, a refused or failed
/// action, or a provider failure, and a failing notifier never changes the result. Real services over one file-backed
/// SQLite database per tenant.
/// </summary>
public sealed class OrderChangeNotificationTests : IDisposable
{
    private readonly RecordingTenantDbFactory _tenants = new();
    private readonly RecordingTenantChangeNotifier _notifier = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly ScriptedPlatformClient _provider = new(FoodPlatform.TrendyolYemek);

    public OrderChangeNotificationTests()
    {
        _tenants.CreateTenantDatabase(_tenant);
        _tenants.CreateTenantDatabase(_otherTenant);
        OrderChangeFixtures.SeedSyncableTenant(_tenants, _tenant);
        OrderChangeFixtures.SeedSyncableTenant(_tenants, _otherTenant);
    }

    public void Dispose() => _tenants.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IReadOnlyList<OrderChanged> OrderChanges => _notifier.Sent.Select(s => s.Change).OfType<OrderChanged>().ToList();

    [Fact]
    public async Task IngestingANewOrder_AnnouncesIt_AfterItIsCommitted()
    {
        var committed = false;
        _notifier.OnNotify = async (tenantId, change) =>
        {
            // A separate connection sees only committed rows.
            await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
            committed = db.Orders.Any(o => o.Id == ((OrderChanged)change).OrderId);
        };
        _provider.Returns(OrderChangeFixtures.External("ING-1"));

        await OrderChangeFixtures.Sync(_tenants, _provider, _notifier).SyncCustomerAsync(_tenant, Ct);

        var order = await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "ING-1");
        var (tenantId, change) = Assert.Single(_notifier.Sent);
        Assert.Equal(_tenant, tenantId);
        Assert.Equal(OrderChanged.For(order.Id, order.UpdatedAt), change);
        Assert.True(committed);
    }

    [Fact]
    public async Task AProviderUpdate_IsAnnouncedAfterItsCommit_AndAnUnchangedOrderIsNot()
    {
        _provider.Returns(OrderChangeFixtures.External("UPD-1"));
        var sync = OrderChangeFixtures.Sync(_tenants, _provider, _notifier);
        await sync.SyncCustomerAsync(_tenant, Ct);

        // The same order again: nothing written, nothing announced.
        await sync.SyncCustomerAsync(_tenant, Ct);
        Assert.Single(_notifier.Sent);

        var accepted = false;
        _notifier.OnNotify = async (tenantId, change) =>
        {
            var stored = await OrderChangeFixtures.ReadOrderAsync(_tenants, tenantId, "UPD-1");
            accepted = stored.InternalStatus == OrderStatus.Accepted;
        };
        _provider.Returns(OrderChangeFixtures.External("UPD-1", status: "Picking"));
        await sync.SyncCustomerAsync(_tenant, Ct);

        var order = await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "UPD-1");
        Assert.Equal(2, OrderChanges.Count);
        Assert.Equal(OrderChanged.For(order.Id, order.UpdatedAt), OrderChanges[1]);
        Assert.True(accepted);
    }

    [Fact]
    public async Task EveryLifecycleAction_IsAnnouncedAfterItsSave()
    {
        _provider.Returns(OrderChangeFixtures.External("LIFE-1"), OrderChangeFixtures.External("LIFE-2"));
        await OrderChangeFixtures.Sync(_tenants, _provider, _notifier).SyncCustomerAsync(_tenant, Ct);
        var first = await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "LIFE-1");
        var second = await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "LIFE-2");
        var actions = OrderChangeFixtures.Actions(_tenants, _provider, _notifier);
        var before = OrderChanges.Count;
        var statusAtNotification = new List<OrderStatus>();
        _notifier.OnNotify = async (tenantId, change) =>
        {
            await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
            statusAtNotification.Add(db.Orders.Single(o => o.Id == ((OrderChanged)change).OrderId).InternalStatus);
        };

        Assert.True((await actions.TryApproveAsync(_tenant, first.Id, Ct)).Succeeded);
        Assert.True((await actions.MarkPreparingAsync(_tenant, first.Id, Ct)).Succeeded);
        Assert.True((await actions.MarkReadyForPickupAsync(_tenant, first.Id, Ct)).Succeeded);
        Assert.True((await actions.TryRejectAsync(_tenant, second.Id, Ct)).Succeeded);

        Assert.Equal(
            [OrderStatus.Accepted, OrderStatus.Preparing, OrderStatus.ReadyForPickup, OrderStatus.Cancelled],
            statusAtNotification);
        var announced = OrderChanges.Skip(before).ToList();
        Assert.Equal([first.Id, first.Id, first.Id, second.Id], announced.Select(c => c.OrderId));
        var finalFirst = await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "LIFE-1");
        Assert.Equal(OrderChanged.For(first.Id, finalFirst.UpdatedAt), announced[2]);
    }

    [Fact]
    public async Task ARefusedOrFailedAction_AnnouncesNothing_AndChangesNothing()
    {
        _provider.Returns(OrderChangeFixtures.External("FAIL-1"));
        await OrderChangeFixtures.Sync(_tenants, _provider, _notifier).SyncCustomerAsync(_tenant, Ct);
        var order = await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "FAIL-1");
        var actions = OrderChangeFixtures.Actions(_tenants, _provider, _notifier);
        var before = _notifier.Sent.Count;

        // Wrong status for the action; an unknown order; the provider refuses the call.
        Assert.False((await actions.MarkPreparingAsync(_tenant, order.Id, Ct)).Succeeded);
        Assert.False((await actions.TryApproveAsync(_tenant, Guid.NewGuid(), Ct)).Succeeded);
        _provider.FailActions = true;
        Assert.False((await actions.TryApproveAsync(_tenant, order.Id, Ct)).Succeeded);

        Assert.Equal(before, _notifier.Sent.Count);
        Assert.Equal(OrderStatus.New, (await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "FAIL-1")).InternalStatus);
    }

    [Fact]
    public async Task AFailingNotifier_DoesNotFailIngestionOrTheAction()
    {
        _notifier.Throw = new InvalidOperationException("channel down");
        _provider.Returns(OrderChangeFixtures.External("BROKEN-1"));

        var result = await OrderChangeFixtures.Sync(_tenants, _provider, _notifier).SyncCustomerWithResultAsync(_tenant, Ct);
        var order = await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "BROKEN-1");
        var approved = await OrderChangeFixtures.Actions(_tenants, _provider, _notifier).TryApproveAsync(_tenant, order.Id, Ct);

        Assert.Equal(1, result.InsertedCount);
        Assert.Equal(0, result.FailedConnections);
        Assert.True(approved.Succeeded);
        Assert.Equal(OrderStatus.Accepted, (await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "BROKEN-1")).InternalStatus);
        Assert.Equal(2, _notifier.Sent.Count);
    }

    [Fact]
    public async Task Announcements_NameTheTenantWhoseDatabaseChanged()
    {
        _provider.Returns(OrderChangeFixtures.External("SAME-ID"));
        var sync = OrderChangeFixtures.Sync(_tenants, _provider, _notifier);

        await sync.SyncCustomerAsync(_tenant, Ct);
        await sync.SyncCustomerAsync(_otherTenant, Ct);

        var a = await OrderChangeFixtures.ReadOrderAsync(_tenants, _tenant, "SAME-ID");
        var b = await OrderChangeFixtures.ReadOrderAsync(_tenants, _otherTenant, "SAME-ID");
        Assert.Equal([(_tenant, a.Id), (_otherTenant, b.Id)], _notifier.Sent.Select(s => (s.TenantId, ((OrderChanged)s.Change).OrderId)));
    }
}
