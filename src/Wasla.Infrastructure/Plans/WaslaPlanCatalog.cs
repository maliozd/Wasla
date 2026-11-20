using Wasla.Application.Abstractions.Plans;
namespace Wasla.Infrastructure.Plans;
public sealed class WaslaPlanCatalog : IWaslaPlanCatalog
{
    // No plan limits printers or Print Bridge devices, and no plan is described by such a count (decision of 2026-11-17,
    // docs/architecture/billing.md#plans-and-packaging). Enterprise has custom pricing and no public amount.
    private static readonly IReadOnlyList<WaslaPlanDefinition> Plans =
    [
        new(
            WaslaPlanCodes.Starter,
            "Pricing.Plan.Starter.Name",
            "Pricing.Plan.Starter.Description",
            ContactSalesPriceLabelKey: null,
            FeatureKeys:
            [
                "Pricing.Plan.Starter.Feature1",
                "Pricing.Plan.Starter.Feature2",
                "Pricing.Plan.Starter.Feature3",
                "Pricing.Plan.Starter.Feature4"
            ],
            IsContactSales: false,
            IsRecommended: false,
            CtaLabelKey: "Pricing.Cta.StartStarter"),

        new(
            WaslaPlanCodes.Pro,
            "Pricing.Plan.Pro.Name",
            "Pricing.Plan.Pro.Description",
            ContactSalesPriceLabelKey: null,
            FeatureKeys:
            [
                "Pricing.Plan.Pro.Feature1",
                "Pricing.Plan.Pro.Feature2",
                "Pricing.Plan.Pro.Feature3",
                "Pricing.Plan.Pro.Feature4",
                "Pricing.Plan.Pro.Feature5"
            ],
            IsContactSales: false,
            IsRecommended: true,
            CtaLabelKey: "Pricing.Cta.StartPro"),

        new(
            WaslaPlanCodes.ProPlus,
            "Pricing.Plan.ProPlus.Name",
            "Pricing.Plan.ProPlus.Description",
            ContactSalesPriceLabelKey: null,
            FeatureKeys:
            [
                "Pricing.Plan.ProPlus.Feature1",
                "Pricing.Plan.ProPlus.Feature2",
                "Pricing.Plan.ProPlus.Feature3"
            ],
            IsContactSales: false,
            IsRecommended: false,
            CtaLabelKey: "Pricing.Cta.StartProPlus"),
        new(
            WaslaPlanCodes.Enterprise,
            "Pricing.Plan.Enterprise.Name",
            "Pricing.Plan.Enterprise.Description",
            ContactSalesPriceLabelKey: "Pricing.ContactSales",
            FeatureKeys:
            [
                "Pricing.Plan.Enterprise.Feature1",
                "Pricing.Plan.Enterprise.Feature2",
                "Pricing.Plan.Enterprise.Feature3",
                "Pricing.Plan.Enterprise.Feature4",
                "Pricing.Plan.Enterprise.Feature5"
            ],
            IsContactSales: true,
            IsRecommended: false,
            CtaLabelKey: "Pricing.Cta.RequestOffer")
    ];
    public IReadOnlyList<WaslaPlanDefinition> GetPublicPlans() => Plans;
    public string? NormalizePlanCode(string? planCode)
    {
        if (string.IsNullOrWhiteSpace(planCode))
            return null;
        var trimmed = planCode.Trim();
        if (string.Equals(trimmed, "Pro+", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, WaslaPlanCodes.ProPlus, StringComparison.OrdinalIgnoreCase))
        {
            return WaslaPlanCodes.ProPlus;
        }
        return Plans.FirstOrDefault(p => string.Equals(p.PlanCode, trimmed, StringComparison.OrdinalIgnoreCase))?.PlanCode;
    }
    public WaslaPlanDefinition? FindByCode(string? planCode)
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