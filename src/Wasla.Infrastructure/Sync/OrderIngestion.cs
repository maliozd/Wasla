using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Realtime;

namespace Wasla.Infrastructure.Sync;

/// <summary>
/// The tenant-scoped write path for one provider order observation (WAS-18): normalization, the idempotent order write
/// keyed by <c>Platform</c> + <c>ExternalOrderId</c>, and the guarded lifecycle transition with its side effects
/// (auto-approve, provider-accepted receipt, live notification). <see cref="OrderSyncService"/> is its polling caller and
/// owns fetch windows, checkpoints and connection health; this class owns what happens to one order.
/// </summary>
internal sealed class OrderIngestion
{
    private readonly IOrderStatusMapper _statusMapper;
    private readonly IOrderAutoApproveService _autoApprove;
    private readonly IOrderReceiptCreationService _receiptCreation;
    private readonly ITenantChangeNotifier _notifier;
    private readonly ILogger _logger;

    /// <param name="logger">The caller's logger, so ingestion messages keep the caller's category.</param>
    public OrderIngestion(
        IOrderStatusMapper statusMapper,
        IOrderAutoApproveService autoApprove,
        IOrderReceiptCreationService receiptCreation,
        ITenantChangeNotifier notifier,
        ILogger logger)
    {
        _statusMapper = statusMapper;
        _autoApprove = autoApprove;
        _receiptCreation = receiptCreation;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// Applies one full package observation (every order field, items and options) to the tenant database behind
    /// <paramref name="db"/>: inserts it when the order is new, otherwise updates it through the locked re-read and the
    /// status merge. <paramref name="connection"/> is the tenant's connection the observation was fetched for.
    /// </summary>
    internal async Task<OrderUpsertResult> IngestPackageAsync(
        Guid customerId,
        TenantDbContext db,
        PlatformConnection connection,
        ExternalOrderDto external,
        bool milestonesObservedLive,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(external.ExternalOrderId))
        {
            _logger.LogWarning(
                "Skipping external order because ExternalOrderId is missing. Platform={Platform}, ExternalOrderCode={ExternalOrderCode}",
                external.Platform,
                external.ExternalOrderCode);
            return new OrderUpsertResult(false, false, true, false, null);
        }

        if (ContradictsConnection(connection, external) is { } field)
        {
            // The provider says this order is not the connection's: storing it would show another location's order to
            // this tenant. Nothing is written; the window still completes, because the order is not this tenant's.
            _logger.LogWarning(
                "Provider order refused: its {Field} does not match the connection it was fetched for, so it was not stored. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, ConnectionStoreId={ConnectionStoreId}, ProviderStoreId={ProviderStoreId}, ConnectionSupplierId={ConnectionSupplierId}, ProviderSupplierId={ProviderSupplierId}, ExternalOrderId={ExternalOrderId}",
                field,
                customerId,
                connection.Id,
                connection.Platform,
                connection.StoreId,
                external.ProviderStoreId,
                connection.SupplierId,
                external.ProviderSupplierId,
                external.ExternalOrderId);
            return new OrderUpsertResult(false, false, true, false, external.ExternalOrderId);
        }

        var key = IdempotencyKey(external.Platform, external.ExternalOrderId);

        var nowUtc = DateTime.UtcNow;
        external = WithPlausibleProviderTimes(external, nowUtc);
        var newStatus = _statusMapper.MapToInternalStatus(
            external.Platform,
            external.ExternalStatus);

        var existing = await db.Orders
            .FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var order = MapToOrderEntity(
                external,
                key,
                receivedAtUtc: nowUtc,
                internalStatus: newStatus,
                nowUtc: nowUtc,
                milestonesObservedLive: milestonesObservedLive);

            order.ChangeVersion = OrderChangeVersions.Initial;
            if (await TryInsertNewOrderAsync(db, order, ct).ConfigureAwait(false))
            {
                // The new order is committed: open Live Screens read their snapshot again.
                await TenantChangePublisher.PublishAsync(_notifier, customerId, new OrderChanged(order.Id, order.ChangeVersion), _logger)
                    .ConfigureAwait(false);

                _logger.LogDebug(
                    "Inserted order. OrderId={OrderId}, ExternalOrderId={ExternalOrderId}, Platform={Platform}, Status={Status}, ReceivedAtUtc={ReceivedAtUtc}",
                    order.Id,
                    order.ExternalOrderId,
                    order.Platform,
                    order.InternalStatus,
                    order.ReceivedAt);

                try
                {
                    await _autoApprove.ProcessNewlyInsertedOrderAsync(
                        customerId,
                        order.Id,
                        order.InternalStatus,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Auto-approve processing failed but sync continued. CustomerId={CustomerId}, OrderId={OrderId}",
                        customerId,
                        order.Id);
                }

                await TryCreateReceiptIfProviderAcceptedAsync(
                    customerId,
                    order.Id,
                    oldStatus: null,
                    newStatus: order.InternalStatus,
                    ct).ConfigureAwait(false);

                return new OrderUpsertResult(true, false, false, false, order.ExternalOrderId);
            }

            // Another caller inserted this order after the read above and committed first (another Worker, or a second
            // observation of the same package). Its insert ran the new-order side effects; this observation is applied
            // to the stored order like any repeated one: unchanged, or through the locked update and the status merge.
            existing = await db.Orders
                .FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The order's identity was taken by a concurrent insert, but the order cannot be read.");

            _logger.LogInformation(
                "Order was stored by a concurrent sync; this observation is applied to it. CustomerId={CustomerId}, OrderId={OrderId}, ExternalOrderId={ExternalOrderId}, Platform={Platform}",
                customerId,
                existing.Id,
                existing.ExternalOrderId,
                existing.Platform);
        }

        // Read child rows before any transaction. An unchanged order must not open the
        // mutation transaction, delete children, or call SaveChanges for the order.
        var persistedItems = await LoadPersistedItemSnapshotsAsync(db, existing.Id, ct).ConfigureAwait(false);
        if (IsSemanticallyUnchanged(existing, persistedItems, external, newStatus))
            return new OrderUpsertResult(false, false, false, true, existing.ExternalOrderId);

        await using var tx = await db.Database.BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        // An operator action or another Worker may have committed a status or items since the reads above (WAS-123):
        // the comparison and the merge below start from the row and items read again under the row lock.
        nowUtc = await LockAndRereadOrderAsync(db, existing, nowUtc, ct).ConfigureAwait(false);
        persistedItems = await LoadPersistedItemSnapshotsAsync(db, existing.Id, ct).ConfigureAwait(false);

        if (IsSemanticallyUnchanged(existing, persistedItems, external, newStatus))
        {
            // The committed row already holds what the provider reports: write nothing, as for an unchanged order.
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            db.Entry(existing).State = EntityState.Detached;
            return new OrderUpsertResult(false, false, false, true, existing.ExternalOrderId);
        }

        var oldStatus = existing.InternalStatus;
        var oldTotal = existing.TotalAmount;
        var oldPlatformStatus = existing.PlatformStatus;

        // EN: Provider status can lag behind local operator actions; merge instead of blindly overwriting InternalStatus so stale New/Accepted never undoes manual progress.
        // TR: Provider statüsü paneldeki manuel aksiyonların gerisinde kalabilir; InternalStatus'u körlemesine ezmemek için merge ediyoruz; eski New/Accepted manuel ilerlemeyi geri almasın.
        var mergedStatus = MergeInternalStatusForSync(existing.InternalStatus, newStatus);

        existing.InternalStatus = mergedStatus;
        existing.PlatformStatus = external.ExternalStatus;
        existing.CustomerName = external.CustomerName;
        existing.TotalAmount = external.Total;
        existing.DeliveryFee = external.DeliveryFee;
        existing.ServiceFee = external.ServiceFee;
        existing.PaymentMethod = external.PaymentMethod;
        existing.PaymentStatus = external.PaymentStatus;
        existing.RawPayloadJson = external.RawPayloadJson;
        // ReceivedAt (first import) is never written here. A provider time the payload lacks keeps the stored one.
        existing.CreatedAtPlatform = external.OrderedAtUtc ?? existing.CreatedAtPlatform;
        existing.PlatformModifiedAt = external.ModifiedAtUtc ?? existing.PlatformModifiedAt;
        existing.ExternalOrderCode = external.ExternalOrderCode;
        existing.CustomerPhone = external.CustomerPhone;
        existing.CustomerAddress = external.CustomerAddress;
        existing.CustomerNote = NormalizeCustomerNote(external.CustomerNote);
        existing.UpdatedAt = nowUtc;

        ApplyStatusTransitionTimestamps(
            existing,
            oldStatus,
            mergedStatus,
            nowUtc,
            milestonesObservedLive);

        await db.OrderItemOptions
            .Where(o => db.OrderItems
                .Where(i => i.OrderId == existing.Id)
                .Select(i => i.Id)
                .Contains(o.OrderItemId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        await db.OrderItems
            .Where(i => i.OrderId == existing.Id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // Items and options keep the provider's list order (Sequence), as on insert.
        var itemSequence = 0;
        foreach (var itemDto in external.Items)
        {
            var item = new OrderItem
            {
                OrderId = existing.Id,
                ProductName = itemDto.ProductName,
                Quantity = itemDto.Quantity,
                UnitPrice = itemDto.UnitPrice,
                TotalPrice = itemDto.TotalPrice,
                Notes = itemDto.Notes,
                Sequence = itemSequence++,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            };

            var optionSequence = 0;
            foreach (var opt in itemDto.Options)
            {
                item.Options.Add(new OrderItemOption
                {
                    Name = opt.Name,
                    Price = opt.Price,
                    Sequence = optionSequence++,
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc
                });
            }

            db.OrderItems.Add(item);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var changeVersion = await OrderChangeVersions.NextAsync(db, existing.Id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await TenantChangePublisher.PublishAsync(_notifier, customerId, new OrderChanged(existing.Id, changeVersion), _logger)
            .ConfigureAwait(false);

        await TryCreateReceiptIfProviderAcceptedAsync(
            customerId,
            existing.Id,
            oldStatus,
            mergedStatus,
            ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Updated order {ExternalOrderId}. Status {OldStatus} -> {MergedStatus} (external mapped {ExternalMappedStatus}), Total {OldTotal} -> {NewTotal}, PlatformStatus {OldPlatformStatus} -> {NewPlatformStatus}",
            existing.ExternalOrderId,
            oldStatus,
            mergedStatus,
            newStatus,
            oldTotal,
            existing.TotalAmount,
            oldPlatformStatus,
            existing.PlatformStatus);

        return new OrderUpsertResult(false, true, false, false, existing.ExternalOrderId);
    }

    /// <summary>
    /// Applies a status-only observation: the provider reports that an order it already sent reached a status, without
    /// the order itself. It never creates an order (an unknown order is left to the full observation that polling
    /// fetches), never replaces items and never touches customer, money or time fields. The status goes through the
    /// same row lock, merge (stale and terminal rules), milestones, change version, notification and provider-accepted
    /// receipt as a full observation; the provider status text is stored only when the merge moves the order on.
    /// </summary>
    /// <remarks>
    /// No production caller yet. The adapter that turns a provider event into <paramref name="observation"/> must use
    /// a verified provider contract; this method does not interpret provider event names (WAS-18, WAS-175).
    /// </remarks>
    internal async Task<OrderUpsertResult> ApplyStatusObservationAsync(
        Guid customerId,
        TenantDbContext db,
        PlatformConnection connection,
        OrderStatusObservation observation,
        bool milestonesObservedLive,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(observation.ExternalOrderId) || observation.Platform != connection.Platform)
        {
            _logger.LogWarning(
                "Status observation refused: it has no order id or another platform than its connection. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, ObservationPlatform={ObservationPlatform}",
                customerId,
                connection.Id,
                connection.Platform,
                observation.Platform);
            return new OrderUpsertResult(false, false, true, false, null);
        }

        var key = IdempotencyKey(observation.Platform, observation.ExternalOrderId);
        var existing = await db.Orders
            .FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            _logger.LogInformation(
                "Status observation for an order Wasla has not stored was not applied; polling imports the full order. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, ExternalOrderId={ExternalOrderId}, ObservedStatus={ObservedStatus}",
                customerId,
                connection.Id,
                observation.Platform,
                observation.ExternalOrderId,
                observation.Status);
            return new OrderUpsertResult(false, false, true, false, observation.ExternalOrderId);
        }

        if (MergeInternalStatusForSync(existing.InternalStatus, observation.Status) == existing.InternalStatus)
        {
            db.Entry(existing).State = EntityState.Detached;
            return new OrderUpsertResult(false, false, false, true, existing.ExternalOrderId);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var nowUtc = await LockAndRereadOrderAsync(db, existing, DateTime.UtcNow, ct).ConfigureAwait(false);

        var oldStatus = existing.InternalStatus;
        var mergedStatus = MergeInternalStatusForSync(oldStatus, observation.Status);
        if (mergedStatus == oldStatus)
        {
            // A writer committed this status or a later one meanwhile: nothing to apply.
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            db.Entry(existing).State = EntityState.Detached;
            return new OrderUpsertResult(false, false, false, true, existing.ExternalOrderId);
        }

        existing.InternalStatus = mergedStatus;
        if (!string.IsNullOrWhiteSpace(observation.PlatformStatus))
            existing.PlatformStatus = observation.PlatformStatus;
        existing.UpdatedAt = nowUtc;
        ApplyStatusTransitionTimestamps(existing, oldStatus, mergedStatus, nowUtc, milestonesObservedLive);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var changeVersion = await OrderChangeVersions.NextAsync(db, existing.Id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await TenantChangePublisher.PublishAsync(_notifier, customerId, new OrderChanged(existing.Id, changeVersion), _logger)
            .ConfigureAwait(false);

        await TryCreateReceiptIfProviderAcceptedAsync(customerId, existing.Id, oldStatus, mergedStatus, ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Applied status observation to order {ExternalOrderId}. Status {OldStatus} -> {MergedStatus} (observed {ObservedStatus})",
            existing.ExternalOrderId,
            oldStatus,
            mergedStatus,
            observation.Status);

        return new OrderUpsertResult(false, true, false, false, existing.ExternalOrderId);
    }

    /// <summary>SHA-256 of <c>"{Platform}:{ExternalOrderId}"</c>, Base64: the order's idempotency key in its tenant.</summary>
    internal static string IdempotencyKey(FoodPlatform platform, string externalOrderId) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{platform}:{externalOrderId}")));

    /// <summary>
    /// Inside the caller's transaction: locks the order row with the <c>UpdatedAt</c> write the update makes anyway, then
    /// reads the row again, so the comparison and the merge start from what is committed and nothing changes it before
    /// the commit (WAS-123). The lock write never moves <c>UpdatedAt</c> back past a writer that committed during the
    /// wait. Returns the time to apply the change at: now, after any wait for the lock, and never before the order's
    /// last committed change.
    /// </summary>
    private static async Task<DateTime> LockAndRereadOrderAsync(TenantDbContext db, Order existing, DateTime nowUtc, CancellationToken ct)
    {
        await db.Orders
            .Where(o => o.Id == existing.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedAt, o => o.UpdatedAt > nowUtc ? o.UpdatedAt : nowUtc), ct)
            .ConfigureAwait(false);
        await db.Entry(existing).ReloadAsync(ct).ConfigureAwait(false);

        var appliedAt = DateTime.UtcNow;
        return appliedAt < existing.UpdatedAt ? existing.UpdatedAt : appliedAt;
    }

    /// <summary>
    /// Which part of a full observation contradicts the tenant connection it was fetched for: "platform", "store" or
    /// "supplier"; null when nothing does. Only a value the payload actually reports is compared, with the connection's
    /// configured value (trimmed; exact, except that two all-digit values compare as numbers, so "00123" is 123): an
    /// optional identifier the payload leaves out, or one the connection does not
    /// set, is not a mismatch. A Trendyol GO connection without <c>SupplierId</c> polls with its <c>StoreId</c> as the
    /// supplier id; its supplier is then not compared, its store still is.
    /// </summary>
    /// <remarks>
    /// This guards an order that was fetched with the tenant's own credentials and connection. It does not, and cannot,
    /// decide which tenant a previously unseen webhook event belongs to (WAS-175).
    /// </remarks>
    internal static string? ContradictsConnection(PlatformConnection connection, ExternalOrderDto external)
    {
        if (external.Platform != connection.Platform)
            return "platform";
        if (Differs(external.ProviderStoreId, connection.StoreId))
            return "store";
        if (Differs(external.ProviderSupplierId, connection.SupplierId))
            return "supplier";
        return null;

        static bool Differs(string? reported, string? configured) =>
            !string.IsNullOrWhiteSpace(reported)
            && !string.IsNullOrWhiteSpace(configured)
            && !SameProviderIdentifier(reported, configured);
    }

    /// <summary>
    /// Whether two provider identifiers name the same thing: trimmed and exact, except that two all-digit values compare
    /// as numbers, because a provider id may arrive as a JSON number (123) while the connection stores the text typed in
    /// ("00123").
    /// </summary>
    internal static bool SameProviderIdentifier(string first, string second) =>
        string.Equals(CanonicalIdentifier(first), CanonicalIdentifier(second), StringComparison.Ordinal);

    private static string CanonicalIdentifier(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit))
            return trimmed;
        var digits = trimmed.TrimStart('0');
        return digits.Length == 0 ? "0" : digits;
    }

    /// <summary>
    /// Inserts a new order graph (order, items, options) and returns true, or returns false when the database refused it
    /// because an order with the same identity (<c>Platform</c> + <c>ExternalOrderId</c>, see
    /// <see cref="IsOrderIdentityConflict"/>) was committed by a concurrent insert. Every other failure is thrown.
    /// </summary>
    /// <remarks>
    /// The insert is <c>SaveChanges</c> in its own transaction, never one the caller opened: SQL Server holds the second
    /// insert on the first one's key lock until that transaction ends, and reports the duplicate key only when the first
    /// one committed (a rollback lets the second insert succeed). So a refused insert means the winner's order is
    /// committed and readable. EF Core rolls its own transaction back before it throws and leaves the refused graph
    /// tracked as Added; detaching that graph returns the context to the state it had before the insert, with no
    /// transaction open, so the caller can read the winner's order and update it on the same context.
    /// </remarks>
    private static async Task<bool> TryInsertNewOrderAsync(TenantDbContext db, Order order, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("A new order is inserted in its own transaction, not inside the caller's.");

        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsOrderIdentityConflict(ex))
        {
            // Snapshot the graph first: detaching an entity fixes up the navigations it is listed in.
            object[] graph = [order, .. order.Items, .. order.Items.SelectMany(i => i.Options)];
            foreach (var entity in graph)
                db.Entry(entity).State = EntityState.Detached;

            if (db.Database.CurrentTransaction is not null)
                throw new InvalidOperationException("The refused order insert left a transaction open.", ex);

            return false;
        }
    }

    /// <summary>The order indexes that make <c>Platform</c> + <c>ExternalOrderId</c> one order per tenant database.</summary>
    private static readonly string[] OrderIdentityIndexes = ["IX_Orders_IdempotencyKey", "IX_Orders_Platform_ExternalOrderId"];

    /// <summary>
    /// True only for a duplicate key on one of the order identity indexes: SQL Server error 2601 or 2627 naming
    /// <c>IX_Orders_IdempotencyKey</c> or <c>IX_Orders_Platform_ExternalOrderId</c>, or SQLite's unique-constraint message
    /// for the same columns. Any other duplicate key, constraint or database error is not a concurrent insert of this
    /// order and must fail the caller.
    /// </summary>
    internal static bool IsOrderIdentityConflict(DbUpdateException exception)
    {
        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql.Number is 2601 or 2627
                    && OrderIdentityIndexes.Any(index => sql.Errors[0].Message.Contains($"'{index}'", StringComparison.Ordinal));
            }

            // SQLite (unit tests only; Infrastructure does not reference its provider).
            if (current.Message.Contains("UNIQUE constraint failed: Orders.IdempotencyKey", StringComparison.Ordinal)
                || current.Message.Contains("UNIQUE constraint failed: Orders.Platform, Orders.ExternalOrderId", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private async Task TryCreateReceiptIfProviderAcceptedAsync(
        Guid customerId,
        Guid orderId,
        OrderStatus? oldStatus,
        OrderStatus newStatus,
        CancellationToken ct)
    {
        if (!ShouldCreateReceiptForAcceptedProviderStatus(oldStatus, newStatus))
            return;

        try
        {
            await _receiptCreation.TryCreateOnOrderAcceptedAsync(customerId, orderId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Receipt creation after provider-accepted order failed but sync continued. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
        }
    }

    private static string? NormalizeCustomerNote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        const int maxLength = 2000;
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool ShouldCreateReceiptForAcceptedProviderStatus(OrderStatus? oldStatus, OrderStatus newStatus)
    {
        if (newStatus != OrderStatus.Accepted)
            return false;

        return oldStatus is null or not OrderStatus.Accepted;
    }

    /// <summary>Earliest provider time accepted as real; anything before it is a missing value read as zero.</summary>
    internal static readonly DateTime EarliestProviderTimeUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>How far a provider time may lie after Wasla's clock (clock skew) and still be accepted.</summary>
    internal static readonly TimeSpan ProviderClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A provider order time as UTC when it is plausible, otherwise null (WAS-125): before
    /// <see cref="EarliestProviderTimeUtc"/>, or more than <see cref="ProviderClockTolerance"/> after
    /// <paramref name="nowUtc"/>. An unspecified kind is taken as UTC, which is what every client produces.
    /// </summary>
    internal static DateTime? PlausibleProviderTime(DateTime? value, DateTime nowUtc)
    {
        if (value is not { } time)
            return null;

        var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return utc < EarliestProviderTimeUtc || utc > nowUtc + ProviderClockTolerance ? null : utc;
    }

    private ExternalOrderDto WithPlausibleProviderTimes(ExternalOrderDto external, DateTime nowUtc)
    {
        var orderedAt = PlausibleProviderTime(external.OrderedAtUtc, nowUtc);
        var modifiedAt = PlausibleProviderTime(external.ModifiedAtUtc, nowUtc);
        if (orderedAt is null && external.OrderedAtUtc is not null || modifiedAt is null && external.ModifiedAtUtc is not null)
        {
            _logger.LogWarning(
                "Provider order time ignored as implausible. Platform={Platform}, ExternalOrderId={ExternalOrderId}, CreationTimeIgnored={CreationTimeIgnored}, ModificationTimeIgnored={ModificationTimeIgnored}",
                external.Platform,
                external.ExternalOrderId,
                orderedAt is null && external.OrderedAtUtc is not null,
                modifiedAt is null && external.ModifiedAtUtc is not null);
        }

        return external with { OrderedAtUtc = orderedAt, ModifiedAtUtc = modifiedAt };
    }

    private static Order MapToOrderEntity(
        ExternalOrderDto external,
        string idempotencyKey,
        DateTime receivedAtUtc,
        OrderStatus internalStatus,
        DateTime nowUtc,
        bool milestonesObservedLive)
    {
        var order = new Order
        {
            Platform = external.Platform,
            ExternalOrderId = external.ExternalOrderId,
            IdempotencyKey = idempotencyKey,
            InternalStatus = internalStatus,
            PlatformStatus = external.ExternalStatus,
            CustomerName = external.CustomerName,
            TotalAmount = external.Total,
            DeliveryFee = external.DeliveryFee,
            ServiceFee = external.ServiceFee,
            PaymentMethod = external.PaymentMethod,
            PaymentStatus = external.PaymentStatus,
            CreatedAtPlatform = external.OrderedAtUtc,
            PlatformModifiedAt = external.ModifiedAtUtc,
            ReceivedAt = receivedAtUtc,
            RawPayloadJson = external.RawPayloadJson,
            ExternalOrderCode = external.ExternalOrderCode,
            CustomerPhone = external.CustomerPhone,
            CustomerAddress = external.CustomerAddress,
            CustomerNote = NormalizeCustomerNote(external.CustomerNote)
        };

        // An order first seen already Accepted, Delivered or Cancelled gets those times only from a live window.
        ApplyStatusTransitionTimestamps(order, OrderStatus.New, internalStatus, nowUtc, milestonesObservedLive);

        // Sequence keeps the order in which the provider listed the lines and their options; the mapped
        // ExternalOrderDto lists them in that order. Readers order by it (OrderItemOrdering).
        var itemSequence = 0;
        foreach (var itemDto in external.Items)
        {
            var item = new OrderItem
            {
                ProductName = itemDto.ProductName,
                Quantity = itemDto.Quantity,
                UnitPrice = itemDto.UnitPrice,
                TotalPrice = itemDto.TotalPrice,
                Notes = itemDto.Notes,
                Sequence = itemSequence++,
            };

            var optionSequence = 0;
            foreach (var opt in itemDto.Options)
            {
                item.Options.Add(new OrderItemOption
                {
                    Name = opt.Name,
                    Price = opt.Price,
                    Sequence = optionSequence++
                });
            }

            order.Items.Add(item);
        }

        return order;
    }

    // EN: Merge prevents downgrades when external/mock payloads lag behind OrderHub; terminals (Delivered/Cancelled/Failed) stay locked except allowed cancel/fail propagation.
    // TR: Dış/mock payload OrderHub'un gerisinde kaldığında düşürme olmasın diye merge; terminal durumlar (Delivered/Cancelled/Failed) kilitlenir, iptal/hata ise kurallara göre geçer.
    private static OrderStatus MergeInternalStatusForSync(OrderStatus existing, OrderStatus incomingFromExternal)
    {
        if (existing == OrderStatus.Delivered)
            return existing;

        if (existing == OrderStatus.Cancelled || existing == OrderStatus.Failed)
            return existing;

        if (incomingFromExternal == OrderStatus.Cancelled || incomingFromExternal == OrderStatus.Failed)
            return incomingFromExternal;

        var existingRank = OperationalProgressRank(existing);
        var incomingRank = OperationalProgressRank(incomingFromExternal);

        if (incomingRank < existingRank)
            return existing;

        return incomingFromExternal;
    }

    private static int OperationalProgressRank(OrderStatus status) => status switch
    {
        OrderStatus.New => 0,
        OrderStatus.Accepted => 1,
        OrderStatus.Preparing => 2,
        OrderStatus.ReadyForPickup => 3,
        OrderStatus.OnTheWay => 4,
        OrderStatus.Delivered => 5,
        OrderStatus.Cancelled => -1,
        OrderStatus.Failed => -1,
        _ => 0
    };

    /// <summary>
    /// Sets the milestone a sync-observed status change reaches, once, to <paramref name="nowUtc"/> when the change was
    /// observed live (<see cref="OrderSyncService.MilestoneObservationLimit"/>); otherwise leaves it null: the time is unknown.
    /// </summary>
    private static void ApplyStatusTransitionTimestamps(
        Order order,
        OrderStatus oldStatus,
        OrderStatus newStatus,
        DateTime nowUtc,
        bool milestonesObservedLive)
    {
        if (oldStatus == newStatus || !milestonesObservedLive) return;

        switch (newStatus)
        {
            case OrderStatus.Accepted when order.AcceptedAt is null:
                order.AcceptedAt = nowUtc;
                break;
            case OrderStatus.Delivered when order.DeliveredAt is null:
                order.DeliveredAt = nowUtc;
                break;
            case OrderStatus.Cancelled when order.CancelledAt is null:
                order.CancelledAt = nowUtc;
                break;
        }
    }

    /// <summary>
    /// Compares the persisted order with the provider payload using the values Wasla
    /// actually stores. Equality is decided before any mutation.
    /// </summary>
    /// <remarks>
    /// RawPayloadJson is a diagnostic copy of the last provider body. Nothing in the
    /// application reads it after save, and provider JSON is not canonical: property
    /// order and non-domain metadata change between overlapping polls. A raw-payload-only
    /// difference does not rewrite the order. When any synchronized field changes, the
    /// existing update path still stores the latest payload.
    /// Subtotal and ExternalItemId are not persisted, so they are not compared.
    /// Money columns are decimal(18,2); values are compared at that scale.
    /// Item and modifier order is not stored, so collections are compared as multisets.
    /// InternalStatus is compared after <see cref="MergeInternalStatusForSync"/>, so a
    /// stale provider status cannot look like a change when the merge keeps operator state.
    /// </remarks>
    private static bool IsSemanticallyUnchanged(
        Order existing,
        IReadOnlyList<PersistedOrderItemSnapshot> persistedItems,
        ExternalOrderDto external,
        OrderStatus mappedStatus)
    {
        var mergedStatus = MergeInternalStatusForSync(existing.InternalStatus, mappedStatus);
        if (existing.InternalStatus != mergedStatus)
            return false;

        if (!SameStoredText(existing.PlatformStatus, external.ExternalStatus))
            return false;

        if (!SameStoredText(existing.ExternalOrderCode, external.ExternalOrderCode))
            return false;

        if (!SameStoredText(existing.CustomerName, external.CustomerName))
            return false;

        if (!SameStoredText(existing.CustomerPhone, external.CustomerPhone))
            return false;

        if (!SameStoredText(existing.CustomerAddress, external.CustomerAddress))
            return false;

        if (!string.Equals(
                NormalizeCustomerNote(existing.CustomerNote),
                NormalizeCustomerNote(external.CustomerNote),
                StringComparison.Ordinal))
            return false;

        if (!MoneyEquals(existing.TotalAmount, external.Total))
            return false;

        if (!MoneyEquals(existing.DeliveryFee, external.DeliveryFee))
            return false;

        if (!MoneyEquals(existing.ServiceFee, external.ServiceFee))
            return false;

        if (existing.PaymentMethod != external.PaymentMethod)
            return false;

        if (existing.PaymentStatus != external.PaymentStatus)
            return false;

        // A provider time the payload lacks keeps the stored one (see UpdateExisting), so only a sent value can differ.
        if (external.OrderedAtUtc is { } orderedAt && existing.CreatedAtPlatform != orderedAt)
            return false;

        if (external.ModifiedAtUtc is { } modifiedAt && existing.PlatformModifiedAt != modifiedAt)
            return false;

        return ItemsSemanticallyEqual(persistedItems, external.Items);
    }

    private static async Task<IReadOnlyList<PersistedOrderItemSnapshot>> LoadPersistedItemSnapshotsAsync(
        TenantDbContext db,
        Guid orderId,
        CancellationToken ct)
    {
        // In the order the screens show (OrderItemOrdering), so the comparison below sees the provider's order.
        var rows = await db.OrderItems
            .AsNoTracking()
            .Where(i => i.OrderId == orderId)
            .OrderBy(i => i.Sequence).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => new
            {
                i.Id,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.TotalPrice,
                i.Notes
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return [];

        var itemIds = rows.Select(r => r.Id).ToArray();
        var optionRows = await db.OrderItemOptions
            .AsNoTracking()
            .Where(o => itemIds.Contains(o.OrderItemId))
            .OrderBy(o => o.Sequence).ThenBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => new
            {
                o.OrderItemId,
                o.Name,
                o.Price
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var optionsByItem = optionRows.ToLookup(o => o.OrderItemId);
        return rows
            .Select(r => new PersistedOrderItemSnapshot(
                r.ProductName,
                r.Quantity,
                r.UnitPrice,
                r.TotalPrice,
                r.Notes,
                optionsByItem[r.Id]
                    .Select(o => new PersistedOptionSnapshot(o.Name, o.Price))
                    .ToArray()))
            .ToList();
    }

    /// <summary>
    /// Item by item in order: the provider's order of the lines, and of each line's options, is part of the order
    /// (WAS-44, <c>Sequence</c>), so the same lines in a different order are a change that rewrites the positions.
    /// </summary>
    private static bool ItemsSemanticallyEqual(
        IReadOnlyList<PersistedOrderItemSnapshot> persistedItems,
        IReadOnlyCollection<ExternalOrderItemDto>? incomingItems)
    {
        var incoming = incomingItems ?? [];
        if (persistedItems.Count != incoming.Count)
            return false;

        var left = persistedItems.Select(ToComparableItem).ToList();
        var right = incoming.Select(ToComparableItem).ToList();

        for (var i = 0; i < left.Count; i++)
        {
            if (CompareItems(left[i], right[i]) != 0)
                return false;
        }

        return true;
    }

    private static ComparableItem ToComparableItem(PersistedOrderItemSnapshot item) =>
        new(
            item.ProductName ?? string.Empty,
            item.Quantity,
            NormalizeMoney(item.UnitPrice),
            NormalizeMoney(item.TotalPrice),
            NormalizeItemNote(item.Notes),
            NormalizeOptions(item.Options.Select(o => (o.Name, o.Price))));

    private static ComparableItem ToComparableItem(ExternalOrderItemDto item) =>
        new(
            item.ProductName ?? string.Empty,
            item.Quantity,
            NormalizeMoney(item.UnitPrice),
            NormalizeMoney(item.TotalPrice),
            NormalizeItemNote(item.Notes),
            NormalizeOptions((item.Options ?? []).Select(o => (o.Name, o.Price))));

    // Kept in the given order: the options' order is compared too.
    private static ComparableOption[] NormalizeOptions(IEnumerable<(string Name, decimal Price)> options) =>
        options
            .Select(o => new ComparableOption(o.Name ?? string.Empty, NormalizeMoney(o.Price)))
            .ToArray();

    private static int CompareItems(ComparableItem left, ComparableItem right)
    {
        var comparison = string.CompareOrdinal(left.ProductName, right.ProductName);
        if (comparison != 0)
            return comparison;

        comparison = left.Quantity.CompareTo(right.Quantity);
        if (comparison != 0)
            return comparison;

        comparison = left.UnitPrice.CompareTo(right.UnitPrice);
        if (comparison != 0)
            return comparison;

        comparison = left.TotalPrice.CompareTo(right.TotalPrice);
        if (comparison != 0)
            return comparison;

        comparison = string.CompareOrdinal(left.Notes, right.Notes);
        if (comparison != 0)
            return comparison;

        comparison = left.Options.Length.CompareTo(right.Options.Length);
        if (comparison != 0)
            return comparison;

        for (var i = 0; i < left.Options.Length; i++)
        {
            comparison = string.CompareOrdinal(left.Options[i].Name, right.Options[i].Name);
            if (comparison != 0)
                return comparison;

            comparison = left.Options[i].Price.CompareTo(right.Options[i].Price);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static bool SameStoredText(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool MoneyEquals(decimal left, decimal right) =>
        NormalizeMoney(left) == NormalizeMoney(right);

    private static decimal NormalizeMoney(decimal value) =>
        decimal.Round(value, MoneyScale, MidpointRounding.AwayFromZero);

    private static string? NormalizeItemNote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private const int MoneyScale = 2;

    private sealed record PersistedOrderItemSnapshot(
        string ProductName,
        int Quantity,
        decimal UnitPrice,
        decimal TotalPrice,
        string? Notes,
        PersistedOptionSnapshot[] Options);

    private readonly record struct PersistedOptionSnapshot(string Name, decimal Price);

    private readonly record struct ComparableOption(string Name, decimal Price);

    private sealed record ComparableItem(
        string ProductName,
        int Quantity,
        decimal UnitPrice,
        decimal TotalPrice,
        string? Notes,
        ComparableOption[] Options);
}

/// <summary>
/// A status-only provider observation (WAS-18): the provider reports that an order it already sent reached
/// <paramref name="Status"/>, without the order's contents. Applied by
/// <see cref="OrderIngestion.ApplyStatusObservationAsync"/>; it can never create an order or change its items or customer
/// fields. <paramref name="Status"/> is the Wasla status the provider adapter derived from a verified provider contract.
/// </summary>
/// <param name="PlatformStatus">The provider's status text to show, or null to keep the stored one.</param>
internal sealed record OrderStatusObservation(
    FoodPlatform Platform,
    string ExternalOrderId,
    OrderStatus Status,
    string? PlatformStatus);
