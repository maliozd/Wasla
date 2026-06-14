using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.Services;

/// <summary>
/// Local JSON-backed print job history for support and reprint (MVP).
/// Retention policy: at most <see cref="MaxEntries"/> records; entries older than
/// <see cref="RetentionDays"/> calendar days (UTC comparison on <see cref="LocalPrintJobRecord.DisplayTimeUtc"/>)
/// are removed during <see cref="Prune"/>.
/// </summary>
public sealed class LocalPrintJobHistoryStore
{
    private const int MaxEntries = 1000;
    private const int RetentionDays = 90;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ILogger<LocalPrintJobHistoryStore> _logger;
    private readonly object _sync = new();
    private List<LocalPrintJobRecord> _entries;

    public LocalPrintJobHistoryStore(ILogger<LocalPrintJobHistoryStore> logger)
    {
        _logger = logger;
        _entries = Load();
    }

    public void Record(LocalPrintJobRecord record)
    {
        lock (_sync)
        {
            var clone = CloneRecord(record);
            var index = _entries.FindIndex(e => e.JobId == clone.JobId);
            if (index >= 0)
                _entries[index] = clone;
            else
                _entries.Insert(0, clone);

            Prune();
            Save();
        }
    }

    public IReadOnlyList<LocalPrintJobRecord> Query(PrintHistoryDateFilter filter, string? orderSearch)
    {
        lock (_sync)
        {
            var cutoff = GetCutoffUtc(filter);
            var search = orderSearch?.Trim();

            return _entries
                .Where(e => e.DisplayTimeUtc >= cutoff)
                .Where(e => MatchesSearch(e, search))
                .OrderByDescending(e => e.DisplayTimeUtc)
                .Select(CloneRecord)
                .ToList();
        }
    }

    public LocalPrintJobRecord? FindByJobId(Guid jobId)
    {
        lock (_sync)
        {
            var match = _entries.FirstOrDefault(e => e.JobId == jobId);
            return match is null ? null : CloneRecord(match);
        }
    }

    public DateTime? GetLastPrintTimeUtc()
    {
        lock (_sync)
        {
            var latest = _entries
                .Where(e => e.Status == LocalPrintJobStatus.Printed)
                .Select(e => e.PrintedAtUtc ?? e.DisplayTimeUtc)
                .OrderByDescending(d => d)
                .FirstOrDefault();

            return latest == default ? null : latest;
        }
    }

    public int CountForLocalDate(DateTime localDate, LocalPrintJobStatus? statusFilter = null)
    {
        lock (_sync)
        {
            return _entries.Count(e =>
                ToLocalDate(e.DisplayTimeUtc) == localDate
                && (statusFilter is null || e.Status == statusFilter));
        }
    }

    private static bool MatchesSearch(LocalPrintJobRecord entry, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        if (!string.IsNullOrWhiteSpace(entry.OrderDisplay)
            && entry.OrderDisplay.Contains(search, StringComparison.OrdinalIgnoreCase))
            return true;

        if (entry.ShortJobId.Contains(search, StringComparison.OrdinalIgnoreCase))
            return true;

        var orderIdText = entry.OrderId.ToString("D");
        if (orderIdText.Contains(search, StringComparison.OrdinalIgnoreCase))
            return true;

        var orderIdCompact = entry.OrderId.ToString("N");
        if (orderIdCompact.Contains(search, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static DateTime ToLocalDate(DateTime utc) => utc.ToLocalTime().Date;

    private static DateTime GetCutoffUtc(PrintHistoryDateFilter filter)
    {
        var localToday = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Local);
        return filter switch
        {
            PrintHistoryDateFilter.Today => localToday.ToUniversalTime(),
            PrintHistoryDateFilter.Last7Days => localToday.AddDays(-6).ToUniversalTime(),
            PrintHistoryDateFilter.Last30Days => localToday.AddDays(-29).ToUniversalTime(),
            _ => localToday.ToUniversalTime()
        };
    }

    private void Prune()
    {
        var retentionCutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        _entries = _entries
            .Where(e => e.DisplayTimeUtc >= retentionCutoff)
            .OrderByDescending(e => e.DisplayTimeUtc)
            .Take(MaxEntries)
            .ToList();
    }

    private List<LocalPrintJobRecord> Load()
    {
        var path = PrintBridgePaths.ProgramDataHistoryPath;

        try
        {
            if (!File.Exists(path))
            {
                _logger.LogDebug("Print history file not found. Starting with empty history. Path={Path}", path);
                return [];
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Print history file is empty. Starting with empty history. Path={Path}", path);
                return [];
            }

            var document = JsonSerializer.Deserialize<HistoryDocument>(json, JsonOptions);
            var entries = document?.Entries?
                .Where(IsValidEntry)
                .Select(CloneRecord)
                .ToList() ?? [];

            _logger.LogInformation("Loaded {Count} print history entries from {Path}", entries.Count, path);
            return entries;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Print history file is missing, unreadable, or corrupted. Backing up and starting with empty history. Path={Path}",
                path);
            BackupCorruptedFile(path);
            return [];
        }
    }

    private void Save()
    {
        var path = PrintBridgePaths.ProgramDataHistoryPath;

        try
        {
            PrintBridgePaths.EnsureProgramDataDirectories();
            var document = new HistoryDocument { Entries = _entries.Select(CloneRecord).ToList() };
            var json = JsonSerializer.Serialize(document, JsonOptions);
            WriteAtomically(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist print history. In-memory history remains available until next successful save. Path={Path}",
                path);
        }
    }

    private static void WriteAtomically(string path, string json)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private void BackupCorruptedFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var backupPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, backupPath, overwrite: false);
            _logger.LogWarning("Corrupted print history backed up to {BackupPath}", backupPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to back up corrupted print history file. Path={Path}", path);
        }
    }

    private static bool IsValidEntry(LocalPrintJobRecord entry) =>
        entry.JobId != Guid.Empty && entry.CreatedAtUtc != default;

    private static LocalPrintJobRecord CloneRecord(LocalPrintJobRecord source) =>
        new()
        {
            JobId = source.JobId,
            OrderId = source.OrderId,
            OrderDisplay = source.OrderDisplay,
            Platform = source.Platform,
            JobType = source.JobType,
            PrinterName = source.PrinterName,
            Status = source.Status,
            CreatedAtUtc = source.CreatedAtUtc,
            LastAttemptAtUtc = source.LastAttemptAtUtc,
            PrintedAtUtc = source.PrintedAtUtc,
            ErrorMessage = source.ErrorMessage,
            StatusNote = source.StatusNote
        };

    private sealed class HistoryDocument
    {
        public List<LocalPrintJobRecord> Entries { get; set; } = [];
    }
}
