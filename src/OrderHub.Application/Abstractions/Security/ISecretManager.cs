namespace OrderHub.Application.Abstractions.Security;

public interface ISecretManager
{
    Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct);

    Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct);
}

