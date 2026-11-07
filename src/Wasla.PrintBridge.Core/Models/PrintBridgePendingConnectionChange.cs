using System.Security.Cryptography;

namespace Wasla.PrintBridge.Models;

/// <summary>
/// A move to another restaurant that the user confirmed (WAS-91), kept on disk from just before the move is requested
/// until its result is saved. It holds everything the identical request needs, so the move can be repeated after a lost
/// response, a restart or a failed settings write; the server answers a repeat with the same result.
/// <para>
/// It holds secrets (the current token, the setup code and the new credentials) with the same protection as the
/// settings file, and is deleted as soon as the move is saved or refused. Never logged or shown.
/// </para>
/// </summary>
public sealed class PrintBridgePendingConnectionChange
{
    public int Version { get; set; } = 1;

    /// <summary>The server Print Bridge was connected to; the move is requested there, with its own token only.</summary>
    public string SourceServerUrl { get; set; } = string.Empty;

    public string SourceToken { get; set; } = string.Empty;

    public string InstallationId { get; set; } = string.Empty;

    /// <summary>The setup link's server; the new connection is saved for it.</summary>
    public string SetupServerUrl { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    /// <summary>The destination device token generated here before the move was requested.</summary>
    public string NewToken { get; set; } = string.Empty;

    public string CompletionCredential { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    internal bool IsComplete =>
        Version == 1
        && !string.IsNullOrWhiteSpace(SourceServerUrl)
        && !string.IsNullOrWhiteSpace(SourceToken)
        && Guid.TryParse(InstallationId, out var installationId) && installationId != Guid.Empty
        && !string.IsNullOrWhiteSpace(SetupServerUrl)
        && !string.IsNullOrWhiteSpace(Code)
        && !string.IsNullOrWhiteSpace(NewToken)
        && !string.IsNullOrWhiteSpace(CompletionCredential);

    /// <summary>32 random bytes in Base64Url: the same form as the tokens and credentials the server generates.</summary>
    public static string GenerateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
