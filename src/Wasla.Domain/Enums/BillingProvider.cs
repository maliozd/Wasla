namespace Wasla.Domain.Enums;

/// <summary>
/// The payment provider that holds a Wasla subscription. It namespaces the provider's own identifiers, so the same
/// reference string from two providers can never name one Wasla record. There is one provider today (WAS-145).
/// </summary>
public enum BillingProvider
{
    Iyzico = 1
}
