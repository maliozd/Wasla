namespace Wasla.Integrations.Platform;

/// <summary>
/// Resolves one connection's provider credentials when a client is about to use them. Every call resolves them again
/// and returns a new value; an implementation keeps no plaintext between calls, so a client that asks once per request
/// holds the credentials only for that request.
/// </summary>
public interface IProviderCredentialSource
{
    Task<ProviderCredentials> GetCredentialsAsync(CancellationToken ct);
}
