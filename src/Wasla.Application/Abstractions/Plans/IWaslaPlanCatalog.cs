namespace Wasla.Application.Abstractions.Plans;



public interface IWaslaPlanCatalog

{

    IReadOnlyList<WaslaPlanDefinition> GetPublicPlans();



    string? NormalizePlanCode(string? planCode);



    WaslaPlanDefinition? FindByCode(string? planCode);



    bool IsSelfServicePlan(string? planCode);

}

