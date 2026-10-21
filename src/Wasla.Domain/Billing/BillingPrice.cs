namespace Wasla.Domain.Billing;

/// <summary>
/// An exact amount and its ISO 4217 currency code, as accepted for one billing operation. Amounts are positive with
/// at most two decimal places; the currency is three upper-case ASCII letters (for example <c>TRY</c>).
/// </summary>
public readonly record struct BillingPrice
{
    public const int CurrencyLength = 3;

    public BillingPrice(decimal amount, string currency)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "A billing amount must be positive.");
        if (decimal.Round(amount, 2) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount), "A billing amount has at most two decimal places.");
        if (!IsCurrencyCode(currency))
            throw new ArgumentException("A currency is a three-letter upper-case ISO 4217 code.", nameof(currency));

        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    private static bool IsCurrencyCode(string? value) =>
        value is { Length: CurrencyLength } && value.All(c => c is >= 'A' and <= 'Z');
}
