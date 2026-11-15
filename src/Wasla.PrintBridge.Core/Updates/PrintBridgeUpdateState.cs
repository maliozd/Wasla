using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Updates;

/// <summary>Where an update attempt is (WAS-63). The first three are interrupted by a restart; <see cref="Ready"/> survives it.</summary>
public enum PrintBridgeUpdateStage
{
    Downloading,
    Verifying,

    /// <summary>Verified; waiting until no print job is in progress to pause printing.</summary>
    WaitingForPrinting,

    /// <summary>Printing is paused and the verified setup may run. Kept through the installation and its restarts.</summary>
    Ready,

    /// <summary>The new version is installed and committed; printing resumed.</summary>
    Succeeded,

    /// <summary>The attempt stopped before the setup could run, or ended in an unexpected state.</summary>
    Failed,

    /// <summary>The setup did not install the new version (cancelled, refused, failed or rolled back); the previous version runs.</summary>
    NotApplied,

    /// <summary>Cancelled by the user, or expired while nobody ran the setup.</summary>
    Cancelled
}

/// <summary>Why an attempt failed. A code for support and the app's text; never free text.</summary>
public enum PrintBridgeUpdateFailure
{
    /// <summary>The service stopped (restart, crash, power loss) before the setup was ready.</summary>
    Interrupted,

    /// <summary>The server or the network failed during the download.</summary>
    Download,

    SizeMismatch,
    HashMismatch,
    NotSigned,

    /// <summary>The signature does not verify (untrusted, revoked, tampered, or its revocation could not be checked).</summary>
    SignatureInvalid,

    /// <summary>Validly signed, but not by the publisher that signed this installation.</summary>
    PublisherMismatch,

    /// <summary>The setup's own version is not the described one, or not newer than the installed one.</summary>
    VersionMismatch,

    /// <summary>A print job did not finish within the wait; nothing was paused.</summary>
    PrintingInProgress,

    /// <summary>After the setup ran, a version that is neither the previous nor the target one is running.</summary>
    UnexpectedVersion,

    /// <summary>The setup was ready but nobody ran it within the gate's lifetime.</summary>
    Expired,

    /// <summary>A file could not be written or read in the updates folder.</summary>
    Storage
}

/// <summary>
/// The durable record of the current or last update attempt (<c>updates\update-state.json</c>, schema 1). It names versions,
/// the setup's size and hash, the stage and timestamps; never a token, a URL or a path.
/// </summary>
public sealed record PrintBridgeUpdateRecord(
    int SchemaVersion,
    Guid AttemptId,
    string FromVersion,
    string ToVersion,
    long SetupBytes,
    string SetupSha256,
    PrintBridgeUpdateStage Stage,
    DateTime StartedAtUtc,
    DateTime? ReadyAtUtc,
    DateTime? FinishedAtUtc,
    PrintBridgeUpdateFailure? Failure)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>True while printing is paused for this attempt (it survives a restart only in <see cref="PrintBridgeUpdateStage.Ready"/>).</summary>
    [JsonIgnore]
    public bool HoldsGate => Stage == PrintBridgeUpdateStage.Ready;

    [JsonIgnore]
    public bool IsFinished => Stage is PrintBridgeUpdateStage.Succeeded or PrintBridgeUpdateStage.Failed
        or PrintBridgeUpdateStage.NotApplied or PrintBridgeUpdateStage.Cancelled;

    public PrintBridgeUpdateRecord Finish(PrintBridgeUpdateStage stage, PrintBridgeUpdateFailure? failure = null) =>
        this with { Stage = stage, Failure = failure, FinishedAtUtc = DateTime.UtcNow };
}

/// <summary>
/// Reads and writes <see cref="PrintBridgeUpdateRecord"/> atomically in <see cref="PrintBridgePaths.UpdatesDirectory"/>, and
/// names the staged setup's files. The service is its only writer.
/// </summary>
public sealed class PrintBridgeUpdateStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly ILogger<PrintBridgeUpdateStateStore> _logger;

    public PrintBridgeUpdateStateStore(ILogger<PrintBridgeUpdateStateStore> logger)
    {
        _logger = logger;
    }

    /// <summary>The verified setup of <paramref name="version"/> in the updates folder.</summary>
    public static string StagedSetupPath(Version version) =>
        Path.Combine(PrintBridgePaths.UpdatesDirectory, $"Wasla.PrintBridge-Setup-x64-{PrintBridgeReleaseVersion.Format(version)}.exe");

    /// <summary>The download in progress of <paramref name="version"/>; renamed to <see cref="StagedSetupPath"/> when complete.</summary>
    public static string PartialSetupPath(Version version) => StagedSetupPath(version) + ".partial";

    /// <summary>How often, and how long apart, a record in use by another process is read again.</summary>
    internal static int ReadAttempts { get; set; } = 10;

    internal static TimeSpan ReadRetry { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The record, or null when there is none or it cannot be used. A record whose content is damaged or of another form is
    /// set aside and logged. A file another process keeps in use (a write in progress, an antivirus scan) is read again a
    /// few times, and if it stays in use it is left as it is: being in use is not damage.
    /// </summary>
    public PrintBridgeUpdateRecord? Load()
    {
        var path = PrintBridgePaths.UpdateStatePath;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                string text;
                // Shared for writing and deleting, so reading never keeps the service from replacing the file.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();

                var record = JsonSerializer.Deserialize<PrintBridgeUpdateRecord>(text, JsonOptions);
                if (record is not null
                    && record.SchemaVersion == PrintBridgeUpdateRecord.CurrentSchemaVersion
                    && record.AttemptId != Guid.Empty
                    && PrintBridgeReleaseVersion.TryParse(record.FromVersion, out _)
                    && PrintBridgeReleaseVersion.TryParse(record.ToVersion, out _))
                    return record;

                _logger.LogWarning("The update state has an unknown form; it is set aside.");
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _logger.LogWarning(ex, "The update state could not be read; it is set aside.");
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < ReadAttempts)
                {
                    Thread.Sleep(ReadRetry);
                    continue;
                }

                _logger.LogWarning(ex, "The update state is in use and could not be read; it is left as it is.");
                return null;
            }
        }

        TrySetAside(path);
        return null;
    }

    /// <summary>Writes the record atomically; throws when it cannot (the caller must not act as if it were saved).</summary>
    public void Save(PrintBridgeUpdateRecord record)
    {
        Directory.CreateDirectory(PrintBridgePaths.UpdatesDirectory);
        PrintBridgeSettingsStore.WriteJsonAtomically(PrintBridgePaths.UpdateStatePath, JsonSerializer.Serialize(record, JsonOptions));
    }

    /// <summary>
    /// Deletes the staged and partial setup files of every version (they are re-downloaded when needed). Returns false when
    /// one is left: a setup that is still running from it keeps it in use.
    /// </summary>
    public bool DeleteStagedFiles()
    {
        var left = false;
        try
        {
            if (!Directory.Exists(PrintBridgePaths.UpdatesDirectory))
                return true;

            foreach (var file in Directory.EnumerateFiles(PrintBridgePaths.UpdatesDirectory, "Wasla.PrintBridge-Setup-x64-*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left = true;
                    _logger.LogDebug(ex, "A staged update file is in use and was not deleted yet.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The updates folder could not be read.");
            return false;
        }

        return !left;
    }

    private static void TrySetAside(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
