using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

/// <summary>How the inbox answered one delivery.</summary>
public enum TrendyolGoMealWebhookAcceptResult
{
    /// <summary>A new event was stored; acknowledge it.</summary>
    Stored,

    /// <summary>The same event was already stored; its delivery was counted. Acknowledge it.</summary>
    Duplicate,

    /// <summary>The body is not a JSON object with a package <c>id</c>: refuse it.</summary>
    Malformed
}

/// <summary>
/// The durable inbox of the Trendyol GO Meal webhook stage pilot. A delivery is authenticated with the integrator's header
/// secret, checked, and stored in the configured tenant's database before the provider is answered; processing happens
/// later (<see cref="TrendyolGoMealWebhookProcessor"/>). The shared secret authenticates the integrator, not a seller:
/// every event is stored for the one configured connection, and a <c>created</c> event that names another seller or
/// store is stored as <see cref="ProviderWebhookEventStatus.Rejected"/> and never processed.
/// </summary>
public sealed class TrendyolGoMealWebhookInbox
{
    private readonly TrendyolGoMealWebhookPilotOptions _options;
    private readonly ITenantDbContextFactory _tenants;
    private readonly ILogger<TrendyolGoMealWebhookInbox> _logger;
    private readonly TimeProvider _time;
    private readonly byte[]? _secretHash;

    public TrendyolGoMealWebhookInbox(
        IOptions<TrendyolGoMealWebhookPilotOptions> options,
        ITenantDbContextFactory tenants,
        ILogger<TrendyolGoMealWebhookInbox> logger,
        TimeProvider time)
    {
        _options = options.Value;
        _tenants = tenants;
        _logger = logger;
        _time = time;
        _secretHash = _options.Enabled && _options.SecretSha256 is { Length: 64 } hex ? Convert.FromHexString(hex) : null;
    }

    public bool IsEnabled => _options.Enabled;

    public string HeaderName => _options.HeaderName;

    public int MaxBodyBytes => _options.MaxBodyBytes;

    /// <summary>Whether the header carries the integrator secret: its SHA-256 compared in constant time.</summary>
    public bool IsAuthentic(string? headerValue)
    {
        if (_secretHash is null || string.IsNullOrEmpty(headerValue))
            return false;

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(headerValue));
        return CryptographicOperations.FixedTimeEquals(presented, _secretHash);
    }

    /// <summary>
    /// Stores one authenticated delivery for the configured tenant, or counts it when the same event is already stored.
    /// Returns only once the event is committed, so the caller may acknowledge it.
    /// </summary>
    public async Task<TrendyolGoMealWebhookAcceptResult> AcceptAsync(string eventTypeSegment, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("The Trendyol GO Meal webhook pilot is not enabled.");

        if (!TryReadEnvelope(body, out var envelope))
        {
            _logger.LogWarning("Trendyol GO Meal webhook refused: the body is not a JSON object with a package id. EventType={EventType}", Bounded(eventTypeSegment));
            return TrendyolGoMealWebhookAcceptResult.Malformed;
        }

        var documentedType = TrendyolGoMealWebhookEvents.DocumentedType(eventTypeSegment);
        var eventType = documentedType ?? Bounded(eventTypeSegment);
        var (status, outcome) = Classify(documentedType, envelope);
        var now = _time.GetUtcNow().UtcDateTime;

        var stored = new ProviderWebhookEvent
        {
            Platform = FoodPlatform.TrendyolYemek,
            EventType = eventType,
            ExternalOrderId = envelope.PackageId,
            DedupKey = DedupKey(eventType, envelope.PackageId, envelope.TimestampMs, body.Span),
            EventTimestampUtc = envelope.TimestampMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null,
            ReceivedAt = now,
            LastDeliveredAt = now,
            DeliveryCount = 1,
            PayloadJson = Encoding.UTF8.GetString(body.Span),
            Status = status,
            NextAttemptAt = now,
            Outcome = outcome,
            ProcessedAt = status is ProviderWebhookEventStatus.Pending ? null : now,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var db = await _tenants.CreateAsync(_options.TenantId, ct).ConfigureAwait(false);
        db.ProviderWebhookEvents.Add(stored);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicateEvent(ex))
        {
            db.Entry(stored).State = EntityState.Detached;
            await db.ProviderWebhookEvents
                .Where(e => e.DedupKey == stored.DedupKey)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.DeliveryCount, e => e.DeliveryCount + 1)
                    .SetProperty(e => e.LastDeliveredAt, now), ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Trendyol GO Meal webhook delivered again; counted on the stored event. EventType={EventType}, ExternalOrderId={ExternalOrderId}",
                eventType,
                envelope.PackageId);
            return TrendyolGoMealWebhookAcceptResult.Duplicate;
        }

        _logger.LogInformation(
            "Trendyol GO Meal webhook stored. EventId={EventId}, EventType={EventType}, ExternalOrderId={ExternalOrderId}, Status={Status}, Outcome={Outcome}",
            stored.Id,
            eventType,
            envelope.PackageId,
            status,
            outcome);
        return TrendyolGoMealWebhookAcceptResult.Stored;
    }

    private (ProviderWebhookEventStatus Status, string? Outcome) Classify(string? documentedType, Envelope envelope)
    {
        if (documentedType is null)
            return (ProviderWebhookEventStatus.Ignored, "UndocumentedEventType");
        if (TrendyolGoMealWebhookEvents.IsInformational(documentedType))
            return (ProviderWebhookEventStatus.Ignored, "NoOrderChange");

        // Only created carries the seller and store. Either one that names another seller or store is refused; a missing
        // one is not, because processing never trusts the payload: it reads the package back with the connection.
        if (envelope.SupplierId is { } supplier && !_options.IsPilotSeller(supplier))
            return (ProviderWebhookEventStatus.Rejected, "SellerMismatch");
        if (envelope.StoreId is { } store && !_options.IsPilotStore(store))
            return (ProviderWebhookEventStatus.Rejected, "StoreMismatch");

        return (ProviderWebhookEventStatus.Pending, null);
    }

    /// <summary>
    /// One event, whatever the number of deliveries: SHA-256 of the platform, the event type, the package id and the
    /// provider's <c>timestamp</c>; for an event without one, of the exact body.
    /// </summary>
    internal static string DedupKey(string eventType, string packageId, long? timestampMs, ReadOnlySpan<byte> body)
    {
        var instant = timestampMs is { } ms
            ? ms.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "body:" + Convert.ToHexString(SHA256.HashData(body));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{FoodPlatform.TrendyolYemek}|{eventType}|{packageId}|{instant}")));
    }

    private static bool IsDuplicateEvent(DbUpdateException exception)
    {
        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
                return sql.Number is 2601 or 2627
                    && sql.Errors[0].Message.Contains("'IX_ProviderWebhookEvents_DedupKey'", StringComparison.Ordinal);

            if (current.Message.Contains("UNIQUE constraint failed: ProviderWebhookEvents.DedupKey", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string Bounded(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= 64 ? text : text[..64];
    }

    private readonly record struct Envelope(string PackageId, long? TimestampMs, string? SupplierId, string? StoreId);

    private static bool TryReadEnvelope(ReadOnlyMemory<byte> body, out Envelope envelope)
    {
        envelope = default;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: > 0 and <= 128 } packageId
                || string.IsNullOrWhiteSpace(packageId))
            {
                return false;
            }

            long? timestamp = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var value) && value > 0
                ? value
                : null;
            envelope = new Envelope(packageId.Trim(), timestamp, Identifier(root, "supplierId"), Identifier(root, "storeId"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>An identifier given as a JSON number or a non-blank string; anything else is treated as absent.</summary>
    private static string? Identifier(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString()!.Trim(),
            _ => null
        };
    }
}
