using OrderHub.Domain.Common;
using OrderHub.Domain.Enums;

namespace OrderHub.Domain.Entities.Central;

/// <summary>
/// Represents a customer (a restaurant tenant) in the central registry.
/// Each customer has its own dedicated database.
/// This entity lives ONLY in CentralDb.
/// </summary>
public class Customer : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL-friendly unique slug, e.g. "restaurant-a".
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Primary domain used to resolve requests to this customer, e.g. "restaurant-a.myorderhub.com".
    /// </summary>
    public string PrimaryDomain { get; set; } = string.Empty;

    /// <summary>
    /// Physical database name. Used in ops/admin tooling only; the connection
    /// string below already encodes how to reach it.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// AES-256-GCM encrypted connection string (Base64).
    /// NEVER log this value, encrypted or not.
    /// </summary>
    public string EncryptedConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Which master key version was used to encrypt the connection string.
    /// Enables key rotation without re-encrypting everything at once.
    /// </summary>
    public int EncryptionKeyVersion { get; set; } = 1;

    /// <summary>
    /// Schema version this customer's database is currently on, e.g. "1.0.0".
    /// The app can check this at startup to decide if the customer is compatible.
    /// </summary>
    public string SchemaVersion { get; set; } = "1.0.0";

    public DateTime? LastMigrationAt { get; set; }

    /// <summary>
    /// Free-text result of the last migration attempt, e.g. "Success" or "Failed: timeout".
    /// </summary>
    public string? LastMigrationResult { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Future billing payment state for tenant subscription (not order payment).</summary>
    public TenantBillingPaymentStatus BillingPaymentStatus { get; set; } = TenantBillingPaymentStatus.Pending;

    public ProvisioningStatus ProvisioningStatus { get; set; } = ProvisioningStatus.NotStarted;

    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.None;
}
