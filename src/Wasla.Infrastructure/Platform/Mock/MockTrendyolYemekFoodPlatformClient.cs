using System.Net.Http;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Platform.Mock;

// EN: Mock echoes use Trendyol GO-style package statuses so they align with DefaultOrderStatusMapper and the real client contract.
// TR: Mock yanıtları Trendyol GO paket statüleriyle uyumludur; DefaultOrderStatusMapper ve gerçek istemci sözleşmesiyle hizalanır.
public sealed class MockTrendyolYemekFoodPlatformClient : IFoodPlatformClient
{
    public FoodPlatform Platform => FoodPlatform.TrendyolYemek;

    /// <summary>Mock orders are generated per run; the fetch window is not used.</summary>
    public TimeSpan? MaxFetchWindow => null;

    public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(ProviderConnectionContext connection, OrderFetchWindow window, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(300, 800), ct);

        if (Random.Shared.NextDouble() < 0.10)
        {
            throw new HttpRequestException("Mock transient failure");
        }

        _ = await connection.Credentials.GetCredentialsAsync(ct);

        var count = Random.Shared.Next(0, 4);
        var list = MockOrders.CreateOrders(Platform, connection, count);
        return MockProviderOrderStatusStore.ApplyOverlays(Platform, list);
    }

    public Task AcceptOrderAsync(ProviderConnectionContext connection, string externalOrderId, int preparationMinutes, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "Picking");
        return Task.CompletedTask;
    }

    public Task MarkInvoicedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "Invoiced");
        return Task.CompletedTask;
    }

    public Task MarkShippedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "Shipped");
        return Task.CompletedTask;
    }

    public Task MarkDeliveredAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "Delivered");
        return Task.CompletedTask;
    }

    public Task RejectOrderAsync(ProviderConnectionContext connection, ProviderOrderReference order, int? reasonId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, order.ExternalOrderId, "UnSupplied");
        return Task.CompletedTask;
    }
}
