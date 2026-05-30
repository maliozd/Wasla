using System.Net.Http;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Platform.Dtos;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;

namespace OrderHub.Infrastructure.Platform.Mock;

public sealed class MockYemeksepetiFoodPlatformClient : IFoodPlatformClient
{
    private readonly ISecretManager _secretManager;

    public MockYemeksepetiFoodPlatformClient(ISecretManager secretManager)
    {
        _secretManager = secretManager;
    }

    public FoodPlatform Platform => FoodPlatform.Yemeksepeti;

    public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(300, 800), ct);

        // ~10% transient platform failure
        if (Random.Shared.NextDouble() < 0.10)
        {
            throw new HttpRequestException("Mock transient failure");
        }

        // Decrypt credentials internally (do not log secrets)
        _ = await _secretManager.DecryptAsync(connection.EncryptedApiKey, connection.EncryptionKeyVersion, ct);
        _ = await _secretManager.DecryptAsync(connection.EncryptedApiSecret, connection.EncryptionKeyVersion, ct);

        var count = Random.Shared.Next(0, 4);
        var list = MockOrders.CreateOrders(Platform, connection, count);
        return MockProviderOrderStatusStore.ApplyOverlays(Platform, list);
    }

    public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct)
    {
        // EN: Partner-picking mock has no separate provider "accepted" string; OrderHub stores Accepted; echo stays RECEIVED until READY_FOR_PICKUP—sync merge blocks downgrade.
        // TR: Partner-picking mock'ta ayrı provider "accepted" string'i yok; Accepted OrderHub'da; echo READY_FOR_PICKUP'a kadar RECEIVED kalabilir—sync merge düşürmeyi engeller.
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "RECEIVED");
        return Task.CompletedTask;
    }

    public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "READY_FOR_PICKUP");
        return Task.CompletedTask;
    }

    public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "DISPATCHED");
        return Task.CompletedTask;
    }

    public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "DELIVERED");
        return Task.CompletedTask;
    }

    public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "CANCELLED");
        return Task.CompletedTask;
    }
}
