using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Printing;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Orders;

public sealed class OrderSyncPrintJobCreationTests : IDisposable
{
    private readonly MultiTenantSqliteFactory _dbFactory = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();

    [Fact]
    public async Task ProviderAcceptedOrder_CreatesOnePendingPrintJob()
    {
        await SeedTenantAsync(_tenantId, autoPrintOnAccepted: true);
        var client = new FakeFoodPlatformClient(FoodPlatform.TrendyolYemek, AcceptedOrder("provider-accepted-1"));
        var sync = CreateSyncService(client);

        await sync.SyncCustomerAsync(_tenantId, TestContext.Current.CancellationToken);

        await using var db = await _dbFactory.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var job = await db.PrintJobs.Include(j => j.Order).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.Equal(PrintJobType.Receipt, job.Type);
        Assert.Equal(OrderStatus.Accepted, job.Order!.InternalStatus);
    }

    [Fact]
    public async Task ProviderNewOrder_DoesNotCreatePrintJob()
    {
        await SeedTenantAsync(_tenantId, autoPrintOnAccepted: true);
        var client = new FakeFoodPlatformClient(FoodPlatform.TrendyolYemek, NewOrder("provider-new-1"));
        var sync = CreateSyncService(client);

        await sync.SyncCustomerAsync(_tenantId, TestContext.Current.CancellationToken);

        await using var db = await _dbFactory.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        Assert.Empty(await db.PrintJobs.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RepeatedAcceptedProviderEvent_CreatesOnlyOnePrintJob()
    {
        await SeedTenantAsync(_tenantId, autoPrintOnAccepted: true);
        var client = new FakeFoodPlatformClient(FoodPlatform.TrendyolYemek, AcceptedOrder("provider-repeat-1"));
        var sync = CreateSyncService(client);

        await sync.SyncCustomerAsync(_tenantId, TestContext.Current.CancellationToken);
        await sync.SyncCustomerAsync(_tenantId, TestContext.Current.CancellationToken);

        await using var db = await _dbFactory.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        Assert.Equal(1, await db.PrintJobs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReceiptTimingManual_DoesNotCreatePrintJob()
    {
        await SeedTenantAsync(_tenantId, autoPrintOnAccepted: false);
        var client = new FakeFoodPlatformClient(FoodPlatform.TrendyolYemek, AcceptedOrder("provider-manual-1"));
        var sync = CreateSyncService(client);

        await sync.SyncCustomerAsync(_tenantId, TestContext.Current.CancellationToken);

        await using var db = await _dbFactory.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        Assert.Empty(await db.PrintJobs.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrintJobIsStoredOnlyInCorrectTenantDatabase()
    {
        await SeedTenantAsync(_tenantId, autoPrintOnAccepted: true);
        await SeedTenantAsync(_otherTenantId, autoPrintOnAccepted: true);
        var client = new FakeFoodPlatformClient(FoodPlatform.TrendyolYemek, AcceptedOrder("provider-tenant-1"));
        var sync = CreateSyncService(client);

        await sync.SyncCustomerAsync(_tenantId, TestContext.Current.CancellationToken);

        await using var tenantDb = await _dbFactory.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        await using var otherDb = await _dbFactory.CreateAsync(_otherTenantId, TestContext.Current.CancellationToken);
        Assert.Equal(1, await tenantDb.PrintJobs.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await otherDb.PrintJobs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistingOrderUpdatedToAccepted_CreatesPendingPrintJob()
    {
        await SeedTenantAsync(_tenantId, autoPrintOnAccepted: true);
        var client = new FakeFoodPlatformClient(FoodPlatform.TrendyolYemek, NewOrder("provider-update-1"));
        var sync = CreateSyncService(client);
        await sync.SyncCustomerAsync(_tenantId, TestContext.Current.CancellationToken);

        client.ReplaceOrders(AcceptedOrder("provider-update-1"));
        await sync.SyncCustomerAsync(_tenantId, TestContext.Current.CancellationToken);

        await using var db = await _dbFactory.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var job = await db.PrintJobs.Include(j => j.Order).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.Equal(OrderStatus.Accepted, job.Order!.InternalStatus);
    }

    [Fact]
    public async Task PollingQueryCanSeeCreatedPendingPrintJob()
    {
        await SeedTenantAsync(_tenantId, autoPrintOnAccepted: true);
        var client = new FakeFoodPlatformClient(FoodPlatform.TrendyolYemek, AcceptedOrder("provider-poll-1"));
        var sync = CreateSyncService(client);
        await sync.SyncCustomerAsync(_tenantId, TestContext.Current.CancellationToken);
        var polling = new PrintBridgeJobService(_dbFactory, NullLogger<PrintBridgeJobService>.Instance);

        var jobs = await polling.GetPendingJobsAsync(_tenantId, max: 10, TestContext.Current.CancellationToken);

        var job = Assert.Single(jobs);
        Assert.Equal("Receipt", job.Type);
        Assert.Contains("provider-poll-1", job.PayloadJson, StringComparison.Ordinal);
    }

    private OrderSyncService CreateSyncService(FakeFoodPlatformClient client)
    {
        var receiptJobs = new ReceiptPrintJobService(
            _dbFactory,
            DefaultReceiptTemplateSettingsService.Instance,
            NullLogger<ReceiptPrintJobService>.Instance);

        var receiptCreation = new TestOrderReceiptCreationService(_dbFactory, receiptJobs);

        return new OrderSyncService(
            _dbFactory,
            new IFoodPlatformClient[] { client },
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            NoAutoApproveService.Instance,
            receiptCreation,
            NullLogger<OrderSyncService>.Instance);
    }

    private async Task SeedTenantAsync(Guid tenantId, bool autoPrintOnAccepted)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, TestContext.Current.CancellationToken);
        db.PlatformConnections.Add(new PlatformConnection
        {
            Id = Guid.NewGuid(),
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = $"store-{tenantId:N}",
            EncryptedApiKey = "encrypted",
            EncryptedApiSecret = "encrypted",
            IsActive = true,
            SyncIntervalSeconds = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.TenantOperationalSettings.Add(new TenantOperationalSettings
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            OrderSyncEnabled = true,
            AutoApproveNewOrders = false,
            AutoPrintReceiptOnAutoApprove = autoPrintOnAccepted,
            ReceiptPrintCopyCount = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static ExternalOrderDto AcceptedOrder(string externalOrderId) =>
        Order(externalOrderId, "Picking");

    private static ExternalOrderDto NewOrder(string externalOrderId) =>
        Order(externalOrderId, "Created");

    private static ExternalOrderDto Order(string externalOrderId, string externalStatus)
    {
        var item = new ExternalOrderItemDto(
            ExternalItemId: $"{externalOrderId}-item-1",
            ProductName: "Lahmacun",
            Quantity: 1,
            UnitPrice: 100m,
            TotalPrice: 100m,
            Notes: null,
            Options: Array.Empty<ExternalOrderItemOptionDto>());

        return new ExternalOrderDto(
            Platform: FoodPlatform.TrendyolYemek,
            ExternalOrderId: externalOrderId,
            ExternalOrderCode: $"TY-{externalOrderId}",
            OrderedAtUtc: DateTime.UtcNow.AddMinutes(-2),
            CustomerName: "Test Customer",
            CustomerPhone: "+905555555555",
            CustomerAddress: "Test Address",
            Subtotal: 100m,
            DeliveryFee: 0m,
            ServiceFee: 0m,
            Total: 100m,
            PaymentMethod: PaymentMethod.CreditCard,
            PaymentStatus: PaymentStatus.Paid,
            ExternalStatus: externalStatus,
            RawPayloadJson: $"{{\"externalOrderId\":\"{externalOrderId}\",\"externalStatus\":\"{externalStatus}\"}}",
            Items: new[] { item });
    }

    public void Dispose() => _dbFactory.Dispose();

    private sealed class FakeFoodPlatformClient : IFoodPlatformClient
    {
        private IReadOnlyCollection<ExternalOrderDto> _orders;

        public FakeFoodPlatformClient(FoodPlatform platform, params ExternalOrderDto[] orders)
        {
            Platform = platform;
            _orders = orders;
        }

        public FoodPlatform Platform { get; }

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(
            PlatformConnection connection,
            CancellationToken ct) =>
            Task.FromResult(_orders);

        public void ReplaceOrders(params ExternalOrderDto[] orders) => _orders = orders;

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) =>
            Task.CompletedTask;

        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoAutoApproveService : IOrderAutoApproveService
    {
        public static readonly NoAutoApproveService Instance = new();

        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class TestOrderReceiptCreationService : IOrderReceiptCreationService
    {
        private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        private readonly ITenantDbContextFactory _dbFactory;
        private readonly IReceiptPrintJobService _receiptJobs;

        public TestOrderReceiptCreationService(
            ITenantDbContextFactory dbFactory,
            IReceiptPrintJobService receiptJobs)
        {
            _dbFactory = dbFactory;
            _receiptJobs = receiptJobs;
        }

        public async Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct)
        {
            await using var db = await _dbFactory.CreateAsync(customerId, ct);
            var settings = await db.TenantOperationalSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == SettingsId, ct);

            if (!(settings?.AutoPrintReceiptOnAutoApprove ?? false))
                return;

            await _receiptJobs.TryCreateReceiptJobAsync(
                customerId,
                orderId,
                settings.ReceiptPrintCopyCount,
                tenantDisplayName: "Test Tenant",
                ct);
        }
    }

    private sealed class DefaultReceiptTemplateSettingsService : IReceiptTemplateSettingsService
    {
        public static readonly DefaultReceiptTemplateSettingsService Instance = new();

        public Task<ReceiptTemplateSettings> GetAsync(
            Guid customerId,
            string? customerDisplayName,
            string? defaultReceiptLanguage,
            CancellationToken ct) =>
            Task.FromResult(ReceiptTemplateSettings.CreateDefaults(customerDisplayName, defaultReceiptLanguage));

        public Task<ReceiptTemplateSettings> UpdateAsync(
            Guid customerId,
            string? customerDisplayName,
            UpdateReceiptTemplateSettingsCommand command,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

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

            modelBuilder.Entity<PlatformConnection>(builder =>
            {
                builder.ToTable("PlatformConnections");
                builder.HasKey(x => x.Id);
                builder.HasIndex(x => new { x.Platform, x.StoreId }).IsUnique();
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

            modelBuilder.Entity<SyncLog>(builder =>
            {
                builder.ToTable("SyncLogs");
                builder.HasKey(x => x.Id);
            });

            modelBuilder.Entity<IntegrationError>(builder =>
            {
                builder.ToTable("IntegrationErrors");
                builder.HasKey(x => x.Id);
            });
        }
    }
}
