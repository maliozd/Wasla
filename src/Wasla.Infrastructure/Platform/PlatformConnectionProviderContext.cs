using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Domain.Entities.TenantDb;

namespace Wasla.Infrastructure.Platform;

/// <summary>
/// The provider-facing view of a <see cref="PlatformConnection"/> an operation has loaded (WAS-231). It captures the
/// connection's values and its stored, encrypted credentials as they were loaded, so an edit saved while the operation
/// runs is used only by the next operation that loads the connection. The credentials are decrypted when a client asks
/// for them, on every ask, and never cached; the database is not read again.
/// </summary>
internal static class PlatformConnectionProviderContext
{
    internal static ProviderConnectionContext ToProviderContext(this PlatformConnection connection, ISecretManager secretManager)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(secretManager);
        return new ProviderConnectionContext(
            connection.Id,
            connection.Platform,
            connection.StoreId,
            connection.SupplierId,
            connection.ExecutorEmail,
            connection.IsActive,
            new StoredCredentialSource(
                secretManager,
                connection.EncryptedApiKey,
                connection.EncryptedApiSecret,
                connection.EncryptionKeyVersion));
    }

    /// <summary>
    /// Decrypts the captured credentials with their stored key version on every call, the API key first and then the
    /// secret, and keeps nothing it decrypted. Its text form shows no stored credential.
    /// </summary>
    private sealed class StoredCredentialSource(
        ISecretManager secretManager,
        string encryptedApiKey,
        string encryptedApiSecret,
        int keyVersion) : IProviderCredentialSource
    {
        public async Task<ProviderCredentials> GetCredentialsAsync(CancellationToken ct)
        {
            var apiKey = await secretManager.DecryptAsync(encryptedApiKey, keyVersion, ct);
            var apiSecret = await secretManager.DecryptAsync(encryptedApiSecret, keyVersion, ct);
            return new ProviderCredentials(apiKey, apiSecret);
        }

        public override string ToString() => "StoredCredentialSource { [redacted] }";
    }
}
