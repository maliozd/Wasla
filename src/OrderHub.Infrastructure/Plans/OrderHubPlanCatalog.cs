using OrderHub.Application.Abstractions.Plans;

namespace OrderHub.Infrastructure.Plans;

public sealed class OrderHubPlanCatalog : IOrderHubPlanCatalog
{
    private static readonly IReadOnlyList<OrderHubPlanDefinition> Plans =
    [
        new(
            OrderHubPlanCodes.Starter,
            "Pricing.Plan.Starter.Name",
            "Pricing.Plan.Starter.Description",
            MonthlyPrice: 300m,
            PriceLabelKey: "Pricing.Plan.Starter.Price",
            FeatureKeys:
            [
                "Pricing.Plan.Starter.Feature1",
                "Pricing.Plan.Starter.Feature2",
                "Pricing.Plan.Starter.Feature3",
                "Pricing.Plan.Starter.Feature4",
                "Pricing.Plan.Starter.Feature5"
            ],
            IsContactSales: false,
            IsRecommended: false,
            IsStartingFromPrice: false,
            IncludedPrintBridgeDevices: 1,
            CtaLabelKey: "Pricing.Cta.StartStarter",
            DeviceLimitKey: "Pricing.Plan.Starter.DeviceLimit"),
        new(
            OrderHubPlanCodes.Pro,
            "Pricing.Plan.Pro.Name",
            "Pricing.Plan.Pro.Description",
            MonthlyPrice: 600m,
            PriceLabelKey: "Pricing.Plan.Pro.Price",
            FeatureKeys:
            [
                "Pricing.Plan.Pro.Feature1",
                "Pricing.Plan.Pro.Feature2",
                "Pricing.Plan.Pro.Feature3",
                "Pricing.Plan.Pro.Feature4",
                "Pricing.Plan.Pro.Feature5",
                "Pricing.Plan.Pro.Feature6"
            ],
            IsContactSales: false,
            IsRecommended: true,
            IsStartingFromPrice: false,
            IncludedPrintBridgeDevices: 3,
            CtaLabelKey: "Pricing.Cta.StartPro",
            DeviceLimitKey: "Pricing.Plan.Pro.DeviceLimit"),
        new(
            OrderHubPlanCodes.ProPlus,
            "Pricing.Plan.ProPlus.Name",
            "Pricing.Plan.ProPlus.Description",
            MonthlyPrice: 1200m,
            PriceLabelKey: "Pricing.Plan.ProPlus.Price",
            FeatureKeys:
            [
                "Pricing.Plan.ProPlus.Feature1",
                "Pricing.Plan.ProPlus.Feature2",
                "Pricing.Plan.ProPlus.Feature3",
                "Pricing.Plan.ProPlus.Feature4",
                "Pricing.Plan.ProPlus.Feature5"
            ],
            IsContactSales: false,
            IsRecommended: false,
            IsStartingFromPrice: false,
            IncludedPrintBridgeDevices: 10,
            CtaLabelKey: "Pricing.Cta.StartProPlus",
            DeviceLimitKey: "Pricing.Plan.ProPlus.DeviceLimit"),
        new(
            OrderHubPlanCodes.Enterprise,
            "Pricing.Plan.Enterprise.Name",
            "Pricing.Plan.Enterprise.Description",
            MonthlyPrice: 2500m,
            PriceLabelKey: "Pricing.Plan.Enterprise.Price",
            FeatureKeys:
            [
                "Pricing.Plan.Enterprise.Feature1",
                "Pricing.Plan.Enterprise.Feature2",
                "Pricing.Plan.Enterprise.Feature3",
                "Pricing.Plan.Enterprise.Feature4",
                "Pricing.Plan.Enterprise.Feature5",
                "Pricing.Plan.Enterprise.Feature6"
            ],
            IsContactSales: true,
            IsRecommended: false,
            IsStartingFromPrice: true,
            IncludedPrintBridgeDevices: null,
            CtaLabelKey: "Pricing.Cta.RequestOffer",
            DeviceLimitKey: "Pricing.Plan.Enterprise.DeviceLimit")
    ];

    public IReadOnlyList<OrderHubPlanDefinition> GetPublicPlans() => Plans;

    public string? NormalizePlanCode(string? planCode)
    {
        if (string.IsNullOrWhiteSpace(planCode))
            return null;

        var trimmed = planCode.Trim();
        if (string.Equals(trimmed, "Pro+", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, OrderHubPlanCodes.ProPlus, StringComparison.OrdinalIgnoreCase))
        {
            return OrderHubPlanCodes.ProPlus;
        }

        return Plans.FirstOrDefault(p =>
                string.Equals(p.PlanCode, trimmed, StringComparison.OrdinalIgnoreCase))
            ?.PlanCode;
    }

    public OrderHubPlanDefinition? FindByCode(string? planCode)
    {
        var normalized = NormalizePlanCode(planCode);
        if (normalized is null)
            return null;

        return Plans.FirstOrDefault(p => p.PlanCode == normalized);
    }

    public bool IsSelfServicePlan(string? planCode)
    {
        var plan = FindByCode(planCode);
        return plan is not null && !plan.IsContactSales;
    }
}
