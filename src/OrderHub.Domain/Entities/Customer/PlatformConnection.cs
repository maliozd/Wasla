using OrderHub.Domain.Common;
using OrderHub.Domain.Enums;

namespace OrderHub.Domain.Entities.Customer;

/// <summary>
/// A connection to a specific food-delivery platform for this customer.
/// Holds encrypted credentials and sync/circuit-breaker state.
/// </summary>
public class PlatformConnection : BaseEntity
{
    public FoodPlatform Platform { get; set; }

    /// <summary>
    /// Store/restaurant identifier assigned by the external platform.
    /// </summary>
    public string StoreId { get; set; } = string.Empty;

    /// <summary>
    /// Optional chain/supplier identifier used by some platforms (e.g. Trendyol GO supplier id).
    /// </summary>
    public string? SupplierId { get; set; }

    /// <summary>
    /// Optional executor email used by some platforms (e.g. x-executor-user header).
    /// </summary>
    public string? ExecutorEmail { get; set; }

    /// <summary>
    /// AES-256-GCM encrypted API key. Never log, never return from API.
    /// </summary>
    public string EncryptedApiKey { get; set; } = string.Empty;

    /// <summary>
    /// AES-256-GCM encrypted API secret. Never log, never return from API.
    /// </summary>
    public string EncryptedApiSecret { get; set; } = string.Empty;

    public int EncryptionKeyVersion { get; set; } = 1;

    public bool IsActive { get; set; } = true;

    // --- Sync state ---

    public DateTime? LastSyncAttempt { get; set; }
    public DateTime? LastSuccessfulSync { get; set; }

    /// <summary>
    /// Number of consecutive failed sync attempts. Reset to 0 on any success.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// If set and in the future, the worker will skip this connection until this time passes.
    /// Set when ConsecutiveFailures reaches the circuit-breaker threshold.
    /// </summary>
    public DateTime? CircuitOpenUntil { get; set; }

    /// <summary>
    /// Polling interval in seconds for this connection. Defaults to 30.
    /// Allows per-connection backoff later without code changes.
    /// </summary>
    public int SyncIntervalSeconds { get; set; } = 30;
}
