using System.Globalization;
using System.Security.Cryptography;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.WebShell;

/// <summary>One bounded page of local print history, already localized and formatted for the page.</summary>
public sealed record ShellHistoryPage(
    string Range,
    int Page,
    int PageSize,
    int Total,
    bool HasPrevious,
    bool HasNext,
    bool Filtered,
    string PageLabel,
    IReadOnlyList<ShellHistoryItem> Items);

/// <param name="Ref">Opaque per-session handle; the page never sees the server job id.</param>
/// <param name="Status"><c>pending</c>, <c>printing</c>, <c>printed</c>, <c>failed</c> or <c>skipped</c>.</param>
/// <param name="Detail">Localized summary for failed, skipped or dry-run jobs; never raw exception text.</param>
public sealed record ShellHistoryItem(
    string Ref,
    string Time,
    string Order,
    string Platform,
    string Printer,
    string Status,
    string StatusLabel,
    string? Detail,
    bool CanReprint);

/// <summary>
/// Projects the engine's local print history into safe page rows. Receipt content, raw error text, payloads,
/// server job ids and tokens are never included. Reprint requests refer to rows through opaque handles that
/// only this host can resolve.
/// </summary>
public sealed class ShellHistory
{
    public const int PageSize = 20;
    private const int MaxHandles = 2000;

    private static readonly string[] PrinterErrorPrefixes =
    [
        "Printer not found",
        "Unable to open printer",
        "Unable to start print",
        "Unable to write to printer",
        "PrinterName is required"
    ];

    private static readonly string[] ServerErrorPrefixes = ["Claim failed:", "mark-printed:"];

    private readonly IPrintBridgeEngine _engine;
    private readonly PrintBridgeLocalizer _localizer;
    private readonly PrintBridgeCultureService _culture;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, string> _refByJob = [];
    private readonly Dictionary<string, Guid> _jobByRef = new(StringComparer.Ordinal);

    public ShellHistory(IPrintBridgeEngine engine, PrintBridgeLocalizer localizer, PrintBridgeCultureService culture)
    {
        _engine = engine;
        _localizer = localizer;
        _culture = culture;
    }

    public ShellHistoryPage Query(PrintHistoryDateFilter range, int page, string? search)
    {
        var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var jobs = _engine.GetPrintHistory(range, trimmedSearch);
        var total = jobs.Count;
        var lastPage = total == 0 ? 0 : (total - 1) / PageSize;
        var current = Math.Clamp(page, 0, lastPage);
        var pageJobs = jobs.Skip(current * PageSize).Take(PageSize).ToArray();
        var from = total == 0 ? 0 : current * PageSize + 1;
        var to = current * PageSize + pageJobs.Length;

        return new ShellHistoryPage(
            Range: ShellMessageContract.HistoryRanges.First(r => r.Value == range).Key,
            Page: current,
            PageSize: PageSize,
            Total: total,
            HasPrevious: current > 0,
            HasNext: current < lastPage,
            Filtered: trimmedSearch is not null,
            PageLabel: string.Create(CultureInfo.InvariantCulture, $"{from}–{to} / {total}"),
            Items: pageJobs.Select(Project).ToArray());
    }

    /// <summary>Server job id for a handle issued by <see cref="Query"/>, or null for anything else.</summary>
    public Guid? Resolve(string itemRef)
    {
        lock (_sync)
            return _jobByRef.TryGetValue(itemRef, out var jobId) ? jobId : null;
    }

    private ShellHistoryItem Project(LocalPrintJobRecord job)
    {
        var dash = _localizer["Common.Dash"];
        return new ShellHistoryItem(
            Ref: HandleFor(job.JobId),
            Time: PrintBridgeDateTimeFormatter.FormatDashboardUtc(_culture.CurrentCulture, job.DisplayTimeUtc),
            Order: string.IsNullOrWhiteSpace(job.OrderDisplay) ? job.ShortJobId : job.OrderDisplay.Trim(),
            Platform: _localizer.GetPlatform(job.Platform),
            Printer: string.IsNullOrWhiteSpace(job.PrinterName) ? dash : job.PrinterName.Trim(),
            Status: ToStatus(job.Status),
            StatusLabel: _localizer.GetJobStatusBadge(job.Status),
            Detail: DescribeDetail(job),
            CanReprint: job.Status == LocalPrintJobStatus.Printed);
    }

    private string? DescribeDetail(LocalPrintJobRecord job)
    {
        switch (job.Status)
        {
            case LocalPrintJobStatus.Failed:
            {
                var error = job.ErrorMessage ?? string.Empty;
                if (PrinterErrorPrefixes.Any(p => error.StartsWith(p, StringComparison.Ordinal)))
                    return _localizer["Shell.History.Error.Printer"];
                if (ServerErrorPrefixes.Any(p => error.StartsWith(p, StringComparison.Ordinal)))
                    return _localizer["Shell.History.Error.Server"];
                return _localizer["Shell.History.Error.Generic"];
            }
            case LocalPrintJobStatus.Skipped:
                return _localizer["Shell.History.Skipped"];
            case LocalPrintJobStatus.Printed when string.Equals(job.StatusNote, "Dry run", StringComparison.Ordinal):
                return _localizer["Shell.History.DryRun"];
            default:
                return null;
        }
    }

    private string HandleFor(Guid jobId)
    {
        lock (_sync)
        {
            if (_refByJob.TryGetValue(jobId, out var existing))
                return existing;

            if (_refByJob.Count >= MaxHandles)
            {
                _refByJob.Clear();
                _jobByRef.Clear();
            }

            string handle;
            do
            {
                handle = "h" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            }
            while (_jobByRef.ContainsKey(handle));

            _refByJob[jobId] = handle;
            _jobByRef[handle] = jobId;
            return handle;
        }
    }

    internal static string ToStatus(LocalPrintJobStatus status) => status switch
    {
        LocalPrintJobStatus.Received => "pending",
        LocalPrintJobStatus.Printing => "printing",
        LocalPrintJobStatus.Printed => "printed",
        LocalPrintJobStatus.Failed => "failed",
        _ => "skipped"
    };
}
