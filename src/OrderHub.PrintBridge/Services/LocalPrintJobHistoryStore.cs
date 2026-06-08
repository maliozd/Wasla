using System.Text.Json;
using System.Text.Json.Serialization;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Models;

namespace OrderHub.PrintBridge.Services;

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

    private readonly object _sync = new();
    private List<LocalPrintJobRecord> _entries;

    public LocalPrintJobHistoryStore()
    {
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
                .Where(e =>
                    string.IsNullOrWhiteSpace(search)
                    || (!string.IsNullOrWhiteSpace(e.OrderDisplay)
                        && e.OrderDisplay.Contains(search, StringComparison.OrdinalIgnoreCase))
                    || e.ShortJobId.Contains(search, StringComparison.OrdinalIgnoreCase))
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
        try
        {
            if (!File.Exists(PrintBridgePaths.ProgramDataHistoryPath))
                return [];

            var json = File.ReadAllText(PrintBridgePaths.ProgramDataHistoryPath);
            var document = JsonSerializer.Deserialize<HistoryDocument>(json, JsonOptions);
            return document?.Entries?.Select(CloneRecord).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void Save()
    {
        try
        {
            PrintBridgePaths.EnsureProgramDataDirectories();
            var document = new HistoryDocument { Entries = _entries.Select(CloneRecord).ToList() };
            var json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(PrintBridgePaths.ProgramDataHistoryPath, json);
        }
        catch
        {
        }
    }

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
