using Wasla.Application.Abstractions.Billing;

namespace Wasla.Web.Models.Marketing;

/// <summary>
/// The marketing home page. <see cref="PlanPrices"/> are the Central Admin plan prices in effect when the page was
/// read (WAS-147); a plan and period missing from it has no price, and the page says so.
/// </summary>
public sealed record MarketingHomeViewModel(IReadOnlyList<BillingPriceQuote> PlanPrices);
