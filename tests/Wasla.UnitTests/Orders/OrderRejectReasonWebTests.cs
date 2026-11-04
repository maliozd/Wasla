using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Auth;
using Wasla.UnitTests.Realtime;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// <c>POST /orders/{id}/reject</c> through the real Web pipeline (<see cref="TenantSessionWebHost"/>: tenant resolution,
/// cookie session, role policies, antiforgery, the real <c>OrderActionService</c>) with recording provider clients.
/// The reason is checked against the stored order of the signed-in tenant before any provider call; a refused request
/// changes nothing.
/// </summary>
public sealed class OrderRejectReasonWebTests : IAsyncLifetime
{
    private readonly RecordingClient _trendyol = new(FoodPlatform.TrendyolYemek);
    private readonly RecordingClient _yemeksepeti = new(FoodPlatform.Yemeksepeti);
    private readonly List<TenantSessionClient> _clients = [];
    private TenantSessionWebHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await TenantSessionWebHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IFoodPlatformClient>();
            services.AddSingleton<IFoodPlatformClient>(_trendyol);
            services.AddSingleton<IFoodPlatformClient>(_yemeksepeti);
        });
        OrderChangeFixtures.SeedSyncableTenant(_host.Tenants, _host.AlphaId);
        OrderChangeFixtures.SeedSyncableTenant(_host.Tenants, _host.AlphaId, FoodPlatform.Yemeksepeti);
        OrderChangeFixtures.SeedSyncableTenant(_host.Tenants, _host.BetaId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            client.Dispose();
        await _host.DisposeAsync();
    }

    [Theory]
    [InlineData(621)]
    [InlineData(622)]
    [InlineData(623)]
    [InlineData(627)]
    public async Task TrendyolGo_WithASupportedReason_RejectsOnceWithThatReason(int reasonId)
    {
        var orderId = SeedNewOrder(_host.AlphaId, FoodPlatform.TrendyolYemek);
        var (client, token) = await SignInAsync(UserRole.Kitchen);

        var response = await RejectAsync(client, token, orderId, reasonId.ToString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ordersRejectSuccess", await MessageAsync(response));
        Assert.Equal(OrderStatus.Cancelled, await StatusAsync(_host.AlphaId, orderId));
        Assert.Equal([(orderId, (int?)reasonId)], _trendyol.Rejects);

        // Repeating it is refused by the status check; the provider is not called again.
        var again = await RejectAsync(client, token, orderId, reasonId.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("ordersInvalidStatusForAction", await MessageAsync(again));
        Assert.Single(_trendyol.Rejects);
    }

    [Theory]
    [InlineData(null, "ordersRejectReasonRequired")]
    [InlineData("", "ordersRejectReasonRequired")]
    [InlineData("624", "ordersRejectReasonNotSupported")]
    [InlineData("626", "ordersRejectReasonNotSupported")]
    [InlineData("0", "ordersRejectReasonNotSupported")]
    [InlineData("abc", "ordersRejectReasonNotSupported")]
    [InlineData("623.5", "ordersRejectReasonNotSupported")]
    public async Task TrendyolGo_WithAMissingOrUnsupportedReason_ChangesNothing(string? reasonId, string expectedMessage)
    {
        var orderId = SeedNewOrder(_host.AlphaId, FoodPlatform.TrendyolYemek);
        var (client, token) = await SignInAsync(UserRole.Owner);

        var response = await RejectAsync(client, token, orderId, reasonId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedMessage, await MessageAsync(response));
        Assert.Equal(OrderStatus.New, await StatusAsync(_host.AlphaId, orderId));
        Assert.Empty(_trendyol.Rejects);
    }

    [Fact]
    public async Task Yemeksepeti_KeepsItsRejectWithoutAReason()
    {
        var orderId = SeedNewOrder(_host.AlphaId, FoodPlatform.Yemeksepeti);
        var (client, token) = await SignInAsync(UserRole.Owner);

        var withReason = await RejectAsync(client, token, orderId, "623");
        Assert.Equal(HttpStatusCode.BadRequest, withReason.StatusCode);
        Assert.Equal("ordersRejectReasonNotSupported", await MessageAsync(withReason));
        Assert.Empty(_yemeksepeti.Rejects);

        // The request the Orders page and Live Screen send for it: no body.
        var response = await client.SendAsync(HttpMethod.Post, $"/orders/{orderId}/reject", content: null, Headers(token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([(orderId, (int?)null)], _yemeksepeti.Rejects);
        Assert.Equal(OrderStatus.Cancelled, await StatusAsync(_host.AlphaId, orderId));
    }

    /// <summary>
    /// Knowing another tenant's order id rejects nothing: the order is looked up in the signed-in tenant's database
    /// only, so the request fails like an unknown order and the other tenant's order and provider are untouched.
    /// </summary>
    [Fact]
    public async Task AnotherTenantsOrder_IsNotRejected()
    {
        var betaOrderId = SeedNewOrder(_host.BetaId, FoodPlatform.TrendyolYemek);
        var (alpha, token) = await SignInAsync(UserRole.Owner);

        var response = await RejectAsync(alpha, token, betaOrderId, "623");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ordersActionFailed", await MessageAsync(response));
        Assert.Equal(OrderStatus.New, await StatusAsync(_host.BetaId, betaOrderId));
        Assert.Empty(_trendyol.Rejects);
    }

    [Fact]
    public async Task WithoutTheAntiforgeryToken_NothingIsRejected()
    {
        var orderId = SeedNewOrder(_host.AlphaId, FoodPlatform.TrendyolYemek);
        var (client, _) = await SignInAsync(UserRole.Owner);

        var response = await client.PostFormAsync($"/orders/{orderId}/reject", [new("reasonId", "623")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(OrderStatus.New, await StatusAsync(_host.AlphaId, orderId));
        Assert.Empty(_trendyol.Rejects);
    }

    [Fact]
    public async Task AViewer_CannotReject()
    {
        var orderId = SeedNewOrder(_host.AlphaId, FoodPlatform.TrendyolYemek);
        // A Viewer may open the Live Screen, which carries a token for its own session.
        var (viewer, token) = await SignInAsync(UserRole.Viewer);

        var response = await RejectAsync(viewer, token, orderId, "623");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OrderStatus.New, await StatusAsync(_host.AlphaId, orderId));
        Assert.Empty(_trendyol.Rejects);
    }

    private Guid SeedNewOrder(Guid tenantId, FoodPlatform platform)
    {
        var orderId = Guid.NewGuid();
        var externalId = $"REJECT-{orderId:N}";
        var order = PrintServices.Order(orderId, tenantId, externalId);
        order.Platform = platform;
        order.IdempotencyKey = $"{platform}:{externalId}:{tenantId:N}";
        order.InternalStatus = OrderStatus.New;
        order.PlatformStatus = "Created";
        _host.Tenants.Seed(tenantId, db => db.Orders.Add(order));
        return orderId;
    }

    private async Task<(TenantSessionClient Client, string Token)> SignInAsync(UserRole role)
    {
        var user = _host.SeedUser(_host.AlphaId, $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@test.local", role);
        var client = _host.Client(TenantSessionWebHost.AlphaHost);
        _clients.Add(client);
        await client.LoginAsync(user.Email);
        return (client, await client.AntiforgeryTokenAsync("/orders/live-display"));
    }

    // As orders-actions.js sends it: the token in the RequestVerificationToken header and the reason as a form field.
    private static Task<HttpResponseMessage> RejectAsync(TenantSessionClient client, string token, Guid orderId, string? reasonId) =>
        client.SendAsync(
            HttpMethod.Post,
            $"/orders/{orderId}/reject",
            reasonId is null ? new FormUrlEncodedContent([]) : new FormUrlEncodedContent([new("reasonId", reasonId)]),
            Headers(token));

    private static KeyValuePair<string, string>[] Headers(string token) =>
        [new("RequestVerificationToken", token), new("X-Requested-With", "XMLHttpRequest")];

    private static async Task<string?> MessageAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("message").GetString();

    private async Task<OrderStatus> StatusAsync(Guid tenantId, Guid orderId)
    {
        await using var db = await _host.Tenants.CreateAsync(tenantId, Ct);
        return await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.InternalStatus).SingleAsync(Ct);
    }

    private sealed class RecordingClient(FoodPlatform platform) : IFoodPlatformClient
    {
        private readonly ConcurrentQueue<(Guid OrderId, int? ReasonId)> _rejects = new();

        public FoodPlatform Platform => platform;
        public TimeSpan? MaxFetchWindow => null;
        public IReadOnlyList<(Guid OrderId, int? ReasonId)> Rejects => _rejects.ToArray();

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<ExternalOrderDto>>([]);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;

        public Task RejectOrderAsync(PlatformConnection connection, Order order, int? reasonId, CancellationToken ct)
        {
            _rejects.Enqueue((order.Id, reasonId));
            return Task.CompletedTask;
        }
    }
}
