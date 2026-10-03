using Wasla.Domain.Enums;

namespace Wasla.Application.Demos;

public sealed record GuidedDemoLine(string NameKey, int Quantity, decimal UnitPrice);

public sealed record GuidedDemoScenario(
    string Code,
    string CustomerNameKey,
    string NoteKey,
    IReadOnlyList<GuidedDemoLine> Lines);

public sealed record GuidedDemoSessionState(
    Guid Id,
    Guid UserId,
    string ScenarioCode,
    OrderStatus Status,
    DateTime ReceivedAtUtc,
    string CustomerNameKey,
    string? NoteKey,
    IReadOnlyList<GuidedDemoLine> Lines,
    DateTime? DeliveredAtUtc = null)
{
    /// <summary>
    /// The automatic step waiting on it and its deadline (<see cref="GuidedDemoTiming"/>), from the time it entered
    /// its status; null while the restaurant moves it itself.
    /// </summary>
    public GuidedDemoAutomaticStep? Automatic { get; init; }
}

public sealed record GuidedDemoActionResult(bool Succeeded, string MessageKey, OrderStatus? Status);

/// <summary>
/// A user's most recent practice order. <paramref name="IsOpen"/> is false once it was delivered,
/// cancelled, finished or expired, so it can no longer move.
/// </summary>
public sealed record GuidedDemoSummary(Guid Id, OrderStatus Status, bool IsOpen);

public interface IGuidedDemoService
{
    Task<GuidedDemoSessionState> StartAsync(Guid tenantId, Guid userId, CancellationToken ct);

    Task<GuidedDemoSessionState?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// The demo to show on this user's Live Screen: the open one, else one delivered within the
    /// practice order's own delivered stage (<see cref="GuidedDemoTiming.StageDuration"/>), so the Delivered update is
    /// visible and then leaves. Real delivered orders keep their longer Live Screen window.
    /// </summary>
    Task<GuidedDemoSessionState?> GetForLiveScreenAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// The user's most recent practice order, open or closed, however long ago it closed. Order
    /// training uses it to resume at the right step, e.g. after a delivery while the page was closed.
    /// </summary>
    Task<GuidedDemoSummary?> GetLatestAsync(Guid tenantId, Guid userId, CancellationToken ct);

    Task<GuidedDemoActionResult> ApplyActionAsync(
        Guid tenantId,
        Guid userId,
        Guid sessionId,
        string action,
        CancellationToken ct);

    Task FinishActiveAsync(Guid tenantId, Guid userId, CancellationToken ct);
}
