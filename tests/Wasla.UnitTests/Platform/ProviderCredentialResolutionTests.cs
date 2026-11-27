using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Integrations.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Integrations.Platform.Dtos;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Integrations.Platform.Mock;
using Wasla.Integrations.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;
using Wasla.Integrations.Platform.Yemeksepeti;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// When provider clients resolve a connection's stored credentials, and what each operation hands them (WAS-231):
/// Trendyol GO decrypts for every request, Yemeksepeti and the mock clients once per fetch, an operation keeps the
/// credentials of the connection it loaded even when they are edited meanwhile, the Yemeksepeti lifecycle stays
/// unsupported, and sync and order actions pass the loaded connection's values and the stored order's provider id and
/// payload. Written against the code before the client contract changed, so the same assertions hold after it; only the
/// construction and call helpers at the end follow the contract. Loopback fakes, fake credentials and SQLite only:
/// nothing here proves a real provider's behaviour or SQL Server locking.
/// </summary>
public sealed class ProviderCredentialResolutionTests : IDisposable
{
    private const string KeyCipher = "cipher-key-a";
    private const string SecretCipher = "cipher-secret-a";
    private const int KeyVersion = 3;
    private const string Executor = "executor-a@example.invalid";

    private static readonly Guid TrendyolConnectionId = Guid.Parse("8f3c2a51-0b7e-4d6a-9c11-2f4e5a6b7c80");
    private static readonly Guid YemeksepetiConnectionId = Guid.Parse("1d2e3f40-5a6b-4c7d-8e9f-a0b1c2d3e4f5");

    private const string StoredPayload =
        """{"id":"pkg-stored","lines":[{"name":"Lahmacun","items":[{"isCancelled":false,"packageItemId":"pkg-stored-1"}]}]}""";

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly RecordingSecrets _secrets = new();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tenants.Dispose();

    [Fact]
    public async Task TrendyolGo_DecryptsKeyAndSecretForEveryRequest_PagesAndActionsAlike()
    {
        var authorizations = new List<string?>();
        var client = TrendyolGo(request =>
        {
            authorizations.Add(request.Headers.Authorization?.ToString());
            if (request.Method != HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK);

            var page = PageOf(request);
            return Json(TrendyolPage(page, totalPages: 2, TrendyolPackage("pkg-" + page)));
        });
        var connection = TrendyolConnection();

        var orders = await FetchAsync(client, connection, TrendyolWindow());

        Assert.Equal(["pkg-0", "pkg-1"], orders.Select(o => o.ExternalOrderId).ToArray());
        Assert.Equal(StoredPairs(KeyCipher, SecretCipher, times: 2), _secrets.Decrypted);

        _secrets.Clear();
        await AcceptAsync(client, connection, "pkg-0");
        await RejectAsync(client, connection, StoredOrder("pkg-stored", StoredPayload), reasonId: 621);

        Assert.Equal(StoredPairs(KeyCipher, SecretCipher, times: 2), _secrets.Decrypted);
        Assert.Equal(4, authorizations.Count);
        Assert.All(authorizations, value => Assert.Equal(BasicAuth(Plain(KeyCipher), Plain(SecretCipher)), value));
    }

    [Fact]
    public async Task Yemeksepeti_DecryptsKeyAndSecretOncePerFetch_WhateverThePagesAndTheCachedToken()
    {
        var tokenRequests = 0;
        var pages = new List<int>();
        var client = Yemeksepeti(request =>
        {
            if (IsYemeksepetiToken(request))
            {
                tokenRequests++;
                return Json("""{"access_token":"tok-1","expires_in":7200}""");
            }

            var page = PageOf(request);
            pages.Add(page);
            return Json(YemeksepetiPage(page, totalPages: 3, YemeksepetiOrder("ys-" + page)));
        });
        var connection = YemeksepetiConnection();

        await FetchAsync(client, connection, AnyWindow());
        Assert.Equal(StoredPairs(KeyCipher, SecretCipher, times: 1), _secrets.Decrypted);

        await FetchAsync(client, connection, AnyWindow());
        Assert.Equal(StoredPairs(KeyCipher, SecretCipher, times: 2), _secrets.Decrypted);
        Assert.Equal(1, tokenRequests);
        Assert.Equal([0, 1, 2, 0, 1, 2], pages);
    }

    public static TheoryData<string, string> UnsupportedYemeksepetiOperations => new()
    {
        { "accept", "Yemeksepeti AcceptOrder is not yet implemented. Use the Yemeksepeti partner portal directly." },
        { "invoiced", "Yemeksepeti MarkInvoiced is not yet implemented." },
        { "shipped", "Yemeksepeti MarkShipped is not yet implemented." },
        { "delivered", "Yemeksepeti MarkDelivered is not yet implemented." },
        { "reject", "Yemeksepeti RejectOrder is not yet implemented." }
    };

    [Theory]
    [MemberData(nameof(UnsupportedYemeksepetiOperations))]
    public void Yemeksepeti_LifecycleOperations_StayUnsupported_ThrowingBeforeAnyRequestOrDecryption(string operation, string message)
    {
        var requests = 0;
        var client = Yemeksepeti(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // The call itself throws: no task is returned, so no request can start.
        var ex = Assert.Throws<NotSupportedException>(() =>
        {
            _ = Lifecycle(client, operation, YemeksepetiConnection(), StoredOrder("ys-1", "{}"));
        });

        Assert.Equal(message, ex.Message);
        Assert.Equal(0, requests);
        Assert.Empty(_secrets.Decrypted);
    }

    [Fact]
    public async Task OrderActions_OnTheRealYemeksepetiClient_FailAndLeaveTheOrder_WithoutRequestOrDecryption()
    {
        var requests = 0;
        var actions = Actions(Yemeksepeti(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await SeedConnectionAsync(YemeksepetiConnection());
        var waiting = await SeedOrderAsync(FoodPlatform.Yemeksepeti, "ys-new", OrderStatus.New);
        var preparing = await SeedOrderAsync(FoodPlatform.Yemeksepeti, "ys-preparing", OrderStatus.Preparing);

        var approve = await actions.TryApproveAsync(_tenant, waiting, Ct);
        var reject = await actions.TryRejectAsync(_tenant, waiting, reasonId: null, Ct);
        var ready = await actions.MarkReadyForPickupAsync(_tenant, preparing, Ct);

        Assert.Equal(new OrderActionResult(false, "Orders.ApproveFailed"), approve);
        Assert.Equal(new OrderActionResult(false, "Orders.RejectFailed"), reject);
        Assert.Equal(new OrderActionResult(false, "Orders.OrderActionFailed"), ready);
        Assert.Equal(OrderStatus.New, await StatusAsync(waiting));
        Assert.Equal(OrderStatus.Preparing, await StatusAsync(preparing));
        Assert.Equal(0, requests);
        Assert.Empty(_secrets.Decrypted);
    }

    [Fact]
    public async Task CredentialEditDuringASyncPass_IsNotUsedByThatPass_ButByTheNextOne()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        await SeedConnectionAsync(TrendyolConnection());
        var authorizations = new List<string?>();
        var client = TrendyolGo(request =>
        {
            authorizations.Add(request.Headers.Authorization?.ToString());
            if (authorizations.Count == 1)
                EditStoredCredentials("cipher-key-b", "cipher-secret-b");

            var page = PageOf(request);
            return Json(TrendyolPage(page, totalPages: 2, TrendyolPackage("pkg-" + authorizations.Count)));
        });

        var first = await Sync(client).SyncTenantWithResultAsync(_tenant, Ct);

        Assert.Equal(0, first.FailedConnections);
        Assert.Equal(2, authorizations.Count);
        Assert.All(authorizations, value => Assert.Equal(BasicAuth(Plain(KeyCipher), Plain(SecretCipher)), value));
        Assert.Equal(StoredPairs(KeyCipher, SecretCipher, times: 2), _secrets.Decrypted);

        _secrets.Clear();
        var second = await Sync(client).SyncTenantWithResultAsync(_tenant, Ct);

        Assert.Equal(0, second.FailedConnections);
        Assert.Equal(4, authorizations.Count);
        Assert.All(authorizations.Skip(2), value => Assert.Equal(BasicAuth(Plain("cipher-key-b"), Plain("cipher-secret-b")), value));
        Assert.Equal(StoredPairs("cipher-key-b", "cipher-secret-b", times: 2), _secrets.Decrypted);
    }

    [Fact]
    public async Task SyncAndOrderActions_HandTheClient_TheLoadedConnection_AndTheStoredOrdersProviderIdAndPayload()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        await SeedConnectionAsync(TrendyolConnection());
        var toAccept = await SeedOrderAsync(FoodPlatform.TrendyolYemek, "pkg-accept", OrderStatus.New);
        var toReject = await SeedOrderAsync(FoodPlatform.TrendyolYemek, "pkg-stored", OrderStatus.New, StoredPayload);
        var preparing = await SeedOrderAsync(FoodPlatform.TrendyolYemek, "pkg-ready", OrderStatus.Preparing);
        var client = new RecordingClient(FoodPlatform.TrendyolYemek);

        await Sync(client).SyncTenantWithResultAsync(_tenant, Ct);
        var accept = await Actions(client).TryApproveAsync(_tenant, toAccept, Ct);
        var reject = await Actions(client).TryRejectAsync(_tenant, toReject, reasonId: 621, Ct);
        var ready = await Actions(client).MarkReadyForPickupAsync(_tenant, preparing, Ct);

        Assert.True(accept.Succeeded);
        Assert.True(reject.Succeeded);
        Assert.True(ready.Succeeded);
        var loaded = new SeenConnection(TrendyolConnectionId, FoodPlatform.TrendyolYemek, "store-a", "supplier-a", Executor, IsActive: true);
        Assert.Equal(
            [
                new SeenCall("fetch", loaded, ExternalOrderId: null, StoredPayload: null),
                new SeenCall("accept", loaded, "pkg-accept", StoredPayload: null),
                new SeenCall("reject", loaded, "pkg-stored", StoredPayload),
                new SeenCall("invoiced", loaded, "pkg-ready", StoredPayload: null)
            ],
            client.Calls);
        Assert.Empty(_secrets.Decrypted);
    }

    [Theory]
    [InlineData(FoodPlatform.TrendyolYemek)]
    [InlineData(FoodPlatform.Yemeksepeti)]
    [InlineData(FoodPlatform.GetirYemek)]
    public async Task MockClients_DecryptKeyAndSecretOncePerCompletedFetch_AndNeverForActions(FoodPlatform platform)
    {
        var client = MockClient(platform);
        var connection = TrendyolConnection();
        connection.Platform = platform;

        // A mock fetch fails at random (about one in ten) before it reads any credential.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await FetchAsync(client, connection, AnyWindow());
                break;
            }
            catch (HttpRequestException) when (attempt < 30)
            {
                Assert.Empty(_secrets.Decrypted);
            }
        }

        Assert.Equal(StoredPairs(KeyCipher, SecretCipher, times: 1), _secrets.Decrypted);

        _secrets.Clear();
        var externalOrderId = "mock-" + Guid.NewGuid().ToString("N");
        foreach (var operation in new[] { "accept", "invoiced", "shipped", "delivered", "reject" })
            await Lifecycle(client, operation, connection, StoredOrder(externalOrderId, "{}"));

        Assert.Empty(_secrets.Decrypted);
    }

    private static IReadOnlyList<(string Cipher, int Version)> StoredPairs(string keyCipher, string secretCipher, int times) =>
        Enumerable.Repeat(new[] { (keyCipher, KeyVersion), (secretCipher, KeyVersion) }, times).SelectMany(pair => pair).ToList();

    private static string Plain(string cipher) => "plain:" + cipher;

    private static string BasicAuth(string key, string secret) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}"));

    private static PlatformConnection TrendyolConnection() => new()
    {
        Id = TrendyolConnectionId,
        Platform = FoodPlatform.TrendyolYemek,
        SupplierId = "supplier-a",
        StoreId = "store-a",
        ExecutorEmail = Executor,
        EncryptedApiKey = KeyCipher,
        EncryptedApiSecret = SecretCipher,
        EncryptionKeyVersion = KeyVersion,
        IsActive = true,
        SyncIntervalSeconds = 0,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static PlatformConnection YemeksepetiConnection() => new()
    {
        Id = YemeksepetiConnectionId,
        Platform = FoodPlatform.Yemeksepeti,
        SupplierId = "chain-1",
        StoreId = "vendor-1",
        EncryptedApiKey = KeyCipher,
        EncryptedApiSecret = SecretCipher,
        EncryptionKeyVersion = KeyVersion,
        IsActive = true,
        SyncIntervalSeconds = 0,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static Order StoredOrder(string externalOrderId, string payload) => new()
    {
        Id = Guid.NewGuid(),
        Platform = FoodPlatform.TrendyolYemek,
        ExternalOrderId = externalOrderId,
        RawPayloadJson = payload
    };

    private static OrderFetchWindow TrendyolWindow() =>
        new(DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow);

    private static OrderFetchWindow AnyWindow() =>
        new(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

    private static int PageOf(HttpRequestMessage request)
    {
        var match = Regex.Match(request.RequestUri?.Query ?? string.Empty, @"(?:^|[?&])page=(\d+)");
        Assert.True(match.Success, request.RequestUri?.ToString());
        return int.Parse(match.Groups[1].Value);
    }

    private static bool IsYemeksepetiToken(HttpRequestMessage request) =>
        (request.RequestUri?.AbsolutePath ?? string.Empty).Contains("token", StringComparison.OrdinalIgnoreCase);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string TrendyolPage(int page, int totalPages, params string[] packages) =>
        $$"""{"page":{{page}},"size":50,"totalPages":{{totalPages}},"totalCount":{{totalPages}},"content":[{{string.Join(',', packages)}}]}""";

    private static string TrendyolPackage(string id)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        return $$"""
            {"id":"{{id}}","orderNumber":"N-{{id}}","packageCreationDate":{{at}},"packageModificationDate":{{at}},"lastModifiedDate":{{at}},"packageStatus":"Created","totalPrice":42.5,"customer":{"firstName":"Test","lastName":"Customer"},"address":{"phone":"5550000000","address1":"Test Street 1","city":"Test City"},"lines":[{"name":"Lahmacun","unitSellingPrice":42.5,"items":[{"isCancelled":false,"packageItemId":"{{id}}-1"}]}]}
            """;
    }

    private static string YemeksepetiPage(int page, int totalPages, params string[] orders) =>
        $$"""{"page":{{page}},"total_pages":{{totalPages}},"total_count":{{totalPages}},"data":[{{string.Join(',', orders)}}]}""";

    private static string YemeksepetiOrder(string id) =>
        $$"""{"id":"{{id}}","code":"{{id}}","status":"RECEIVED","total_price":10}""";

    private async Task SeedConnectionAsync(PlatformConnection connection)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync(Ct);
    }

    private void EditStoredCredentials(string keyCipher, string secretCipher)
    {
        // A concurrent edit through another context, as the Web edit page would make it.
        using var db = _tenants.CreateAsync(_tenant, CancellationToken.None).GetAwaiter().GetResult();
        var stored = db.PlatformConnections.Single();
        stored.EncryptedApiKey = keyCipher;
        stored.EncryptedApiSecret = secretCipher;
        db.SaveChanges();
    }

    private async Task<Guid> SeedOrderAsync(FoodPlatform platform, string externalOrderId, OrderStatus status, string payload = "{}")
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var orderId = Guid.NewGuid();
        // Imported under the platform's connection these tests seed (WAS-241).
        var imported = platform == FoodPlatform.Yemeksepeti ? YemeksepetiConnection() : TrendyolConnection();
        db.Orders.Add(new Order
        {
            Id = orderId,
            Platform = platform,
            ExternalOrderId = externalOrderId,
            ExternalOrderCode = "C-" + externalOrderId,
            IdempotencyKey = $"{platform}:{externalOrderId}:{_tenant:N}",
            StoreId = imported.StoreId,
            SupplierId = imported.SupplierId,
            InternalStatus = status,
            PlatformStatus = "Created",
            CustomerName = "Test Customer",
            CustomerPhone = "+900000000000",
            CustomerAddress = "Test Address",
            TotalAmount = 10m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = DateTime.UtcNow.AddMinutes(-5),
            ReceivedAt = DateTime.UtcNow.AddMinutes(-4),
            RawPayloadJson = payload,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        return orderId;
    }

    private async Task<OrderStatus> StatusAsync(Guid orderId)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        return await db.Orders.Where(o => o.Id == orderId).Select(o => o.InternalStatus).SingleAsync(Ct);
    }

    // ── Construction and calls: the only part that follows the client contract ─────────────────────────────

    private TrendyolGoFoodPlatformClient TrendyolGo(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(
            new HttpClient(new DelegateHandler(responder)) { BaseAddress = new Uri("https://trendyol.example/") },
            Options.Create(new TrendyolGoOptions { AgentName = "Wasla", BaseUrl = "https://trendyol.example/" }),
            new TrendyolGoDeploymentApiRouter(),
            NullLogger<TrendyolGoFoodPlatformClient>.Instance,
            new LimiterClock().Limiter(),
            TimeProvider.System);

    private YemeksepetiFoodPlatformClient Yemeksepeti(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(
            new SingleClientFactory(new HttpClient(new DelegateHandler(responder)) { BaseAddress = new Uri("https://yemeksepeti.example/") }),
            Options.Create(new YemeksepetiOptions
            {
                BaseUrl = "https://yemeksepeti.example/",
                TokenPath = "/v2/oauth/token",
                DefaultPageSize = 20
            }),
            NullLogger<YemeksepetiFoodPlatformClient>.Instance);

    private static IFoodPlatformClient MockClient(FoodPlatform platform) => platform switch
    {
        FoodPlatform.TrendyolYemek => new MockTrendyolYemekFoodPlatformClient(),
        FoodPlatform.Yemeksepeti => new MockYemeksepetiFoodPlatformClient(),
        FoodPlatform.GetirYemek => new MockGetirYemekFoodPlatformClient(),
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    private OrderSyncService Sync(IFoodPlatformClient client) =>
        new(
            _tenants,
            [client],
            _secrets,
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            NoSideEffects.Instance,
            NoSideEffects.Instance,
            NullTenantChangeNotifier.Instance,
            NullLogger<OrderSyncService>.Instance);

    private OrderActionService Actions(IFoodPlatformClient client) =>
        new(_tenants, [client], _secrets, NullTenantChangeNotifier.Instance, NullLogger<OrderActionService>.Instance);

    // A call gets the connection as Orders hands it over: through the host adapter, decrypting with _secrets.
    private Task<IReadOnlyCollection<ExternalOrderDto>> FetchAsync(
        IFoodPlatformClient client,
        PlatformConnection connection,
        OrderFetchWindow window) =>
        client.FetchOrdersAsync(connection.ToProviderContext(_secrets), window, Ct);

    private Task AcceptAsync(IFoodPlatformClient client, PlatformConnection connection, string externalOrderId) =>
        client.AcceptOrderAsync(connection.ToProviderContext(_secrets), externalOrderId, preparationMinutes: 0, Ct);

    private Task RejectAsync(IFoodPlatformClient client, PlatformConnection connection, Order order, int? reasonId) =>
        client.RejectOrderAsync(connection.ToProviderContext(_secrets), Reference(order), reasonId, Ct);

    /// <summary>Calls one lifecycle operation and returns its task without awaiting it.</summary>
    private Task Lifecycle(IFoodPlatformClient client, string operation, PlatformConnection connection, Order order)
    {
        var context = connection.ToProviderContext(_secrets);
        return operation switch
        {
            "accept" => client.AcceptOrderAsync(context, order.ExternalOrderId, preparationMinutes: 0, Ct),
            "invoiced" => client.MarkInvoicedAsync(context, order.ExternalOrderId, Ct),
            "shipped" => client.MarkShippedAsync(context, order.ExternalOrderId, Ct),
            "delivered" => client.MarkDeliveredAsync(context, order.ExternalOrderId, Ct),
            "reject" => client.RejectOrderAsync(context, Reference(order), reasonId: null, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static ProviderOrderReference Reference(Order order) => new(order.ExternalOrderId, order.RawPayloadJson);

    private sealed record SeenConnection(
        Guid ConnectionId,
        FoodPlatform Platform,
        string StoreId,
        string? SupplierId,
        string? ExecutorEmail,
        bool IsActive);

    private sealed record SeenCall(string Operation, SeenConnection Connection, string? ExternalOrderId, string? StoredPayload);

    private sealed class RecordingClient(FoodPlatform platform) : IFoodPlatformClient
    {
        private readonly ConcurrentQueue<SeenCall> _calls = new();

        public ProviderPlatform Platform => platform.ToProviderPlatform();
        public TimeSpan? MaxFetchWindow => null;
        public IReadOnlyList<SeenCall> Calls => _calls.ToArray();

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(ProviderConnectionContext connection, OrderFetchWindow window, CancellationToken ct)
        {
            _calls.Enqueue(new SeenCall("fetch", Seen(connection), null, null));
            return Task.FromResult<IReadOnlyCollection<ExternalOrderDto>>([]);
        }

        public Task AcceptOrderAsync(ProviderConnectionContext connection, string externalOrderId, int preparationMinutes, CancellationToken ct) =>
            Record("accept", connection, externalOrderId);

        public Task MarkInvoicedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) =>
            Record("invoiced", connection, externalOrderId);

        public Task MarkShippedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) =>
            Record("shipped", connection, externalOrderId);

        public Task MarkDeliveredAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) =>
            Record("delivered", connection, externalOrderId);

        public Task RejectOrderAsync(ProviderConnectionContext connection, ProviderOrderReference order, int? reasonId, CancellationToken ct)
        {
            _calls.Enqueue(new SeenCall("reject", Seen(connection), order.ExternalOrderId, order.StoredPayloadJson));
            return Task.CompletedTask;
        }

        private Task Record(string operation, ProviderConnectionContext connection, string externalOrderId)
        {
            _calls.Enqueue(new SeenCall(operation, Seen(connection), externalOrderId, null));
            return Task.CompletedTask;
        }

        private static SeenConnection Seen(ProviderConnectionContext connection) =>
            new(connection.ConnectionId, connection.Platform.ToFoodPlatform(), connection.StoreId, connection.SupplierId, connection.ExecutorEmail, connection.IsActive);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records every decryption in order and returns a plaintext marked as decrypted.</summary>
    private sealed class RecordingSecrets : ISecretManager
    {
        private readonly ConcurrentQueue<(string Cipher, int Version)> _decrypted = new();

        public IReadOnlyList<(string Cipher, int Version)> Decrypted => _decrypted.ToArray();

        public void Clear() => _decrypted.Clear();

        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            throw new NotSupportedException("These tests never encrypt.");

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct)
        {
            _decrypted.Enqueue((encryptedBase64, keyVersion));
            return Task.FromResult(Plain(encryptedBase64));
        }
    }

    private sealed class NoSideEffects : IOrderAutoApproveService, IOrderReceiptCreationService
    {
        public static readonly NoSideEffects Instance = new();

        public Task ProcessNewlyInsertedOrderAsync(Guid tenantId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) =>
            Task.CompletedTask;

        public Task TryCreateOnOrderAcceptedAsync(Guid tenantId, Guid orderId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
