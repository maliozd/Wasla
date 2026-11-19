namespace Wasla.Application.Abstractions.Plans;

/// <summary>
/// A public Wasla Orders plan: names, features and call to action. It holds no price: what a self-service plan costs is
/// the Central Admin plan price in effect (WAS-147, <c>IBillingPlanPriceReader</c>), monthly and yearly set separately.
/// <see cref="ContactSalesPriceLabelKey"/> is the custom-pricing label of a contact-sales plan, never an amount. Plans
/// are not distinguished by a printer or device count: there is no device limit (decision of 2026-11-17).
/// </summary>
public sealed record WaslaPlanDefinition(
    string PlanCode,
    string DisplayNameKey,
    string DescriptionKey,
    string? ContactSalesPriceLabelKey,
    IReadOnlyList<string> FeatureKeys,
    bool IsContactSales,
    bool IsRecommended,
    string CtaLabelKey);
