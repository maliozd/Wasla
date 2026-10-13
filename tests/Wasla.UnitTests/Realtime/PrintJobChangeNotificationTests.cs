using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-23: every receipt job creation and every Print Bridge status transition is announced after it is committed,
/// with the order-scoped <c>PrintJob.StatusVersion</c> as its version; nothing is announced for work that did not
/// happen; and a failing notifier never changes the result of the work. Real print services over one file-backed
/// SQLite database per tenant (SQLite serializes writers; this is not a SQL Server locking test).
/// </summary>
public sealed class PrintJobChangeNotificationTests : IDisposable
{
    private readonly RecordingTenantDbFactory _tenants = new();
    private readonly RecordingTenantChangeNotifier _notifier = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly PrintServices _print;

    public PrintJobChangeNotificationTests()
    {
        _tenants.CreateTenantDatabase(_tenant);
        _tenants.CreateTenantDatabase(_otherTenant);
        _print = new PrintServices(_tenants, _notifier);
    }

    public void Dispose() => _tenants.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatingAReceiptJob_AnnouncesPending_AfterTheJobIsCommitted()
    {
        var orderId = PrintServices.SeedOrder(_tenants, _tenant);
        var committedAtNotification = false;
        _notifier.OnNotify = async (tenantId, change) =>
        {
            // Read on a separate connection: only a committed row is visible here.
            var jobs = await _print.ReadJobsAsync(tenantId, ((PrintJobChanged)change).OrderId);
            committedAtNotification = jobs.Count == 1 && jobs[0].Status == PrintJobStatus.Pending;
        };

        Assert.True(await _print.Receipts.TryCreateReceiptJobAsync(_tenant, orderId, 1, "Test", Ct));

        var (tenantId, change) = Assert.Single(_notifier.Sent);
        var job = Assert.Single(await _print.ReadJobsAsync(_tenant, orderId));
        Assert.Equal(_tenant, tenantId);
        Assert.Equal(new PrintJobChanged(orderId, job.Id, PrintJobStatus.Pending, 1), change);
        Assert.Equal(1, job.StatusVersion);
        Assert.True(committedAtNotification);
    }

    [Fact]
    public async Task ACreationThatDoesNotHappen_AnnouncesNothing()
    {
        var orderId = PrintServices.SeedOrder(_tenants, _tenant);
        Assert.True(await _print.Receipts.TryCreateReceiptJobAsync(_tenant, orderId, 1, "Test", Ct));
        var before = _notifier.Sent.Count;

        // An active job already exists; an unknown order; a reprint of an active job; a manual print while queued.
        Assert.False(await _print.Receipts.TryCreateReceiptJobAsync(_tenant, orderId, 1, "Test", Ct));
        Assert.False(await _print.Receipts.TryCreateReceiptJobAsync(_tenant, Guid.NewGuid(), 1, "Test", Ct));
        var active = Assert.Single(await _print.ReadJobsAsync(_tenant, orderId));
        Assert.False((await _print.History.CreateReprintAsync(_tenant, active.Id, "Test", Ct)).Success);
        Assert.Equal(ManualOrderPrintOutcome.AlreadyQueued, (await _print.Manual.QueueReceiptPrintAsync(_tenant, orderId, "Test", Ct)).Outcome);

        Assert.Equal(before, _notifier.Sent.Count);
    }

    [Fact]
    public async Task BridgeReports_AnnouncePrintingThenPrinted_WithGrowingVersions()
    {
        var orderId = PrintServices.SeedOrder(_tenants, _tenant);
        await _print.Receipts.TryCreateReceiptJobAsync(_tenant, orderId, 1, "Test", Ct);
        var jobId = Assert.Single(await _print.ReadJobsAsync(_tenant, orderId)).Id;

        Assert.Equal(PrintJobClaimResult.Claimed, (await _print.Bridge.TryMarkPrintingAsync(_tenant, jobId, "Kitchen", Guid.NewGuid(), Ct)).Result);
        Assert.Equal(PrintJobClaimResult.Claimed, (await _print.Bridge.TryMarkPrintedAsync(_tenant, jobId, Ct)).Result);

        Assert.Equal(
            [
                new PrintJobChanged(orderId, jobId, PrintJobStatus.Pending, 1),
                new PrintJobChanged(orderId, jobId, PrintJobStatus.Printing, 2),
                new PrintJobChanged(orderId, jobId, PrintJobStatus.Printed, 3)
            ],
            _notifier.PrintJobChanges);
        Assert.All(_notifier.Sent, s => Assert.Equal(_tenant, s.TenantId));
        Assert.Equal(3, (await _print.ReadJobAsync(_tenant, jobId)).StatusVersion);
    }

    [Fact]
    public async Task AFailedPrint_IsAnnounced_AndAReprintStartsAboveIt()
    {
        var orderId = PrintServices.SeedOrder(_tenants, _tenant);
        await _print.Receipts.TryCreateReceiptJobAsync(_tenant, orderId, 1, "Test", Ct);
        var first = Assert.Single(await _print.ReadJobsAsync(_tenant, orderId)).Id;
        await _print.Bridge.TryMarkPrintingAsync(_tenant, first, "Kitchen", null, Ct);
        await _print.Bridge.TryMarkFailedAsync(_tenant, first, "Printer offline", Ct);

        // The operator's manual print of a failed receipt goes through the reprint path.
        var requeued = await _print.Manual.QueueReceiptPrintAsync(_tenant, orderId, "Test", Ct);
        Assert.Equal(ManualOrderPrintOutcome.Requeued, requeued.Outcome);
        var second = requeued.PrintJobId!.Value;
        await _print.Bridge.TryMarkPrintingAsync(_tenant, second, "Kitchen", null, Ct);
        await _print.Bridge.TryMarkPrintedAsync(_tenant, second, Ct);

        Assert.Equal(
            [
                new PrintJobChanged(orderId, first, PrintJobStatus.Pending, 1),
                new PrintJobChanged(orderId, first, PrintJobStatus.Printing, 2),
                new PrintJobChanged(orderId, first, PrintJobStatus.Failed, 3),
                new PrintJobChanged(orderId, second, PrintJobStatus.Pending, 4),
                new PrintJobChanged(orderId, second, PrintJobStatus.Printing, 5),
                new PrintJobChanged(orderId, second, PrintJobStatus.Printed, 6)
            ],
            _notifier.PrintJobChanges);

        // The read model reports the newest job and the same version the last notification carried.
        var state = await _print.Manual.GetReceiptPrintStateAsync(_tenant, orderId, Ct);
        Assert.Equal(new OrderReceiptPrintState(PrintJobStatus.Printed, false, true, 6), state);
    }

    [Fact]
    public async Task RepeatedOrLateReports_ChangeNothing_AndAnnounceNothing()
    {
        var orderId = PrintServices.SeedOrder(_tenants, _tenant);
        await _print.Receipts.TryCreateReceiptJobAsync(_tenant, orderId, 1, "Test", Ct);
        var jobId = Assert.Single(await _print.ReadJobsAsync(_tenant, orderId)).Id;
        await _print.Bridge.TryMarkPrintingAsync(_tenant, jobId, "Kitchen", null, Ct);
        await _print.Bridge.TryMarkPrintedAsync(_tenant, jobId, Ct);
        var before = _notifier.Sent.Count;

        // The Bridge retries mark-printed; a stale claim and a late failure arrive; an unknown job is reported.
        Assert.Equal(PrintJobClaimResult.Skipped, (await _print.Bridge.TryMarkPrintedAsync(_tenant, jobId, Ct)).Result);
        Assert.Equal(PrintJobClaimResult.Skipped, (await _print.Bridge.TryMarkPrintingAsync(_tenant, jobId, "Other", null, Ct)).Result);
        Assert.Equal(PrintJobClaimResult.Skipped, (await _print.Bridge.TryMarkFailedAsync(_tenant, jobId, "late", Ct)).Result);
        Assert.Equal(PrintJobClaimResult.NotFound, (await _print.Bridge.TryMarkPrintedAsync(_tenant, Guid.NewGuid(), Ct)).Result);

        Assert.Equal(before, _notifier.Sent.Count);
        var job = await _print.ReadJobAsync(_tenant, jobId);
        Assert.Equal(PrintJobStatus.Printed, job.Status);
        Assert.Equal(3, job.StatusVersion);
    }

    [Fact]
    public async Task ConcurrentFinalReports_OneWins_AndExactlyOneIsAnnounced()
    {
        var orderId = PrintServices.SeedOrder(_tenants, _tenant);
        await _print.Receipts.TryCreateReceiptJobAsync(_tenant, orderId, 1, "Test", Ct);
        var jobId = Assert.Single(await _print.ReadJobsAsync(_tenant, orderId)).Id;
        await _print.Bridge.TryMarkPrintingAsync(_tenant, jobId, "Kitchen", null, Ct);
        var before = _notifier.Sent.Count;

        var reports = await Task.WhenAll(
            Task.Run(() => _print.Bridge.TryMarkPrintedAsync(_tenant, jobId, Ct), Ct),
            Task.Run(() => _print.Bridge.TryMarkFailedAsync(_tenant, jobId, "spooler", Ct), Ct),
            Task.Run(() => _print.Bridge.TryMarkPrintedAsync(_tenant, jobId, Ct), Ct));

        Assert.Equal(1, reports.Count(r => r.Result == PrintJobClaimResult.Claimed));
        var announced = Assert.Single(_notifier.PrintJobChanges.Skip(before));
        var job = await _print.ReadJobAsync(_tenant, jobId);
        Assert.Equal(job.Status, announced.Status);
        Assert.Equal(3, announced.Version);
        Assert.Equal(3, job.StatusVersion);
    }

    [Fact]
    public async Task AFailingNotifier_DoesNotFailTheWork()
    {
        _notifier.Throw = new InvalidOperationException("channel down");
        var orderId = PrintServices.SeedOrder(_tenants, _tenant);

        Assert.True(await _print.Receipts.TryCreateReceiptJobAsync(_tenant, orderId, 1, "Test", Ct));
        var jobId = Assert.Single(await _print.ReadJobsAsync(_tenant, orderId)).Id;
        Assert.Equal(PrintJobClaimResult.Claimed, (await _print.Bridge.TryMarkPrintingAsync(_tenant, jobId, "Kitchen", null, Ct)).Result);
        Assert.Equal(PrintJobClaimResult.Claimed, (await _print.Bridge.TryMarkPrintedAsync(_tenant, jobId, Ct)).Result);
        var reprint = await _print.Manual.QueueReceiptPrintAsync(_tenant, orderId, "Test", Ct);

        Assert.True(reprint.Success);
        Assert.Equal(4, _notifier.Sent.Count);
        var jobs = await _print.ReadJobsAsync(_tenant, orderId);
        Assert.Equal([PrintJobStatus.Printed, PrintJobStatus.Pending], jobs.Select(j => j.Status));
    }

    [Fact]
    public async Task Notifications_NameTheTenantWhoseDatabaseChanged()
    {
        var orderA = PrintServices.SeedOrder(_tenants, _tenant);
        var orderB = PrintServices.SeedOrder(_tenants, _otherTenant);

        await _print.Receipts.TryCreateReceiptJobAsync(_tenant, orderA, 1, "A", Ct);
        await _print.Receipts.TryCreateReceiptJobAsync(_otherTenant, orderB, 1, "B", Ct);

        Assert.Equal(
            [(_tenant, orderA), (_otherTenant, orderB)],
            _notifier.Sent.Select(s => (s.TenantId, ((PrintJobChanged)s.Change).OrderId)));
        // Versions are per order, so each tenant's first job starts at 1.
        Assert.All(_notifier.PrintJobChanges, c => Assert.Equal(1, c.Version));
    }
}
