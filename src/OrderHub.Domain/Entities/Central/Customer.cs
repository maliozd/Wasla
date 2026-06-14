using OrderHub.Domain.Common;
using OrderHub.Domain.Enums;

namespace OrderHub.Domain.Entities.Central;

/// <summary>
/// Central registry record for a restaurant business using Wasla (the SaaS tenant).
/// Each record has its own dedicated tenant database (<see cref="DatabaseName"/>).
/// This entity lives ONLY in CentralDb.
/// </summary>
/// <remarks>
/// Terminology note: despite the name <c>Customer</c>, this entity represents the
/// tenant (restaurant/business), not an end customer who places a food order.
/// Order-level fields such as <c>CustomerName</c> on <see cref="OrderHub.Domain.Entities.Customer.Order"/>
/// refer to the food orderer and must keep Customer terminology.
/// A future rename of this type to Tenant is planned; see docs/tenant-vs-customer.md.
/// </remarks>
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
