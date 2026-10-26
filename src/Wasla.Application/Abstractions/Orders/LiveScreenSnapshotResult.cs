using System.Text.Json.Serialization;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Orders;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Orders;

public sealed record LiveScreenSnapshotResult(
    DateTime ServerTimeUtc,
    IReadOnlyList<LiveScreenOrderDto> Orders,
    int TodayOrderCount,
    int CancelledOrderCount)
{
    /// <summary>
    /// Set only on the snapshot of a user who is in order training: their Live Screen is isolated from real orders,
    /// whatever the tenant's operational mode. Null, and serialized as null, for everyone else.
    /// </summary>
    public LiveScreenTrainingIsolation? Training { get; init; }

    /// <summary>
    /// The effective state of each automation for the Live Screen's status indicators, for every Live Screen user.
    /// Read-only operational status: no setting, credential or configuration value. Null, and serialized as null, only
    /// when the status could not be read; the page then keeps what it showed.
    /// </summary>
    public LiveScreenAutomationStatus? Automation { get; init; }

    /// <summary>
    /// Whether platform orders are actually being synchronized, from the tenant's platform connection records (see
    /// <see cref="LiveScreenSyncStatus"/>), for every Live Screen user. Null, and serialized as null, only when the
    /// status could not be read; the page then shows a connection issue rather than a "Live" it cannot back up.
    /// </summary>
    public LiveScreenSyncStatus? Sync { get; init; }
}

/// <summary>
/// The Live Screen's connection indicator. An open page, a working poll or a successful request is never evidence: every
/// state comes from the tenant's platform connection records (see <c>LiveScreenSyncRules</c>).
/// </summary>
public enum LiveScreenSyncState
{
    /// <summary>The tenant has no platform connection.</summary>
    NotConnected = 0,

    /// <summary>
    /// Synchronization is switched off: the tenant's Order Sync setting is off, or every connection is deactivated. For
    /// one platform: that connection is deactivated, or the tenant's Order Sync setting is off.
    /// </summary>
    SyncDisabled = 1,

    /// <summary>Every active connection completed a sync recently enough, and none failed its last attempt.</summary>
    Live = 2,

    /// <summary>
    /// An active connection failed its last attempt, is paused after repeated failures, has never completed a sync, or
    /// has not completed one for too long. One such connection is enough: other healthy platforms do not hide it.
    /// </summary>
    Issue = 3
}

/// <summary>
/// The connection indicator: the overall state, the oldest last successful sync of the active connections (null when
/// one has never completed a sync, or none is active), and each platform's own state. Read-only status: no store id,
/// credential, failure count or error text.
/// </summary>
public sealed record LiveScreenSyncStatus(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] LiveScreenSyncState State,
    DateTime? LastSuccessfulSyncUtc,
    IReadOnlyList<LiveScreenPlatformSync> Platforms);

public sealed record LiveScreenPlatformSync(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] FoodPlatform Platform,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] LiveScreenSyncState State);

/// <summary>
/// What each automation does right now: Active, Off, or PendingSetup (configured on, waiting for the tenant to leave
/// Setup). Order synchronization is never PendingSetup. Only effective states are sent, not the settings themselves.
/// </summary>
public sealed record LiveScreenAutomationStatus(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AutomationState OrderSync,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AutomationState AutoApprove,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AutomationState AutoReceipt)
{
    public static LiveScreenAutomationStatus From(TenantAutomationStatus status) =>
        new(status.OrderSync, status.AutoApprove, status.AutoReceipt);
}

/// <summary>
/// An isolated trainee's snapshot: real orders are left out of <see cref="LiveScreenSnapshotResult.Orders"/> (they
/// are still synchronized and kept), so the practice board holds only the practice order. Training never hides the
/// real backlog: <paramref name="WaitingRealOrders"/> is how many real orders are active (the count the header shows
/// outside training), and <paramref name="RealOrderIds"/> are the ids of every real order the normal snapshot holds,
/// so the Live Screen announces a new one exactly once, during training and after it. Ids only: no order or customer
/// detail is carried here.
/// </summary>
public sealed record LiveScreenTrainingIsolation(int WaitingRealOrders, IReadOnlyList<Guid> RealOrderIds)
{
    public bool Isolated => true;

    /// <summary>The isolation of <paramref name="snapshot"/>'s real orders, read before they leave its orders.</summary>
    public static LiveScreenTrainingIsolation Of(LiveScreenSnapshotResult snapshot)
    {
        var ids = new List<Guid>(snapshot.Orders.Count);
        var waiting = 0;
        foreach (var order in snapshot.Orders)
        {
            if (order.IsDemo)
                continue;
            ids.Add(order.Id);
            if (LiveScreenVisibility.IsActive(order.Status))
                waiting++;
        }

        return new LiveScreenTrainingIsolation(waiting, ids);
    }
}

public sealed record LiveScreenOrderDto(
    Guid Id,
    string DisplayNumber,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] FoodPlatform Platform,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] OrderStatus Status,
    DateTime ReceivedAtUtc,
    DateTime? DeliveredAtUtc,
    string CustomerName,
    string CustomerAddress,
    string? CustomerNote,
    decimal TotalAmount,
    IReadOnlyList<LiveScreenLineItemDto> Items,
    bool IsDemo = false)
{
    /// <summary>
    /// Only on the user's own practice order: the automatic step waiting on it and its deadline, from the same rule
    /// the Worker uses. The Live Screen shows a countdown from it and never acts on it. Null for every real order.
    /// </summary>
    public LiveScreenDemoAutomation? DemoAutomation { get; init; }

    /// <summary>
    /// The order's <c>ChangeVersion</c> when this snapshot was read; 0 for a practice order. A live
    /// <c>OrderChanged</c> whose version is not higher than this is already shown by the snapshot.
    /// </summary>
    public long ChangeVersion { get; init; }

    /// <summary>
    /// When the order was placed on the platform, as the provider reported it (WAS-125); null when it sent no creation
    /// time, and for a practice order. The Live Screen shows and counts from it, and falls back to
    /// <see cref="ReceivedAtUtc"/> under the "received in Wasla" label when it is null.
    /// </summary>
    public DateTime? PlacedAtUtc { get; init; }

    /// <summary>The operational order time: <see cref="PlacedAtUtc"/>, otherwise <see cref="ReceivedAtUtc"/>.</summary>
    [JsonIgnore]
    public DateTime OrderedAtUtc => PlacedAtUtc ?? ReceivedAtUtc;
}

/// <summary>
/// A practice order's next automatic step: PickUp, Deliver or Leave, due at <paramref name="DueAtUtc"/> (server UTC;
/// compare with the snapshot's ServerTimeUtc), out of a stage of <paramref name="DurationSeconds"/>.
/// </summary>
public sealed record LiveScreenDemoAutomation(string Action, DateTime DueAtUtc, int DurationSeconds);

public sealed record LiveScreenLineItemDto(
    string ProductName,
    int Quantity,
    string? Notes);
