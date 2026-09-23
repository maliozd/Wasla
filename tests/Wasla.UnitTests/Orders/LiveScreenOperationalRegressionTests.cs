using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Platform.Dtos;
using Wasla.Application.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// Behavioral protection for Live Screen order actions, acceptance receipts, and detail data.
/// Does not freeze the today-only query, the 100-row page, or full-page HTML replacement.
/// The custom SQLite model exercises service behavior; it does not validate production EF mappings,
/// SQL Server constraints, or concurrent requests.
/// </summary>
public sealed class LiveScreenOperationalRegressionTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TenantSqlite _tenantDb = new();
    private readonly CentralSqlite _centralDb = new();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lifecycle_FollowsTheExistingPathAndRejectsInvalidJumps()
    {
        var client = new RecordingPlatformClient();
        var actions = CreateActions(client);
        var orderId = await SeedOrderAsync(OrderStatus.New);

        var approved = await actions.TryApproveAsync(_tenantId, orderId, Ct);
        Assert.True(approved.Succeeded);
        Assert.Equal("Orders.ApproveSuccess", approved.MessageKey);
        Assert.Equal(new[] { "accept" }, client.Calls);
        await AssertStatusAsync(orderId, OrderStatus.Accepted, accepted: true, delivered: false, cancelled: false);

        var preparingTooEarly = await actions.MarkReadyForPickupAsync(_tenantId, orderId, Ct);
        Assert.False(preparingTooEarly.Succeeded);
        Assert.Equal("Orders.InvalidStatusForAction", preparingTooEarly.MessageKey);
        Assert.Equal(new[] { "accept" }, client.Calls);
        await AssertStatusAsync(orderId, OrderStatus.Accepted, accepted: true, delivered: false, cancelled: false);

        var preparing = await actions.MarkPreparingAsync(_tenantId, orderId, Ct);
        Assert.True(preparing.Succeeded);
        Assert.Equal(new[] { "accept" }, client.Calls);
        await AssertStatusAsync(orderId, OrderStatus.Preparing, accepted: true, delivered: false, cancelled: false);

        var ready = await actions.MarkReadyForPickupAsync(_tenantId, orderId, Ct);
        Assert.True(ready.Succeeded);
        Assert.Equal(new[] { "accept", "invoiced" }, client.Calls);

        var courierTooEarly = await actions.MarkDeliveredAsync(_tenantId, orderId, Ct);
        Assert.False(courierTooEarly.Succeeded);
        Assert.Equal(new[] { "accept", "invoiced" }, client.Calls);

        var onTheWay = await actions.MarkOnTheWayAsync(_tenantId, orderId, Ct);
        Assert.True(onTheWay.Succeeded);
        Assert.Equal(new[] { "accept", "invoiced", "shipped" }, client.Calls);

        var delivered = await actions.MarkDeliveredAsync(_tenantId, orderId, Ct);
        Assert.True(delivered.Succeeded);
        Assert.Equal(new[] { "accept", "invoiced", "shipped", "delivered" }, client.Calls);
        await AssertStatusAsync(orderId, OrderStatus.Delivered, accepted: true, delivered: true, cancelled: false);

        var again = await actions.MarkDeliveredAsync(_tenantId, orderId, Ct);
        Assert.False(again.Succeeded);
        Assert.Equal("Orders.InvalidStatusForAction", again.MessageKey);
        Assert.Equal(4, client.Calls.Count);

        await using var db = await _tenantDb.CreateAsync(_tenantId, Ct);
        Assert.Equal(0, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task Reject_FromNew_CancelsWithoutAReceiptAndLaterActionsFail()
    {
        var client = new RecordingPlatformClient();
        var actions = CreateActions(client);
        var orderId = await SeedOrderAsync(OrderStatus.New);

        var rejected = await actions.TryRejectAsync(_tenantId, orderId, Ct);

        Assert.True(rejected.Succeeded);
        Assert.Equal("Orders.RejectSuccess", rejected.MessageKey);
        Assert.Equal(new[] { "reject" }, client.Calls);
        await AssertStatusAsync(orderId, OrderStatus.Cancelled, accepted: false, delivered: false, cancelled: true);

        var preparing = await actions.MarkPreparingAsync(_tenantId, orderId, Ct);
        Assert.False(preparing.Succeeded);
        Assert.Equal(new[] { "reject" }, client.Calls);

        await using var db = await _tenantDb.CreateAsync(_tenantId, Ct);
        Assert.Equal(0, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task Approve_WhenTheProviderFails_LeavesTheOrderNew()
    {
        var client = new RecordingPlatformClient { Failure = new InvalidOperationException("provider down") };
        var actions = CreateActions(client);
        var orderId = await SeedOrderAsync(OrderStatus.New);

        var result = await actions.TryApproveAsync(_tenantId, orderId, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("Orders.ApproveFailed", result.MessageKey);
        Assert.Empty(client.Calls);
        await AssertStatusAsync(orderId, OrderStatus.New, accepted: false, delivered: false, cancelled: false);
    }

    [Fact]
    public async Task AcceptanceReceipt_IsCreatedOnlyWhenTheAcceptedSettingIsOn()
    {
        var orderId = await SeedOrderAsync(OrderStatus.Accepted, autoPrintOnAccepted: true, copyCount: 2);
        await SeedCentralTenantAsync("Sushi M");
        var receipts = CreateReceipts();

        await receipts.TryCreateOnOrderAcceptedAsync(_tenantId, orderId, Ct);
        await receipts.TryCreateOnOrderAcceptedAsync(_tenantId, orderId, Ct);

        await using var db = await _tenantDb.CreateAsync(_tenantId, Ct);
        var job = await db.PrintJobs.SingleAsync(Ct);
        Assert.Equal(orderId, job.OrderId);
        Assert.Equal(PrintJobType.Receipt, job.Type);
        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.Equal(2, job.CopyCount);
        Assert.Contains("Sushi M", job.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptanceReceipt_IsSkippedWhenTheSettingIsOff()
    {
        var orderId = await SeedOrderAsync(OrderStatus.Accepted, autoPrintOnAccepted: false);
        await SeedCentralTenantAsync("Sushi M");

        await CreateReceipts().TryCreateOnOrderAcceptedAsync(_tenantId, orderId, Ct);

        await using var db = await _tenantDb.CreateAsync(_tenantId, Ct);
        Assert.Equal(0, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task AcceptanceReceipt_FailureDoesNotEscapeTheBestEffortCall()
    {
        var orderId = await SeedOrderAsync(OrderStatus.Accepted, autoPrintOnAccepted: true);
        await SeedCentralTenantAsync("Sushi M");
        var receiptJobs = new ThrowingReceiptJobs();
        var receipts = new OrderReceiptCreationService(
            _tenantDb,
            _centralDb.Context,
            receiptJobs,
            NullLogger<OrderReceiptCreationService>.Instance);

        var pending = receipts.TryCreateOnOrderAcceptedAsync(_tenantId, orderId, Ct);

        await pending;
        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Equal(1, receiptJobs.CallCount);
    }

    [Fact]
    public async Task ListAndDetailReads_PreserveItemNotesWhileDetailsIncludeOptions()
    {
        var orderId = await SeedOrderAsync(OrderStatus.New, itemNotes: "No onion", optionName: "Extra sauce");
        var reader = new OrderReadService(_tenantDb, TimeProvider.System, NullLogger<OrderReadService>.Instance);

        var cards = await reader.GetListAsync(
            _tenantId,
            platform: null,
            status: null,
            startDateUtc: null,
            endDateUtc: null,
            sortBy: "receivedAt",
            sortDirection: "desc",
            page: 1,
            pageSize: 25,
            search: null,
            Ct,
            includeLineItems: true);
        var management = await reader.GetListAsync(
            _tenantId,
            platform: null,
            status: null,
            startDateUtc: null,
            endDateUtc: null,
            sortBy: "receivedAt",
            sortDirection: "desc",
            page: 1,
            pageSize: 25,
            search: null,
            Ct,
            includeLineItems: false);
        var detail = await reader.GetByIdAsync(_tenantId, orderId, Ct);
        var otherTenant = await reader.GetByIdAsync(Guid.NewGuid(), orderId, Ct);

        var cardItem = Assert.Single(Assert.Single(cards.Items).LineItems);
        Assert.Equal("Lahmacun", cardItem.ProductName);
        Assert.Equal("No onion", cardItem.Notes);
        Assert.Empty(Assert.Single(management.Items).LineItems);

        Assert.NotNull(detail);
        Assert.Equal("+905555555555", detail.CustomerPhone);
        Assert.Equal("Test Address", detail.CustomerAddress);
        var detailItem = Assert.Single(detail.Items);
        Assert.Equal("No onion", detailItem.Notes);
        Assert.Equal("Extra sauce", Assert.Single(detailItem.Options).Name);
        Assert.Null(otherTenant);
    }

    [Fact]
    public void LiveScreenSources_ReferenceItemNotesAndLifecycleActionTokens()
    {
        var cards = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_LiveScreenOrders.cshtml"));
        var actions = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrderLifecycleActions.cshtml"));

        // Token presence only: this source contract does not prove conditional association or rendered visibility.
        Assert.Contains("item.Notes", cards, StringComparison.Ordinal);
        Assert.Contains("data-order-action=\"approve\"", actions, StringComparison.Ordinal);
        Assert.Contains("data-order-action=\"reject\"", actions, StringComparison.Ordinal);
        Assert.Contains("data-order-action=\"start-preparing\"", actions, StringComparison.Ordinal);
        Assert.Contains("data-order-action=\"mark-ready\"", actions, StringComparison.Ordinal);
        Assert.Contains("data-order-action=\"hand-to-courier\"", actions, StringComparison.Ordinal);
        Assert.Contains("data-order-action=\"mark-delivered\"", actions, StringComparison.Ordinal);
    }

    private OrderActionService CreateActions(RecordingPlatformClient client) =>
        new(_tenantDb, [client], NullLogger<OrderActionService>.Instance);

    private OrderReceiptCreationService CreateReceipts() =>
        new(
            _tenantDb,
            _centralDb.Context,
            new ReceiptPrintJobService(
                _tenantDb,
                new FixedReceiptTemplateSettings(),
                NullLogger<ReceiptPrintJobService>.Instance),
            NullLogger<OrderReceiptCreationService>.Instance);

    private async Task<Guid> SeedOrderAsync(
        OrderStatus status,
        bool autoPrintOnAccepted = false,
        int copyCount = 1,
        string? itemNotes = null,
        string? optionName = null)
    {
        await using var db = await _tenantDb.CreateAsync(_tenantId, Ct);
        if (!await db.TenantOperationalSettings.AnyAsync(Ct))
        {
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
        }

        if (!await db.PlatformConnections.AnyAsync(Ct))
        {
            db.PlatformConnections.Add(new PlatformConnection
            {
                Id = Guid.NewGuid(),
                Platform = FoodPlatform.TrendyolYemek,
                StoreId = "store-1",
                EncryptedApiKey = "enc-key",
                EncryptedApiSecret = "enc-secret",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var item = new OrderItem
        {
            Id = itemId,
            ProductName = "Lahmacun",
            Quantity = 1,
            UnitPrice = 100m,
            TotalPrice = 100m,
            Notes = itemNotes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        if (optionName is not null)
        {
            item.Options.Add(new OrderItemOption
            {
                Id = Guid.NewGuid(),
                Name = optionName,
                Price = 5m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        db.Orders.Add(new Order
        {
            Id = orderId,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = "ORDER-1",
            ExternalOrderCode = "TY-ORDER-1",
            IdempotencyKey = $"TrendyolYemek:ORDER-1:{_tenantId:N}",
            InternalStatus = status,
            PlatformStatus = "Created",
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
            Items = [item]
        });
        await db.SaveChangesAsync(Ct);
        return orderId;
    }

    private async Task SeedCentralTenantAsync(string name)
    {
        _centralDb.Context.Tenants.Add(new Tenant
        {
            Id = _tenantId,
            Name = name,
            Slug = "sushi-m",
            PrimaryDomain = "sushi-m.wasla.local",
            DatabaseName = "Wasla_SushiM",
            EncryptedConnectionString = "enc",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _centralDb.Context.SaveChangesAsync(Ct);
    }

    private async Task AssertStatusAsync(Guid orderId, OrderStatus status, bool accepted, bool delivered, bool cancelled)
    {
        await using var db = await _tenantDb.CreateAsync(_tenantId, Ct);
        var order = await db.Orders.SingleAsync(o => o.Id == orderId, Ct);
        Assert.Equal(status, order.InternalStatus);
        Assert.Equal(accepted, order.AcceptedAt.HasValue);
        Assert.Equal(delivered, order.DeliveredAt.HasValue);
        Assert.Equal(cancelled, order.CancelledAt.HasValue);
    }

    public void Dispose()
    {
        _tenantDb.Dispose();
        _centralDb.Dispose();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class RecordingPlatformClient : IFoodPlatformClient
    {
        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public List<string> Calls { get; } = new();
        public Exception? Failure { get; set; }

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(
            PlatformConnection connection,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<ExternalOrderDto>>([]);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) =>
            Record("accept");

        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Record("invoiced");

        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Record("shipped");

        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Record("delivered");

        public Task RejectOrderAsync(
            PlatformConnection connection,
            string externalOrderId,
            IReadOnlyList<string> itemIdList,
            int reasonId,
            CancellationToken ct) =>
            Record("reject");

        private Task Record(string name)
        {
            if (Failure is not null)
                throw Failure;

            Calls.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingReceiptJobs : IReceiptPrintJobService
    {
        public int CallCount { get; private set; }

        public Task<bool> TryCreateReceiptJobAsync(
            Guid customerId,
            Guid orderId,
            int copyCount,
            string? tenantDisplayName,
            CancellationToken ct)
        {
            CallCount++;
            throw new InvalidOperationException("print pipeline unavailable");
        }
    }

    private sealed class FixedReceiptTemplateSettings : IReceiptTemplateSettingsService
    {
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

    private sealed class CentralSqlite : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public CentralDbContext Context { get; }

        public CentralSqlite()
        {
            _connection.Open();
            Context = new CentralDbContext(
                new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);
            Context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class TenantSqlite : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();
                _connections.Add(customerId, connection);
                using var setup = new TestTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new TestTenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
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
            });
        }
    }
}
