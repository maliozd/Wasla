using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Diagnostics;

namespace Wasla.Infrastructure.Platform.TrendyolGo;

public sealed class TrendyolGoFoodPlatformClient : IFoodPlatformClient
{
    /// <summary>Provider page size. Matches the historical packages request.</summary>
    internal const int FetchPageSize = 50;

    /// <summary>
    /// Defensive cap per fetch window. Twenty pages cover 1,000 packages modified within one window.
    /// </summary>
    internal const int MaxFetchPages = 20;

    /// <summary>
    /// Longest modification-time window one fetch requests. The official "Sipariş Paketlerini Çekme" documentation
    /// gives no maximum range, so a longer interval is split into consecutive windows of the one-hour span this
    /// client has always queried (see <c>OrderFetchWindowPlanner</c>).
    /// </summary>
    internal static readonly TimeSpan FetchWindowLength = TimeSpan.FromHours(1);

    /// <summary>
    /// Every <c>packageStatuses</c> value the official documentation lists, with its casing and in its order. The
    /// terminal statuses (Cancelled, UnSupplied, Delivered) are required: without them a cancellation or a delivery
    /// reported by Trendyol GO never reaches Wasla.
    /// </summary>
    internal const string PackageStatusesFilter = "Created,Picking,Invoiced,Cancelled,UnSupplied,Shipped,Delivered";

    /// <summary>
    /// Retries of the same page after HTTP 429. A page gets at most three attempts; the third 429 fails the window.
    /// </summary>
    internal const int MaxThrottledRetries = 2;

    /// <summary>Wait after a 429 without a usable <c>Retry-After</c>.</summary>
    internal static readonly TimeSpan DefaultThrottleDelay = TimeSpan.FromSeconds(10);

    /// <summary>Longest wait honoured from <c>Retry-After</c>; a longer value is shortened to this.</summary>
    internal static readonly TimeSpan MaxThrottleDelay = TimeSpan.FromSeconds(60);

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
    private readonly TrendyolRequestRateLimiter _requestLimiter;
    private readonly TimeProvider _time;

    /// <param name="requestLimiter">
    /// The process-wide singleton. Required: a client with its own limiter would have its own budget.
    /// </param>
    public TrendyolGoFoodPlatformClient(
        HttpClient httpClient,
        ISecretManager secretManager,
        IOrderStatusMapper statusMapper,
        IOptions<TrendyolGoOptions> options,
        ILogger<TrendyolGoFoodPlatformClient> logger,
        TrendyolRequestRateLimiter requestLimiter,
        TimeProvider time)
    {
        _httpClient = httpClient;
        _secretManager = secretManager;
        _statusMapper = statusMapper;
        _options = options;
        _logger = logger;
        _requestLimiter = requestLimiter;
        _time = time;
    }

    internal TrendyolRequestRateLimiter RequestLimiter => _requestLimiter;

    /// <summary>Named HttpClient key used during DI registration.</summary>
    public const string TrendyolGoHttpClientName = "TrendyolGo";

    public FoodPlatform Platform => FoodPlatform.TrendyolYemek;

    public TimeSpan? MaxFetchWindow => FetchWindowLength;

    private static string? ResolveSupplierId(PlatformConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.SupplierId)) return connection.SupplierId.Trim();
        if (!string.IsNullOrWhiteSpace(connection.StoreId)) return connection.StoreId.Trim();
        return null;
    }

    public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(
        PlatformConnection connection,
        OrderFetchWindow window,
        CancellationToken ct)
    {
        var supplierId = ResolveSupplierId(connection);
        if (string.IsNullOrWhiteSpace(supplierId))
            throw new InvalidOperationException("SupplierId could not be resolved (SupplierId and StoreId are empty)");

        if (window.EndUtc < window.StartUtc || window.EndUtc - window.StartUtc > FetchWindowLength)
            throw new ArgumentOutOfRangeException(nameof(window), "The fetch window must be ordered and at most FetchWindowLength long.");

        var startMs = ToUnixMilliseconds(window.StartUtc);
        var endMs = ToUnixMilliseconds(window.EndUtc);
        var collected = new List<ExternalOrderDto>();
        var seenPackages = new Dictionary<string, string>(StringComparer.Ordinal);
        int? previousTotalPages = null;
        var pagesFetched = 0;
        var swAll = Stopwatch.StartNew();

        for (var pageIndex = 0; pageIndex < MaxFetchPages; pageIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchPackagePageAsync(connection, supplierId, startMs, endMs, pageIndex, ct);
            pagesFetched++;

            if (page.TotalPages is < 0 || page.TotalCount is < 0 || page.Page is < 0)
                throw PaginationFault("invalid pagination metadata", pagesFetched, collected.Count, page.TotalPages);

            if (page.Page is int echoedPage && echoedPage != pageIndex)
                throw PaginationFault("response page did not advance", pagesFetched, collected.Count, page.TotalPages);

            var content = page.Content ?? [];
            _logger.LogDebug(
                "Provider page fetched. Provider={Provider} Operation={Operation} Page={Page} PageCount={PageCount} PageSize={PageSize} ReportedTotalPages={ReportedTotalPages}",
                "TrendyolGo",
                "FetchOrders",
                pageIndex,
                content.Count,
                FetchPageSize,
                page.TotalPages);

            if (content.Count == 0)
            {
                if (ClaimsFurtherPages(page.TotalPages, previousTotalPages, pageIndex))
                    throw PaginationFault("empty page while more pages were reported", pagesFetched, collected.Count, page.TotalPages ?? previousTotalPages);

                break;
            }

            AddPackages(content, collected, seenPackages, pagesFetched);

            if (!HasAnotherPage(page.TotalPages, pageIndex, content.Count, FetchPageSize))
                break;

            if (pageIndex + 1 >= MaxFetchPages)
                throw PaginationFault("page cap reached while more pages were reported", pagesFetched, collected.Count, page.TotalPages ?? previousTotalPages);

            previousTotalPages = page.TotalPages ?? previousTotalPages;
        }

        swAll.Stop();
        _logger.LogDebug(
            "Provider fetch completed. Provider={Provider} Operation={Operation} WindowStartUtc={WindowStartUtc:O} WindowEndUtc={WindowEndUtc:O} PagesFetched={PagesFetched} OrdersFetched={OrdersFetched} ElapsedMs={ElapsedMs}",
            "TrendyolGo",
            "FetchOrders",
            window.StartUtc,
            window.EndUtc,
            pagesFetched,
            collected.Count,
            swAll.ElapsedMilliseconds);

        return collected;
    }

    private async Task<TrendyolGoPackagesResponse> FetchPackagePageAsync(
        PlatformConnection connection,
        string supplierId,
        long startMs,
        long endMs,
        int pageIndex,
        CancellationToken ct)
    {
        var query = new List<string>
        {
            $"packageStatuses={PackageStatusesFilter}",
            $"size={FetchPageSize}",
            $"page={pageIndex}"
        };

        if (!string.IsNullOrWhiteSpace(connection.StoreId))
            query.Add($"storeId={Uri.EscapeDataString(connection.StoreId)}");

        query.Add($"packageModificationStartDate={startMs}");
        query.Add($"packageModificationEndDate={endMs}");

        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages?{string.Join("&", query)}";

        // Every attempt, including a 429 retry, takes a permit first. A 429 retries this same page.
        for (var attempt = 0; ; attempt++)
        {
            await _requestLimiter.AcquireAsync(ct);

            using var req = await BuildRequestAsync(HttpMethod.Get, path, connection, supplierId, ct);
            var sw = Stopwatch.StartNew();
            using var resp = await _httpClient.SendAsync(req, ct);
            sw.Stop();

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxThrottledRetries)
            {
                var (delay, source) = ThrottleDelay(resp.Headers.RetryAfter);
                _logger.LogWarning(
                    "Provider throttled the request; retrying the same page. Provider={Provider} Operation={Operation} StatusCode={StatusCode} Page={Page} Attempt={Attempt} RetryDelayMs={RetryDelayMs} RetryDelaySource={RetryDelaySource}",
                    "TrendyolGo",
                    "FetchOrders",
                    (int)resp.StatusCode,
                    pageIndex,
                    attempt + 1,
                    (long)delay.TotalMilliseconds,
                    source);
                _requestLimiter.Defer(delay);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                throw ProviderFailure("TrendyolGo", "FetchOrders", resp, sw.ElapsedMilliseconds, externalOrderId: null);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<TrendyolGoPackagesResponse>(stream, JsonOptions, ct)
                ?? new TrendyolGoPackagesResponse();
        }
    }

    /// <summary>
    /// <c>Retry-After</c> as seconds or as an HTTP date, kept between zero and <see cref="MaxThrottleDelay"/>;
    /// <see cref="DefaultThrottleDelay"/> when the header is missing or cannot be parsed.
    /// </summary>
    private (TimeSpan Delay, string Source) ThrottleDelay(RetryConditionHeaderValue? retryAfter)
    {
        TimeSpan? requested = retryAfter?.Delta;
        if (requested is null && retryAfter?.Date is { } date)
            requested = date - _time.GetUtcNow();

        if (requested is not { } value)
            return (DefaultThrottleDelay, "fallback");

        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        return (value > MaxThrottleDelay ? MaxThrottleDelay : value, "retry-after");
    }

    private void AddPackages(
        List<TrendyolGoPackage> content,
        List<ExternalOrderDto> collected,
        Dictionary<string, string> seenPackages,
        int pagesFetched)
    {
        foreach (var package in content)
        {
            var dto = MapToExternalOrderDto(package);
            if (string.IsNullOrWhiteSpace(dto.ExternalOrderId))
            {
                collected.Add(dto);
                continue;
            }

            var signature = PackageSignature(dto);
            if (seenPackages.TryGetValue(dto.ExternalOrderId, out var existing))
            {
                if (!string.Equals(existing, signature, StringComparison.Ordinal))
                {
                    throw PaginationFault(
                        "conflicting duplicate package",
                        pagesFetched,
                        ordersFetched: collected.Count,
                        reportedTotalPages: null);
                }

                _logger.LogDebug(
                    "Duplicate package skipped. Provider={Provider} Operation={Operation} ExternalOrderId={ExternalOrderId}",
                    "TrendyolGo",
                    "FetchOrders",
                    dto.ExternalOrderId);
                continue;
            }

            seenPackages[dto.ExternalOrderId] = signature;
            collected.Add(dto);
        }
    }

    private static long ToUnixMilliseconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static string PackageSignature(ExternalOrderDto dto) =>
        string.Join('\u001f', dto.ExternalStatus, dto.Total.ToString(System.Globalization.CultureInfo.InvariantCulture), dto.CustomerNote, dto.Items.Count);

    private static bool HasAnotherPage(int? totalPages, int pageIndex, int count, int pageSize)
    {
        if (totalPages is > 0)
            return pageIndex + 1 < totalPages.Value;

        return count >= pageSize;
    }

    private static bool ClaimsFurtherPages(int? totalPages, int? previousTotalPages, int pageIndex)
    {
        if (totalPages is > 0)
            return pageIndex + 1 < totalPages.Value;

        return previousTotalPages is > 0 && pageIndex + 1 < previousTotalPages.Value;
    }

    private InvalidOperationException PaginationFault(string reason, int pagesFetched, int ordersFetched, int? reportedTotalPages)
    {
        _logger.LogWarning(
            "Provider pagination failed. Provider={Provider} Operation={Operation} Reason={Reason} PagesFetched={PagesFetched} OrdersFetched={OrdersFetched} ReportedTotalPages={ReportedTotalPages}",
            "TrendyolGo",
            "FetchOrders",
            reason,
            pagesFetched,
            ordersFetched,
            reportedTotalPages);
        return new InvalidOperationException(
            $"TrendyolGo FetchOrders pagination failed: {reason}. PagesFetched={pagesFetched}.");
    }

    public async Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/picked";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { packageId = externalOrderId, preparationTime = preparationMinutes }, options: JsonOptions);
        await SendAndEnsureSuccessAsync(req, "AcceptOrder", externalOrderId, ct);
    }

    public async Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/invoiced";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { packageId = externalOrderId, actualDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, options: JsonOptions);
        await SendAndEnsureSuccessAsync(req, "MarkInvoiced", externalOrderId, ct);
    }

    public async Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/{externalOrderId}/manual-shipped";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { actualDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, options: JsonOptions);
        await SendAndEnsureSuccessAsync(req, "MarkShipped", externalOrderId, ct);
    }

    public async Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/{externalOrderId}/manual-delivered";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { actualDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, options: JsonOptions);
        await SendAndEnsureSuccessAsync(req, "MarkDelivered", externalOrderId, ct);
    }

    public async Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct)
    {
        var supplierId = EnsureSupplierAndExecutor(connection);
        var path = $"/integrator/order/meal/suppliers/{supplierId}/packages/unsupplied";
        using var req = await BuildRequestAsync(HttpMethod.Put, path, connection, supplierId, ct);
        req.Content = JsonContent.Create(new { packageId = externalOrderId, itemIdList, reasonId }, options: JsonOptions);
        await SendAndEnsureSuccessAsync(req, "RejectOrder", externalOrderId, ct);
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
            Items: items,
            CustomerNote: package.CustomerNote);
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

    private async Task SendAndEnsureSuccessAsync(
        HttpRequestMessage request,
        string operation,
        string? externalOrderId,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var resp = await _httpClient.SendAsync(request, ct);
        sw.Stop();
        if (resp.IsSuccessStatusCode)
            return;

        throw ProviderFailure("TrendyolGo", operation, resp, sw.ElapsedMilliseconds, externalOrderId);
    }

    private ProviderRequestException ProviderFailure(
        string provider,
        string operation,
        HttpResponseMessage response,
        long elapsedMs,
        string? externalOrderId)
    {
        var statusCode = (int)response.StatusCode;
        _logger.LogWarning(
            "Provider request failed. Provider={Provider} Operation={Operation} StatusCode={StatusCode} ElapsedMs={ElapsedMs} ExternalOrderId={ExternalOrderId}",
            provider,
            operation,
            statusCode,
            elapsedMs,
            externalOrderId);
        return new ProviderRequestException(provider, operation, statusCode);
    }

    // ---- internal DTOs (minimal) ----

    private sealed class TrendyolGoPackagesResponse
    {
        public int? Page { get; set; }
        public int? Size { get; set; }
        public int? TotalPages { get; set; }
        public int? TotalCount { get; set; }
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

