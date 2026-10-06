using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.Yemeksepeti;
using Wasla.UnitTests.Diagnostics;

namespace Wasla.UnitTests.Platform;

public sealed class TrendyolPaginationTests
{
    private const string SensitiveBody =
        "{\"customerName\":\"Ali Veli\",\"phone\":\"5551112233\",\"address\":\"Secret Street 5\"}";

    // 2026-10-06T09:00:00Z .. 10:00:00Z, which the API receives as epoch milliseconds.
    private static readonly OrderFetchWindow Window = new(
        new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Request_UsesTheDocumentedPackagesQuery_WithEveryStatusAndTheWindowInMilliseconds()
    {
        var requests = new List<Uri>();
        var client = Create(request =>
        {
            requests.Add(request.RequestUri!);
            return Json(Page(0, 1, Package("only")));
        });

        await client.FetchOrdersAsync(Connection(), Window, CancellationToken.None);

        var uri = Assert.Single(requests);
        Assert.Equal("/integrator/order/meal/suppliers/supplier-1/packages", uri.AbsolutePath);
        Assert.Equal(
            "?packageStatuses=Created,Picking,Invoiced,Cancelled,UnSupplied,Shipped,Delivered"
            + "&size=50&page=0&storeId=store-1"
            + "&packageModificationStartDate=1791277200000&packageModificationEndDate=1791280800000",
            uri.Query);

        var statuses = QueryValue(uri, "packageStatuses").Split(',');
        Assert.Contains("Delivered", statuses);
        Assert.Contains("Cancelled", statuses);
        Assert.Contains("UnSupplied", statuses);
        Assert.Equal(
            ["Created", "Picking", "Invoiced", "Cancelled", "UnSupplied", "Shipped", "Delivered"],
            statuses);
    }

    [Theory]
    [InlineData("Delivered")]
    [InlineData("Cancelled")]
    [InlineData("UnSupplied")]
    public async Task TerminalPackages_AreReturnedWithTheirProviderStatus(string status)
    {
        var client = Create(_ => Json(Page(0, 1, Package("terminal", status))));

        var orders = await client.FetchOrdersAsync(Connection(), Window, CancellationToken.None);

        Assert.Equal(status, Assert.Single(orders).ExternalStatus);
    }

    [Fact]
    public async Task WindowLongerThanTheFetchWindow_IsRefusedBeforeAnyRequest()
    {
        var requests = 0;
        var client = Create(_ =>
        {
            requests++;
            return Json(Page(0, 1));
        });
        var tooLong = new OrderFetchWindow(Window.StartUtc, Window.StartUtc + TrendyolGoFoodPlatformClient.FetchWindowLength + TimeSpan.FromMilliseconds(1));
        var reversed = new OrderFetchWindow(Window.EndUtc, Window.StartUtc);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.FetchOrdersAsync(Connection(), tooLong, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.FetchOrdersAsync(Connection(), reversed, CancellationToken.None));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task OnePage_FetchesPageZeroOnce()
    {
        var pages = new List<int>();
        var client = Create(request =>
        {
            pages.Add(ReadPage(request));
            AssertWindow(request);
            return Json(Page(0, 1, Package("only")));
        });

        var orders = await client.FetchOrdersAsync(Connection(), Window, CancellationToken.None);

        Assert.Equal([0], pages);
        Assert.Equal(["only"], orders.Select(o => o.ExternalOrderId).ToArray());
    }

    [Fact]
    public async Task MultiplePages_CombinesEveryPackage_AndKeepsThePartialFinalPage()
    {
        var pages = new List<int>();
        var client = Create(request =>
        {
            var page = ReadPage(request);
            pages.Add(page);
            AssertWindow(request);
            var id = page switch
            {
                0 => "a",
                1 => "b",
                _ => "c"
            };
            return Json(Page(page, 3, Package(id)));
        });

        var orders = await client.FetchOrdersAsync(Connection(), Window, CancellationToken.None);

        Assert.Equal([0, 1, 2], pages);
        Assert.Equal(["a", "b", "c"], orders.Select(o => o.ExternalOrderId).ToArray());
    }

    [Fact]
    public async Task FullPageWithoutTotalPages_ContinuesUntilTheShortPage()
    {
        var pages = new List<int>();
        var client = Create(request =>
        {
            var page = ReadPage(request);
            pages.Add(page);
            if (page == 0)
            {
                var packages = Enumerable.Range(0, TrendyolGoFoodPlatformClient.FetchPageSize)
                    .Select(i => Package($"p{i}"));
                return Json(PageWithoutTotal(packages));
            }

            return Json(PageWithoutTotal([Package("tail")]));
        });

        var orders = await client.FetchOrdersAsync(Connection(), Window, CancellationToken.None);

        Assert.Equal([0, 1], pages);
        Assert.Equal(TrendyolGoFoodPlatformClient.FetchPageSize + 1, orders.Count);
        Assert.Contains(orders, o => o.ExternalOrderId == "tail");
    }

    [Fact]
    public async Task CancellationDuringPagination_DoesNotReturnPartialSuccess()
    {
        var cts = new CancellationTokenSource();
        var pages = new List<int>();
        var client = Create(request =>
        {
            var page = ReadPage(request);
            pages.Add(page);
            cts.Cancel();
            return Json(Page(0, 3, Package("a")));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FetchOrdersAsync(Connection(), Window, cts.Token));
        Assert.Equal([0], pages);
    }

    [Fact]
    public async Task HttpFailureOnLaterPage_FailsWithoutLoggingTheBody()
    {
        var logger = new CollectingLogger<TrendyolGoFoodPlatformClient>();
        var pages = new List<int>();
        var client = Create(request =>
        {
            var page = ReadPage(request);
            pages.Add(page);
            if (page == 0)
                return Json(Page(0, 2, Package("a")));

            return new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(SensitiveBody)
            };
        }, logger);

        var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(Connection(), Window, CancellationToken.None));

        Assert.Equal([0, 1], pages);
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Equal("FetchOrders", ex.Operation);
        AssertSafe(ex, logger.Entries);
    }

    [Fact]
    public async Task PageCap_FailsWhenTheProviderStillReportsMorePages()
    {
        var logger = new CollectingLogger<TrendyolGoFoodPlatformClient>();
        var pages = new List<int>();
        var reported = TrendyolGoFoodPlatformClient.MaxFetchPages + 2;
        var client = Create(request =>
        {
            var page = ReadPage(request);
            pages.Add(page);
            return Json(Page(page, reported, Package($"p{page}")));
        }, logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchOrdersAsync(Connection(), Window, CancellationToken.None));

        Assert.Equal(TrendyolGoFoodPlatformClient.MaxFetchPages, pages.Count);
        Assert.Equal(Enumerable.Range(0, TrendyolGoFoodPlatformClient.MaxFetchPages), pages);
        Assert.Contains("page cap", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("page cap", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public async Task RepeatedPageMetadata_StopsInsteadOfLooping()
    {
        var pages = new List<int>();
        var client = Create(request =>
        {
            var page = ReadPage(request);
            pages.Add(page);
            return Json(Page(0, 5, Package("stuck")));
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchOrdersAsync(Connection(), Window, CancellationToken.None));

        Assert.Equal([0, 1], pages);
        Assert.Contains("did not advance", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyPageWhileMorePagesAreReported_FailsInsteadOfDroppingOrders()
    {
        var pages = new List<int>();
        var client = Create(request =>
        {
            var page = ReadPage(request);
            pages.Add(page);
            return page == 0
                ? Json(Page(0, 3, Package("a")))
                : Json(Page(page, 3));
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchOrdersAsync(Connection(), Window, CancellationToken.None));

        Assert.Equal([0, 1], pages);
        Assert.Contains("empty page", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IdenticalPackageOnTheNextPage_IsReturnedOnce()
    {
        var client = Create(request =>
        {
            var page = ReadPage(request);
            return Json(Page(page, 2, Package("same", "Created", 10)));
        });

        var orders = await client.FetchOrdersAsync(Connection(), Window, CancellationToken.None);

        Assert.Equal(["same"], orders.Select(o => o.ExternalOrderId).ToArray());
    }

    [Fact]
    public async Task ConflictingDuplicate_IsNotHidden()
    {
        var client = Create(request =>
        {
            var page = ReadPage(request);
            var status = page == 0 ? "Created" : "Picking";
            return Json(Page(page, 2, Package("same", status, 10)));
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchOrdersAsync(Connection(), Window, CancellationToken.None));

        Assert.Contains("conflicting duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PagesFetched=2", ex.Message, StringComparison.Ordinal);
    }

    private static void AssertWindow(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        Assert.Equal("50", QueryValue(uri, "size"));
        Assert.Equal("Created,Picking,Invoiced,Cancelled,UnSupplied,Shipped,Delivered", QueryValue(uri, "packageStatuses"));
        Assert.Equal("1791277200000", QueryValue(uri, "packageModificationStartDate"));
        Assert.Equal("1791280800000", QueryValue(uri, "packageModificationEndDate"));
        Assert.Equal("store-1", QueryValue(uri, "storeId"));
    }

    private static string QueryValue(Uri uri, string name)
    {
        var match = Regex.Match(uri.Query, $@"(?:^\?|&){Regex.Escape(name)}=([^&]*)");
        Assert.True(match.Success, $"{name} is missing from {uri.Query}");
        return match.Groups[1].Value;
    }

    private static int ReadPage(HttpRequestMessage request)
    {
        var query = request.RequestUri?.Query ?? request.RequestUri?.OriginalString ?? string.Empty;
        var match = Regex.Match(query, @"(?:^|[?&])page=(\d+)");
        Assert.True(match.Success, query);
        return int.Parse(match.Groups[1].Value);
    }

    private static string Page(int page, int totalPages, params string[] packages) =>
        $$"""
        {"page":{{page}},"size":50,"totalPages":{{totalPages}},"totalCount":{{totalPages}},"content":[{{string.Join(',', packages)}}]}
        """;

    private static string PageWithoutTotal(IEnumerable<string> packages) =>
        $$"""{"content":[{{string.Join(',', packages)}}]}""";

    private static string Package(string id, string status = "Created", decimal total = 10) =>
        $$"""{"id":"{{id}}","orderNumber":"{{id}}","packageCreationDate":1710000000000,"packageStatus":"{{status}}","totalPrice":{{total}}}""";

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static TrendyolGoFoodPlatformClient Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ILogger<TrendyolGoFoodPlatformClient>? logger = null)
    {
        return new TrendyolGoFoodPlatformClient(
            new HttpClient(new DelegateHandler(responder))
            {
                BaseAddress = new Uri("https://trendyol.example/")
            },
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            Options.Create(new TrendyolGoOptions { AgentName = "Wasla", BaseUrl = "https://trendyol.example/" }),
            logger ?? NullLogger<TrendyolGoFoodPlatformClient>.Instance);
    }

    private static PlatformConnection Connection() => new()
    {
        Platform = FoodPlatform.TrendyolYemek,
        StoreId = "store-1",
        SupplierId = "supplier-1",
        ExecutorEmail = "executor@example.invalid",
        EncryptedApiKey = "key",
        EncryptedApiSecret = "secret",
        IsActive = true,
        SyncIntervalSeconds = 0
    };

    private static void AssertSafe(Exception exception, IReadOnlyList<CollectedLog> entries)
    {
        Assert.DoesNotContain("5551112233", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ali Veli", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Street", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Body:", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain("5551112233", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Ali Veli", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SensitiveBody, entry.Message, StringComparison.Ordinal);
        });
    }
}

public sealed class YemeksepetiPaginationTests
{
    private const string SensitiveBody =
        "{\"customerName\":\"Ali Veli\",\"phone\":\"5551112233\",\"client_secret\":\"super-secret\"}";

    // The Yemeksepeti client is not checkpointed yet and ignores the window.
    private static readonly OrderFetchWindow Window = new(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

    [Fact]
    public async Task OnePage_FetchesPageZeroOnce()
    {
        var ordersPages = new List<int>();
        var client = Create(request =>
        {
            if (IsToken(request))
                return Token();

            ordersPages.Add(ReadPage(request));
            AssertWindow(request);
            return Json(Page(0, 1, Order("only")));
        });

        var orders = await client.FetchOrdersAsync(Connection(), Window, CancellationToken.None);

        Assert.Equal([0], ordersPages);
        Assert.Equal(["only"], orders.Select(o => o.ExternalOrderId).ToArray());
    }

    [Fact]
    public async Task MultiplePages_ReusesTheAccessToken_AndCombinesThePartialFinalPage()
    {
        var ordersPages = new List<int>();
        var tokens = new List<string>();
        var tokenRequests = 0;
        var client = Create(request =>
        {
            if (IsToken(request))
            {
                tokenRequests++;
                return Token();
            }

            ordersPages.Add(ReadPage(request));
            tokens.Add(request.Headers.Authorization?.Parameter ?? string.Empty);
            AssertWindow(request);
            var page = ReadPage(request);
            var id = page switch
            {
                0 => "a",
                1 => "b",
                _ => "c"
            };
            return Json(Page(page, 3, Order(id)));
        });

        var orders = await client.FetchOrdersAsync(Connection(), Window, CancellationToken.None);

        Assert.Equal(1, tokenRequests);
        Assert.Equal([0, 1, 2], ordersPages);
        Assert.All(tokens, token => Assert.Equal("tok-1", token));
        Assert.Equal(["a", "b", "c"], orders.Select(o => o.ExternalOrderId).ToArray());
    }

    [Fact]
    public async Task CancellationDuringPagination_DoesNotReturnPartialSuccess()
    {
        var cts = new CancellationTokenSource();
        var ordersPages = new List<int>();
        var client = Create(request =>
        {
            if (IsToken(request))
                return Token();

            ordersPages.Add(ReadPage(request));
            cts.Cancel();
            return Json(Page(0, 3, Order("a")));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FetchOrdersAsync(Connection(), Window, cts.Token));
        Assert.Equal([0], ordersPages);
    }

    [Fact]
    public async Task HttpFailureOnLaterPage_FailsWithoutLoggingTheBody()
    {
        var logger = new CollectingLogger<YemeksepetiFoodPlatformClient>();
        var ordersPages = new List<int>();
        var client = Create(request =>
        {
            if (IsToken(request))
                return Token();

            var page = ReadPage(request);
            ordersPages.Add(page);
            if (page == 0)
                return Json(Page(0, 2, Order("a")));

            return new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(SensitiveBody)
            };
        }, logger);

        var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(Connection(), Window, CancellationToken.None));

        Assert.Equal([0, 1], ordersPages);
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Equal("FetchOrders", ex.Operation);
        Assert.DoesNotContain("5551112233", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Body:", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain("5551112233", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SensitiveBody, entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PageCap_FailsWhenTheProviderStillReportsMorePages()
    {
        var logger = new CollectingLogger<YemeksepetiFoodPlatformClient>();
        var ordersPages = new List<int>();
        var reported = YemeksepetiFoodPlatformClient.MaxFetchPages + 2;
        var client = Create(request =>
        {
            if (IsToken(request))
                return Token();

            var page = ReadPage(request);
            ordersPages.Add(page);
            return Json(Page(page, reported, Order($"p{page}")));
        }, logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchOrdersAsync(Connection(), Window, CancellationToken.None));

        Assert.Equal(YemeksepetiFoodPlatformClient.MaxFetchPages, ordersPages.Count);
        Assert.Equal(0, ordersPages[0]);
        Assert.Equal(YemeksepetiFoodPlatformClient.MaxFetchPages - 1, ordersPages[^1]);
        Assert.Equal(ordersPages.Distinct().Count(), ordersPages.Count);
        Assert.Contains("page cap", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("page cap", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("page=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ShortPageWithoutTotalPages_Stops()
    {
        var ordersPages = new List<int>();
        var client = Create(request =>
        {
            if (IsToken(request))
                return Token();

            ordersPages.Add(ReadPage(request));
            return Json("""{"data":[{"id":"only","code":"only","status":"RECEIVED","total_price":10}]}""");
        }, pageSize: 2);

        var orders = await client.FetchOrdersAsync(Connection(), Window, CancellationToken.None);

        Assert.Equal([0], ordersPages);
        Assert.Equal(["only"], orders.Select(o => o.ExternalOrderId).ToArray());
    }

    [Fact]
    public async Task IdenticalOrderOnTheNextPage_IsReturnedOnce_AndConflictIsNotHidden()
    {
        var identical = Create(request =>
        {
            if (IsToken(request))
                return Token();

            var page = ReadPage(request);
            return Json(Page(page, 2, Order("same", "RECEIVED")));
        });
        var once = await identical.FetchOrdersAsync(Connection(), Window, CancellationToken.None);
        Assert.Equal(["same"], once.Select(o => o.ExternalOrderId).ToArray());

        var conflicting = Create(request =>
        {
            if (IsToken(request))
                return Token();

            var page = ReadPage(request);
            return Json(Page(page, 2, Order("same", page == 0 ? "RECEIVED" : "CANCELLED")));
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            conflicting.FetchOrdersAsync(Connection(), Window, CancellationToken.None));
        Assert.Contains("conflicting duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PagesFetched=2", ex.Message, StringComparison.Ordinal);
    }

    private static void AssertWindow(HttpRequestMessage request)
    {
        var query = request.RequestUri?.Query ?? string.Empty;
        Assert.Contains("page_size=20", query, StringComparison.Ordinal);
        Assert.Contains("start_time=", query, StringComparison.Ordinal);
        Assert.Contains("end_time=", query, StringComparison.Ordinal);
    }

    private static bool IsToken(HttpRequestMessage request) =>
        (request.RequestUri?.AbsolutePath ?? string.Empty).Contains("token", StringComparison.OrdinalIgnoreCase);

    private static int ReadPage(HttpRequestMessage request)
    {
        var query = request.RequestUri?.Query ?? string.Empty;
        var match = Regex.Match(query, @"(?:^|[?&])page=(\d+)");
        Assert.True(match.Success, query);
        return int.Parse(match.Groups[1].Value);
    }

    private static string Page(int page, int totalPages, params string[] orders) =>
        $$"""{"page":{{page}},"total_pages":{{totalPages}},"total_count":{{totalPages}},"data":[{{string.Join(',', orders)}}]}""";

    private static string Order(string id, string status = "RECEIVED") =>
        $$"""{"id":"{{id}}","code":"{{id}}","status":"{{status}}","total_price":10}""";

    private static HttpResponseMessage Token() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"tok-1","expires_in":7200}""")
        };

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static YemeksepetiFoodPlatformClient Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ILogger<YemeksepetiFoodPlatformClient>? logger = null,
        int pageSize = 20)
    {
        var http = new HttpClient(new DelegateHandler(responder))
        {
            BaseAddress = new Uri("https://yemeksepeti.example/")
        };
        return new YemeksepetiFoodPlatformClient(
            new SingleClientFactory(http),
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            Options.Create(new YemeksepetiOptions
            {
                BaseUrl = "https://yemeksepeti.example/",
                TokenPath = "/v2/oauth/token",
                DefaultPageSize = pageSize
            }),
            logger ?? NullLogger<YemeksepetiFoodPlatformClient>.Instance);
    }

    private static PlatformConnection Connection() => new()
    {
        Platform = FoodPlatform.Yemeksepeti,
        StoreId = "vendor-1",
        SupplierId = "chain-1",
        EncryptedApiKey = "client-id",
        EncryptedApiSecret = "client-secret",
        IsActive = true,
        SyncIntervalSeconds = 0
    };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

internal sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(responder(request));
    }
}

internal sealed class PassthroughSecrets : ISecretManager
{
    public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
        Task.FromResult((plaintext, 1));

    public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
        Task.FromResult(encryptedBase64);
}
