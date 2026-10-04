using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.Yemeksepeti;
using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Diagnostics;

public sealed class ProviderFailureLoggingTests
{
    private const string SensitiveBody =
        "{\"customerName\":\"Ali Veli\",\"phone\":\"5551112233\",\"address\":\"Secret Street 5\",\"client_secret\":\"super-secret\"}";

    [Fact]
    public async Task TrendyolFailure_DoesNotLogOrThrowResponseBody()
    {
        var logger = new CollectingLogger<TrendyolGoFoodPlatformClient>();
        var client = new TrendyolGoFoodPlatformClient(
            new HttpClient(new FixedHandler(HttpStatusCode.BadRequest, SensitiveBody))
            {
                BaseAddress = new Uri("https://trendyol.example/")
            },
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            Options.Create(new TrendyolGoOptions { AgentName = "Wasla", BaseUrl = "https://trendyol.example/" }),
            logger);

        var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(Connection(), CancellationToken.None));

        AssertSafe(ex, logger.Entries);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("FetchOrders", ex.Operation);
    }

    [Fact]
    public async Task YemeksepetiTokenFailure_DoesNotLogResponseBody_AndSuccessFetchIsDebug()
    {
        var failureLogger = new CollectingLogger<YemeksepetiFoodPlatformClient>();
        var failing = CreateYemeksepeti(failureLogger, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(SensitiveBody)
        });

        var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            failing.FetchOrdersAsync(Connection(), CancellationToken.None));
        AssertSafe(ex, failureLogger.Entries);

        var successLogger = new CollectingLogger<YemeksepetiFoodPlatformClient>();
        var succeeding = CreateYemeksepeti(successLogger, request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("token", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":7200}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[],\"customerName\":\"Ali Veli\",\"phone\":\"5551112233\"}")
            };
        });

        var orders = await succeeding.FetchOrdersAsync(Connection(), CancellationToken.None);
        Assert.Empty(orders);
        Assert.Contains(successLogger.Entries, entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("fetch completed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(successLogger.Entries, entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("fetch completed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(successLogger.Entries, entry => entry.Message.Contains("5551112233", StringComparison.Ordinal));
        Assert.DoesNotContain(successLogger.Entries, entry => entry.Message.Contains("Ali Veli", StringComparison.Ordinal));
    }

    [Fact]
    public void SanitizeErrorMessage_DropsProviderBodyAndCredentialFragments()
    {
        var sanitized = OrderSyncService.SanitizeErrorMessage(new HttpRequestException(
            "TrendyolGo fetch failed: 400 BadRequest token=abc123 Body: {\"phone\":\"5551112233\",\"name\":\"Ali Veli\"}"));

        Assert.DoesNotContain("5551112233", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Ali Veli", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", sanitized, StringComparison.Ordinal);
        Assert.Contains("token=[redacted]", sanitized, StringComparison.Ordinal);
        Assert.Contains("400", sanitized, StringComparison.Ordinal);
    }

    private static void AssertSafe(Exception exception, IReadOnlyList<CollectedLog> entries)
    {
        Assert.DoesNotContain("5551112233", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ali Veli", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Street", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Body:", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain("5551112233", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Ali Veli", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SensitiveBody, entry.Message, StringComparison.Ordinal);
        });
    }

    private static YemeksepetiFoodPlatformClient CreateYemeksepeti(
        ILogger<YemeksepetiFoodPlatformClient> logger,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var http = new HttpClient(new FixedHandler(responder))
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
                TokenPath = "/v2/oauth/token"
            }),
            logger);
    }

    private static PlatformConnection Connection() => new()
    {
        Platform = FoodPlatform.TrendyolYemek,
        StoreId = "store-1",
        SupplierId = "chain-1",
        ExecutorEmail = "executor@example.invalid",
        EncryptedApiKey = "client-id",
        EncryptedApiSecret = "client-secret",
        IsActive = true,
        SyncIntervalSeconds = 0
    };

    private sealed class PassthroughSecrets : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            Task.FromResult((plaintext, 1));

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            Task.FromResult(encryptedBase64);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FixedHandler(HttpStatusCode statusCode, string body)
            : this(_ => new HttpResponseMessage(statusCode) { Content = new StringContent(body) })
        {
        }

        public FixedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
