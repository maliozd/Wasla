namespace Wasla.Domain.Enums;

/// <summary>
/// The provider environment a billing record was created in. Provider identifiers are unique only within one
/// environment, and a write verified in one environment never changes a record of the other.
/// </summary>
public enum BillingEnvironment
{
    Sandbox = 1,
    Production = 2
}
