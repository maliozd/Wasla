namespace Wasla.Integrations.Platform.Mock;

/// <summary>
/// Marks a mock provider client: it generates orders and never calls a provider. Operational health reports a platform
/// whose registered client carries this mark as Mock and any other as Real, so the report does not depend on where the
/// mock clients are declared (WAS-232).
/// </summary>
public interface IMockFoodPlatformClient
{
}
