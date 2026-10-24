namespace Wasla.Application.Abstractions.Plans;

/// <summary>
/// A public Wasla Orders plan: names, features, device limit and call to action. It holds no price: what a self-service
/// plan costs is the Central Admin plan price in effect (WAS-147, <c>IBillingPlanPriceReader</c>), monthly and yearly
/// set separately. <see cref="ContactSalesPriceLabelKey"/> is the fixed "starting from" label of a contact-sales plan.
/// </summary>
public sealed record WaslaPlanDefinition(
    string PlanCode,
    string DisplayNameKey,
    string DescriptionKey,
    string? ContactSalesPriceLabelKey,
    IReadOnlyList<string> FeatureKeys,
    bool IsContactSales,
    bool IsRecommended,
    bool IsStartingFromPrice,
    int? IncludedPrintBridgeDevices,
    string CtaLabelKey,
    string DeviceLimitKey);

