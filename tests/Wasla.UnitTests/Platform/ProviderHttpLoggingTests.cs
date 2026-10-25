using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.Yemeksepeti;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// WAS-97: with every log category at Trace (as an operator might lower <c>System.Net.Http.HttpClient</c> while
/// diagnosing), the provider clients and the <see cref="IHttpClientFactory"/> request logging they run through emit no
/// credential, token, cookie, provider header value or response body. The clients come from the production Real-mode
/// registration and call a fake provider on 127.0.0.1 with synthetic canary values.
/// </summary>
public sealed class ProviderHttpLoggingTests
{
    private const string TrendyolApiKey = "canary-tgo-api-key-5d1f";
    private const string TrendyolApiSecret = "canary-tgo-api-secret-8c2e";
    private const string ExecutorUser = "canary-executor-3b7a@wasla.test";
    private const string YemeksepetiClientId = "canary-ys-client-id-4e9b";
    private const string YemeksepetiClientSecret = "canary-ys-client-secret-1a6c";
    private const string YemeksepetiAccessToken = "canary-ys-access-token-7f3d";
    private const string ProviderCookie = "canary-provider-cookie-2c8f";
    private const string ProviderResponseHeader = "canary-provider-response-header-9e1a";
    private const string ProviderErrorBody = "canary-provider-error-body-6b4d";

    private static readonly OrderFetchWindow Window = new(DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow);

    [Fact]
    public async Task ProviderHttpLogs_AtTrace_ContainNoCredentialTokenCookieOrBody()
    {
        await using var origin = await LoopbackProviderOrigin.StartAsync();
        origin.AccessToken = YemeksepetiAccessToken;
        origin.CookieValue = ProviderCookie;
        origin.ResponseHeaders["x-provider-session"] = ProviderResponseHeader;
        origin.Fail = request => request.Path.Contains("/suppliers/supplier-failing/", StringComparison.Ordinal)
            ? (500, $$"""{"error":"{{ProviderErrorBody}}","client_secret":"{{YemeksepetiClientSecret}}"}""")
            : null;
        var logs = new CapturingLoggerProvider();
        await using var services = ProductionServicesLoggingAtTrace(origin.BaseAddress, logs);

        await using (var scope = services.CreateAsyncScope())
        {
            var clients = scope.ServiceProvider.GetServices<IFoodPlatformClient>().ToArray();
            var trendyol = clients.OfType<TrendyolGoFoodPlatformClient>().Single();
            var yemeksepeti = clients.OfType<YemeksepetiFoodPlatformClient>().Single();

            await trendyol.FetchOrdersAsync(Trendyol("supplier-ok"), Window, CancellationToken.None);
            await trendyol.AcceptOrderAsync(Trendyol("supplier-ok"), "package-1", 15, CancellationToken.None);
            await Assert.ThrowsAsync<ProviderRequestException>(() =>
                trendyol.FetchOrdersAsync(Trendyol("supplier-failing"), Window, CancellationToken.None));

            var connection = Yemeksepeti();
            await yemeksepeti.FetchOrdersAsync(connection, Window, CancellationToken.None);
            await yemeksepeti.FetchOrdersAsync(connection, Window, CancellationToken.None);
        }

        // The requests really carried the canaries, and the factory logged their headers at Trace, in each provider's
        // own categories.
        Assert.Contains(origin.Requests, r => r.Header("x-executor-user") == ExecutorUser);
        Assert.Contains(origin.Requests, r => r.Body.Contains(YemeksepetiClientSecret, StringComparison.Ordinal));
        Assert.Contains(origin.Requests, r => r.Header("Authorization") == $"Bearer {YemeksepetiAccessToken}");
        var entries = logs.Entries;
        foreach (var name in new[] { TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName, YemeksepetiFoodPlatformClient.YemeksepetiHttpClientName })
        {
            var category = $"System.Net.Http.HttpClient.{name}.ClientHandler";
            Assert.Contains(entries, e => e.Category == category && e.Level == LogLevel.Trace && e.Message.Contains("Authorization: ", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.Category == category && e.Level == LogLevel.Trace && e.Message.Contains("Set-Cookie: ", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.Category == $"System.Net.Http.HttpClient.{name}.LogicalHandler" && e.Scopes.Count > 0);
        }

        var canaries = new[]
        {
            TrendyolApiKey,
            TrendyolApiSecret,
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TrendyolApiKey}:{TrendyolApiSecret}")),
            ExecutorUser,
            YemeksepetiClientId,
            YemeksepetiClientSecret,
            YemeksepetiAccessToken,
            ProviderCookie,
            ProviderResponseHeader,
            ProviderErrorBody
        };
        var leaks = entries
            .SelectMany(e => canaries.Where(c => e.AllText.Contains(c, StringComparison.Ordinal)).Select(c => $"{c} in {e.Level} {e.Category}: {e.AllText}"))
            .ToArray();
        Assert.True(leaks.Length == 0, "Logs contain canary values:" + Environment.NewLine + string.Join(Environment.NewLine, leaks));

        // Header names stay visible with their values redacted, and safe operational detail stays: provider,
        // operation and status of the failure.
        Assert.Contains(entries, e => e.Message.Contains("Authorization: *", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Message.Contains("x-executor-user: *", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Message.Contains("Set-Cookie: *", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Message.Contains("x-provider-session: *", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Level == LogLevel.Warning
                                      && e.Message.Contains("Provider=TrendyolGo", StringComparison.Ordinal)
                                      && e.Message.Contains("StatusCode=500", StringComparison.Ordinal));
    }

    private static ServiceProvider ProductionServicesLoggingAtTrace(Uri baseAddress, CapturingLoggerProvider logs)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platforms:ProviderMode"] = "Real",
                ["ConnectionStrings:CentralDb"] = "Server=unused;Database=unused",
                ["Platform:TrendyolGo:BaseUrl"] = baseAddress.AbsoluteUri,
                ["Platform:Yemeksepeti:BaseUrl"] = baseAddress.AbsoluteUri,
                ["Platform:Yemeksepeti:TokenPath"] = "/v2/oauth/token"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .ClearProviders()
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter("System.Net.Http.HttpClient", LogLevel.Trace)
            .AddProvider(logs));
        services.AddWaslaInfrastructure(configuration);
        services.AddSingleton<ISecretManager, PassthroughSecrets>();
        return services.BuildServiceProvider();
    }

    // PassthroughSecrets returns the stored value as the decrypted one, so these fields hold the canaries.
    private static PlatformConnection Trendyol(string supplierId) => new()
    {
        Id = Guid.NewGuid(),
        Platform = FoodPlatform.TrendyolYemek,
        SupplierId = supplierId,
        EncryptedApiKey = TrendyolApiKey,
        EncryptedApiSecret = TrendyolApiSecret,
        ExecutorEmail = ExecutorUser,
        IsActive = true,
        SyncIntervalSeconds = 0
    };

    private static PlatformConnection Yemeksepeti() => new()
    {
        Id = Guid.NewGuid(),
        Platform = FoodPlatform.Yemeksepeti,
        SupplierId = "chain-canary",
        StoreId = "vendor-canary",
        EncryptedApiKey = YemeksepetiClientId,
        EncryptedApiSecret = YemeksepetiClientSecret,
        IsActive = true,
        SyncIntervalSeconds = 0
    };

    private sealed record CapturedEntry(
        LogLevel Level,
        string Category,
        string Message,
        string Properties,
        string? Exception,
        IReadOnlyList<string> Scopes)
    {
        public string AllText => string.Join('\n', new[] { Message, Properties, Exception ?? string.Empty }.Concat(Scopes));
    }

    /// <summary>
    /// Captures every entry with its formatted message, each structured property, the exception and every scope that
    /// is active when it is written (scope text and scope properties).
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedEntry> _entries = new();
        private readonly AsyncLocal<ImmutableScopeStack?> _scopes = new();

        public IReadOnlyList<CapturedEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

        public void Dispose()
        {
        }

        private static string Describe(object? state)
        {
            var text = new StringBuilder(state?.ToString());
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                foreach (var (key, value) in properties)
                    text.Append('\n').Append(key).Append('=').Append(value);
            }

            return text.ToString();
        }

        private sealed class Logger(string category, CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                var parent = provider._scopes.Value;
                provider._scopes.Value = new ImmutableScopeStack(Describe(state), parent);
                return new ScopeHandle(provider, parent);
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scopes = new List<string>();
                for (var scope = provider._scopes.Value; scope is not null; scope = scope.Parent)
                    scopes.Add(scope.Text);

                provider._entries.Enqueue(new CapturedEntry(
                    logLevel,
                    category,
                    formatter(state, exception),
                    Describe(state),
                    exception?.ToString(),
                    scopes));
            }
        }

        private sealed record ImmutableScopeStack(string Text, ImmutableScopeStack? Parent);

        private sealed class ScopeHandle(CapturingLoggerProvider provider, ImmutableScopeStack? parent) : IDisposable
        {
            public void Dispose() => provider._scopes.Value = parent;
        }
    }
}
