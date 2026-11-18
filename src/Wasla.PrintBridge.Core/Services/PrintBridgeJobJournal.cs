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

    /// <summary>
    /// Being handed to the spooler (or, in test mode, written to a file), or (WAS-216, with <see cref="PrintJournalEntry.Submission"/>)
    /// handed over and still in the Windows queue. Whether paper came out is not known yet. A version without WAS-216
    /// reads it as an interrupted submission and makes it <see cref="Uncertain"/>, never printing it again.
    /// </summary>
    Submitting,

    /// <summary>
    /// Windows finished sending every copy to the printer (WAS-216: the jobs left the queue; before WAS-216, the spooler
    /// accepted them); the server has not confirmed <c>mark-printed</c> yet. Not proof that paper came out.
    /// </summary>
    Printed,

    /// <summary>
    /// Whether paper came out is unknown: an interrupted submission, a job that failed after data was written, a job
    /// removed from the Windows queue, or copies only partly sent. Never printed again and never reported on its own; it
    /// waits for the user's check (printed or not), which is then reported.
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
    /// <summary>The Windows jobs of this receipt (WAS-216); null before the receipt is handed to Windows, and in older journals.</summary>
    public PrintJournalSubmission? Submission { get; init; }

    public bool BelongsTo(WaslaOptions connection) =>
        string.Equals(ServerUrl, connection.ServerUrl, StringComparison.Ordinal)
        && string.Equals(TokenFingerprint, PrintBridgeJobJournal.Fingerprint(connection.AgentToken), StringComparison.Ordinal);
}

/// <summary>
/// How a receipt was handed to Windows (WAS-216): one RAW job per copy, each with a document name that is unique to this
/// submission, so a job is recognised as this receipt's even after a restart and never confused with a reused job id.
/// </summary>
public sealed record PrintJournalSubmission(
    string PrinterName,
    string SubmissionId,
    int CopyCount,
    IReadOnlyList<PrintJournalCopy> Copies)
{
    public PrintJournalSubmission With(PrintJournalCopy copy) =>
        this with { Copies = [.. Copies.Where(c => c.Number != copy.Number), copy] };

    public static string DocumentName(string submissionId, int number, int copyCount) =>
        $"Wasla Receipt {submissionId} {number}/{copyCount}";
}

/// <summary>
/// One copy's Windows job. <see cref="WindowsJobId"/> is null until StartDocPrinter returned it; <see cref="Spooled"/>
/// once every byte was written and the job finished. The Seen flags remember what Windows reported while it was queued.
/// </summary>
public sealed record PrintJournalCopy(
    int Number,
    string DocumentName,
    DateTime StartedAtUtc,
    int? WindowsJobId = null,
    bool Spooled = false,
    bool SeenProblem = false,
    bool SeenPrinting = false,
    bool SeenDeleting = false,
    PrintCopyOutcome? Outcome = null);

/// <summary>What became of one copy, as far as Windows can tell.</summary>
public enum PrintCopyOutcome
{
    /// <summary>Windows finished sending it to the printer. Not proof that paper came out.</summary>
    Sent,

    /// <summary>No Windows job holds any of its data: it cannot print.</summary>
    NotSubmitted,

    /// <summary>Some or all of its data may have reached the printer, but Windows did not finish it normally.</summary>
    MaybePrinted
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

    public void Record(Guid jobId, PrintJournalStage stage, WaslaOptions connection, PrintJournalSubmission? submission = null)
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
                DateTime.UtcNow)
            {
                Submission = submission
            });
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
