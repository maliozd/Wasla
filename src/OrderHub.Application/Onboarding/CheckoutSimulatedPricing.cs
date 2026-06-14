using OrderHub.Application.Abstractions.Plans;

namespace OrderHub.Application.Onboarding;

public static class CheckoutSimulatedPricing
{
    private static readonly IReadOnlyDictionary<string, decimal> MonthlyPriceByPlan =
      new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
      {
          [OrderHubPlanCodes.Starter] = 499m,
          [OrderHubPlanCodes.Pro] = 999m,
          [OrderHubPlanCodes.ProPlus] = 1499m,
          [OrderHubPlanCodes.Enterprise] = 1999m
      };

    public static decimal GetMonthlyPriceTry(string planCode) =>
        MonthlyPriceByPlan.TryGetValue(planCode.Trim(), out var price) ? price : MonthlyPriceByPlan[OrderHubPlanCodes.Starter];

    public static decimal GetTotalPriceTry(string planCode, string billingPeriod)
    {
        var monthly = GetMonthlyPriceTry(planCode);
        return IsYearlyBilling(billingPeriod) ? monthly * 10m : monthly;
    }

    public static bool IsYearlyBilling(string billingPeriod) =>
        string.Equals(billingPeriod, "Yearly", StringComparison.OrdinalIgnoreCase);
}
