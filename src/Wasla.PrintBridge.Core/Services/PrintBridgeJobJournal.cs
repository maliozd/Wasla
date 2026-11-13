using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

/// <summary>How far a claimed job got before its last report (WAS-61).</summary>
public enum PrintJournalStage
{
    /// <summary>Claimed by this engine; the receipt was not handed to Windows, or Windows refused it. It was not printed.</summary>
    NotPrinted,

    /// <summary>Being handed to the spooler (or, in test mode, written to a file). Whether paper came out is unknown.</summary>
    Submitting,

    /// <summary>Windows accepted the receipt; the server has not confirmed <c>mark-printed</c> yet.</summary>
    Printed,

    /// <summary>
    /// Found <see cref="Submitting"/> after a restart: whether paper came out is unknown. Never printed again and never
    /// reported on its own; it waits for the user's check (printed or not), which is then reported.
    /// </summary>
    Uncertain
}

/// <summary>
/// One job between its successful claim and its confirmed last report. It names the connection that claimed the job by
/// server URL and a SHA-256 fingerprint of the token; the token itself is never written here.
/// </summary>
public sealed record PrintJournalEntry(
    Guid JobId,
    PrintJournalStage Stage,
    string ServerUrl,
    string TokenFingerprint,
    DateTime UpdatedAtUtc)
{
    public bool BelongsTo(WaslaOptions connection) =>
        string.Equals(ServerUrl, connection.ServerUrl, StringComparison.Ordinal)
        && string.Equals(TokenFingerprint, PrintBridgeJobJournal.Fingerprint(connection.AgentToken), StringComparison.Ordinal);
}

/// <summary>
/// The engine's record of claimed jobs whose last report is not confirmed (WAS-61), in <see cref="PrintBridgePaths.JournalPath"/>.
/// The runtime writes it at each stage of a job and reads it before polling, so a job interrupted by a crash, a kill, a
/// power loss, a stop that outlasted its grace period, or a report the server never confirmed is finished conservatively:
/// a receipt that was not printed is reported failed, a printed one is reported printed again (the server ignores a
/// repeat), and one whose print outcome is unknown is never printed again and never reported.
/// <para>
/// Writes are atomic (a temporary file replaces the old one). A write that fails is logged and the job goes on: the
/// journal can lose recovery information, never print or report anything by itself.
/// </para>
/// </summary>
public sealed class PrintBridgeJobJournal
{
    private const int MaxEntries = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ILogger<PrintBridgeJobJournal> _logger;
    private readonly object _sync = new();
    private List<PrintJournalEntry>? _entries;

    public PrintBridgeJobJournal(ILogger<PrintBridgeJobJournal> logger)
    {
        _logger = logger;
    }

    /// <summary>A fingerprint of <paramref name="token"/> that cannot be turned back into it (SHA-256, hex).</summary>
    public static string Fingerprint(string? token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token?.Trim() ?? string.Empty)));

    public IReadOnlyList<PrintJournalEntry> Entries
    {
        get
        {
            lock (_sync)
                return [.. Load()];
        }
    }

    public void Record(Guid jobId, PrintJournalStage stage, WaslaOptions connection)
    {
        lock (_sync)
        {
            var entries = Load();
            entries.RemoveAll(e => e.JobId == jobId);
            entries.Add(new PrintJournalEntry(
                jobId,
                stage,
                connection.ServerUrl,
                Fingerprint(connection.AgentToken),
                DateTime.UtcNow));
            if (entries.Count > MaxEntries)
                entries.RemoveRange(0, entries.Count - MaxEntries);
            Save(entries);
        }
    }

    public void Remove(Guid jobId)
    {
        lock (_sync)
        {
            var entries = Load();
            if (entries.RemoveAll(e => e.JobId == jobId) > 0)
                Save(entries);
        }
    }

    private List<PrintJournalEntry> Load()
    {
        if (_entries is not null)
            return _entries;

        var path = PrintBridgePaths.JournalPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<PrintJournalEntry>>(File.ReadAllText(path), JsonOptions);
                _entries = loaded?.Where(e => e is not null && e.JobId != Guid.Empty).ToList() ?? [];
                return _entries;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unreadable content is not a job: setting it aside keeps the engine running. A job it named stays as the
            // server has it (Printing), which is what happens without a journal.
            _logger.LogWarning(ex, "The print journal could not be read; it is set aside.");
            TrySetAside(path);
        }

        _entries = [];
        return _entries;
    }

    private void Save(List<PrintJournalEntry> entries)
    {
        try
        {
            PrintBridgePaths.EnsureProgramDataDirectories();
            if (entries.Count == 0)
            {
                if (File.Exists(PrintBridgePaths.JournalPath))
                    File.Delete(PrintBridgePaths.JournalPath);
                return;
            }

            PrintBridgeSettingsStore.WriteJsonAtomically(
                PrintBridgePaths.JournalPath,
                JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The print journal could not be written; recovery after a crash may be incomplete.");
        }
    }

    private static void TrySetAside(string path)
    {
        try
        {
            File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
