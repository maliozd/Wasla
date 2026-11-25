namespace Wasla.Integrations.Platform;

/// <summary>
/// How an external order is paid, as a provider client reads it from the platform's payload. Its members, names and
/// numeric values mirror the Orders <c>PaymentMethod</c> exactly (WAS-233); Orders converts at its boundary, and a parity
/// test keeps them equal.
/// </summary>
public enum ProviderPaymentMethod
{
    Unknown = 0,
    Cash = 1,
    CreditCard = 2,
    OnlinePayment = 3
}
