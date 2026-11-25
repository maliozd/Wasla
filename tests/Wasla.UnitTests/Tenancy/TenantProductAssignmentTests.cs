using System.CommandLine;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.PlatformConnections;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.Infrastructure.Tenant;
using Wasla.Integrations.Platform;
using Wasla.Integrations.Platform.Dtos;
using Wasla.UnitTests.Platform;
using Wasla.UnitTests.Setup;
using Wasla.Web.Areas.Admin.Models.TenantOperations;
using CentralTenant = Wasla.Domain.Entities.Central.Tenant;

namespace Wasla.UnitTests.Tenancy;

/// <summary>
/// WAS-235: each tenant's product assignment is recorded once, in CentralDb (<see cref="CentralTenant.Product"/>), and
/// every Orders entry point serves a tenant only while it is assigned to Orders. A tenant on POS, or between the
/// products, is refused like an inactive one. These tests use SQLite for the databases; the column, its default and the
/// shared tenant query are tested on SQL Server in <c>TenantProductSqlServerTests</c>.
/// </summary>
public sealed class TenantProductAssignmentTests : IDisposable
{
    private readonly SqliteConnection _centralConnection = new("Data Source=:memory:");
    private readonly OperationalModeTestDatabases _tenants = new();

    public TenantProductAssignmentTests()
    {
        _centralConnection.Open();
        using var central = Central();
        central.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _centralConnection.Dispose();
        _tenants.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The record ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheStoredValues_AreStable_AndANewTenantIsOrders()
    {
        Assert.Equal(
            [(1, "Orders"), (2, "MovingToPos"), (3, "Pos"), (4, "MovingToOrders")],
            Enum.GetValues<TenantProduct>().Select(p => ((int)p, p.ToString())));
        Assert.Equal(TenantProduct.Orders, new CentralTenant().Product);
    }

    [Fact]
    public void TheColumn_IsARequiredInt_WhoseDatabaseDefaultIsOrders()
    {
        using var central = Central();
        var property = central.Model.FindEntityType(typeof(CentralTenant))!.FindProperty(nameof(CentralTenant.Product))!;

        Assert.False(property.IsNullable);
        Assert.Equal(typeof(int), property.GetProviderClrType());
        Assert.Equal(TenantProduct.Orders, property.GetDefaultValue());
        Assert.Equal((TenantProduct)0, property.Sentinel);
    }

    [Theory]
    [InlineData(TenantProduct.Orders, true)]
    [InlineData(TenantProduct.MovingToPos, false)]
    [InlineData(TenantProduct.Pos, false)]
    [InlineData(TenantProduct.MovingToOrders, false)]
    public async Task OnlyAnOrdersTenant_IsServed_AndResolved(TenantProduct product, bool served)
    {
        var tenant = await AddTenantAsync(product);

        Assert.Equal(served, await new TenantProductReader(Central()).IsOrdersTenantAsync(tenant.Id, Ct));
        var resolved = await new TenantResolver(new MemoryCache(new MemoryCacheOptions()), Central()).ResolveByHostAsync(tenant.PrimaryDomain, Ct);
        Assert.Equal(served, resolved is not null);
    }

    [Fact]
    public async Task AMissingTenant_IsNotServed()
    {
        Assert.False(await new TenantProductReader(Central()).IsOrdersTenantAsync(Guid.NewGuid(), Ct));
    }

    // ---- Worker: the sync service --------------------------------------------------------------------------------

    [Fact]
    public async Task ATenantNotOnOrders_IsNotSynced_AndAnOrdersTenantIs()
    {
        var pos = await AddTenantAsync(TenantProduct.Pos);
        var orders = await AddTenantAsync(TenantProduct.Orders);
        foreach (var tenant in new[] { pos, orders })
        {
            await _tenants.SeedSettingsAsync(tenant.Id, TenantOperationalMode.Live, autoApprove: true, autoPrint: true);
            await AddConnectionAsync(tenant.Id, FoodPlatform.TrendyolYemek);
        }

        var client = new RecordingClient(ProviderPlatform.TrendyolYemek) { Orders = [NewOrder("p-1")] };
        var factory = new RecordingTenantFactory(_tenants);
        var sync = Sync(factory, [client]);

        var posResult = await sync.SyncTenantWithResultAsync(pos.Id, Ct);
        var backfill = await sync.BackfillTenantAsync(pos.Id, Ct);

        Assert.True(posResult.WasNotOrdersTenant);
        Assert.True(backfill.WasNotOrdersTenant);
        Assert.Equal(0, client.Fetches);
        Assert.DoesNotContain(pos.Id, factory.Requested);

        var ordersResult = await sync.SyncTenantWithResultAsync(orders.Id, Ct);

        Assert.False(ordersResult.WasNotOrdersTenant);
        Assert.Equal(1, ordersResult.InsertedCount);
        Assert.Equal(1, client.Fetches);
        await using var posDb = await _tenants.CreateAsync(pos.Id, Ct);
        Assert.Equal(0, await posDb.Orders.CountAsync(Ct));
        Assert.Equal(0, await posDb.PrintJobs.CountAsync(Ct));
    }

    /// <summary>
    /// The assignment is read again before each connection pass. A change during the first connection's fetch stops the
    /// pass before the second connection; the first one still finishes, so this narrows the window but does not stop
    /// work already in flight (WAS-240).
    /// </summary>
    [Fact]
    public async Task AnAssignmentChangeDuringAPass_TakesEffectAtTheNextConnection()
    {
        var tenant = await AddTenantAsync(TenantProduct.Orders);
        await _tenants.SeedSettingsAsync(tenant.Id, TenantOperationalMode.Live);
        await AddConnectionAsync(tenant.Id, FoodPlatform.TrendyolYemek);
        await AddConnectionAsync(tenant.Id, FoodPlatform.Yemeksepeti);

        async Task MoveToPos()
        {
            await using var central = Central();
            await central.Tenants.Where(t => t.Id == tenant.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.Product, TenantProduct.MovingToPos), Ct);
        }

        var trendyol = new RecordingClient(ProviderPlatform.TrendyolYemek) { Orders = [NewOrder("t-1")], OnFetch = MoveToPos };
        var yemeksepeti = new RecordingClient(ProviderPlatform.Yemeksepeti) { Orders = [NewOrder("y-1", ProviderPlatform.Yemeksepeti)], OnFetch = MoveToPos };

        var result = await Sync(_tenants, [trendyol, yemeksepeti]).SyncTenantWithResultAsync(tenant.Id, Ct);

        Assert.True(result.WasNotOrdersTenant);
        Assert.Equal(1, trendyol.Fetches + yemeksepeti.Fetches);
        Assert.Equal(1, result.InsertedCount);
        await using var db = await _tenants.CreateAsync(tenant.Id, Ct);
        Assert.Equal(1, await db.Orders.CountAsync(Ct));
        Assert.Single(await db.PlatformConnections.Where(c => c.LastSyncAttempt == null).ToListAsync(Ct));
    }

    // ---- Web and Api: services ------------------------------------------------------------------------------------

    [Fact]
    public async Task OrderActions_ForATenantNotOnOrders_AreRefusedBeforeTheDatabaseOrTheProvider()
    {
        var client = new RecordingClient(ProviderPlatform.TrendyolYemek);
        var actions = new OrderActionService(
            new ThrowingTenantFactory(), [client], new PassthroughSecrets(), NullTenantChangeNotifier.Instance,
            NullLogger<OrderActionService>.Instance, new FixedProducts(served: false));
        var tenant = Guid.NewGuid();
        var order = Guid.NewGuid();

        Assert.Equal("Orders.ActionFailed", (await actions.TryApproveAsync(tenant, order, Ct)).MessageKey);
        Assert.Equal("Orders.ActionFailed", (await actions.TryRejectAsync(tenant, order, 621, Ct)).MessageKey);
        Assert.Equal("Orders.ActionFailed", (await actions.MarkReadyForPickupAsync(tenant, order, Ct)).MessageKey);
        Assert.Equal("Orders.ActionFailed", (await actions.MarkOnTheWayAsync(tenant, order, Ct)).MessageKey);
        Assert.Equal("Orders.ActionFailed", (await actions.MarkDeliveredAsync(tenant, order, Ct)).MessageKey);
        Assert.False(await actions.TryUpdateStatusAsync(tenant, order, OrderStatus.Cancelled, Ct));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task PlatformConnectionChanges_ForATenantNotOnOrders_AreAnsweredAsNotFound_WithoutTheDatabase()
    {
        var connections = new PlatformConnectionService(
            new ThrowingTenantFactory(), new PassthroughSecrets(), new CreatePlatformConnectionCommandValidator(), new FixedProducts(served: false));
        var tenant = Guid.NewGuid();

        var created = await connections.CreateAsync(
            tenant, new CreatePlatformConnectionCommand(FoodPlatform.TrendyolYemek, "1331", "key", "secret", true, "107385", "ops@example.invalid"), Ct);
        var updated = await connections.UpdateAsync(
            tenant, Guid.NewGuid(), new UpdatePlatformConnectionCommand(FoodPlatform.TrendyolYemek, "1331", true, 60, "107385", "ops@example.invalid", null, null), Ct);

        Assert.Equal((false, "NotFound"), (created.Succeeded, created.ErrorCode));
        Assert.Equal((false, "NotFound"), (updated.Succeeded, updated.ErrorCode));
        Assert.False(await connections.SetActiveAsync(tenant, Guid.NewGuid(), true, Ct));
        Assert.False(await connections.RemoveAsync(tenant, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task WebhookEvents_OfAPilotTenantNotOnOrders_StayPendingAndUnread()
    {
        var tenant = Guid.NewGuid();
        var factory = new ThrowingTenantFactory();
        var processor = new TrendyolGoMealWebhookProcessor(
            Options.Create(new TrendyolGoMealWebhookPilotOptions { Enabled = true, TenantId = tenant }),
            factory,
            Sync(factory, [], new FixedProducts(served: false)),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            NullLogger<TrendyolGoMealWebhookProcessor>.Instance,
            TimeProvider.System);

        Assert.Equal(0, await processor.ProcessDueAsync(10, Ct));
    }

    [Theory]
    [InlineData(TenantProduct.Orders, true)]
    [InlineData(TenantProduct.MovingToPos, false)]
    [InlineData(TenantProduct.Pos, false)]
    public async Task PrintBridge_SignsInOnlyForAnOrdersTenant_AndRefusesOthersLikeAnInactiveTenant(TenantProduct product, bool signsIn)
    {
        var tenant = await AddTenantAsync(product);
        const string token = "fake-print-bridge-token-0123456789abcdef";
        await using (var central = Central())
        {
            central.PrintBridgeDevices.Add(new PrintBridgeDevice
            {
                TenantId = tenant.Id,
                Name = "Kitchen",
                TokenHash = PrintBridgeTokenHasher.HashToken(token),
                IsActive = true
            });
            await central.SaveChangesAsync(Ct);
        }

        var result = await new PrintBridgeAuthService(Central(), NullLogger<PrintBridgeAuthService>.Instance)
            .AuthenticateDetailedAsync(token, new PrintBridgeClientInfo("Kitchen", "1.0.0", null, null), Ct);

        Assert.Equal(signsIn, result.Succeeded);
        if (!signsIn)
            Assert.Equal(PrintBridgeAuthFailureCode.TenantInactive, result.FailureCode);
    }

    // ---- Composition --------------------------------------------------------------------------------------------

    /// <summary>
    /// The hosts compose these services with the registered reader (<c>AddWaslaInfrastructure</c>, and the Worker's own
    /// registration of <see cref="OrderSyncService"/>), so the refusals above apply outside the tests too.
    /// </summary>
    [Fact]
    public async Task TheComposedServices_UseTheRegisteredReader()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platforms:ProviderMode"] = "Mock",
                ["ConnectionStrings:CentralDb"] = "Server=unused;Database=unused"
            })
            .Build());
        // The hosts' AesSecretManager needs ENCRYPTION_MASTER_KEY, which CI does not have; nothing is decrypted here.
        services.AddSingleton<ISecretManager, PassthroughSecrets>();
        services.AddScoped<OrderSyncService>();

        await using (var registered = services.BuildServiceProvider())
        await using (var scope = registered.CreateAsyncScope())
            Assert.IsType<TenantProductReader>(scope.ServiceProvider.GetRequiredService<ITenantProductReader>());

        var factory = new ThrowingTenantFactory();
        services.AddScoped<ITenantProductReader>(_ => new FixedProducts(served: false));
        services.AddSingleton<ITenantDbContextFactory>(factory);
        await using var provider = services.BuildServiceProvider();
        await using var composed = provider.CreateAsyncScope();
        var tenant = Guid.NewGuid();

        Assert.True((await composed.ServiceProvider.GetRequiredService<OrderSyncService>().SyncTenantWithResultAsync(tenant, Ct)).WasNotOrdersTenant);
        Assert.Equal("Orders.ActionFailed", (await composed.ServiceProvider.GetRequiredService<IOrderActionService>().TryApproveAsync(tenant, Guid.NewGuid(), Ct)).MessageKey);
        Assert.False(await composed.ServiceProvider.GetRequiredService<IPlatformConnectionService>().SetActiveAsync(tenant, Guid.NewGuid(), true, Ct));
    }

    // ---- Migration tooling --------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMigrationGate_SkipsTenantsNotOnOrders_EvenWithoutAReadableDatabase()
    {
        await RecordEveryCentralMigrationAsync();
        await AddTenantAsync(TenantProduct.Pos, "pos-tenant");
        await AddTenantAsync(TenantProduct.MovingToPos, "moving-tenant");

        var (code, output) = await CliAsync("migration-status", "--require-current");

        Assert.Equal(0, code);
        Assert.Contains("Not assigned to Wasla Orders, not checked: moving-tenant, pos-tenant", output, StringComparison.Ordinal);
        Assert.Contains("All active databases are current for this build.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("could not read migration status", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMigrationGate_StillChecksAnOrdersTenant()
    {
        await RecordEveryCentralMigrationAsync();
        await AddTenantAsync(TenantProduct.Pos, "pos-tenant");
        await AddTenantAsync(TenantProduct.Orders, "orders-tenant");

        var (code, output) = await CliAsync("migration-status", "--require-current");

        Assert.Equal(1, code);
        Assert.Contains("orders-tenant", output, StringComparison.Ordinal);
        Assert.Contains("(could not read migration status)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigrateAllTenants_SkipsTenantsNotOnOrders_AndRefusesToTargetOne()
    {
        await AddTenantAsync(TenantProduct.Pos, "pos-tenant");

        var (allCode, allOutput) = await CliAsync("migrate-all-tenants", "--dry-run");
        var (onlyCode, onlyOutput) = await CliAsync("migrate-all-tenants", "--only", "pos-tenant");

        Assert.Equal(0, allCode);
        Assert.Contains("Skipped, not assigned to Wasla Orders: pos-tenant", allOutput, StringComparison.Ordinal);
        Assert.Contains("Active tenants to process: 0", allOutput, StringComparison.Ordinal);
        Assert.Equal(2, onlyCode);
        Assert.Contains("Tenant pos-tenant is not assigned to Wasla Orders; its Orders database is not migrated.", onlyOutput, StringComparison.Ordinal);
    }

    // ---- Central Admin ------------------------------------------------------------------------------------------

    [Fact]
    public async Task CentralAdmin_ShowsTheAssignment_ReadOnly()
    {
        var pos = await AddTenantAsync(TenantProduct.Pos, "pos-tenant");
        await AddTenantAsync(TenantProduct.Orders, "orders-tenant");
        await using var central = Central();
        var service = new CentralAdminTenantOperationsService(central, TimeProvider.System);

        var list = await service.GetTenantListAsync(new TenantListQuery(), Ct);
        var detail = await service.GetTenantDetailAsync(pos.Id, 1, Ct);

        Assert.Equal(
            [("orders-tenant", TenantProduct.Orders), ("pos-tenant", TenantProduct.Pos)],
            list.Items.OrderBy(i => i.Slug).Select(i => (i.Slug, i.Product)));
        Assert.Equal(TenantProduct.Pos, detail!.Product);
    }

    [Theory]
    [InlineData(TenantProduct.Orders, "Admin.Ops.Product.Orders")]
    [InlineData(TenantProduct.MovingToPos, "Admin.Ops.Product.MovingToPos")]
    [InlineData(TenantProduct.Pos, "Admin.Ops.Product.Pos")]
    [InlineData(TenantProduct.MovingToOrders, "Admin.Ops.Product.MovingToOrders")]
    public void CentralAdmin_NamesEveryProductState(TenantProduct product, string key)
    {
        Assert.Equal(key, AdminOperationsPresenter.Product(product).LabelKey);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private CentralDbContext Central() =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_centralConnection).Options);

    private async Task<CentralTenant> AddTenantAsync(TenantProduct product, string? slug = null)
    {
        var id = Guid.NewGuid();
        slug ??= "t-" + id.ToString("N")[..12];
        var tenant = new CentralTenant
        {
            Id = id,
            Name = "Restaurant " + slug,
            Slug = slug,
            PrimaryDomain = slug + ".app.wasla.local",
            DatabaseName = "Wasla_test_" + id.ToString("N"),
            EncryptedConnectionString = "unused",
            IsActive = true,
            Product = product
        };
        await using var central = Central();
        central.Tenants.Add(tenant);
        await central.SaveChangesAsync(Ct);
        return tenant;
    }

    private async Task AddConnectionAsync(Guid tenant, FoodPlatform platform)
    {
        await using var db = await _tenants.CreateAsync(tenant, Ct);
        db.PlatformConnections.Add(new PlatformConnection
        {
            Platform = platform,
            StoreId = "store-" + platform,
            EncryptedApiKey = "fake-encrypted-key",
            EncryptedApiSecret = "fake-encrypted-secret",
            IsActive = true,
            SyncIntervalSeconds = 0
        });
        await db.SaveChangesAsync(Ct);
    }

    private OrderSyncService Sync(ITenantDbContextFactory factory, IFoodPlatformClient[] clients, ITenantProductReader? products = null) =>
        new(
            factory,
            clients,
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            NullTenantChangeNotifier.Instance,
            NullLogger<OrderSyncService>.Instance,
            products: products ?? new TenantProductReader(Central()));

    private async Task RecordEveryCentralMigrationAsync()
    {
        await using var central = Central();
        await central.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);", Ct);
        foreach (var migration in central.Database.GetMigrations())
            await central.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" VALUES ({0}, '8.0.10');", [migration], Ct);
    }

    private async Task<(int Code, string Output)> CliAsync(params string[] args)
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<CentralDbContext>(options => options.UseSqlite(_centralConnection));
                services.AddSingleton<ISecretManager, NoTenantSecrets>();
            })
            .Build();

        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            var code = await CliCommandTree.Build(host).InvokeAsync(args);
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static ExternalOrderDto NewOrder(string externalOrderId, ProviderPlatform platform = ProviderPlatform.TrendyolYemek) =>
        new(
            Platform: platform,
            ExternalOrderId: externalOrderId,
            ExternalOrderCode: "N-" + externalOrderId,
            OrderedAtUtc: new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc),
            CustomerName: "Test Customer",
            CustomerPhone: "5000000000",
            CustomerAddress: "Test Street 1",
            Subtotal: 100m,
            DeliveryFee: 0m,
            ServiceFee: 0m,
            Total: 100m,
            PaymentMethod: ProviderPaymentMethod.CreditCard,
            PaymentStatus: ProviderPaymentStatus.Paid,
            ExternalStatus: platform == ProviderPlatform.TrendyolYemek ? "Created" : "RECEIVED",
            RawPayloadJson: "{}",
            Items: [new ExternalOrderItemDto(externalOrderId + "-1", "Lahmacun", 1, 100m, 100m, null, [])]);

    private sealed class RecordingClient(ProviderPlatform platform) : IFoodPlatformClient
    {
        public IReadOnlyCollection<ExternalOrderDto> Orders { get; init; } = [];

        public Func<Task>? OnFetch { get; init; }

        public int Fetches { get; private set; }

        public int Calls { get; private set; }

        public ProviderPlatform Platform => platform;

        public TimeSpan? MaxFetchWindow => null;

        public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(ProviderConnectionContext connection, OrderFetchWindow window, CancellationToken ct)
        {
            Fetches++;
            Calls++;
            if (OnFetch is not null)
                await OnFetch();
            return Orders;
        }

        public Task AcceptOrderAsync(ProviderConnectionContext connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Record();
        public Task MarkInvoicedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) => Record();
        public Task MarkShippedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) => Record();
        public Task MarkDeliveredAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) => Record();
        public Task RejectOrderAsync(ProviderConnectionContext connection, ProviderOrderReference order, int? reasonId, CancellationToken ct) => Record();

        private Task Record()
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedProducts(bool served) : ITenantProductReader
    {
        public Task<bool> IsOrdersTenantAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(served);
    }

    /// <summary>Fails the test if a tenant database is opened.</summary>
    private sealed class ThrowingTenantFactory : ITenantDbContextFactory
    {
        public Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct) =>
            throw new InvalidOperationException("The tenant database of a tenant that is not on Orders must not be opened.");
    }

    private sealed class RecordingTenantFactory(ITenantDbContextFactory inner) : ITenantDbContextFactory
    {
        public List<Guid> Requested { get; } = [];

        public Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct)
        {
            Requested.Add(tenantId);
            return inner.CreateAsync(tenantId, ct);
        }
    }

    private sealed class NoAutoApprove : IOrderAutoApproveService
    {
        public Task ProcessNewlyInsertedOrderAsync(Guid tenantId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoReceipts : IOrderReceiptCreationService
    {
        public Task TryCreateOnOrderAcceptedAsync(Guid tenantId, Guid orderId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoTenantSecrets : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            throw new InvalidOperationException("No secret may be encrypted by these tests.");

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            throw new InvalidOperationException("No tenant connection string may be decrypted by these tests.");
    }
}
