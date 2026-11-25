using Wasla.Application.Abstractions.Platform;
using Wasla.Integrations.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Infrastructure.Platform;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Hands a test's stored connection or order to a provider client the way Orders does (WAS-231): through the host
/// adapter, from the values the entity has at the call. A test that changes the entity between calls is therefore seen
/// like a connection loaded again by the next operation.
/// </summary>
internal static class ProviderTestContexts
{
    /// <summary>The connection with its stored credential fields used as the plaintext (<see cref="PassthroughSecrets"/>).</summary>
    internal static ProviderConnectionContext AsProviderContext(this PlatformConnection connection) =>
        connection.ToProviderContext(new PassthroughSecrets());

    internal static ProviderConnectionContext AsProviderContext(this PlatformConnection connection, ISecretManager secretManager) =>
        connection.ToProviderContext(secretManager);

    internal static ProviderOrderReference AsProviderReference(this Order order) =>
        new(order.ExternalOrderId, order.RawPayloadJson);
}
