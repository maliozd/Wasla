using Wasla.Application.Abstractions.Security;
using System.Security.Cryptography;

namespace Wasla.Infrastructure.Security;

public sealed class AesSecretManager : ISecretManager
{
    public const string MasterKeyEnvVarName = "ENCRYPTION_MASTER_KEY";

    private const int KeySizeBytes = 32;   // AES-256
    private const int NonceSizeBytes = 12; // recommended for GCM
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;
    private readonly int _keyVersion;

    public AesSecretManager()
    {
        _key = GetMasterKeyOrThrow();
        _keyVersion = 1;
    }

    public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct)
    {
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
        ct.ThrowIfCancellationRequested();

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var gcm = new AesGcm(_key, TagSizeBytes))
        {
            gcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData: null);
        }

        // payload = nonce || ciphertext || tag
        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);

        var encryptedBase64 = Convert.ToBase64String(payload);
        return Task.FromResult((encryptedBase64, _keyVersion));
    }

    public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct)
    {
        if (encryptedBase64 is null) throw new ArgumentNullException(nameof(encryptedBase64));
        ct.ThrowIfCancellationRequested();

        // keyVersion currently unused (single master key). Kept for rotation support.
        _ = keyVersion;

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encryptedBase64);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Encrypted payload is not valid Base64.", ex);
        }

        if (payload.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Encrypted payload is too short.");
        }

        var nonce = new byte[NonceSizeBytes];
        Buffer.BlockCopy(payload, 0, nonce, 0, NonceSizeBytes);

        var ciphertextLength = payload.Length - NonceSizeBytes - TagSizeBytes;
        var ciphertext = new byte[ciphertextLength];
        Buffer.BlockCopy(payload, NonceSizeBytes, ciphertext, 0, ciphertextLength);

        var tag = new byte[TagSizeBytes];
        Buffer.BlockCopy(payload, NonceSizeBytes + ciphertextLength, tag, 0, TagSizeBytes);

        var plaintextBytes = new byte[ciphertextLength];
        using (var gcm = new AesGcm(_key, TagSizeBytes))
        {
            gcm.Decrypt(nonce, ciphertext, tag, plaintextBytes, associatedData: null);
        }

        var plaintext = System.Text.Encoding.UTF8.GetString(plaintextBytes);
        return Task.FromResult(plaintext);
    }

    public static void ValidateMasterKeyOrThrow()
    {
        _ = GetMasterKeyOrThrow();
    }

    private static byte[] GetMasterKeyOrThrow()
    {
        var base64 = Environment.GetEnvironmentVariable(MasterKeyEnvVarName);
        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new InvalidOperationException($"Missing required environment variable '{MasterKeyEnvVarName}'.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Environment variable '{MasterKeyEnvVarName}' must be Base64.",
                ex);
        }

        if (key.Length != KeySizeBytes)
        {
            throw new InvalidOperationException(
                $"Environment variable '{MasterKeyEnvVarName}' must decode to {KeySizeBytes} bytes (got {key.Length}).");
        }

        return key;
    }
}

