using System.Net.Http;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Platform.Mock;

public sealed class MockGetirYemekFoodPlatformClient : IFoodPlatformClient, IMockFoodPlatformClient
{
    public FoodPlatform Platform => FoodPlatform.GetirYemek;

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

    // EN: Mock echo tokens (VERIFY/PREPARE/ON_THE_WAY/…) align with verify/prepare/handover/cancel in this dev client—not confirmed production codes.
    // TR: Mock echo tokenları (VERIFY/PREPARE/…) bu dev istemcide verify/prepare/handover/cancel ile hizalanır—kesin production kodu değildir.
    // EN: Simplified Getir flow skips a separate ready endpoint; Wasla keeps ReadyForPickup as shared internal step before handover; PREPARE vs DB gap is handled by sync merge.
    // TR: Basitleştirilmiş Getir akışında ayrı ready endpoint yok; Wasla ReadyForPickup'i handover öncesi ortak iç adım tutar; PREPARE ile DB farkını sync merge kapatır.

    public Task AcceptOrderAsync(ProviderConnectionContext connection, string externalOrderId, int preparationMinutes, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "VERIFY");
        return Task.CompletedTask;
    }

    public Task MarkInvoicedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "PREPARE");
        return Task.CompletedTask;
    }

    public Task MarkShippedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct)
    {
        MockProviderOrderStatusStore.Set(Platform, externalOrderId, "ON_THE_WAY");
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
