using Wasla.Application.Abstractions.Plans;

namespace Wasla.Application.Onboarding;

public static class CheckoutSimulatedPricing
{
    private static readonly IReadOnlyDictionary<string, decimal> MonthlyPriceByPlan =
      new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
      {
          [WaslaPlanCodes.Starter] = 499m,
          [WaslaPlanCodes.Pro] = 999m,
          [WaslaPlanCodes.ProPlus] = 1499m,
          [WaslaPlanCodes.Enterprise] = 1999m
      };

    public static decimal GetMonthlyPriceTry(string planCode) =>
        MonthlyPriceByPlan.TryGetValue(planCode.Trim(), out var price) ? price : MonthlyPriceByPlan[WaslaPlanCodes.Starter];

    public static decimal GetTotalPriceTry(string planCode, string billingPeriod)
    {
        var monthly = GetMonthlyPriceTry(planCode);
        return IsYearlyBilling(billingPeriod) ? monthly * 10m : monthly;
    }

    public static bool IsYearlyBilling(string billingPeriod) =>
        string.Equals(billingPeriod, "Yearly", StringComparison.OrdinalIgnoreCase);
}
