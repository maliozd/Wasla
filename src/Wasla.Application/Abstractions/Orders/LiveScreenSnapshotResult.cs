using System.Text.Json.Serialization;
using Wasla.Application.Abstractions.Setup;
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
}

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
/// are still synchronized and kept), and only how many arrived since the user started training is shown. No order
/// detail is carried here.
/// </summary>
public sealed record LiveScreenTrainingIsolation(int RealOrdersReceived)
{
    public bool Isolated => true;
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
