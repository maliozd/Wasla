using System.Net.Http;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Platform.Mock;

// EN: Mock echoes use Trendyol GO-style package statuses so they align with DefaultOrderStatusMapper and the real client contract.
// TR: Mock yanıtları Trendyol GO paket statüleriyle uyumludur; DefaultOrderStatusMapper ve gerçek istemci sözleşmesiyle hizalanır.
public sealed class MockTrendyolYemekFoodPlatformClient : IFoodPlatformClient
{
    private readonly ISecretManager _secretManager;

    public MockTrendyolYemekFoodPlatformClient(ISecretManager secretManager)
    {
        _secretManager = secretManager;
    }

    public FoodPlatform Platform => FoodPlatform.TrendyolYemek;

    public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(300, 800), ct);

        if (Random.Shared.NextDouble() < 0.10)
        {
            throw new HttpRequestException("Mock transient failure");
        }

        _ = await _secretManager.DecryptAsync(connection.EncryptedApiKey, connection.EncryptionKeyVersion, ct);
        _ = await _secretManager.DecryptAsync(connection.EncryptedApiSecret, connection.EncryptionKeyVersion, ct);

        var count = Random.Shared.Next(0, 4);
        var list = MockOrders.CreateOrders(Platform, connection, count);
        return MockProviderOrderStatusStore.ApplyOverlays(Platform, list);
    }

    public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "Picking");
        return Task.CompletedTask;
    }

    public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "Invoiced");
        return Task.CompletedTask;
    }

    public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "Shipped");
        return Task.CompletedTask;
    }

    public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "Delivered");
        return Task.CompletedTask;
    }

    public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "UnSupplied");
        return Task.CompletedTask;
    }
}
