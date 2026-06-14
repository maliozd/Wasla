namespace OrderHub.Application.Abstractions.Plans;



public interface IOrderHubPlanCatalog

{

    IReadOnlyList<OrderHubPlanDefinition> GetPublicPlans();



    string? NormalizePlanCode(string? planCode);



    OrderHubPlanDefinition? FindByCode(string? planCode);



    bool IsSelfServicePlan(string? planCode);

}

