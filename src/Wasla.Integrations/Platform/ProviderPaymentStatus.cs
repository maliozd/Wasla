namespace Wasla.Integrations.Platform;

/// <summary>
/// Whether an external order is paid, as a provider client reads it from the platform's payload. Its members, names and
/// numeric values mirror the Orders <c>PaymentStatus</c> exactly (WAS-233); Orders converts at its boundary, and a parity
/// test keeps them equal.
/// </summary>
public enum ProviderPaymentStatus
{
    Unknown = 0,
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Refunded = 4
}
