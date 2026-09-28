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
    DateTime? DeliveredAtUtc = null);

public sealed record GuidedDemoActionResult(bool Succeeded, string MessageKey, OrderStatus? Status);

public interface IGuidedDemoService
{
    Task<GuidedDemoSessionState> StartAsync(Guid tenantId, Guid userId, CancellationToken ct);

    Task<GuidedDemoSessionState?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// The demo to show on this user's Live Screen: the open one, else one delivered within the
    /// same short window the Live Screen keeps real delivered orders, so the Delivered update is visible.
    /// </summary>
    Task<GuidedDemoSessionState?> GetForLiveScreenAsync(Guid tenantId, Guid userId, CancellationToken ct);

    Task<GuidedDemoActionResult> ApplyActionAsync(
        Guid tenantId,
        Guid userId,
        Guid sessionId,
        string action,
        CancellationToken ct);

    Task FinishActiveAsync(Guid tenantId, Guid userId, CancellationToken ct);
}
