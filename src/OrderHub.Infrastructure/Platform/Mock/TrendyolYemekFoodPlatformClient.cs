using System.Net.Http;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Platform.Dtos;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;

namespace OrderHub.Infrastructure.Platform.Mock;

public sealed class TrendyolYemekFoodPlatformClient : IFoodPlatformClient
{
    private readonly ISecretManager _secretManager;

    public TrendyolYemekFoodPlatformClient(ISecretManager secretManager)
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
        return MockOrders.CreateOrders(Platform, connection, count);
    }

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

