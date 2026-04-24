using System.Net.Http;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Platform.Dtos;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;

namespace OrderHub.Infrastructure.Platform.Mock;

public sealed class YemeksepetiFoodPlatformClient : IFoodPlatformClient
{
    private readonly ISecretManager _secretManager;

    public YemeksepetiFoodPlatformClient(ISecretManager secretManager)
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
        return MockOrders.CreateOrders(Platform, connection, count);
    }
}

