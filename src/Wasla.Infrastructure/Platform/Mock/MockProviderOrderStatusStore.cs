using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Platform.Mock;

// EN: In-memory overlay keeps mock fetch echoes aligned with the last manual lifecycle action per external id—avoids confusing default CREATED/RECEIVED/Created strings in local dev.
// TR: Bellek içi overlay; mock fetch son manuel lifecycle aksiyonunu yansıtır—lokal geliştirmede varsayılan CREATED/RECEIVED/Created ile OrderHub durumunun çakışmasını azaltır.
internal static class MockProviderOrderStatusStore
{
    private static readonly ConcurrentDictionary<string, string> StatusByKey = new();

    private static string Key(FoodPlatform platform, string externalOrderId) => $"{(int)platform}\u001f{externalOrderId}";

    public static void Set(FoodPlatform platform, string externalOrderId, string externalStatus)
    {
        if (string.IsNullOrWhiteSpace(externalOrderId) || string.IsNullOrWhiteSpace(externalStatus)) return;
        StatusByKey[Key(platform, externalOrderId)] = externalStatus;
    }

    public static IReadOnlyList<ExternalOrderDto> ApplyOverlays(
        FoodPlatform platform,
        IReadOnlyCollection<ExternalOrderDto> orders)
    {
        if (orders.Count == 0) return Array.Empty<ExternalOrderDto>();
        if (StatusByKey.IsEmpty) return new List<ExternalOrderDto>(orders);

        var list = new List<ExternalOrderDto>(orders.Count);
        foreach (var o in orders)
        {
            if (StatusByKey.TryGetValue(Key(platform, o.ExternalOrderId), out var st) && !string.IsNullOrEmpty(st))
            {
                list.Add(o with
                {
                    ExternalStatus = st,
                    RawPayloadJson = TryPatchRawPayloadExternalStatus(o.RawPayloadJson, st)
                });
            }
            else
            {
                list.Add(o);
            }
        }

        return list;
    }

    private static string TryPatchRawPayloadExternalStatus(string raw, string newStatus)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        try
        {
            var node = JsonNode.Parse(raw) as JsonObject;
            if (node is null) return raw;
            if (node["externalStatus"] is not null) node["externalStatus"] = newStatus;
            return node.ToJsonString();
        }
        catch
        {
            return raw;
        }
    }
}
