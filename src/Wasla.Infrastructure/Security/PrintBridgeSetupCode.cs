using System.Security.Cryptography;
using System.Text;

namespace Wasla.Infrastructure.Security;

/// <summary>
/// Generates and hashes cryptographically secure, URL-safe one-time values
/// (setup codes and completion credentials) for the automatic Print Bridge setup flow.
///
/// Raw values are returned to the caller exactly once and never persisted; only the
/// SHA-256 hash is stored. Mirrors <see cref="PrintBridgeTokenHasher"/> conventions.
/// </summary>
public static class PrintBridgeSetupCode
{
    /// <summary>Number of random bytes (256 bits) used for each generated value.</summary>
    public const int RandomByteCount = 32;

    /// <summary>Generate a 256-bit cryptographically secure value encoded as Base64Url.</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(RandomByteCount);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>SHA-256 hash (standard Base64) of a raw value. Throws when the value is empty.</summary>
    public static string Hash(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            throw new ArgumentException("Value is required.", nameof(rawValue));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawValue.Trim()));
        return Convert.ToBase64String(hash);
    }

    /// <summary>Constant-time comparison of a candidate raw value against a stored hash.</summary>
    public static bool Verify(string? candidateRawValue, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(candidateRawValue) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var candidateHash = Hash(candidateRawValue);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidateHash),
            Encoding.UTF8.GetBytes(storedHash));
    }
}
