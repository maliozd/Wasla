using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Realtime;

/// <summary>
/// Tells open tenant screens that something they show has changed. The database stays the source of truth: a
/// notification only prompts a screen to read the current state again through its normal, authorized request.
/// Callers publish only after the change has been committed. Delivery is best effort: an implementation may drop a
/// notification, and it must neither throw into the caller nor delay it beyond a short bound.
/// </summary>
public interface ITenantChangeNotifier
{
    /// <param name="tenantId">The tenant whose data changed, taken from server-side context, never from a request.</param>
    Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct);
}

/// <summary>A change notification. It carries identifiers and ordering only, never order content or secrets.</summary>
public abstract record TenantChange;

/// <summary>
/// A receipt print job of an order was created or changed status. <paramref name="Version"/> is the job's
/// <c>StatusVersion</c>, which only grows within an order, so a screen can drop duplicates and stale notifications.
/// </summary>
public sealed record PrintJobChanged(Guid OrderId, Guid PrintJobId, PrintJobStatus Status, long Version) : TenantChange
{
    public const string TypeName = nameof(PrintJobChanged);
}
