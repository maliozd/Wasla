using System.Net.Http;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Platform.Mock;

public sealed class MockYemeksepetiFoodPlatformClient : IFoodPlatformClient, IMockFoodPlatformClient
{
    public FoodPlatform Platform => FoodPlatform.Yemeksepeti;

    /// <summary>Mock orders are generated per run; the fetch window is not used.</summary>
    public TimeSpan? MaxFetchWindow => null;

    public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(ProviderConnectionContext connection, OrderFetchWindow window, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(300, 800), ct);

        // ~10% transient platform failure
        if (Random.Shared.NextDouble() < 0.10)
        {
            throw new HttpRequestException("Mock transient failure");
        }

        // Decrypt credentials internally (do not log secrets)
        _ = await connection.Credentials.GetCredentialsAsync(ct);

        var count = Random.Shared.Next(0, 4);
        var list = MockOrders.CreateOrders(Platform, connection, count);
        return MockProviderOrderStatusStore.ApplyOverlays(Platform, list);
    }

    public Task AcceptOrderAsync(ProviderConnectionContext connection, string externalOrderId, int preparationMinutes, CancellationToken ct)
    {
        // EN: Partner-picking mock has no separate provider "accepted" string; Wasla stores Preparing; echo stays RECEIVED until READY_FOR_PICKUP—sync merge blocks downgrade.
        // TR: Partner-picking mock'ta ayrı provider "accepted" string'i yok; Preparing Wasla'da; echo READY_FOR_PICKUP'a kadar RECEIVED kalabilir—sync merge düşürmeyi engeller.
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "RECEIVED");
        return Task.CompletedTask;
    }

    public Task MarkInvoicedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "READY_FOR_PICKUP");
        return Task.CompletedTask;
    }

    public Task MarkShippedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "DISPATCHED");
        return Task.CompletedTask;
    }

    public Task MarkDeliveredAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "DELIVERED");
        return Task.CompletedTask;
    }

    public Task RejectOrderAsync(ProviderConnectionContext connection, ProviderOrderReference order, int? reasonId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, order.ExternalOrderId, "CANCELLED");
        return Task.CompletedTask;
    }
}
