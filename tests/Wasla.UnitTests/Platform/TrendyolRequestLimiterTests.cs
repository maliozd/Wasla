using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.UnitTests.Diagnostics;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The process-wide Trendyol GO request limiter and the client's 429 handling, on fake time only: a wait moves a fake
/// clock (or blocks on a token), so nothing here sleeps or reaches the network.
/// </summary>
public sealed class TrendyolRequestLimiterTests
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OrderFetchWindow Window = new(
        new DateTime(2026, 10, 6, 11, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));

    private const string SensitiveBody =
        """{"customer":{"firstName":"Ayşe","lastName":"Gizlisoy"},"address":{"phone":"5321112233"}}""";

    [Fact]
    public void Defaults_AreFortyRequestsPerRollingTenSeconds()
    {
        Assert.Equal(40, TrendyolRequestRateLimiter.DefaultPermitLimit);
        Assert.Equal(TenSeconds, TrendyolRequestRateLimiter.DefaultWindow);
    }

    [Fact]
    public async Task LowVolumeRequest_ProceedsImmediately()
    {
        var clock = new LimiterClock();
        var limiter = clock.Limiter();

        var acquire = limiter.AcquireAsync(CancellationToken.None);

        Assert.True(acquire.IsCompletedSuccessfully);
        await acquire;
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task FortyFirstRequest_WaitsUntilTestTimeAdvancesPastTheRollingWindow()
    {
        var clock = new LimiterClock();
        var released = new TaskCompletionSource();
        TimeSpan? requested = null;
        var limiter = new TrendyolRequestRateLimiter(
            clock,
            async (wait, ct) =>
            {
                requested = wait;
                await released.Task.WaitAsync(ct);
                clock.Now += wait;
            },
            TrendyolRequestRateLimiter.DefaultPermitLimit,
            TrendyolRequestRateLimiter.DefaultWindow);

        for (var i = 0; i < 40; i++)
            Assert.True(limiter.AcquireAsync(CancellationToken.None).IsCompletedSuccessfully);

        var fortyFirst = limiter.AcquireAsync(CancellationToken.None);
        Assert.False(fortyFirst.IsCompleted);
        Assert.Equal(TenSeconds, requested);

        released.SetResult();
        await fortyFirst.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 10, TimeSpan.Zero), clock.Now);
    }

    [Fact]
    public async Task Window_IsRolling_NotFixed()
    {
        var clock = new LimiterClock();
        var limiter = clock.Limiter();
        var start = clock.Now;

        for (var i = 0; i < 20; i++)
            await limiter.AcquireAsync(CancellationToken.None);
        clock.Now = start.AddSeconds(5);
        for (var i = 0; i < 20; i++)
            await limiter.AcquireAsync(CancellationToken.None);
        Assert.Empty(clock.Waits);

        await limiter.AcquireAsync(CancellationToken.None);
        Assert.Equal([TimeSpan.FromSeconds(5)], clock.Waits);
        Assert.Equal(start.AddSeconds(10), clock.Now);

        // The first twenty have left the window; the second twenty have not.
        for (var i = 0; i < 19; i++)
            await limiter.AcquireAsync(CancellationToken.None);
        Assert.Single(clock.Waits);
        await limiter.AcquireAsync(CancellationToken.None);
        Assert.Equal(2, clock.Waits.Count);
        Assert.Equal(start.AddSeconds(15), clock.Now);
    }

    [Fact]
    public async Task CancellationWhileWaiting_ExitsPromptly_ForTheWaiterAndTheQueueBehindIt()
    {
        var clock = new LimiterClock();
        var limiter = new TrendyolRequestRateLimiter(
            clock,
            (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            permitLimit: 1,
            TrendyolRequestRateLimiter.DefaultWindow);
        await limiter.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(cts.Token);
        var queued = limiter.AcquireAsync(cts.Token);
        Assert.False(waiting.IsCompleted);
        Assert.False(queued.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Defer_HoldsEveryLaterRequestUntilTheRetryTime()
    {
        var clock = new LimiterClock();
        var limiter = clock.Limiter();

        limiter.Defer(TimeSpan.FromSeconds(7));
        await limiter.AcquireAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(7)], clock.Waits);
    }

    [Fact]
    public async Task EveryPageRequest_TakesOnePermit()
    {
        var clock = new LimiterClock();
        var sentAt = new List<(int Page, DateTimeOffset At)>();
        var client = Create(clock, clock.Limiter(permitLimit: 2), request =>
        {
            var page = ReadPage(request);
            sentAt.Add((page, clock.Now));
            return Json(Page(page, totalPages: 3, Package($"p{page}")));
        });

        var orders = await client.FetchOrdersAsync(Connection("supplier-a"), Window, CancellationToken.None);

        Assert.Equal(3, orders.Count);
        Assert.Equal([0, 1, 2], sentAt.Select(s => s.Page));
        Assert.Equal(sentAt[0].At, sentAt[1].At);
        Assert.Equal(sentAt[0].At + TenSeconds, sentAt[2].At);
        Assert.Equal([TenSeconds], clock.Waits);
    }

    [Fact]
    public async Task ClientsForDifferentTenants_ShareOneLimiter()
    {
        var clock = new LimiterClock();
        var shared = clock.Limiter(permitLimit: 2);
        var sentAt = new List<(string Supplier, DateTimeOffset At)>();
        HttpResponseMessage Responder(HttpRequestMessage request)
        {
            sentAt.Add((Regex.Match(request.RequestUri!.AbsolutePath, "suppliers/([^/]+)/").Groups[1].Value, clock.Now));
            return Json(Page(0, totalPages: 1, Package("x")));
        }

        var tenantA = Create(clock, shared, Responder);
        var tenantB = Create(clock, shared, Responder);

        await tenantA.FetchOrdersAsync(Connection("supplier-a"), Window, CancellationToken.None);
        await tenantB.FetchOrdersAsync(Connection("supplier-b"), Window, CancellationToken.None);
        await tenantA.FetchOrdersAsync(Connection("supplier-a"), Window, CancellationToken.None);

        Assert.Equal(["supplier-a", "supplier-b", "supplier-a"], sentAt.Select(s => s.Supplier));
        Assert.Equal(sentAt[0].At + TenSeconds, sentAt[2].At);
        Assert.Same(tenantA.RequestLimiter, tenantB.RequestLimiter);
    }

    [Fact]
    public void RealMode_RegistersOneLimiterForEveryTrendyolClientInstance()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platforms:ProviderMode"] = "Real",
                ["ConnectionStrings:CentralDb"] = "Server=unused;Database=unused",
                ["Platform:TrendyolGo:BaseUrl"] = "https://trendyol.example/",
                ["Platform:Yemeksepeti:BaseUrl"] = "https://yemeksepeti.example/"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaInfrastructure(configuration);
        services.AddSingleton<ISecretManager, PassthroughSecrets>();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TrendyolRequestRateLimiter));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider();
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();
        var clientA = scopeA.ServiceProvider.GetServices<IFoodPlatformClient>().OfType<TrendyolGoFoodPlatformClient>().Single();
        var clientB = scopeB.ServiceProvider.GetServices<IFoodPlatformClient>().OfType<TrendyolGoFoodPlatformClient>().Single();

        Assert.NotSame(clientA, clientB);
        Assert.Same(clientA.RequestLimiter, clientB.RequestLimiter);
        Assert.Same(provider.GetRequiredService<TrendyolRequestRateLimiter>(), clientA.RequestLimiter);
    }

    [Theory]
    [InlineData("delta")]
    [InlineData("date")]
    public async Task Throttled_WithRetryAfter_RetriesTheSamePageAfterTheProviderDelay(string form)
    {
        var clock = new LimiterClock();
        var pages = new List<int>();
        var client = Create(clock, clock.Limiter(), request =>
        {
            var page = ReadPage(request);
            pages.Add(page);
            if (page == 1 && pages.Count(p => p == 1) == 1)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(SensitiveBody) };
                throttled.Headers.RetryAfter = form == "delta"
                    ? new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7))
                    : new System.Net.Http.Headers.RetryConditionHeaderValue(clock.Now.AddSeconds(7));
                return throttled;
            }

            return Json(Page(page, totalPages: 3, Package($"p{page}")));
        });

        var orders = await client.FetchOrdersAsync(Connection("supplier-a"), Window, CancellationToken.None);

        Assert.Equal([0, 1, 1, 2], pages);
        Assert.Equal(["p0", "p1", "p2"], orders.Select(o => o.ExternalOrderId));
        Assert.Equal([TimeSpan.FromSeconds(7)], clock.Waits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("soon")]
    public async Task Throttled_WithoutAUsableRetryAfter_UsesTheBoundedFallback(string? header)
    {
        var clock = new LimiterClock();
        var attempts = 0;
        var client = Create(clock, clock.Limiter(), _ =>
        {
            if (attempts++ == 0)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                if (header is not null)
                    throttled.Headers.TryAddWithoutValidation("Retry-After", header);
                return throttled;
            }

            return Json(Page(0, totalPages: 1, Package("only")));
        });

        await client.FetchOrdersAsync(Connection("supplier-a"), Window, CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal([TrendyolGoFoodPlatformClient.DefaultThrottleDelay], clock.Waits);
        Assert.Equal(TimeSpan.FromSeconds(10), TrendyolGoFoodPlatformClient.DefaultThrottleDelay);
    }

    [Fact]
    public async Task Throttled_WithAVeryLongRetryAfter_WaitsAtMostTheCap()
    {
        var clock = new LimiterClock();
        var attempts = 0;
        var client = Create(clock, clock.Limiter(), _ =>
        {
            if (attempts++ > 0)
                return Json(Page(0, totalPages: 1, Package("only")));

            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return throttled;
        });

        await client.FetchOrdersAsync(Connection("supplier-a"), Window, CancellationToken.None);

        Assert.Equal([TrendyolGoFoodPlatformClient.MaxThrottleDelay], clock.Waits);
        Assert.Equal(TimeSpan.FromSeconds(60), TrendyolGoFoodPlatformClient.MaxThrottleDelay);
    }

    [Fact]
    public async Task ThrottledThreeTimes_FailsTheWindow_WithoutMovingToTheNextPage_OrLeakingTheBody()
    {
        var clock = new LimiterClock();
        var logger = new CollectingLogger<TrendyolGoFoodPlatformClient>();
        var pages = new List<int>();
        var client = Create(clock, clock.Limiter(), request =>
        {
            pages.Add(ReadPage(request));
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(SensitiveBody) };
        }, logger);

        var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(Connection("supplier-a"), Window, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(1 + TrendyolGoFoodPlatformClient.MaxThrottledRetries, pages.Count);
        Assert.Equal(2, TrendyolGoFoodPlatformClient.MaxThrottledRetries);
        Assert.All(pages, page => Assert.Equal(0, page));
        Assert.Equal(
            [TrendyolGoFoodPlatformClient.DefaultThrottleDelay, TrendyolGoFoodPlatformClient.DefaultThrottleDelay],
            clock.Waits);

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake-key-supplier-a:fake-secret-supplier-a"));
        foreach (var text in logger.Entries.Select(e => e.Message + "\n" + e.Exception).Append(ex.ToString()))
        {
            Assert.DoesNotContain("Ayşe", text, StringComparison.Ordinal);
            Assert.DoesNotContain("5321112233", text, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-secret-supplier-a", text, StringComparison.Ordinal);
            Assert.DoesNotContain(basic, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Basic ", text, StringComparison.Ordinal);
        }

        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("throttled", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ThrottledRetry_TakesAPermitOfItsOwn()
    {
        var clock = new LimiterClock();
        var attempts = 0;
        var client = Create(clock, clock.Limiter(permitLimit: 1), _ =>
        {
            if (attempts++ > 0)
                return Json(Page(0, totalPages: 1, Package("only")));

            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return throttled;
        });

        await client.FetchOrdersAsync(Connection("supplier-a"), Window, CancellationToken.None);

        // Retry-After: 0 asks for no pause, but the retry still needs a permit: with a limit of one it waits a window.
        Assert.Equal(2, attempts);
        Assert.Equal([TenSeconds], clock.Waits);
    }

    [Fact]
    public async Task CancellationDuringTheThrottleWait_StopsPromptly()
    {
        var clock = new LimiterClock();
        var limiter = new TrendyolRequestRateLimiter(
            clock,
            (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            TrendyolRequestRateLimiter.DefaultPermitLimit,
            TrendyolRequestRateLimiter.DefaultWindow);
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var client = Create(clock, limiter, _ =>
        {
            attempts++;
            cts.Cancel();
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FetchOrdersAsync(Connection("supplier-a"), Window, cts.Token).WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(1, attempts);
    }

    private static TrendyolGoFoodPlatformClient Create(
        TimeProvider time,
        TrendyolRequestRateLimiter limiter,
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ILogger<TrendyolGoFoodPlatformClient>? logger = null) =>
        new(
            new HttpClient(new DelegateHandler(responder)) { BaseAddress = new Uri("https://trendyol.example/") },
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            Options.Create(new TrendyolGoOptions { AgentName = "Wasla", BaseUrl = "https://trendyol.example/" }),
            logger ?? NullLogger<TrendyolGoFoodPlatformClient>.Instance,
            limiter,
            time);

    private static PlatformConnection Connection(string supplier) => new()
    {
        Platform = FoodPlatform.TrendyolYemek,
        SupplierId = supplier,
        StoreId = "store-" + supplier,
        ExecutorEmail = "executor@example.invalid",
        EncryptedApiKey = "fake-key-" + supplier,
        EncryptedApiSecret = "fake-secret-" + supplier,
        IsActive = true
    };

    private static int ReadPage(HttpRequestMessage request) =>
        int.Parse(Regex.Match(request.RequestUri!.Query, @"[?&]page=(\d+)").Groups[1].Value);

    private static string Page(int page, int totalPages, params string[] packages) =>
        $$"""{"page":{{page}},"size":50,"totalPages":{{totalPages}},"totalCount":{{totalPages}},"content":[{{string.Join(',', packages)}}]}""";

    private static string Package(string id) =>
        $$"""{"id":"{{id}}","orderNumber":"{{id}}","packageCreationDate":1791280000000,"packageStatus":"Created","totalPrice":10}""";

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
