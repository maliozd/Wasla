using System.Globalization;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Billing;

namespace Wasla.Web.Formatting;

/// <summary>
/// How a Wasla plan price reads on the public pricing section, signup and the checkout summary (WAS-147), so every
/// customer-facing page words the same price the same way. Presentation only: which price applies is decided in
/// Infrastructure; nothing here falls back to an amount when there is no price.
/// </summary>
public static class PlanPriceText
{
    public const string Monthly = "Monthly";
    public const string Yearly = "Yearly";

    public static BillingPriceQuote? Find(IEnumerable<BillingPriceQuote> prices, string planCode, string billingPeriod) =>
        prices.FirstOrDefault(p => p.PlanCode == planCode && p.BillingPeriod == billingPeriod);

    /// <summary>"1.200 TL", "299,90 TL" in the request culture; whole amounts without decimals.</summary>
    public static string Amount(BillingPrice price, IStringLocalizer localizer)
    {
        var culture = CultureInfo.CurrentCulture;
        var number = price.Amount.ToString(decimal.Truncate(price.Amount) == price.Amount ? "N0" : "N2", culture);
        var currency = price.Currency == "TRY" ? localizer["Billing.CurrencyTry"].Value : price.Currency;
        return number + " " + currency;
    }

    /// <summary>"1.200 TL / ay", or the "price not set" text when there is no price for that period.</summary>
    public static string PerPeriod(BillingPriceQuote? quote, string billingPeriod, IStringLocalizer localizer) =>
        quote is null
            ? localizer["Billing.PriceNotSet"].Value
            : string.Format(
                CultureInfo.CurrentCulture,
                localizer[billingPeriod == Yearly ? "Pricing.PricePerYear" : "Pricing.PricePerMonth"].Value,
                Amount(quote.Price, localizer));

    /// <summary>"KDV dahil" or "KDV hariç"; nothing when there is no price.</summary>
    public static string? Vat(BillingPriceQuote? quote, IStringLocalizer localizer) =>
        quote is null ? null : localizer[quote.VatIncluded ? "Billing.VatIncluded" : "Billing.VatExcluded"].Value;

    /// <summary>
    /// The yearly saving against twelve monthly payments, only when the configured prices show one: both prices exist,
    /// in the same currency, both with or both without VAT, and the yearly price is lower. Otherwise no claim at all.
    /// </summary>
    public static string? YearlySaving(BillingPriceQuote? monthly, BillingPriceQuote? yearly, IStringLocalizer localizer)
    {
        if (monthly is null || yearly is null
            || monthly.Price.Currency != yearly.Price.Currency
            || monthly.VatIncluded != yearly.VatIncluded)
            return null;

        var saving = monthly.Price.Amount * 12m - yearly.Price.Amount;
        return saving <= 0m
            ? null
            : string.Format(
                CultureInfo.CurrentCulture,
                localizer["Pricing.YearlySaving"].Value,
                Amount(new BillingPrice(saving, yearly.Price.Currency), localizer));
    }
}
