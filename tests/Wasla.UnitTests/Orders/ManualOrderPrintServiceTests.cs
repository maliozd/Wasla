using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Printing;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Printing;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// Phase 2B5: manual receipt printing reuses the existing receipt-job and reprint pipeline.
/// </summary>
public sealed class ManualOrderPrintServiceTests : IDisposable
{
    private readonly MultiTenantSqliteFactory _dbFactory = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManualPrint_WithoutPreviousJob_QueuesOnePendingReceiptJob()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.True(result.Success);
        Assert.Equal(ManualOrderPrintOutcome.Queued, result.Outcome);
        Assert.Equal(ManualOrderPrintMessageKeys.Queued, result.MessageKey);

        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        var job = await db.PrintJobs.SingleAsync(Ct);
        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.Equal(PrintJobType.Receipt, job.Type);
        Assert.Equal(orderId, job.OrderId);
        Assert.Contains("ORDER-1", job.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualPrint_IsNotGatedByTheAutomaticPrintSetting()
    {
        // Automatic printing is off; an explicit operator action must still queue a receipt.
        var orderId = await SeedOrderAsync(_tenantId, autoPrintOnAccepted: false);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.Queued, result.Outcome);
        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        Assert.Equal(1, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task ManualPrint_UsesConfiguredReceiptCopyCount()
    {
        var orderId = await SeedOrderAsync(_tenantId, copyCount: 3);
        var service = CreateService();

        await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        var job = await db.PrintJobs.SingleAsync(Ct);
        Assert.Equal(3, job.CopyCount);
    }

    [Theory]
    [InlineData(PrintJobStatus.Pending)]
    [InlineData(PrintJobStatus.Printing)]
    public async Task ManualPrint_WithActiveJob_DoesNotCreateADuplicate(PrintJobStatus activeStatus)
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await AddPrintJobAsync(_tenantId, orderId, activeStatus);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.False(result.Success);
        Assert.Equal(ManualOrderPrintOutcome.AlreadyQueued, result.Outcome);
        Assert.Equal(ManualOrderPrintMessageKeys.AlreadyQueued, result.MessageKey);

        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        Assert.Equal(1, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task RepeatedRapidRequests_QueueOnlyOneJob()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        var service = CreateService();

        var first = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);
        var second = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);
        var third = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.Queued, first.Outcome);
        Assert.Equal(ManualOrderPrintOutcome.AlreadyQueued, second.Outcome);
        Assert.Equal(ManualOrderPrintOutcome.AlreadyQueued, third.Outcome);

        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        Assert.Equal(1, await db.PrintJobs.CountAsync(Ct));
    }

    [Theory]
    [InlineData(PrintJobStatus.Printed)]
    [InlineData(PrintJobStatus.Failed)]
    public async Task ManualPrint_AfterCompletedJob_GoesThroughTheReprintPath(PrintJobStatus completedStatus)
    {
        var orderId = await SeedOrderAsync(_tenantId);
        var sourceJobId = await AddPrintJobAsync(_tenantId, orderId, completedStatus);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.True(result.Success);
        Assert.Equal(ManualOrderPrintOutcome.Requeued, result.Outcome);
        Assert.Equal(ManualOrderPrintMessageKeys.ReprintQueued, result.MessageKey);

        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        var jobs = await db.PrintJobs.OrderBy(j => j.CreatedAt).ToListAsync(Ct);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(completedStatus, jobs[0].Status);
        Assert.Equal(sourceJobId, jobs[0].Id);
        Assert.Equal(PrintJobStatus.Pending, jobs[1].Status);
        Assert.Equal(result.PrintJobId, jobs[1].Id);
    }

    [Fact]
    public async Task ManualPrint_AfterCancelledJobOnly_CreatesAFreshReceiptJob()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Cancelled);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.Queued, result.Outcome);
        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        Assert.Equal(1, await db.PrintJobs.CountAsync(j => j.Status == PrintJobStatus.Pending, Ct));
    }

    [Fact]
    public async Task ManualPrint_OlderPrintedPlusNewerCancelled_ReprintsFromPrinted()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Printed, createdOffsetMinutes: -10, copyCount: 2);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Cancelled, createdOffsetMinutes: -1);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.Requeued, result.Outcome);
        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        var jobs = await db.PrintJobs.OrderBy(j => j.CreatedAt).ToListAsync(Ct);
        Assert.Equal(3, jobs.Count);
        Assert.Equal(PrintJobStatus.Printed, jobs[0].Status);
        Assert.Equal(PrintJobStatus.Cancelled, jobs[1].Status);
        Assert.Equal(PrintJobStatus.Pending, jobs[2].Status);
        Assert.Equal(2, jobs[2].CopyCount);
        Assert.DoesNotContain(jobs, j => j.Status == PrintJobStatus.Cancelled && j.Id == result.PrintJobId);
    }

    [Fact]
    public async Task ManualPrint_OlderFailedPlusNewerCancelled_ReprintsFromFailed()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Failed, createdOffsetMinutes: -8, copyCount: 3);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Cancelled);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.Requeued, result.Outcome);
        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        var pending = await db.PrintJobs.SingleAsync(j => j.Status == PrintJobStatus.Pending, Ct);
        Assert.Equal(3, pending.CopyCount);
        Assert.Equal(result.PrintJobId, pending.Id);
    }

    [Fact]
    public async Task ManualPrint_MultipleCompletedJobs_ReprintsFromTheLatestReusable()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Printed, createdOffsetMinutes: -20, copyCount: 1);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Failed, createdOffsetMinutes: -5, copyCount: 3);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Printed, createdOffsetMinutes: -1, copyCount: 2);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.Requeued, result.Outcome);
        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        var pending = await db.PrintJobs.SingleAsync(j => j.Status == PrintJobStatus.Pending, Ct);
        Assert.Equal(2, pending.CopyCount);
        Assert.Equal(4, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task ManualPrint_TiedCreatedAt_SelectsReusableJobByDescendingId()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        var tiedAt = DateTime.UtcNow.AddMinutes(-4);
        var firstId = await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Printed, createdAt: tiedAt, copyCount: 1);
        var secondId = await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Failed, createdAt: tiedAt, copyCount: 3);
        var expectedCopyCount = secondId.CompareTo(firstId) > 0 ? 3 : 1;
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.Requeued, result.Outcome);
        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        var pending = await db.PrintJobs.SingleAsync(j => j.Status == PrintJobStatus.Pending, Ct);
        Assert.Equal(expectedCopyCount, pending.CopyCount);
    }

    [Fact]
    public async Task ManualPrint_ActiveJobPlusOlderPrinted_DoesNotCreateASecondCopy()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Printed, createdOffsetMinutes: -10);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Pending);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.AlreadyQueued, result.Outcome);
        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        Assert.Equal(2, await db.PrintJobs.CountAsync(Ct));
        Assert.Equal(1, await db.PrintJobs.CountAsync(j => j.Status == PrintJobStatus.Pending, Ct));
    }

    [Fact]
    public async Task ManualPrint_ActiveJobPlusNewerCancelled_DoesNotCreateASecondCopy()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Printing, createdOffsetMinutes: -3);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Cancelled);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.AlreadyQueued, result.Outcome);
        await using var db = await _dbFactory.CreateAsync(_tenantId, Ct);
        Assert.Equal(2, await db.PrintJobs.CountAsync(Ct));
        Assert.Equal(0, await db.PrintJobs.CountAsync(j => j.Status == PrintJobStatus.Pending, Ct));
    }

    [Fact]
    public async Task ManualPrint_ForUnknownOrder_ReturnsOrderNotFound()
    {
        await SeedOrderAsync(_tenantId);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_tenantId, Guid.NewGuid(), "Test Tenant", Ct);

        Assert.False(result.Success);
        Assert.Equal(ManualOrderPrintOutcome.OrderNotFound, result.Outcome);
        Assert.Equal(ManualOrderPrintMessageKeys.OrderNotFound, result.MessageKey);
    }

    [Fact]
    public async Task ManualPrint_CannotReachAnotherTenantsOrder()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await SeedSettingsAsync(_otherTenantId);
        var service = CreateService();

        var result = await service.QueueReceiptPrintAsync(_otherTenantId, orderId, "Other Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.OrderNotFound, result.Outcome);

        await using var otherDb = await _dbFactory.CreateAsync(_otherTenantId, Ct);
        await using var ownerDb = await _dbFactory.CreateAsync(_tenantId, Ct);
        Assert.Equal(0, await otherDb.PrintJobs.CountAsync(Ct));
        Assert.Equal(0, await ownerDb.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task PrintState_ReportsNothingQueuedForAFreshOrder()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        var service = CreateService();

        var state = await service.GetReceiptPrintStateAsync(_tenantId, orderId, Ct);

        Assert.Null(state.LatestStatus);
        Assert.False(state.HasActiveJob);
        Assert.False(state.CanReprint);
    }

    [Fact]
    public async Task PrintState_ReportsActiveJobAndBlocksReprint()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Printed, createdOffsetMinutes: -5);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Pending);
        var service = CreateService();

        var state = await service.GetReceiptPrintStateAsync(_tenantId, orderId, Ct);

        Assert.Equal(PrintJobStatus.Pending, state.LatestStatus);
        Assert.True(state.HasActiveJob);
        Assert.False(state.CanReprint);
    }

    [Fact]
    public async Task PrintState_AllowsReprintAfterCompletion()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        await AddPrintJobAsync(_tenantId, orderId, PrintJobStatus.Failed);
        var service = CreateService();

        var state = await service.GetReceiptPrintStateAsync(_tenantId, orderId, Ct);

        Assert.Equal(PrintJobStatus.Failed, state.LatestStatus);
        Assert.False(state.HasActiveJob);
        Assert.True(state.CanReprint);
    }

    [Fact]
    public async Task ManuallyQueuedJob_IsVisibleToThePrintBridgePollingQuery()
    {
        var orderId = await SeedOrderAsync(_tenantId);
        var service = CreateService();
        await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);
        var polling = new PrintBridgeJobService(_dbFactory, NullLogger<PrintBridgeJobService>.Instance);

        var jobs = await polling.GetPendingJobsAsync(_tenantId, max: 10, Ct);

        var job = Assert.Single(jobs);
        Assert.Equal("Receipt", job.Type);
        Assert.Contains("ORDER-1", job.PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PrintJobStatus.Printed, "tr")]
    [InlineData(PrintJobStatus.Failed, "en")]
    [InlineData(PrintJobStatus.Printed, "ar")]
    [InlineData(PrintJobStatus.Failed, "ru")]
    public async Task Reprint_UsesCurrentTenantTemplateAndOrder_PreservingSourceCopies(
        PrintJobStatus completedStatus, string language)
    {
        var orderId = await SeedOrderAsync(_tenantId, copyCount: 2);
        var service = CreateService();
        var first = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);
        Assert.True(first.Success);
        string originalPayload;
        Guid sourceJobId;
        await using (var db = await _dbFactory.CreateAsync(_tenantId, Ct))
        {
            var source = await db.PrintJobs.SingleAsync(Ct);
            sourceJobId = source.Id;
            originalPayload = source.PayloadJson;
            source.Status = completedStatus;
            var settings = await db.TenantOperationalSettings.SingleAsync(Ct);
            settings.ReceiptPrintCopyCount = 3;
            settings.ReceiptTemplateSettingsJson = JsonSerializer.Serialize(new ReceiptTemplateSettings
            {
                ReceiptLanguage = language,
                ReceiptHeaderText = "Updated header",
                ReceiptFooterText = "Updated footer",
                ShowCustomerName = false,
                ShowCustomerPhone = false,
                ShowDeliveryAddress = false,
                ShowProductNotes = false,
                ShowPaymentMethod = true
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var order = await db.Orders.SingleAsync(Ct);
            order.ExternalOrderCode = "UPDATED-CODE";
            await db.SaveChangesAsync(Ct);
        }

        // A different tenant's settings must not influence the rebuilt receipt.
        await SeedOrderAsync(_otherTenantId);
        var result = await service.QueueReceiptPrintAsync(_tenantId, orderId, "Test Tenant", Ct);

        Assert.Equal(ManualOrderPrintOutcome.Requeued, result.Outcome);
        await using var verification = await _dbFactory.CreateAsync(_tenantId, Ct);
        var pending = await verification.PrintJobs.SingleAsync(j => j.Id == result.PrintJobId, Ct);
        Assert.Equal(2, pending.CopyCount);
        using var payload = JsonDocument.Parse(pending.PayloadJson);
        Assert.Equal("UPDATED-CODE", payload.RootElement.GetProperty("externalOrderCode").GetString());
        var template = payload.RootElement.GetProperty("template");
        Assert.Equal(language, template.GetProperty("language").GetString());
        Assert.Equal("Updated header", template.GetProperty("headerText").GetString());
        Assert.Equal("Updated footer", template.GetProperty("footerText").GetString());
        Assert.False(template.GetProperty("showCustomerName").GetBoolean());
        Assert.False(template.GetProperty("showCustomerPhone").GetBoolean());
        Assert.False(template.GetProperty("showDeliveryAddress").GetBoolean());
        Assert.False(template.GetProperty("showProductNotes").GetBoolean());
        Assert.True(template.GetProperty("showPaymentMethod").GetBoolean());
        var original = await verification.PrintJobs.SingleAsync(j => j.Id == sourceJobId, Ct);
        Assert.Equal(originalPayload, original.PayloadJson);
        Assert.Equal(completedStatus, original.Status);
    }

    private ManualOrderPrintService CreateService()
    {
        var templateSettings = new ReceiptTemplateSettingsService(
            _dbFactory,
            new UpdateReceiptTemplateSettingsCommandValidator(),
            NullLogger<ReceiptTemplateSettingsService>.Instance);
        var receiptJobs = new ReceiptPrintJobService(
            _dbFactory,
            templateSettings,
            NullLogger<ReceiptPrintJobService>.Instance);

        var history = new PrintJobHistoryService(
            _dbFactory,
            templateSettings,
            NullLogger<PrintJobHistoryService>.Instance);

        return new ManualOrderPrintService(
            _dbFactory,
            receiptJobs,
            history,
            NullLogger<ManualOrderPrintService>.Instance);
    }

    private async Task<Guid> SeedOrderAsync(
        Guid tenantId,
        bool autoPrintOnAccepted = true,
        int copyCount = 1)
    {
        await SeedSettingsAsync(tenantId, autoPrintOnAccepted, copyCount);

        var orderId = Guid.NewGuid();
        await using var db = await _dbFactory.CreateAsync(tenantId, Ct);
        db.Orders.Add(new Order
        {
            Id = orderId,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = "ORDER-1",
            ExternalOrderCode = "TY-ORDER-1",
            IdempotencyKey = $"TrendyolYemek:ORDER-1:{tenantId:N}",
            InternalStatus = OrderStatus.Accepted,
            PlatformStatus = "Picking",
            CustomerName = "Test Customer",
            CustomerPhone = "+905555555555",
            CustomerAddress = "Test Address",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = DateTime.UtcNow.AddMinutes(-5),
            ReceivedAt = DateTime.UtcNow.AddMinutes(-4),
            RawPayloadJson = "{}",
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

    private async Task SeedSettingsAsync(
        Guid tenantId,
        bool autoPrintOnAccepted = true,
        int copyCount = 1)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, Ct);
        if (await db.TenantOperationalSettings.AnyAsync(Ct))
            return;

        db.TenantOperationalSettings.Add(new TenantOperationalSettings
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            OrderSyncEnabled = true,
            AutoApproveNewOrders = false,
            AutoPrintReceiptOnAutoApprove = autoPrintOnAccepted,
            ReceiptPrintCopyCount = copyCount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> AddPrintJobAsync(
        Guid tenantId,
        Guid orderId,
        PrintJobStatus status,
        int createdOffsetMinutes = 0,
        int copyCount = 1,
        DateTime? createdAt = null)
    {
        var jobId = Guid.NewGuid();
        var stampedAt = createdAt ?? DateTime.UtcNow.AddMinutes(createdOffsetMinutes);
        await using var db = await _dbFactory.CreateAsync(tenantId, Ct);
        db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            OrderId = orderId,
            Type = PrintJobType.Receipt,
            Status = status,
            CopyCount = copyCount,
            PayloadJson = "{}",
            AttemptCount = status == PrintJobStatus.Failed ? 1 : 0,
            CreatedAt = stampedAt,
            UpdatedAt = stampedAt
        });
        await db.SaveChangesAsync(Ct);
        return jobId;
    }

    public void Dispose() => _dbFactory.Dispose();

    private sealed class MultiTenantSqliteFactory : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                _connections[customerId] = connection;
                using var setup = new TestTenantDbContext(CreateOptions(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new TestTenantDbContext(CreateOptions(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> CreateOptions(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlite(connection)
                .Options;
    }

    private sealed class TestTenantDbContext : TenantDbContext
    {
        public TestTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenantOperationalSettings>(builder =>
            {
                builder.ToTable("TenantOperationalSettings");
                builder.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Order>(builder =>
            {
                builder.ToTable("Orders");
                builder.HasKey(x => x.Id);
                builder.HasIndex(x => x.IdempotencyKey).IsUnique();
                builder.HasMany(x => x.Items).WithOne(x => x.Order).HasForeignKey(x => x.OrderId);
            });

            modelBuilder.Entity<OrderItem>(builder =>
            {
                builder.ToTable("OrderItems");
                builder.HasKey(x => x.Id);
                builder.HasMany(x => x.Options).WithOne(x => x.OrderItem).HasForeignKey(x => x.OrderItemId);
            });

            modelBuilder.Entity<OrderItemOption>(builder =>
            {
                builder.ToTable("OrderItemOptions");
                builder.HasKey(x => x.Id);
            });

            modelBuilder.Entity<PrintJob>(builder =>
            {
                builder.ToTable("PrintJobs");
                builder.HasKey(x => x.Id);
                builder.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId);
                builder.HasIndex(x => new { x.OrderId, x.Type });
            });
        }
    }
}
