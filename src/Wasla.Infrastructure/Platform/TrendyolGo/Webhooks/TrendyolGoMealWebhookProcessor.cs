using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Platform;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Sync;

namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

/// <summary>
/// Processes the stored Trendyol GO Meal webhook events of the stage pilot, after they were acknowledged. Every event
/// is claimed by one processor at a time, checked against the configured connection, and then:
/// <list type="bullet">
/// <item><c>created</c>, <c>shipped</c>, <c>delivered</c>: a targeted reconciliation reads the connection's recent
/// packages back with its own credentials and ingests them through the shared path. The <c>created</c> payload is not
/// mapped directly: its documented differences from the polling package (no <c>packageModificationDate</c>,
/// <c>timestamp</c> in place of <c>packageCreationDate</c>, <c>lines[].quantity</c>) are not yet verified against
/// stage. No event name is mapped to a status.</item>
/// <item><c>cancelled</c>, <c>unsupplied</c>: their own <c>status</c> field is a documented package status. For an
/// order Wasla has stored it is applied as a status-only observation; otherwise the event is reconciled.</item>
/// </list>
/// The shared ingestion path keeps every effect single: one order per package, auto-approve and receipts once, one live
/// notification per change. A failed attempt is retried with backoff; after the last one the event is
/// <see cref="ProviderWebhookEventStatus.Failed"/> (it can be replayed) and checkpointed polling still imports the order.
/// </summary>
public sealed class TrendyolGoMealWebhookProcessor
{
    /// <summary>How long a claim lasts; an event whose processor stopped is taken up again after it.</summary>
    internal static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(2);

    /// <summary>The wait before each retry; its length is the number of attempts an event gets.</summary>
    internal static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    private readonly TrendyolGoMealWebhookPilotOptions _options;
    private readonly ITenantDbContextFactory _tenants;
    private readonly OrderSyncService _sync;
    private readonly IOrderStatusMapper _statusMapper;
    private readonly ILogger<TrendyolGoMealWebhookProcessor> _logger;
    private readonly TimeProvider _time;

    public TrendyolGoMealWebhookProcessor(
        IOptions<TrendyolGoMealWebhookPilotOptions> options,
        ITenantDbContextFactory tenants,
        OrderSyncService sync,
        IOrderStatusMapper statusMapper,
        ILogger<TrendyolGoMealWebhookProcessor> logger,
        TimeProvider time)
    {
        _options = options.Value;
        _tenants = tenants;
        _sync = sync;
        _statusMapper = statusMapper;
        _logger = logger;
        _time = time;
    }

    public bool IsEnabled => _options.Enabled;

    /// <summary>Processes up to <paramref name="maxEvents"/> due events, oldest first; returns how many it processed.</summary>
    public async Task<int> ProcessDueAsync(int maxEvents, CancellationToken ct)
    {
        if (!_options.Enabled)
            return 0;

        var now = Now();
        List<Guid> due;
        await using (var db = await _tenants.CreateAsync(_options.TenantId, ct).ConfigureAwait(false))
        {
            due = await db.ProviderWebhookEvents.AsNoTracking()
                .Where(e => (e.Status == ProviderWebhookEventStatus.Pending && e.NextAttemptAt <= now)
                            || (e.Status == ProviderWebhookEventStatus.Processing && e.LockedUntil < now))
                .OrderBy(e => e.ReceivedAt)
                .Select(e => e.Id)
                .Take(maxEvents)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        var processed = 0;
        foreach (var id in due)
        {
            if (!await TryClaimAsync(id, ct).ConfigureAwait(false))
                continue;

            await ProcessClaimedAsync(id, ct).ConfigureAwait(false);
            processed++;
        }

        return processed;
    }

    /// <summary>
    /// Puts a failed or processed event back in the queue with fresh attempts. Safe because processing is idempotent: a
    /// replay never creates a second order or repeats a side effect. Rejected and ignored events are never replayed.
    /// </summary>
    public async Task<bool> ReplayAsync(Guid eventId, CancellationToken ct)
    {
        await using var db = await _tenants.CreateAsync(_options.TenantId, ct).ConfigureAwait(false);
        return await ReplayAsync(db, eventId, Now(), ct).ConfigureAwait(false);
    }

    /// <summary><see cref="ReplayAsync(Guid, CancellationToken)"/> on a given tenant database (the CLI's).</summary>
    public static async Task<bool> ReplayAsync(TenantDbContext db, Guid eventId, DateTime now, CancellationToken ct)
    {
        var changed = await db.ProviderWebhookEvents
            .Where(e => e.Id == eventId
                        && (e.Status == ProviderWebhookEventStatus.Failed || e.Status == ProviderWebhookEventStatus.Processed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ProviderWebhookEventStatus.Pending)
                .SetProperty(e => e.Attempts, 0)
                .SetProperty(e => e.NextAttemptAt, now)
                .SetProperty(e => e.LockedUntil, (DateTime?)null)
                .SetProperty(e => e.LastError, (string?)null)
                .SetProperty(e => e.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    private async Task<bool> TryClaimAsync(Guid id, CancellationToken ct)
    {
        var now = Now();
        var lockedUntil = now + ClaimDuration;
        await using var db = await _tenants.CreateAsync(_options.TenantId, ct).ConfigureAwait(false);
        // One conditional update: of several processors (or Worker processes) only one moves the row to Processing.
        var claimed = await db.ProviderWebhookEvents
            .Where(e => e.Id == id
                        && ((e.Status == ProviderWebhookEventStatus.Pending && e.NextAttemptAt <= now)
                            || (e.Status == ProviderWebhookEventStatus.Processing && e.LockedUntil < now)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ProviderWebhookEventStatus.Processing)
                .SetProperty(e => e.LockedUntil, lockedUntil)
                .SetProperty(e => e.Attempts, e => e.Attempts + 1)
                .SetProperty(e => e.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return claimed == 1;
    }

    private async Task ProcessClaimedAsync(Guid id, CancellationToken ct)
    {
        ProviderWebhookEvent stored;
        await using (var db = await _tenants.CreateAsync(_options.TenantId, ct).ConfigureAwait(false))
            stored = await db.ProviderWebhookEvents.AsNoTracking().SingleAsync(e => e.Id == id, ct).ConfigureAwait(false);

        Decision decision;
        try
        {
            decision = await DecideAsync(stored, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown: the claim expires and the event is taken up again.
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailedAttemptAsync(stored, ex, ct).ConfigureAwait(false);
            return;
        }

        await CompleteAsync(stored, decision, ct).ConfigureAwait(false);
    }

    private async Task<Decision> DecideAsync(ProviderWebhookEvent stored, CancellationToken ct)
    {
        var connectionCheck = await CheckConnectionAsync(ct).ConfigureAwait(false);
        if (connectionCheck is not null)
            return connectionCheck;

        if (!await _sync.IsOrderSyncEnabledAsync(_options.TenantId, ct).ConfigureAwait(false))
            return new Decision(ProviderWebhookEventStatus.Processed, "OrderSyncDisabled", null);

        var expectedStatus = TrendyolGoMealWebhookEvents.DocumentedCancellationStatus(stored.EventType);
        if (expectedStatus is not null && ReadStatus(stored.PayloadJson) == expectedStatus)
        {
            var orderId = await _sync.FindOrderIdAsync(_options.TenantId, FoodPlatform.TrendyolYemek, stored.ExternalOrderId, ct).ConfigureAwait(false);
            if (orderId is not null)
            {
                var observation = new OrderStatusObservation(
                    FoodPlatform.TrendyolYemek,
                    stored.ExternalOrderId,
                    _statusMapper.MapToInternalStatus(FoodPlatform.TrendyolYemek, expectedStatus),
                    expectedStatus);
                var result = await _sync.ApplyStatusObservationAsync(
                        _options.TenantId,
                        _options.PlatformConnectionId,
                        observation,
                        milestonesObservedLive: IsRecent(stored),
                        ct)
                    .ConfigureAwait(false);
                return new Decision(ProviderWebhookEventStatus.Processed, result.Updated ? "StatusApplied" : "StatusUnchanged", orderId);
            }
        }

        if (stored.EventType is TrendyolGoMealWebhookEvents.Created
            or TrendyolGoMealWebhookEvents.Shipped
            or TrendyolGoMealWebhookEvents.Delivered
            || expectedStatus is not null)
        {
            await _sync.ReconcileConnectionAsync(
                    _options.TenantId,
                    _options.PlatformConnectionId,
                    stored.EventTimestampUtc ?? stored.ReceivedAt,
                    ct)
                .ConfigureAwait(false);
            var orderId = await _sync.FindOrderIdAsync(_options.TenantId, FoodPlatform.TrendyolYemek, stored.ExternalOrderId, ct).ConfigureAwait(false);
            return orderId is not null
                ? new Decision(ProviderWebhookEventStatus.Processed, "Reconciled", orderId)
                : throw new PackageNotVisibleException();
        }

        // Stored as Pending but not an event Wasla acts on: never touch an order for it.
        return new Decision(ProviderWebhookEventStatus.Ignored, "UndocumentedEventType", null);
    }

    /// <summary>
    /// Null when the configured connection is the pilot's: the tenant's active Trendyol GO connection with the configured
    /// seller and store. Otherwise the event is refused and nothing is changed.
    /// </summary>
    private async Task<Decision?> CheckConnectionAsync(CancellationToken ct)
    {
        await using var db = await _tenants.CreateAsync(_options.TenantId, ct).ConfigureAwait(false);
        var connection = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == _options.PlatformConnectionId, ct)
            .ConfigureAwait(false);
        if (connection is null || !connection.IsActive)
            throw new InvalidOperationException("The configured platform connection is missing or inactive.");

        if (_options.IsPilotConnection(connection))
            return null;

        _logger.LogError(
            "Trendyol GO Meal webhook pilot: the configured connection does not match the configured seller and store; the event was refused. ConnectionId={ConnectionId}",
            _options.PlatformConnectionId);
        return new Decision(ProviderWebhookEventStatus.Rejected, "ConnectionMismatch", null);
    }

    private bool IsRecent(ProviderWebhookEvent stored) =>
        stored.EventTimestampUtc is { } at && Now() - at <= OrderSyncService.MilestoneObservationLimit;

    private async Task CompleteAsync(ProviderWebhookEvent stored, Decision decision, CancellationToken ct)
    {
        var now = Now();
        await using var db = await _tenants.CreateAsync(_options.TenantId, ct).ConfigureAwait(false);
        await db.ProviderWebhookEvents
            .Where(e => e.Id == stored.Id && e.Status == ProviderWebhookEventStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, decision.Status)
                .SetProperty(e => e.Outcome, decision.Outcome)
                .SetProperty(e => e.OrderId, decision.OrderId)
                .SetProperty(e => e.LockedUntil, (DateTime?)null)
                .SetProperty(e => e.LastError, (string?)null)
                .SetProperty(e => e.ProcessedAt, now)
                .SetProperty(e => e.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Trendyol GO Meal webhook processed. EventId={EventId}, EventType={EventType}, ExternalOrderId={ExternalOrderId}, Status={Status}, Outcome={Outcome}, OrderId={OrderId}",
            stored.Id,
            stored.EventType,
            stored.ExternalOrderId,
            decision.Status,
            decision.Outcome,
            decision.OrderId);
    }

    private async Task RecordFailedAttemptAsync(ProviderWebhookEvent stored, Exception ex, CancellationToken ct)
    {
        var now = Now();
        var attempt = stored.Attempts;
        var exhausted = attempt >= RetryDelays.Length;
        var message = ex is PackageNotVisibleException
            ? "The reconciliation did not return the package yet."
            : OrderSyncService.SanitizeErrorMessage(ex);

        await using var db = await _tenants.CreateAsync(_options.TenantId, ct).ConfigureAwait(false);
        await db.ProviderWebhookEvents
            .Where(e => e.Id == stored.Id && e.Status == ProviderWebhookEventStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, exhausted ? ProviderWebhookEventStatus.Failed : ProviderWebhookEventStatus.Pending)
                .SetProperty(e => e.NextAttemptAt, exhausted ? now : now + RetryDelays[Math.Max(0, attempt - 1)])
                .SetProperty(e => e.Outcome, ex is PackageNotVisibleException ? "PackageNotVisible" : "Error")
                .SetProperty(e => e.LastError, message)
                .SetProperty(e => e.LockedUntil, (DateTime?)null)
                .SetProperty(e => e.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        _logger.LogWarning(
            "Trendyol GO Meal webhook processing attempt failed. EventId={EventId}, EventType={EventType}, ExternalOrderId={ExternalOrderId}, Attempt={Attempt}, Exhausted={Exhausted}, ErrorType={ErrorType}, Error={Error}",
            stored.Id,
            stored.EventType,
            stored.ExternalOrderId,
            attempt,
            exhausted,
            ex.GetType().Name,
            message);
    }

    private static string? ReadStatus(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private sealed record Decision(ProviderWebhookEventStatus Status, string Outcome, Guid? OrderId);

    /// <summary>The reconciliation succeeded but did not return the event's package (yet): retried with backoff.</summary>
    private sealed class PackageNotVisibleException() : Exception("The reconciliation did not return the package.");
}
