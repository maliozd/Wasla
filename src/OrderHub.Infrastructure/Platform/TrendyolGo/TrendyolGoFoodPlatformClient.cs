using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Platform.Dtos;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;

namespace OrderHub.Infrastructure.Platform.TrendyolGo;

public sealed class TrendyolGoFoodPlatformClient : IFoodPlatformClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ISecretManager _secretManager;
    private readonly IOrderStatusMapper _statusMapper;
    private readonly IOptions<TrendyolGoOptions> _options;
    private readonly ILogger<TrendyolGoFoodPlatformClient> _logger;

    public TrendyolGoFoodPlatformClient(
        HttpClient httpClient,
        ISecretManager secretManager,
        IOrderStatusMapper statusMapper,
        IOptions<TrendyolGoOptions> options,
        ILogger<TrendyolGoFoodPlatformClient> logger)
    {
        _httpClient = httpClient;
        _secretManager = secretManager;
        _statusMapper = statusMapper;
        _options = options;
        _logger = logger;
    }

    public FoodPlatform Platform => FoodPlatform.TrendyolYemek;

    private static string? ResolveSupplierId(PlatformConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.SupplierId)) return connection.SupplierId.Trim();
        if (!string.IsNullOrWhiteSpace(connection.StoreId)) return connection.StoreId.Trim();
        return null;
    }

    public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, CancellationToken ct)
    {
        var supplierId = ResolveSupplierId(connection);
        if (string.IsNullOrWhiteSpace(supplierId))
            throw new InvalidOperationException("SupplierId could not be resolved (SupplierId and StoreId are empty)");

        var query = new List<string>
        {
            "packageStatuses=Created,Picking,Invoiced,Shipped",
            "size=50",
            "page=0"
        };

        if (!string.IsNullOrWhiteSpace(connection.StoreId))
            query.Add($"storeId={Uri.EscapeDataString(connection.StoreId)}");

        var sinceMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        query.Add($"packageModificationStartDate={sinceMs}");

        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages?{string.Join("&", query)}";

        using var req = await BuildRequestAsync(HttpMethod.Get, path, connection, supplierId, ct);
        using var resp = await _httpClient.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"TrendyolGo fetch failed: {(int)resp.StatusCode} {resp.StatusCode}. Body: {Truncate(body, 500)}",
                null,
                resp.StatusCode);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var page = await JsonSerializer.DeserializeAsync<TrendyolGoPackagesResponse>(stream, JsonOptions, ct);
        if (page?.Content is null || page.Content.Count == 0) return Array.Empty<ExternalOrderDto>();

        return page.Content.Select(p => MapToExternalOrderDto(p)).ToList();
    }

    public async Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/picked";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { packageId = externalOrderId, preparationTime = preparationMinutes }, options: JsonOptions);
        using var resp = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, "AcceptOrder", externalOrderId, ct);
    }

    public async Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/invoiced";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { packageId = externalOrderId, actualDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, options: JsonOptions);
        using var resp = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, "MarkInvoiced", externalOrderId, ct);
    }

    public async Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/{externalOrderId}/manual-shipped";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { actualDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, options: JsonOptions);
        using var resp = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, "MarkShipped", externalOrderId, ct);
    }

    public async Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/{externalOrderId}/manual-delivered";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { actualDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, options: JsonOptions);
        using var resp = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, "MarkDelivered", externalOrderId, ct);
    }

    public async Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/unsupplied";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { packageId = externalOrderId, itemIdList, reasonId }, options: JsonOptions);
        using var resp = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, "RejectOrder", externalOrderId, ct);
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string path, PlatformConnection connection, string supplierId, CancellationToken ct)
    {
        var apiKey = await _secretManager.DecryptAsync(connection.EncryptedApiKey, connection.EncryptionKeyVersion, ct);
        var apiSecret = await _secretManager.DecryptAsync(connection.EncryptedApiSecret, connection.EncryptionKeyVersion, ct);
        var basicCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"));

        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredentials);
        req.Headers.Add("x-agentname", _options.Value.AgentName);

        req.Headers.Add("x-executor-user", connection.ExecutorEmail
            ?? throw new InvalidOperationException("ExecutorEmail not set on PlatformConnection"));

        req.Headers.UserAgent.Clear();
        req.Headers.TryAddWithoutValidation("User-Agent", $"{supplierId} - {_options.Value.AgentName}");

        return req;
    }

    private string EnsureSupplierAndExecutor(PlatformConnection connection)
    {
        var supplierId = ResolveSupplierId(connection);
        if (string.IsNullOrWhiteSpace(supplierId))
            throw new InvalidOperationException("SupplierId could not be resolved (SupplierId and StoreId are empty)");
        if (string.IsNullOrWhiteSpace(connection.ExecutorEmail))
            throw new InvalidOperationException("ExecutorEmail not set on PlatformConnection");
        return supplierId;
    }

    private ExternalOrderDto MapToExternalOrderDto(TrendyolGoPackage package)
    {
        var customerName = $"{package.Customer?.FirstName} {package.Customer?.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(customerName)) customerName = "Customer";

        var phone = package.Address?.Phone ?? string.Empty;
        var address = BuildAddress(package.Address);

        var orderedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(package.PackageCreationDate).UtcDateTime;

        var internalStatus = _statusMapper.MapToInternalStatus(FoodPlatform.TrendyolYemek, package.PackageStatus ?? string.Empty);

        var (paymentMethod, paymentStatus) = MapPayment(package.Payment);

        var items = (package.Lines ?? new List<TrendyolGoLine>())
            .Select((line, idx) => MapLine(package.Id, line, idx))
            .Where(i => i.Quantity > 0)
            .ToList();

        var subtotal = items.Sum(i => i.TotalPrice);

        return new ExternalOrderDto(
            Platform: FoodPlatform.TrendyolYemek,
            ExternalOrderId: package.Id ?? string.Empty,
            ExternalOrderCode: package.OrderNumber ?? package.OrderCode ?? package.Id ?? string.Empty,
            OrderedAtUtc: orderedAtUtc,
            CustomerName: customerName,
            CustomerPhone: phone,
            CustomerAddress: address,
            Subtotal: subtotal,
            DeliveryFee: 0m,
            ServiceFee: 0m,
            Total: package.TotalPrice,
            PaymentMethod: paymentMethod,
            PaymentStatus: paymentStatus,
            ExternalStatus: package.PackageStatus ?? string.Empty,
            RawPayloadJson: JsonSerializer.Serialize(package, JsonOptions),
            Items: items);
    }

    private static ExternalOrderItemDto MapLine(string? packageId, TrendyolGoLine line, int idx)
    {
        var items = line.Items ?? new List<TrendyolGoLineItem>();
        var qty = items.Count(i => i is { IsCancelled: false });

        var options = new List<ExternalOrderItemOptionDto>();
        if (line.ModifierProducts is { Count: > 0 })
        {
            foreach (var mod in line.ModifierProducts)
            {
                FlattenModifier(mod, prefix: string.Empty, options);
            }
        }

        var extId = $"{packageId}-line-{idx + 1}";
        var unit = line.UnitSellingPrice;
        var total = unit * qty;

        return new ExternalOrderItemDto(
            ExternalItemId: extId,
            ProductName: line.Name ?? "Item",
            Quantity: qty,
            UnitPrice: unit,
            TotalPrice: total,
            Notes: null,
            Options: options);
    }

    private static void FlattenModifier(TrendyolGoModifierProduct mod, string prefix, List<ExternalOrderItemOptionDto> options)
    {
        var name = mod.Name ?? "Modifier";
        var current = string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix} > {name}";

        if (mod.ModifierProducts is { Count: > 0 })
        {
            foreach (var child in mod.ModifierProducts)
                FlattenModifier(child, current, options);
        }
        else
        {
            // leaf
            options.Add(new ExternalOrderItemOptionDto(current, mod.Price));
        }

        if (mod.ExtraIngredients is { Count: > 0 })
        {
            foreach (var x in mod.ExtraIngredients)
                options.Add(new ExternalOrderItemOptionDto($"{current} > {x.Name}", x.Price));
        }

        if (mod.RemovedIngredients is { Count: > 0 })
        {
            foreach (var r in mod.RemovedIngredients)
                options.Add(new ExternalOrderItemOptionDto($"{current} > {r.Name} (çıkarıldı)", 0m));
        }
    }

    private static (PaymentMethod Method, PaymentStatus Status) MapPayment(TrendyolGoPayment? payment)
    {
        var type = payment?.PaymentType ?? string.Empty;
        if (string.Equals(type, "PAY_WITH_CARD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "PAY_WITH_MEAL_CARD", StringComparison.OrdinalIgnoreCase))
        {
            return (PaymentMethod.OnlinePayment, PaymentStatus.Paid);
        }

        if (string.Equals(type, "PAY_WITH_ON_DELIVERY", StringComparison.OrdinalIgnoreCase))
        {
            var onDeliveryType = payment?.OnDelivery?.PaymentType ?? string.Empty;
            if (string.Equals(onDeliveryType, "CASH", StringComparison.OrdinalIgnoreCase))
                return (PaymentMethod.Cash, PaymentStatus.Pending);
            if (string.Equals(onDeliveryType, "CARD", StringComparison.OrdinalIgnoreCase))
                return (PaymentMethod.CreditCard, PaymentStatus.Pending);
            return (PaymentMethod.Unknown, PaymentStatus.Pending);
        }

        return (PaymentMethod.Unknown, PaymentStatus.Unknown);
    }

    private static string BuildAddress(TrendyolGoAddress? a)
    {
        if (a is null) return string.Empty;
        if (string.Equals(a.Address1, "Trendyol Yemek", StringComparison.OrdinalIgnoreCase)) return string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.Address1)) parts.Add(a.Address1);
        if (!string.IsNullOrWhiteSpace(a.Neighborhood)) parts.Add(a.Neighborhood);
        if (!string.IsNullOrWhiteSpace(a.City)) parts.Add(a.City);
        return string.Join(", ", parts);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage resp, string operation, string externalOrderId, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var msg = $"TrendyolGo {operation} failed for package {externalOrderId}: {(int)resp.StatusCode}. Body: {Truncate(body, 500)}";
        _logger.LogError("{Message}", msg);
        throw new HttpRequestException(msg, null, resp.StatusCode);
    }

    private static string Truncate(string s, int maxLen) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= maxLen ? s : s[..maxLen]);

    // ---- internal DTOs (minimal) ----

    private sealed class TrendyolGoPackagesResponse
    {
        public int Page { get; set; }
        public int Size { get; set; }
        public int TotalPages { get; set; }
        public int TotalCount { get; set; }
        public List<TrendyolGoPackage>? Content { get; set; }
    }

    private sealed class TrendyolGoPackage
    {
        public string? Id { get; set; }
        public string? OrderCode { get; set; }
        public string? OrderNumber { get; set; }
        public long PackageCreationDate { get; set; }
        public string? PackageStatus { get; set; }
        public decimal TotalPrice { get; set; }
        public TrendyolGoCustomer? Customer { get; set; }
        public TrendyolGoAddress? Address { get; set; }
        public List<TrendyolGoLine>? Lines { get; set; }
        public TrendyolGoPayment? Payment { get; set; }
        public string? CustomerNote { get; set; }
    }

    private sealed class TrendyolGoCustomer
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
    }

    private sealed class TrendyolGoAddress
    {
        public string? Phone { get; set; }
        public string? Address1 { get; set; }
        public string? City { get; set; }
        public string? Neighborhood { get; set; }
    }

    private sealed class TrendyolGoLine
    {
        public string? Name { get; set; }
        public decimal UnitSellingPrice { get; set; }
        public List<TrendyolGoLineItem>? Items { get; set; }
        public List<TrendyolGoModifierProduct>? ModifierProducts { get; set; }
    }

    private sealed class TrendyolGoLineItem
    {
        public bool IsCancelled { get; set; }
        public string? PackageItemId { get; set; }
        public string? LineItemId { get; set; }
    }

    private sealed class TrendyolGoModifierProduct
    {
        public string? Name { get; set; }
        public decimal Price { get; set; }
        public List<TrendyolGoModifierProduct>? ModifierProducts { get; set; }
        public List<TrendyolGoIngredient>? ExtraIngredients { get; set; }
        public List<TrendyolGoIngredient>? RemovedIngredients { get; set; }
    }

    private sealed class TrendyolGoIngredient
    {
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    private sealed class TrendyolGoPayment
    {
        public string? PaymentType { get; set; }
        public TrendyolGoOnDelivery? OnDelivery { get; set; }
        public TrendyolGoMealCard? MealCard { get; set; }
    }

    private sealed class TrendyolGoOnDelivery
    {
        public string? PaymentType { get; set; }
    }

    private sealed class TrendyolGoMealCard
    {
        public string? CardSourceType { get; set; }
    }
}

