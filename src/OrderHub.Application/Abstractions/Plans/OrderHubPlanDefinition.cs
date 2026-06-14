namespace OrderHub.Application.Abstractions.Plans;

public sealed record OrderHubPlanDefinition(
    string PlanCode,
    string DisplayNameKey,
    string DescriptionKey,
    decimal? MonthlyPrice,
    string PriceLabelKey,
    IReadOnlyList<string> FeatureKeys,
    bool IsContactSales,
    bool IsRecommended,
    bool IsStartingFromPrice,
    int? IncludedPrintBridgeDevices,
    string CtaLabelKey,
    string DeviceLimitKey);
