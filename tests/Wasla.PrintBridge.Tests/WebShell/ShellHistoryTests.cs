using System.Text.RegularExpressions;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

[Collection(ProcessCultureCollection.Name)]
public sealed partial class ShellHistoryTests : IDisposable
{
    private readonly CultureScope _cultureScope = new();
    private readonly ShellTestRig _rig;
    private readonly PrintBridgeLocalizer _localizer;

    public ShellHistoryTests()
    {
        _rig = new ShellTestRig(Culture());
        _localizer = new PrintBridgeLocalizer(_rig.Culture);
    }

    public void Dispose()
    {
        _rig.Dispose();
        _cultureScope.Dispose();
    }

    [Theory]
    [InlineData(0, 0, 20, true, "1–20 / 45")]
    [InlineData(1, 1, 20, true, "21–40 / 45")]
    [InlineData(2, 2, 5, false, "41–45 / 45")]
    [InlineData(99, 2, 5, false, "41–45 / 45")]
    [InlineData(-3, 0, 20, true, "1–20 / 45")]
    public void Pages_AreBoundedAndClampedToTheAvailableRange(int requested, int page, int count, bool hasNext, string label)
    {
        _rig.Engine.History = Jobs(45);

        var result = _rig.History.Query(PrintHistoryDateFilter.Today, requested, null);

        Assert.Equal(page, result.Page);
        Assert.Equal(count, result.Items.Count);
        Assert.Equal(ShellHistory.PageSize, result.PageSize);
        Assert.Equal(45, result.Total);
        Assert.Equal(page > 0, result.HasPrevious);
        Assert.Equal(hasNext, result.HasNext);
        Assert.Equal(label, result.PageLabel);
    }

    [Fact]
    public void EmptyHistory_IsOneEmptyPage()
    {
        var result = _rig.History.Query(PrintHistoryDateFilter.Last30Days, 3, null);

        Assert.Equal(new ShellHistoryPage("last30Days", 0, ShellHistory.PageSize, 0, false, false, false, "0–0 / 0", []), result with { Items = [] });
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(PrintHistoryDateFilter.Today, "today")]
    [InlineData(PrintHistoryDateFilter.Last7Days, "last7Days")]
    [InlineData(PrintHistoryDateFilter.Last30Days, "last30Days")]
    public void RangeAndSearch_AreForwardedToTheEngine(PrintHistoryDateFilter range, string name)
    {
        var result = _rig.History.Query(range, 0, "  GTR-10  ");

        Assert.Equal(name, result.Range);
        Assert.True(result.Filtered);
        Assert.Equal((range, "GTR-10"), _rig.Engine.LastHistoryQuery);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSearch_IsNoFilter(string? search)
    {
        var result = _rig.History.Query(PrintHistoryDateFilter.Today, 0, search);

        Assert.False(result.Filtered);
        Assert.Equal((PrintHistoryDateFilter.Today, (string?)null), _rig.Engine.LastHistoryQuery);
    }

    [Fact]
    public void Rows_UseOpaqueHandles_NeverTheServerJobId()
    {
        var jobs = Jobs(3);
        _rig.Engine.History = jobs;

        var first = _rig.History.Query(PrintHistoryDateFilter.Today, 0, null);
        var again = _rig.History.Query(PrintHistoryDateFilter.Today, 0, null);
        var json = ShellMessageSerializer.SerializeHistoryResult(new ShellHistoryResult(NewRequestId(), first), 1);

        Assert.All(first.Items, item => Assert.Matches(HandleRegex(), item.Ref));
        Assert.Equal(3, first.Items.Select(i => i.Ref).Distinct().Count());
        Assert.Equal(first.Items.Select(i => i.Ref), again.Items.Select(i => i.Ref));
        foreach (var job in jobs)
        {
            Assert.DoesNotContain(job.JobId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(job.JobId.ToString("N"), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(job.OrderId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(jobs.Select(j => (Guid?)j.JobId), first.Items.Select(i => _rig.History.Resolve(i.Ref)));
    }

    [Theory]
    [InlineData("h0000000000000000")]
    [InlineData("")]
    [InlineData("../../job")]
    [InlineData("4b6f0c8e-1f8a-4f5e-9d9e-0f3a9c000001")]
    public void UnknownHandles_ResolveToNothing(string itemRef)
    {
        _rig.Engine.History = Jobs(2);
        _rig.History.Query(PrintHistoryDateFilter.Today, 0, null);

        Assert.Null(_rig.History.Resolve(itemRef));
    }

    [Theory]
    [InlineData(@"Printer not found: \\print-server\Kitchen (token=" + SentinelToken + ")", "Shell.History.Error.Printer")]
    [InlineData("Unable to open printer 'POS-58'. Win32 error 1801", "Shell.History.Error.Printer")]
    [InlineData("Unable to write to printer 'POS-58'.", "Shell.History.Error.Printer")]
    [InlineData("Claim failed: 409 at " + SentinelServerUrl + "/api/print-bridge/jobs", "Shell.History.Error.Server")]
    [InlineData("mark-printed: HttpRequestException secret-host.internal", "Shell.History.Error.Server")]
    [InlineData("System.NullReferenceException: Object reference not set", "Shell.History.Error.Generic")]
    [InlineData(null, "Shell.History.Error.Generic")]
    public void FailedRows_ShowACategory_NeverTheRawError(string? error, string expectedKey)
    {
        _rig.Engine.History = [Job(LocalPrintJobStatus.Failed, error: error)];

        var page = _rig.History.Query(PrintHistoryDateFilter.Today, 0, null);
        var json = ShellMessageSerializer.SerializeHistoryResult(new ShellHistoryResult(NewRequestId(), page), 1);

        var item = Assert.Single(page.Items);
        Assert.Equal("failed", item.Status);
        Assert.Equal(_localizer[expectedKey], item.Detail);
        Assert.False(item.CanReprint);
        foreach (var leak in new[] { "print-server", SentinelToken, SentinelServerUrl, "secret-host", "Exception", "Win32", "Claim failed", "mark-printed", "Unable to", "Object reference" })
            Assert.DoesNotContain(leak, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkippedDryRunAndPrintedRows_AreDescribedAndOnlyPrintedRowsCanBeReprinted()
    {
        _rig.Engine.History =
        [
            Job(LocalPrintJobStatus.Skipped),
            Job(LocalPrintJobStatus.Printed, note: "Dry run"),
            Job(LocalPrintJobStatus.Printed),
            Job(LocalPrintJobStatus.Printing),
            Job(LocalPrintJobStatus.Received)
        ];

        var items = _rig.History.Query(PrintHistoryDateFilter.Today, 0, null).Items;

        Assert.Equal(["skipped", "printed", "printed", "printing", "pending"], items.Select(i => i.Status));
        Assert.Equal(
            [_localizer["Shell.History.Skipped"], _localizer["Shell.History.DryRun"], null, null, null],
            items.Select(i => i.Detail));
        Assert.Equal([false, true, true, false, false], items.Select(i => i.CanReprint));
        Assert.All(items, i => Assert.Equal(_localizer.GetJobStatusBadge(StatusFor(i.Status)), i.StatusLabel));
    }

    [Fact]
    public void MissingValues_FallBackToTheShortJobIdAndTheLocalizedDash()
    {
        var job = new LocalPrintJobRecord { JobId = Guid.NewGuid(), Status = LocalPrintJobStatus.Printed, CreatedAtUtc = DateTime.UtcNow };
        _rig.Engine.History = [job];

        var item = Assert.Single(_rig.History.Query(PrintHistoryDateFilter.Today, 0, null).Items);

        Assert.Equal(job.ShortJobId, item.Order);
        Assert.Equal(_localizer["Common.Dash"], item.Printer);
        Assert.Equal(_localizer.GetPlatform(null), item.Platform);
    }

    [Fact]
    public void HistoryModel_CarriesNoReceiptContentIdsOrErrors()
    {
        var names = typeof(ShellHistoryItem).GetProperties().Select(p => p.Name)
            .Concat(typeof(ShellHistoryPage).GetProperties().Select(p => p.Name))
            .ToArray();

        foreach (var forbidden in new[] { "JobId", "OrderId", "Error", "Payload", "Receipt", "Token", "Url", "Path", "Machine" })
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    private static LocalPrintJobRecord[] Jobs(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new LocalPrintJobRecord
            {
                JobId = Guid.NewGuid(),
                OrderId = Guid.NewGuid(),
                OrderDisplay = $"GTR-{1000 + i}",
                Platform = "Getir",
                PrinterName = "POS-58",
                Status = LocalPrintJobStatus.Printed,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-i),
                PrintedAtUtc = DateTime.UtcNow.AddMinutes(-i)
            })
            .ToArray();

    private static LocalPrintJobRecord Job(LocalPrintJobStatus status, string? error = null, string? note = null) =>
        new()
        {
            JobId = Guid.NewGuid(),
            OrderDisplay = "GTR-1001",
            PrinterName = "POS-58",
            Status = status,
            CreatedAtUtc = DateTime.UtcNow,
            ErrorMessage = error,
            StatusNote = note
        };

    private static LocalPrintJobStatus StatusFor(string status) => status switch
    {
        "pending" => LocalPrintJobStatus.Received,
        "printing" => LocalPrintJobStatus.Printing,
        "printed" => LocalPrintJobStatus.Printed,
        "failed" => LocalPrintJobStatus.Failed,
        _ => LocalPrintJobStatus.Skipped
    };

    [GeneratedRegex("^h[0-9a-f]{16}$")]
    private static partial Regex HandleRegex();
}
