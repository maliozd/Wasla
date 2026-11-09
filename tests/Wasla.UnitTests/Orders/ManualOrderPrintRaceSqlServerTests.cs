using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Realtime;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-119: a first print never becomes another physical copy without the operator's confirmation, on a real SQL Server.
/// The first request is held after its own read of the order's receipts, at the template read that every receipt creation
/// makes before its transaction. That read is a service the request is given, so no production code waits for a test.
/// Meanwhile a second request, on its own connection pool, queues a receipt and takes it to the state the test names.
/// </summary>
public sealed class ManualOrderPrintRaceSqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    private const string TenantName = "Race Restaurant";
    private const int ConfiguredCopies = 2;
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(PrintJobStatus.Printed)]
    [InlineData(PrintJobStatus.Failed)]
    public async Task AFirstPrint_WhileAnotherReceiptFinishes_CreatesNoCopy_AndAsksForConfirmation(PrintJobStatus finished)
    {
        var orderId = await SeedOrderAsync();
        var held = new HeldTemplateRead();
        var first = Service("FirstPrint", held).QueueReceiptPrintAsync(Tenant, orderId, TenantName, Ct);
        await held.Reached.WaitAsync(Deadline, Ct);

        await ReceiptFromAnotherRequestAsync(orderId, finished);

        held.Resume();
        var result = await first.WaitAsync(Deadline, Ct);

        Assert.False(result.Success);
        Assert.Equal(ManualOrderPrintOutcome.ReprintConfirmationRequired, result.Outcome);
        Assert.Equal(ManualOrderPrintMessageKeys.ReprintConfirmationRequired, result.MessageKey);
        Assert.Equal([finished], await ReceiptStatusesAsync(orderId));
    }

    [Theory]
    [InlineData(PrintJobStatus.Pending)]
    [InlineData(PrintJobStatus.Printing)]
    public async Task AFirstPrint_WhileAnotherReceiptIsActive_CreatesNothing(PrintJobStatus active)
    {
        var orderId = await SeedOrderAsync();
        var held = new HeldTemplateRead();
        var first = Service("FirstPrint", held).QueueReceiptPrintAsync(Tenant, orderId, TenantName, Ct);
        await held.Reached.WaitAsync(Deadline, Ct);

        await ReceiptFromAnotherRequestAsync(orderId, active);

        held.Resume();
        var result = await first.WaitAsync(Deadline, Ct);

        Assert.Equal(ManualOrderPrintOutcome.AlreadyQueued, result.Outcome);
        Assert.Equal([active], await ReceiptStatusesAsync(orderId));
    }

    [Fact]
    public async Task AFirstPrint_WhileAnotherReceiptIsCancelled_StillPrints()
    {
        var orderId = await SeedOrderAsync();
        var held = new HeldTemplateRead();
        var first = Service("FirstPrint", held).QueueReceiptPrintAsync(Tenant, orderId, TenantName, Ct);
        await held.Reached.WaitAsync(Deadline, Ct);

        await ReceiptFromAnotherRequestAsync(orderId, PrintJobStatus.Cancelled);

        held.Resume();
        var result = await first.WaitAsync(Deadline, Ct);

        Assert.Equal(ManualOrderPrintOutcome.Queued, result.Outcome);
        Assert.Equal([PrintJobStatus.Pending, PrintJobStatus.Cancelled], await ReceiptStatusesAsync(orderId));
    }

    [Fact]
    public async Task ARealFirstPrint_CreatesOneReceipt_WithTheOrderAndTheConfiguredCopies()
    {
        var orderId = await SeedOrderAsync();

        var result = await Service("FirstPrint", new DefaultReceiptTemplates()).QueueReceiptPrintAsync(Tenant, orderId, TenantName, Ct);

        Assert.True(result.Success);
        Assert.Equal(ManualOrderPrintOutcome.Queued, result.Outcome);
        await using var db = database.Open();
        var job = await db.PrintJobs.AsNoTracking().SingleAsync(j => j.OrderId == orderId, Ct);
        Assert.Equal(PrintJobType.Receipt, job.Type);
        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.Equal(ConfiguredCopies, job.CopyCount);
        Assert.Contains("Lahmacun", job.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(TenantName, job.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfirmedPrint_WhileAnotherReceiptFinishes_CreatesTheConfirmedCopy()
    {
        var orderId = await SeedOrderAsync();
        var held = new HeldTemplateRead();
        var confirmed = Service("FirstPrint", held).QueueConfirmedReprintAsync(Tenant, orderId, TenantName, Ct);
        await held.Reached.WaitAsync(Deadline, Ct);

        await ReceiptFromAnotherRequestAsync(orderId, PrintJobStatus.Printed);

        held.Resume();
        var result = await confirmed.WaitAsync(Deadline, Ct);

        Assert.True(result.Success);
        Assert.Equal(ManualOrderPrintOutcome.Queued, result.Outcome);
        Assert.Equal([PrintJobStatus.Pending, PrintJobStatus.Printed], await ReceiptStatusesAsync(orderId));
    }

    [Theory]
    [InlineData(PrintJobStatus.Printed)]
    [InlineData(PrintJobStatus.Failed)]
    public async Task AConfirmedReprint_OfAFinishedReceipt_CreatesOneCopy(PrintJobStatus finished)
    {
        var orderId = await SeedOrderAsync();
        await ReceiptFromAnotherRequestAsync(orderId, finished);

        var result = await Service("FirstPrint", new DefaultReceiptTemplates()).QueueConfirmedReprintAsync(Tenant, orderId, TenantName, Ct);

        Assert.True(result.Success);
        Assert.Equal(ManualOrderPrintOutcome.Requeued, result.Outcome);
        Assert.Equal([PrintJobStatus.Pending, finished], await ReceiptStatusesAsync(orderId));
    }

    [Fact]
    public async Task AConfirmedReprint_WhileAReceiptIsActive_CreatesNothing()
    {
        var orderId = await SeedOrderAsync();
        await ReceiptFromAnotherRequestAsync(orderId, PrintJobStatus.Printing);

        var result = await Service("FirstPrint", new DefaultReceiptTemplates()).QueueConfirmedReprintAsync(Tenant, orderId, TenantName, Ct);

        Assert.Equal(ManualOrderPrintOutcome.AlreadyQueued, result.Outcome);
        Assert.Equal([PrintJobStatus.Printing], await ReceiptStatusesAsync(orderId));
    }

    [Fact]
    public async Task TheAutomaticPath_KeepsItsOwnRule_AndStopsOnlyForAnActiveReceipt()
    {
        var orderId = await SeedOrderAsync();
        await ReceiptFromAnotherRequestAsync(orderId, PrintJobStatus.Printed);
        var automatic = Receipts(database.Instance("Automatic"), new DefaultReceiptTemplates());

        Assert.True(await automatic.TryCreateReceiptJobAsync(Tenant, orderId, ConfiguredCopies, TenantName, Ct));
        Assert.False(await automatic.TryCreateReceiptJobAsync(Tenant, orderId, ConfiguredCopies, TenantName, Ct));

        Assert.Equal([PrintJobStatus.Pending, PrintJobStatus.Printed], await ReceiptStatusesAsync(orderId));
    }

    /// <summary>
    /// The first print has made its decision and is held right before its INSERT, still inside its transaction. A receipt
    /// written now by anyone else has to wait for that transaction, so it cannot slip between the decision and the insert.
    /// </summary>
    [Fact]
    public async Task WhileAFirstPrintIsDeciding_AnotherReceiptWaitsForItsTransaction()
    {
        var orderId = await SeedOrderAsync();
        var held = new HoldBeforeReceiptInsert();
        var service = Service("FirstPrint", new DefaultReceiptTemplates(), held);

        var first = service.QueueReceiptPrintAsync(Tenant, orderId, TenantName, Ct);
        await held.Reached.WaitAsync(Deadline, Ct);

        var other = WriteFinishedReceiptAsync(orderId);
        await WaitUntilBlockedAsync(other);

        held.Resume();
        var result = await first.WaitAsync(Deadline, Ct);
        await other.WaitAsync(Deadline, Ct);

        Assert.Equal(ManualOrderPrintOutcome.Queued, result.Outcome);
        Assert.Equal([PrintJobStatus.Pending, PrintJobStatus.Printed], await ReceiptStatusesAsync(orderId));
    }

    private ManualOrderPrintService Service(string instance, IReceiptTemplateSettingsService templates, IInterceptor? interceptor = null)
    {
        var tenants = interceptor is null
            ? database.Instance(instance)
            : new InterceptedFactory(database.ConnectionString(instance), interceptor);

        return new ManualOrderPrintService(
            tenants,
            Receipts(tenants, templates),
            new PrintJobHistoryService(tenants, templates, NullTenantChangeNotifier.Instance, NullLogger<PrintJobHistoryService>.Instance),
            NullLogger<ManualOrderPrintService>.Instance);
    }

    private static ReceiptPrintJobService Receipts(ITenantDbContextFactory tenants, IReceiptTemplateSettingsService templates) =>
        new(tenants, templates, NullTenantChangeNotifier.Instance, NullLogger<ReceiptPrintJobService>.Instance);

    /// <summary>Queues a receipt the normal way on another connection pool, then moves it on as the Print Bridge would.</summary>
    private async Task ReceiptFromAnotherRequestAsync(Guid orderId, PrintJobStatus status)
    {
        var queued = await Service("OtherRequest", new DefaultReceiptTemplates()).QueueReceiptPrintAsync(Tenant, orderId, TenantName, Ct);
        Assert.Equal(ManualOrderPrintOutcome.Queued, queued.Outcome);

        await using var db = database.Open("PrintBridge");
        await db.PrintJobs.Where(j => j.OrderId == orderId).ExecuteUpdateAsync(set => set.SetProperty(j => j.Status, status), Ct);
    }

    private async Task WriteFinishedReceiptAsync(Guid orderId)
    {
        await using var db = database.Open("OtherWriter");
        db.PrintJobs.Add(new PrintJob
        {
            OrderId = orderId,
            Type = PrintJobType.Receipt,
            Status = PrintJobStatus.Printed,
            CopyCount = 1,
            PayloadJson = "{}",
            AttemptCount = 1,
            PrintedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task WaitUntilBlockedAsync(Task writer)
    {
        await using var inspector = database.Open("Inspector");
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            Assert.False(writer.IsCompleted, "The other receipt was written while the first print was still deciding.");
            var blocked = await inspector.Database
                .SqlQuery<int>($"""
                    SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests r
                    JOIN sys.dm_exec_sessions waiter ON waiter.session_id = r.session_id
                    JOIN sys.dm_exec_sessions holder ON holder.session_id = r.blocking_session_id
                    WHERE waiter.program_name = 'Wasla.ConcurrencyTests.OtherWriter'
                      AND holder.program_name = 'Wasla.ConcurrencyTests.FirstPrint'
                    """)
                .SingleAsync(Ct);
            if (blocked > 0)
                return;
            await Task.Delay(25, Ct);
        }

        Assert.Fail("The other receipt was never blocked by the first print's transaction.");
    }

    private async Task<List<PrintJobStatus>> ReceiptStatusesAsync(Guid orderId)
    {
        await using var db = database.Open();
        return await db.PrintJobs
            .AsNoTracking()
            .Where(j => j.OrderId == orderId && j.Type == PrintJobType.Receipt)
            .OrderBy(j => j.Status)
            .Select(j => j.Status)
            .ToListAsync(Ct);
    }

    private async Task<Guid> SeedOrderAsync()
    {
        await using var db = database.Open();
        if (!await db.TenantOperationalSettings.AnyAsync(s => s.Id == SettingsId, Ct))
        {
            db.TenantOperationalSettings.Add(new TenantOperationalSettings
            {
                Id = SettingsId,
                OrderSyncEnabled = true,
                AutoApproveNewOrders = false,
                AutoPrintReceiptOnAutoApprove = false,
                ReceiptPrintCopyCount = ConfiguredCopies,
                OperationalMode = TenantOperationalMode.Live,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var orderId = Guid.NewGuid();
        var externalId = $"PRINT-{orderId:N}";
        db.Orders.Add(new Order
        {
            Id = orderId,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = externalId,
            ExternalOrderCode = $"TY-{externalId[..14]}",
            IdempotencyKey = $"TrendyolYemek:{externalId}",
            InternalStatus = OrderStatus.Preparing,
            PlatformStatus = "Picking",
            CustomerName = "Print Customer",
            CustomerPhone = "+905555555555",
            CustomerAddress = "Test Address",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = DateTime.UtcNow.AddMinutes(-5),
            ReceivedAt = DateTime.UtcNow.AddMinutes(-4),
            AcceptedAt = DateTime.UtcNow.AddMinutes(-3),
            RawPayloadJson = "{}",
            ChangeVersion = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items =
            [
                new OrderItem
                {
                    Id = Guid.NewGuid(),
                    ProductName = "Lahmacun",
                    Quantity = 1,
                    UnitPrice = 100m,
                    TotalPrice = 100m,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            ]
        });
        await db.SaveChangesAsync(Ct);
        return orderId;
    }

    /// <summary>Holds the template read of one receipt creation: after the request's own read, before its transaction.</summary>
    private sealed class HeldTemplateRead : IReceiptTemplateSettingsService
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Resume() => _resume.TrySetResult();

        public async Task<ReceiptTemplateSettings> GetAsync(Guid tenantId, string? tenantDisplayName, string? defaultReceiptLanguage, CancellationToken ct)
        {
            _reached.TrySetResult();
            await _resume.Task.WaitAsync(Deadline, ct).ConfigureAwait(false);
            return ReceiptTemplateSettings.CreateDefaults(tenantDisplayName, defaultReceiptLanguage);
        }

        public Task<ReceiptTemplateSettings> UpdateAsync(Guid tenantId, string? tenantDisplayName, UpdateReceiptTemplateSettingsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>Holds one request right before its receipt INSERT, inside its transaction.</summary>
    private sealed class HoldBeforeReceiptInsert : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Resume() => _resume.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [PrintJobs]", StringComparison.Ordinal))
            {
                _reached.TrySetResult();
                await _resume.Task.WaitAsync(Deadline, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    private sealed class InterceptedFactory(string connectionString, IInterceptor interceptor) : ITenantDbContextFactory
    {
        public Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(connectionString).AddInterceptors(interceptor).Options));
    }
}
