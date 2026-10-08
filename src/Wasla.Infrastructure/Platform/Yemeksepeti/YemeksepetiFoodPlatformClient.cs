using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Diagnostics;

namespace Wasla.Infrastructure.Platform.Yemeksepeti;

/// <summary>
/// Uses Yemeksepeti Partner Picking Orders API.
/// Authenticates via OAuth2 client_credentials and fetches vendor orders.
/// Lifecycle fulfillment endpoints (accept/reject/ship/deliver) are not yet implemented.
/// </summary>
public sealed class YemeksepetiFoodPlatformClient : IFoodPlatformClient
{
    /// <summary>
    /// Defensive cap. Fifty pages at the default page size of 20 cover 1,000 orders
    /// in the one-hour fetch window.
    /// </summary>
    internal const int MaxFetchPages = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ISecretManager _secretManager;
    private readonly IOrderStatusMapper _statusMapper;
    private readonly YemeksepetiOptions _options;
    private readonly ILogger<YemeksepetiFoodPlatformClient> _logger;
    private readonly TimeProvider _time;

    /// <summary>
    /// Upper bound on cached tokens. Once no acquisition is in progress, the cache holds only entries with a token and
    /// at most this many. While acquisitions run, each one adds at most one entry on top. A failed or cancelled
    /// acquisition leaves no entry. Expired entries are pruned whenever a token is acquired. Past this bound a new
    /// token is used for its own fetch only and is not cached.
    /// </summary>
    internal const int MaxCachedTokens = 4096;

    // Tokens are valid for ~2 hours; we refresh 5 minutes early to avoid expiry during a request.
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    // This client is a process-wide singleton shared by every tenant, so a cached token is bound to the platform
    // connection, the token endpoint and the exact credentials that obtained it (see TokenCacheKey). The key is an
    // HMAC under a random per-instance key: it holds no credential text and is useless outside this process.
    private readonly ConcurrentDictionary<string, TokenSlot> _tokenCache = new(StringComparer.Ordinal);
    private readonly byte[] _tokenCacheKeySecret = RandomNumberGenerator.GetBytes(32);

    public YemeksepetiFoodPlatformClient(
        IHttpClientFactory httpClientFactory,
        ISecretManager secretManager,
        IOrderStatusMapper statusMapper,
        IOptions<YemeksepetiOptions> options,
        ILogger<YemeksepetiFoodPlatformClient> logger,
        TimeProvider? time = null)
    {
        _httpClient = httpClientFactory.CreateClient(YemeksepetiHttpClientName);
        _secretManager = secretManager;
        _statusMapper = statusMapper;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Number of cached token entries, for tests of the cache bound.</summary>
    internal int CachedTokenCount => _tokenCache.Count;

    /// <summary>Named HttpClient key used during DI registration.</summary>
    public const string YemeksepetiHttpClientName = "Yemeksepeti";

    public FoodPlatform Platform => FoodPlatform.Yemeksepeti;

    /// <summary>
    /// Not checkpointed yet: every fetch still asks for the last hour and ignores the sync window.
    /// </summary>
    public TimeSpan? MaxFetchWindow => null;

    // ── Credential resolution ─────────────────────────────────────────────────

    /// <summary>
    /// PlatformConnection field mapping for Yemeksepeti:
    ///   SupplierId  → chainId  (required)
    ///   StoreId     → vendorId (required; falls back to SupplierId if not set)
    ///   EncryptedApiKey    → OAuth2 clientId
    ///   EncryptedApiSecret → OAuth2 clientSecret
    /// </summary>
    private static (string ChainId, string VendorId) ResolveIds(PlatformConnection connection)
    {
        var chainId = connection.SupplierId?.Trim();
        if (string.IsNullOrWhiteSpace(chainId))
            throw new InvalidOperationException(
                $"Yemeksepeti requires SupplierId (chainId) on PlatformConnection {connection.Id}. Set it in the admin panel.");

        var vendorId = !string.IsNullOrWhiteSpace(connection.StoreId)
            ? connection.StoreId.Trim()
            : chainId;

        return (chainId, vendorId);
    }

    // ── Fetch orders ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(
        PlatformConnection connection, OrderFetchWindow window, CancellationToken ct)
    {
        var (chainId, vendorId) = ResolveIds(connection);

        var clientId = await _secretManager.DecryptAsync(
            connection.EncryptedApiKey, connection.EncryptionKeyVersion, ct);
        var clientSecret = await _secretManager.DecryptAsync(
            connection.EncryptedApiSecret, connection.EncryptionKeyVersion, ct);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException(
                $"Yemeksepeti credentials (clientId/clientSecret) are missing for PlatformConnection {connection.Id}.");

        var cacheKey = TokenCacheKey(connection.Id, clientId, clientSecret);
        var token = await GetOrRefreshTokenAsync(connection.Id, cacheKey, clientId, clientSecret, ct);
        try
        {
            return await FetchOrdersWithTokenAsync(chainId, vendorId, token.AccessToken, ct);
        }
        catch (ProviderRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // A rejected token is not reused: the next attempt (including a sync retry) asks for a new one.
            EvictToken(cacheKey, token);
            _logger.LogInformation(
                "Yemeksepeti token rejected; cached token discarded. ConnectionId={ConnectionId} StatusCode={StatusCode}",
                connection.Id,
                (int)ex.StatusCode);
            throw;
        }
    }

    private async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersWithTokenAsync(
        string chainId, string vendorId, string token, CancellationToken ct)
    {
        var pageSize = _options.DefaultPageSize;
        if (pageSize <= 0)
            throw new InvalidOperationException("Yemeksepeti DefaultPageSize must be positive.");

        var now = DateTimeOffset.UtcNow;
        // TODO: Honor the checkpointed OrderFetchWindow (set MaxFetchWindow) once the Partner API range limits are confirmed; for now use a safe 1-hour lookback.
        var startTime = now.AddHours(-1).ToUnixTimeMilliseconds();
        var endTime = now.ToUnixTimeMilliseconds();

        _logger.LogDebug(
            "Yemeksepeti fetch started. ChainId={ChainId} VendorId={VendorId} StartTime={Start} EndTime={End} PageSize={PageSize}",
            chainId, vendorId, startTime, endTime, pageSize);

        var collected = new List<ExternalOrderDto>();
        var seenOrders = new Dictionary<string, string>(StringComparer.Ordinal);
        int? previousTotalPages = null;
        var pagesFetched = 0;
        var swAll = Stopwatch.StartNew();

        for (var pageIndex = 0; pageIndex < MaxFetchPages; pageIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchOrderPageAsync(chainId, vendorId, token, startTime, endTime, pageSize, pageIndex, ct);
            pagesFetched++;

            if (page.TotalPages is < 0 || page.TotalCount is < 0 || page.Page is < 0)
                throw PaginationFault("invalid pagination metadata", pagesFetched, collected.Count, page.TotalPages);

            if (page.Page is int echoedPage && echoedPage != pageIndex)
                throw PaginationFault("response page did not advance", pagesFetched, collected.Count, page.TotalPages);

            var data = page.Data ?? [];
            _logger.LogDebug(
                "Provider page fetched. Provider={Provider} Operation={Operation} Page={Page} PageCount={PageCount} PageSize={PageSize} ReportedTotalPages={ReportedTotalPages}",
                "Yemeksepeti",
                "FetchOrders",
                pageIndex,
                data.Count,
                pageSize,
                page.TotalPages);

            if (data.Count == 0)
            {
                if (ClaimsFurtherPages(page.TotalPages, previousTotalPages, pageIndex))
                    throw PaginationFault("empty page while more pages were reported", pagesFetched, collected.Count, page.TotalPages ?? previousTotalPages);

                break;
            }

            AddOrders(data, collected, seenOrders, pagesFetched);

            if (!HasAnotherPage(page.TotalPages, pageIndex, data.Count, pageSize))
                break;

            if (pageIndex + 1 >= MaxFetchPages)
                throw PaginationFault("page cap reached while more pages were reported", pagesFetched, collected.Count, page.TotalPages ?? previousTotalPages);

            previousTotalPages = page.TotalPages ?? previousTotalPages;
        }

        swAll.Stop();
        _logger.LogDebug(
            "Yemeksepeti fetch completed. ChainId={ChainId} VendorId={VendorId} PagesFetched={PagesFetched} OrdersFetched={OrdersFetched} Count={Count} ElapsedMs={ElapsedMs}",
            chainId, vendorId, pagesFetched, collected.Count, collected.Count, swAll.ElapsedMilliseconds);

        return collected;
    }

    private async Task<YemeksepetiOrdersResponse> FetchOrderPageAsync(
        string chainId,
        string vendorId,
        string token,
        long startTime,
        long endTime,
        int pageSize,
        int pageIndex,
        CancellationToken ct)
    {
        // TODO: Confirm exact orders endpoint path with Yemeksepeti Partner API docs.
        // Documented shape: GET /v2/chains/{chainId}/vendors/{vendorId}/orders
        var path = $"/v2/chains/{Uri.EscapeDataString(chainId)}/vendors/{Uri.EscapeDataString(vendorId)}/orders" +
                   $"?start_time={startTime}&end_time={endTime}&page_size={pageSize}&page={pageIndex}";

        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var sw = Stopwatch.StartNew();
        using var resp = await _httpClient.SendAsync(req, ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            throw LogProviderFailure("Yemeksepeti", "FetchOrders", (int)resp.StatusCode, sw.ElapsedMilliseconds);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<YemeksepetiOrdersResponse>(stream, JsonOptions, ct)
            ?? new YemeksepetiOrdersResponse();
    }

    private void AddOrders(
        List<YemeksepetiOrder> data,
        List<ExternalOrderDto> collected,
        Dictionary<string, string> seenOrders,
        int pagesFetched)
    {
        foreach (var order in data)
        {
            var dto = MapToExternalOrderDto(order);
            if (string.IsNullOrWhiteSpace(dto.ExternalOrderId))
            {
                collected.Add(dto);
                continue;
            }

            var signature = string.Join(
                '\u001f',
                dto.ExternalStatus,
                dto.Total.ToString(System.Globalization.CultureInfo.InvariantCulture),
                dto.CustomerNote,
                dto.Items.Count);
            if (seenOrders.TryGetValue(dto.ExternalOrderId, out var existing))
            {
                if (!string.Equals(existing, signature, StringComparison.Ordinal))
                    throw PaginationFault("conflicting duplicate order", pagesFetched, collected.Count, null);

                _logger.LogDebug(
                    "Duplicate order skipped. Provider={Provider} Operation={Operation} ExternalOrderId={ExternalOrderId}",
                    "Yemeksepeti",
                    "FetchOrders",
                    dto.ExternalOrderId);
                continue;
            }

            seenOrders[dto.ExternalOrderId] = signature;
            collected.Add(dto);
        }
    }

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
            "Yemeksepeti",
            "FetchOrders",
            reason,
            pagesFetched,
            ordersFetched,
            reportedTotalPages);
        return new InvalidOperationException(
            $"Yemeksepeti FetchOrders pagination failed: {reason}. PagesFetched={pagesFetched}.");
    }

    // ── Token handling ────────────────────────────────────────────────────────

    /// <summary>
    /// Isolation key of a cached token: HMAC-SHA256 over the platform connection id, the token endpoint, the client id
    /// and the client secret, each length-prefixed so no two different tuples encode the same input.
    /// A token is therefore reused only by the connection that obtained it, and only while it presents the same
    /// credentials to the same endpoint. Another connection, a rotated secret or the same client id with another
    /// secret gets its own entry and its own token request.
    /// </summary>
    private string TokenCacheKey(Guid connectionId, string clientId, string clientSecret)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _tokenCacheKeySecret);
        AppendField(hmac, connectionId.ToString("N"));
        AppendField(hmac, _httpClient.BaseAddress?.AbsoluteUri ?? string.Empty);
        AppendField(hmac, _options.TokenPath);
        AppendField(hmac, clientId);
        AppendField(hmac, clientSecret);
        return Convert.ToHexString(hmac.GetHashAndReset());
    }

    private static void AppendField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private async Task<CachedToken> GetOrRefreshTokenAsync(
        Guid connectionId, string cacheKey, string clientId, string clientSecret, CancellationToken ct)
    {
        var slot = EnterSlot(cacheKey);
        try
        {
            if (TryGetReusable(slot, out var cached))
                return cached;

            // One token request per key at a time: concurrent fetches of the same connection wait for it instead of
            // each asking the provider. Different keys never wait for each other. A wait cancelled here never
            // acquired the gate, so it must not release it.
            await slot.Gate.WaitAsync(ct);
            try
            {
                if (TryGetReusable(slot, out cached))
                    return cached;

                var token = await RequestTokenAsync(connectionId, clientId, clientSecret, ct);
                slot.Token = token;
                PruneTokenCache();
                if (_tokenCache.Count > MaxCachedTokens)
                {
                    // Not kept: the slot is retired when its last user leaves, unless a later owner caches a token.
                    slot.Invalidate(token);
                    _logger.LogWarning(
                        "Yemeksepeti token cache is full; the token is used for this fetch only. ConnectionId={ConnectionId} MaxCachedTokens={MaxCachedTokens}",
                        connectionId,
                        MaxCachedTokens);
                }

                return token;
            }
            finally
            {
                slot.Gate.Release();
            }
        }
        finally
        {
            // A failed or cancelled acquisition leaves the slot without a token; the last user to leave removes it.
            ExitSlot(cacheKey, slot);
        }
    }

    // Registers the caller as a user of the key's slot. A retired slot has already been removed from the cache under
    // its lock, so looking again returns the slot that replaced it or creates one.
    private TokenSlot EnterSlot(string cacheKey)
    {
        while (true)
        {
            var slot = _tokenCache.GetOrAdd(cacheKey, static _ => new TokenSlot());
            lock (slot.Sync)
            {
                if (!slot.Retired)
                {
                    slot.Users++;
                    return slot;
                }
            }
        }
    }

    private void ExitSlot(string cacheKey, TokenSlot slot)
    {
        lock (slot.Sync)
        {
            slot.Users--;
            if (slot.Users == 0 && slot.Token is null)
                RetireLocked(cacheKey, slot);
        }
    }

    // Caller holds slot.Sync and has seen that the slot has no users. Only a user writes a token, and no caller can
    // become a user of a retired slot, so a retired slot never holds a token again. The removal compares the slot
    // instance, so a replacement slot registered under the same key is never removed.
    private void RetireLocked(string cacheKey, TokenSlot slot)
    {
        slot.Retired = true;
        _tokenCache.TryRemove(new KeyValuePair<string, TokenSlot>(cacheKey, slot));
    }

    // Refresh 5 minutes before expiry to avoid race at boundary.
    private bool TryGetReusable(TokenSlot slot, [NotNullWhen(true)] out CachedToken? token)
    {
        token = slot.Token;
        return token is not null && token.ExpiresAt > _time.GetUtcNow() + TokenRefreshMargin;
    }

    // Removes entries whose token can no longer be used (expired or never obtained) and that no caller is using.
    private void PruneTokenCache()
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _tokenCache)
        {
            var slot = entry.Value;
            lock (slot.Sync)
            {
                if (slot.Users == 0 && (slot.Token is not { } token || token.ExpiresAt <= now))
                    RetireLocked(entry.Key, slot);
            }
        }
    }

    private void EvictToken(string cacheKey, CachedToken token)
    {
        if (!_tokenCache.TryGetValue(cacheKey, out var slot))
            return;

        lock (slot.Sync)
        {
            slot.Invalidate(token);
            if (slot.Users == 0 && slot.Token is null)
                RetireLocked(cacheKey, slot);
        }
    }

    private async Task<CachedToken> RequestTokenAsync(
        Guid connectionId, string clientId, string clientSecret, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "client_credentials",
            ["client_id"]     = clientId,
            ["client_secret"] = clientSecret
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _options.TokenPath)
        {
            Content = new FormUrlEncodedContent(form)
        };

        var sw = Stopwatch.StartNew();
        using var resp = await _httpClient.SendAsync(req, ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Provider request failed. Provider={Provider} Operation={Operation} StatusCode={StatusCode} ElapsedMs={ElapsedMs} ConnectionId={ConnectionId}",
                "Yemeksepeti",
                "Token",
                (int)resp.StatusCode,
                sw.ElapsedMilliseconds,
                connectionId);
            throw new ProviderRequestException("Yemeksepeti", "Token", (int)resp.StatusCode);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var tokenResp = await JsonSerializer.DeserializeAsync<YemeksepetiTokenResponse>(stream, JsonOptions, ct);

        if (string.IsNullOrWhiteSpace(tokenResp?.AccessToken))
            throw new InvalidOperationException(
                "Yemeksepeti token response did not contain a valid access_token.");

        var expirySeconds = tokenResp.ExpiresIn > 0 ? tokenResp.ExpiresIn : 7200;
        var expiresAt = _time.GetUtcNow().AddSeconds(expirySeconds);

        _logger.LogInformation(
            "Yemeksepeti token refreshed. ConnectionId={ConnectionId} ExpiresAt={ExpiresAt}",
            connectionId, expiresAt);

        return new CachedToken(tokenResp.AccessToken!, expiresAt);
    }

    // ── Lifecycle stubs (not yet implemented) ─────────────────────────────────

    public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct)
        => throw new NotSupportedException(
            "Yemeksepeti AcceptOrder is not yet implemented. Use the Yemeksepeti partner portal directly.");

    public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
        => throw new NotSupportedException(
            "Yemeksepeti MarkInvoiced is not yet implemented.");

    public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
        => throw new NotSupportedException(
            "Yemeksepeti MarkShipped is not yet implemented.");

    public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
        => throw new NotSupportedException(
            "Yemeksepeti MarkDelivered is not yet implemented.");

    public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct)
        => throw new NotSupportedException(
            "Yemeksepeti RejectOrder is not yet implemented.");

    // ── Mapping ───────────────────────────────────────────────────────────────

    private ExternalOrderDto MapToExternalOrderDto(YemeksepetiOrder o)
    {
        var customerName = o.Customer?.Name?.Trim() ?? "Customer";
        if (string.IsNullOrWhiteSpace(customerName)) customerName = "Customer";

        var phone   = o.Customer?.Phone ?? string.Empty;
        var address = o.DeliveryAddress?.FullAddress?.Trim() ?? string.Empty;

        // TODO: Confirm exact date/time field name and format in the real response.
        var orderedAt = TryParseDateTime(o.CreationDate);

        var externalStatus = o.Status ?? string.Empty;
        var internalStatus = _statusMapper.MapToInternalStatus(FoodPlatform.Yemeksepeti, externalStatus);

        var items = (o.Basket ?? [])
            .Select(MapBasketItem)
            .ToList();

        var subtotal    = items.Sum(i => i.TotalPrice);
        var deliveryFee = o.DeliveryFee ?? 0m;
        var total       = o.TotalPrice ?? subtotal + deliveryFee;

        return new ExternalOrderDto(
            Platform: FoodPlatform.Yemeksepeti,
            ExternalOrderId:   o.Id ?? string.Empty,
            ExternalOrderCode: o.Code ?? o.Id ?? string.Empty,
            OrderedAtUtc:      orderedAt,
            CustomerName:      customerName,
            CustomerPhone:     phone,
            CustomerAddress:   address,
            Subtotal:          subtotal,
            DeliveryFee:       deliveryFee,
            ServiceFee:        0m,
            Total:             total,
            PaymentMethod:     MapPaymentMethod(o.PaymentMethod),
            PaymentStatus:     MapPaymentStatus(o.PaymentMethod),
            ExternalStatus:    externalStatus,
            RawPayloadJson:    JsonSerializer.Serialize(o, JsonOptions),
            Items:             items,
            CustomerNote:      null);
    }

    private static ExternalOrderItemDto MapBasketItem(YemeksepetiBasketItem item)
    {
        var qty       = item.Quantity > 0 ? item.Quantity : 1;
        var unitPrice = item.UnitPrice ?? 0m;
        var total     = item.TotalPrice ?? unitPrice * qty;

        var options = (item.Options ?? [])
            .Select(opt => new ExternalOrderItemOptionDto(opt.Name ?? string.Empty, opt.Price ?? 0m))
            .ToList();

        return new ExternalOrderItemDto(
            ExternalItemId: item.Id ?? Guid.NewGuid().ToString(),
            ProductName:    item.Name ?? "Item",
            Quantity:       qty,
            UnitPrice:      unitPrice,
            TotalPrice:     total,
            Notes:          item.Note,
            Options:        options);
    }

    private static DateTime TryParseDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DateTime.UtcNow;
        // TODO: Confirm date format from real API; try ISO-8601 first, fallback to UtcNow.
        if (DateTimeOffset.TryParse(raw, out var dto)) return dto.UtcDateTime;
        if (long.TryParse(raw, out var ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return DateTime.UtcNow;
    }

    private static PaymentMethod MapPaymentMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method)) return PaymentMethod.Unknown;
        var u = method.Trim().ToUpperInvariant();
        return u switch
        {
            "ONLINE" or "CREDIT_CARD" or "CARD_ON_DELIVERY" or "DEBIT_CARD" => PaymentMethod.OnlinePayment,
            "CASH_ON_DELIVERY" or "CASH"                                     => PaymentMethod.Cash,
            _ => PaymentMethod.Unknown
        };
    }

    private static PaymentStatus MapPaymentStatus(string? method)
    {
        if (string.IsNullOrWhiteSpace(method)) return PaymentStatus.Unknown;
        var u = method.Trim().ToUpperInvariant();
        if (u is "ONLINE" or "CREDIT_CARD") return PaymentStatus.Paid;
        if (u is "CASH_ON_DELIVERY" or "CASH" or "CARD_ON_DELIVERY") return PaymentStatus.Pending;
        return PaymentStatus.Unknown;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ProviderRequestException LogProviderFailure(string provider, string operation, int statusCode, long elapsedMs)
    {
        _logger.LogWarning(
            "Provider request failed. Provider={Provider} Operation={Operation} StatusCode={StatusCode} ElapsedMs={ElapsedMs}",
            provider,
            operation,
            statusCode,
            elapsedMs);
        return new ProviderRequestException(provider, operation, statusCode);
    }

    // ── Token cache entry ─────────────────────────────────────────────────────

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed class TokenSlot
    {
        private CachedToken? _token;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Guards <see cref="Users"/> and <see cref="Retired"/> and every removal of this slot.</summary>
        public object Sync { get; } = new();

        /// <summary>Callers between <c>EnterSlot</c> and <c>ExitSlot</c>, waiting or requesting included.</summary>
        public int Users { get; set; }

        /// <summary>Removed from the cache; never used again.</summary>
        public bool Retired { get; set; }

        public CachedToken? Token
        {
            get => Volatile.Read(ref _token);
            set => Volatile.Write(ref _token, value);
        }

        // Clears only the token that was rejected, never one a concurrent refresh has already replaced it with.
        public void Invalidate(CachedToken rejected) => Interlocked.CompareExchange(ref _token, null, rejected);
    }

    // ── Provider DTOs ─────────────────────────────────────────────────────────
    // Tolerant/nullable: do not fail if optional fields are missing.
    // TODO: Validate against real Yemeksepeti Partner API response and adjust field names.

    private sealed class YemeksepetiTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class YemeksepetiOrdersResponse
    {
        [JsonPropertyName("data")]
        public List<YemeksepetiOrder>? Data { get; set; }

        [JsonPropertyName("total_count")]
        public int? TotalCount { get; set; }

        [JsonPropertyName("page")]
        public int? Page { get; set; }

        [JsonPropertyName("total_pages")]
        public int? TotalPages { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class YemeksepetiOrder
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        // TODO: Confirm field name — may be "creation_date", "created_at", or a Unix timestamp.
        [JsonPropertyName("creation_date")]
        public string? CreationDate { get; set; }

        [JsonPropertyName("customer")]
        public YemeksepetiCustomer? Customer { get; set; }

        [JsonPropertyName("delivery_address")]
        public YemeksepetiAddress? DeliveryAddress { get; set; }

        [JsonPropertyName("total_price")]
        public decimal? TotalPrice { get; set; }

        [JsonPropertyName("delivery_fee")]
        public decimal? DeliveryFee { get; set; }

        // TODO: Confirm payment field name (may be "payment_method", "payment_type", etc.).
        [JsonPropertyName("payment_method")]
        public string? PaymentMethod { get; set; }

        [JsonPropertyName("basket")]
        public List<YemeksepetiBasketItem>? Basket { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class YemeksepetiCustomer
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("phone")]
        public string? Phone { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class YemeksepetiAddress
    {
        [JsonPropertyName("full_address")]
        public string? FullAddress { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class YemeksepetiBasketItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("unit_price")]
        public decimal? UnitPrice { get; set; }

        [JsonPropertyName("total_price")]
        public decimal? TotalPrice { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }

        [JsonPropertyName("options")]
        public List<YemeksepetiItemOption>? Options { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class YemeksepetiItemOption
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("price")]
        public decimal? Price { get; set; }
    }
}
