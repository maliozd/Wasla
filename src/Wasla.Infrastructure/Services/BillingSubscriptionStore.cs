using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// CentralDb implementation of <see cref="IBillingSubscriptionStore"/> (WAS-147).
/// </summary>
/// <remarks>
/// Each write reads the subscription, lets the domain decide, and saves with the <c>Revision</c> concurrency token.
/// When another writer saved first (a stale revision) or won a unique index, the write is evaluated again against the
/// stored state, so a repeated callback becomes <see cref="BillingStoreOutcome.Unchanged"/> and an outdated one
/// <see cref="BillingStoreOutcome.Stale"/>; after <see cref="MaxAttempts"/> it reports
/// <see cref="BillingStoreOutcome.ConcurrencyConflict"/> without writing. Other database errors are not reclassified.
/// Logs carry Wasla ids and outcomes only: no provider references, amounts or contact data.
/// </remarks>
public sealed class BillingSubscriptionStore : IBillingSubscriptionStore
{
    internal const int MaxAttempts = 3;

    private readonly CentralDbContext _central;
    private readonly TimeProvider _time;
    private readonly ILogger<BillingSubscriptionStore> _logger;

    public BillingSubscriptionStore(CentralDbContext central, TimeProvider time, ILogger<BillingSubscriptionStore> logger)
    {
        _central = central;
        _time = time;
        _logger = logger;
    }

    public async Task<BillingStartResult> StartForRegistrationAsync(
        Guid pendingRegistrationId,
        BillingProviderScope scope,
        BillingPrice price,
        CancellationToken ct)
    {
        if (price == default)
            throw new ArgumentException("A price is required.", nameof(price));

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var existing = await _central.BillingSubscriptions.AsNoTracking()
                .Where(s => s.PendingRegistrationId == pendingRegistrationId)
                .Select(s => new { s.Id, s.Provider, s.Environment, s.Amount, s.Currency })
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                var same = existing.Provider == scope.Provider && existing.Environment == scope.Environment
                           && existing.Amount == price.Amount && existing.Currency == price.Currency;
                return new BillingStartResult(same ? BillingStoreOutcome.Unchanged : BillingStoreOutcome.Conflict, existing.Id);
            }

            var registration = await _central.PendingRegistrations.AsNoTracking()
                .Where(r => r.Id == pendingRegistrationId)
                .Select(r => new { r.Status, r.ExpiresAtUtc, r.PlanCode, r.BillingPeriod })
                .FirstOrDefaultAsync(ct);
            if (registration is null)
                return new BillingStartResult(BillingStoreOutcome.NotFound, null);

            var now = UtcNow();
            if (registration.Status is not (PendingRegistrationStatus.AwaitingPayment or PendingRegistrationStatus.PaymentFailed)
                || registration.ExpiresAtUtc <= now
                || registration.BillingPeriod is not (BillingSubscription.MonthlyPeriod or BillingSubscription.YearlyPeriod))
                return new BillingStartResult(BillingStoreOutcome.NotEligible, null);

            var subscription = BillingSubscription.StartPending(
                pendingRegistrationId,
                scope.Provider,
                scope.Environment,
                registration.PlanCode,
                registration.BillingPeriod,
                price,
                now);
            _central.BillingSubscriptions.Add(subscription);
            try
            {
                await _central.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Billing subscription started. SubscriptionId={SubscriptionId} RegistrationId={RegistrationId}",
                    subscription.Id,
                    pendingRegistrationId);
                return new BillingStartResult(BillingStoreOutcome.Applied, subscription.Id);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A concurrent start for the same registration won; the next pass reads it.
                Detach(subscription);
            }
        }

        return new BillingStartResult(BillingStoreOutcome.ConcurrencyConflict, null);
    }

    public async Task<BillingSubscriptionView?> GetForRegistrationAsync(Guid pendingRegistrationId, CancellationToken ct) =>
        await ReadAsync(_central.BillingSubscriptions.Where(s => s.PendingRegistrationId == pendingRegistrationId), ct);

    public async Task<BillingSubscriptionView?> GetForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            return null;

        return await ReadAsync(_central.BillingSubscriptions.Where(s => s.TenantId == tenantId), ct);
    }

    public async Task<Guid?> FindByProviderReferenceAsync(
        BillingProviderScope scope,
        string externalSubscriptionReference,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalSubscriptionReference))
            return null;

        return await _central.BillingSubscriptions.AsNoTracking()
            .Where(s => s.Provider == scope.Provider
                        && s.Environment == scope.Environment
                        && s.ExternalSubscriptionReference == externalSubscriptionReference)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }

    public Task<BillingStoreOutcome> BindProviderReferencesAsync(
        Guid subscriptionId,
        BillingProviderScope scope,
        string externalSubscriptionReference,
        string? externalSubscriberReference,
        CancellationToken ct) =>
        WriteAsync(subscriptionId, scope, "BindProviderReferences", async (subscription, now) =>
        {
            var heldElsewhere = await _central.BillingSubscriptions.AsNoTracking()
                .AnyAsync(s => s.Id != subscriptionId
                               && s.Provider == scope.Provider
                               && s.Environment == scope.Environment
                               && s.ExternalSubscriptionReference == externalSubscriptionReference, ct);
            if (heldElsewhere)
                return (BillingStoreOutcome.Conflict, null);

            var result = subscription.BindProviderReferences(externalSubscriptionReference, externalSubscriberReference, now);
            return (Map(result), null);
        }, ct);

    public Task<BillingStoreOutcome> ApplyProviderStatusAsync(
        Guid subscriptionId,
        BillingProviderScope scope,
        SubscriptionStatus status,
        DateTime observedAtUtc,
        CancellationToken ct) =>
        WriteAsync(subscriptionId, scope, "ApplyProviderStatus", (subscription, now) =>
            Task.FromResult<(BillingStoreOutcome, BillingPayment?)>(
                (Map(subscription.ApplyProviderStatus(status, observedAtUtc, now)), null)), ct);

    public Task<BillingStoreOutcome> RecordVerifiedPaymentAsync(
        Guid subscriptionId,
        BillingProviderScope scope,
        BillingProviderPayment payment,
        CancellationToken ct) =>
        WriteAsync(subscriptionId, scope, "RecordVerifiedPayment", async (subscription, now) =>
        {
            // One query for both unique rules: read separately, a concurrent insert committed between them would make a
            // repeat of that same payment look like a second payment for its period.
            var existing = await _central.BillingPayments.AsNoTracking()
                .Where(p => (p.Provider == scope.Provider
                             && p.Environment == scope.Environment
                             && p.ExternalPaymentReference == payment.ExternalPaymentReference)
                            || (p.SubscriptionId == subscriptionId && p.PeriodStartUtc == payment.PeriodStartUtc))
                .ToListAsync(ct);
            if (existing.Count > 0)
            {
                var repeated = existing.Count == 1
                               && existing[0].SubscriptionId == subscriptionId
                               && existing[0].Describes(
                                   payment.ExternalPaymentReference,
                                   payment.Price,
                                   payment.PeriodStartUtc,
                                   payment.PeriodEndUtc);
                return (repeated ? BillingStoreOutcome.Unchanged : BillingStoreOutcome.Conflict, null);
            }

            var (result, recorded) = subscription.RecordVerifiedPayment(
                payment.ExternalPaymentReference,
                payment.Price,
                payment.PeriodStartUtc,
                payment.PeriodEndUtc,
                payment.VerifiedAtUtc,
                now);
            if (recorded is not null)
                _central.BillingPayments.Add(recorded);
            return (Map(result), recorded);
        }, ct);

    public Task<BillingStoreOutcome> LinkTenantAsync(Guid subscriptionId, Guid tenantId, CancellationToken ct) =>
        WriteAsync(subscriptionId, scope: null, "LinkTenant", async (subscription, now) =>
        {
            if (tenantId == Guid.Empty)
                return (BillingStoreOutcome.NotEligible, null);

            var registration = await _central.PendingRegistrations.AsNoTracking()
                .Where(r => r.Id == subscription.PendingRegistrationId)
                .Select(r => new { r.Status, r.TenantId })
                .FirstOrDefaultAsync(ct);
            var tenantExists = await _central.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, ct);
            if (registration is not { Status: PendingRegistrationStatus.Provisioned } || registration.TenantId != tenantId
                || !tenantExists)
                return (BillingStoreOutcome.NotEligible, null);

            var linkedElsewhere = await _central.BillingSubscriptions.AsNoTracking()
                .AnyAsync(s => s.Id != subscriptionId && s.TenantId == tenantId, ct);
            if (linkedElsewhere)
                return (BillingStoreOutcome.Conflict, null);

            return (Map(subscription.LinkTenant(tenantId, now)), null);
        }, ct);

    private async Task<BillingStoreOutcome> WriteAsync(
        Guid subscriptionId,
        BillingProviderScope? scope,
        string operation,
        Func<BillingSubscription, DateTime, Task<(BillingStoreOutcome Outcome, BillingPayment? Added)>> change,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // A copy tracked earlier in this scope would hide the stored revision; read it fresh.
            foreach (var tracked in _central.ChangeTracker.Entries<BillingSubscription>()
                         .Where(e => e.Entity.Id == subscriptionId)
                         .ToList())
                tracked.State = EntityState.Detached;

            var subscription = await _central.BillingSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
            if (subscription is null)
                return BillingStoreOutcome.NotFound;

            if (scope is { } expected
                && (subscription.Provider != expected.Provider || subscription.Environment != expected.Environment))
            {
                Detach(subscription);
                _logger.LogWarning(
                    "Billing write refused: provider scope does not match. SubscriptionId={SubscriptionId} Operation={Operation}",
                    subscriptionId,
                    operation);
                return BillingStoreOutcome.ScopeMismatch;
            }

            var (outcome, added) = await change(subscription, UtcNow());
            if (outcome != BillingStoreOutcome.Applied)
            {
                Detach(subscription, added);
                if (outcome is BillingStoreOutcome.Conflict or BillingStoreOutcome.InvalidTransition)
                    _logger.LogWarning(
                        "Billing write refused. SubscriptionId={SubscriptionId} Operation={Operation} Outcome={Outcome}",
                        subscriptionId,
                        operation,
                        outcome);
                return outcome;
            }

            try
            {
                await _central.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Billing subscription changed. SubscriptionId={SubscriptionId} Operation={Operation} Revision={Revision}",
                    subscriptionId,
                    operation,
                    subscription.Revision);
                return BillingStoreOutcome.Applied;
            }
            catch (DbUpdateConcurrencyException)
            {
                Detach(subscription, added);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                Detach(subscription, added);
            }
        }

        _logger.LogWarning(
            "Billing write gave up after concurrent changes. SubscriptionId={SubscriptionId} Operation={Operation}",
            subscriptionId,
            operation);
        return BillingStoreOutcome.ConcurrencyConflict;
    }

    private async Task<BillingSubscriptionView?> ReadAsync(IQueryable<BillingSubscription> query, CancellationToken ct)
    {
        var row = await query.AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null)
            return null;

        // Every verified period that has not ended, not only one "current" period: a later payment must neither hide
        // an earlier one that still covers now nor stand in for an unpaid gap before it.
        var readAt = UtcNow();
        var unexpired = await _central.BillingPayments.AsNoTracking()
            .Where(p => p.SubscriptionId == row.Id && p.PeriodEndUtc > readAt)
            .OrderBy(p => p.PeriodStartUtc)
            .Select(p => new { p.PeriodStartUtc, p.PeriodEndUtc })
            .ToListAsync(ct);

        return new BillingSubscriptionView(
            row.Id,
            row.PendingRegistrationId,
            row.TenantId,
            new BillingProviderScope(row.Provider, row.Environment),
            row.PlanCode,
            row.BillingPeriod,
            row.Price,
            row.Status,
            row.StatusObservedAtUtc,
            row.ExternalSubscriptionReference,
            row.ExternalSubscriberReference,
            unexpired.Select(p => new BillingPaidPeriod(p.PeriodStartUtc, p.PeriodEndUtc)).ToArray(),
            readAt);
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private void Detach(params object?[] entities)
    {
        foreach (var entity in entities)
        {
            if (entity is not null)
                _central.Entry(entity).State = EntityState.Detached;
        }
    }

    private static BillingStoreOutcome Map(BillingChangeResult result) => result switch
    {
        BillingChangeResult.Applied => BillingStoreOutcome.Applied,
        BillingChangeResult.Unchanged => BillingStoreOutcome.Unchanged,
        BillingChangeResult.Stale => BillingStoreOutcome.Stale,
        BillingChangeResult.InvalidTransition => BillingStoreOutcome.InvalidTransition,
        BillingChangeResult.Conflict => BillingStoreOutcome.Conflict,
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    /// <summary>A duplicate key on SQL Server (2601, 2627) or SQLite; every other database error is rethrown.</summary>
    internal static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: 2601 or 2627 })
                return true;
            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
