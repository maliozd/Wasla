namespace Wasla.Application.Orders;

/// <summary>
/// The currency of every order amount. Orders and tenants store no currency, and the business currency is Turkish lira.
/// Screens show amounts in this currency whatever the UI culture; the culture decides digit grouping, the decimal
/// separator and where the symbol goes, never the currency. The Live Screen's browser formatter
/// (<c>orders-live-store.js</c>) passes the same code to <c>Intl.NumberFormat</c>.
/// </summary>
public static class OrderCurrency
{
    /// <summary>ISO 4217 code.</summary>
    public const string Code = "TRY";

    /// <summary>The narrow symbol shown with amounts, as <c>Intl.NumberFormat</c> renders <see cref="Code"/>.</summary>
    public const string Symbol = "₺";
}
