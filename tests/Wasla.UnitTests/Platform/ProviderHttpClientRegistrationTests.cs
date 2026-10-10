using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Platform;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.Yemeksepeti;
using static Wasla.UnitTests.Platform.ProviderCookieIsolationTests;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// WAS-97: every real provider has its own named <see cref="HttpClient"/> (base address, timeout, handler pool and log
/// category), registered through <see cref="ProviderHttpClientRegistration.AddProviderHttpClient"/>, and nothing
/// registered after it can mix provider configuration or turn cookies and redirects back on. The clients come from the
/// production Real-mode registration (<see cref="ServiceCollectionExtensions.AddWaslaInfrastructure"/>), each provider
/// pointed at its own origin on 127.0.0.1. Every credential is fake and no request leaves the machine.
/// </summary>
public sealed class ProviderHttpClientRegistrationTests
{
    private static readonly OrderFetchWindow Window = new(DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow);

    private static readonly string[] ProviderClientNames =
        [YemeksepetiFoodPlatformClient.YemeksepetiHttpClientName, TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName];

    [Fact]
    public async Task RealMode_EachProvider_HasItsOwnClient_Origin_HandlerPool_AndCredentials()
    {
        await using var trendyolOrigin = await LoopbackProviderOrigin.StartAsync();
        await using var yemeksepetiOrigin = await LoopbackProviderOrigin.StartAsync();
        yemeksepetiOrigin.AccessToken = "yemeksepeti-token";
        var built = new ConcurrentQueue<(string Name, HttpMessageHandler Primary)>();
        await using var services = Services(trendyolOrigin.BaseAddress, yemeksepetiOrigin.BaseAddress, s =>
            // Outermost, so it sees each handler after every configuration and filter has run.
            s.Insert(0, ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter>(new HandlerObserver(built))));

        // Sequential calls, a new scope each, alternating providers and tenants.
        foreach (var tenant in new[] { "a", "b" })
        {
            await using (var scope = services.CreateAsyncScope())
                await Trendyol(scope).FetchOrdersAsync(TrendyolConnection(tenant), Window, CancellationToken.None);
            await using (var scope = services.CreateAsyncScope())
                await Yemeksepeti(scope).FetchOrdersAsync(YemeksepetiConnection(tenant), Window, CancellationToken.None);
        }

        // Overlapping calls of both providers. Each origin holds its requests until all of them have arrived; the
        // Yemeksepeti tokens are cached, so each of those fetches is one request.
        trendyolOrigin.HoldUntil(3);
        yemeksepetiOrigin.HoldUntil(2);
        await Task.WhenAll(
            new[] { "t0", "t1", "t2" }.Select(async tenant =>
            {
                await using var scope = services.CreateAsyncScope();
                await Trendyol(scope).FetchOrdersAsync(TrendyolConnection(tenant), Window, CancellationToken.None);
            }).Concat(new[] { "a", "b" }.Select(async tenant =>
            {
                await using var scope = services.CreateAsyncScope();
                await Yemeksepeti(scope).FetchOrdersAsync(YemeksepetiConnection(tenant), Window, CancellationToken.None);
            })));

        // One registered name per provider, one pooled handler per name for every scope, never shared.
        Assert.Equal(
            ProviderClientNames.Order(),
            services.GetServices<ProviderHttpClientRegistration.ProviderHttpClientName>().Select(n => n.Name).Order());
        Assert.Equal(ProviderClientNames.Order(), built.Select(b => b.Name).Order());
        Assert.Equal(2, built.Select(b => b.Primary).Distinct(ReferenceEqualityComparer.Instance).Count());
        foreach (var (name, primary) in built)
        {
            var sockets = Assert.IsType<SocketsHttpHandler>(primary);
            Assert.False(sockets.UseCookies, $"Provider client '{name}' stores and replays cookies.");
            Assert.False(sockets.AllowAutoRedirect, $"Provider client '{name}' follows redirects.");
        }

        var factory = services.GetRequiredService<IHttpClientFactory>();
        Assert.Equal(trendyolOrigin.BaseAddress, factory.CreateClient(TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName).BaseAddress);
        Assert.Equal(yemeksepetiOrigin.BaseAddress, factory.CreateClient(YemeksepetiFoodPlatformClient.YemeksepetiHttpClientName).BaseAddress);

        AssertOnlyTrendyolRequests(trendyolOrigin, expectedCount: 5);
        AssertOnlyYemeksepetiRequests(yemeksepetiOrigin, expectedCount: 6, "yemeksepeti-token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherProviderOnTheSharedInterface_CannotTakeOverTrendyolOrYemeksepetiConfiguration(bool throughProviderRegistration)
    {
        await using var trendyolOrigin = await LoopbackProviderOrigin.StartAsync();
        await using var yemeksepetiOrigin = await LoopbackProviderOrigin.StartAsync();
        await using var otherOrigin = await LoopbackProviderOrigin.StartAsync();
        yemeksepetiOrigin.AccessToken = "yemeksepeti-token";
        await using var services = Services(trendyolOrigin.BaseAddress, yemeksepetiOrigin.BaseAddress, s =>
        {
            if (throughProviderRegistration)
            {
                // How a future provider must register.
                s.AddProviderHttpClient("OtherProvider", (_, client) => client.BaseAddress = otherOrigin.BaseAddress)
                    .AddTypedClient<IFoodPlatformClient, OtherProviderClient>();
            }
            else
            {
                // The collision-prone pattern: a typed client named after the shared interface.
                s.AddHttpClient<IFoodPlatformClient, OtherProviderClient>(client => client.BaseAddress = otherOrigin.BaseAddress);
            }
        });

        await using (var scope = services.CreateAsyncScope())
        {
            var clients = scope.ServiceProvider.GetServices<IFoodPlatformClient>().ToArray();
            await clients.OfType<TrendyolGoFoodPlatformClient>().Single()
                .FetchOrdersAsync(TrendyolConnection("a"), Window, CancellationToken.None);
            await clients.OfType<YemeksepetiFoodPlatformClient>().Single()
                .FetchOrdersAsync(YemeksepetiConnection("a"), Window, CancellationToken.None);
            await clients.OfType<OtherProviderClient>().Single()
                .FetchOrdersAsync(TrendyolConnection("other"), Window, CancellationToken.None);
        }

        Assert.True(otherOrigin.Requests.Count == 1, "The other provider's origin received:" + Environment.NewLine + string.Join(Environment.NewLine, otherOrigin.Requests));
        AssertOnlyTrendyolRequests(trendyolOrigin, expectedCount: 1);
        AssertOnlyYemeksepetiRequests(yemeksepetiOrigin, expectedCount: 2, "yemeksepeti-token");
        var other = otherOrigin.Requests[0];
        Assert.Equal("GET /other/orders", $"{other.Method} {other.Path}");
        Assert.Equal("Bearer other-provider-token", other.Header("Authorization"));
        Assert.Null(other.Header("x-executor-user"));
        Assert.Null(other.Header("x-agentname"));
        Assert.All(trendyolOrigin.Requests.Concat(yemeksepetiOrigin.Requests), r => Assert.Null(r.Header(OtherProviderClient.KeyHeader)));
    }

    [Fact]
    public void ProviderClientName_CanBeRegisteredOnlyOnce()
    {
        var services = new ServiceCollection();
        services.AddWaslaInfrastructure(Configuration(new Uri("http://127.0.0.1:9/"), new Uri("http://127.0.0.1:9/")));

        foreach (var name in ProviderClientNames)
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                services.AddProviderHttpClient(name, (_, client) => client.BaseAddress = new Uri("http://127.0.0.1:9/other/")));
            Assert.Contains($"'{name}'", ex.Message, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string> ForeignClientConfigurations() => ["named client", "client defaults"];

    [Theory]
    [MemberData(nameof(ForeignClientConfigurations))]
    public async Task ForeignConfigurationOfAProviderClient_FailsInsteadOfMovingIt(string configuration)
    {
        await using var providerOrigin = await LoopbackProviderOrigin.StartAsync();
        await using var foreignOrigin = await LoopbackProviderOrigin.StartAsync();
        await using var services = Services(providerOrigin.BaseAddress, providerOrigin.BaseAddress, s =>
        {
            if (configuration == "named client")
                s.AddHttpClient(TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName, client => client.BaseAddress = foreignOrigin.BaseAddress);
            else
                s.ConfigureHttpClientDefaults(builder => builder.ConfigureHttpClient(client => client.BaseAddress = foreignOrigin.BaseAddress));
        });

        string[] affected = configuration == "named client"
            ? [TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName]
            : ProviderClientNames;

        // At host startup (Web, Api and Worker run the startup validator when they start)...
        var startup = Record.Exception(() => services.GetRequiredService<IStartupValidator>().Validate());
        Assert.NotNull(startup);
        var failures = startup is AggregateException aggregate ? aggregate.InnerExceptions.ToArray() : [startup];
        Assert.Equal(
            affected.Order(),
            failures.Select(f => Assert.IsType<OptionsValidationException>(f).OptionsName).Order());

        // ...and, without a host, when the client is first created.
        await using (var scope = services.CreateAsyncScope())
        {
            var resolution = Assert.Throws<OptionsValidationException>(() => scope.ServiceProvider.GetServices<IFoodPlatformClient>().ToArray());
            Assert.Contains(resolution.OptionsName, affected);
            Assert.Contains($"'{resolution.OptionsName}'", resolution.Message, StringComparison.Ordinal);
        }

        Assert.Empty(foreignOrigin.Requests);
        Assert.Empty(providerOrigin.Requests);
    }

    public static TheoryData<string> LaterHandlerOverrides() =>
    [
        "named SocketsHttpHandler",
        "named HttpClientHandler",
        "client defaults",
        "later filter"
    ];

    [Theory]
    [MemberData(nameof(LaterHandlerOverrides))]
    public async Task LaterHandlerOverride_CannotTurnCookiesOrRedirectsBackOn(string overrideKind)
    {
        await using var provider = await LoopbackProviderOrigin.StartAsync();
        await using var target = await LoopbackProviderOrigin.StartAsync();
        // Tenant r's first request is redirected; the target answers like the provider would.
        provider.Redirect = request =>
            request.Path.Contains("/suppliers/supplier-r/", StringComparison.Ordinal)
            || request.Body.Contains("client_id=client-r", StringComparison.Ordinal)
                ? (302, new Uri(target.BaseAddress, request.Path.TrimStart('/')))
                : null;
        await using var services = Services(provider.BaseAddress, provider.BaseAddress, s => AddHandlerOverride(s, overrideKind));

        var outcomes = new List<string>();
        foreach (var tenant in new[] { "a", "b", "r" })
        {
            await using var scope = services.CreateAsyncScope();
            outcomes.Add(await Outcome($"TrendyolGo {tenant}", () => Trendyol(scope).FetchOrdersAsync(TrendyolConnection(tenant), Window, CancellationToken.None)));
        }

        foreach (var tenant in new[] { "a", "b", "r" })
        {
            await using var scope = services.CreateAsyncScope();
            outcomes.Add(await Outcome($"Yemeksepeti {tenant}", () => Yemeksepeti(scope).FetchOrdersAsync(YemeksepetiConnection(tenant), Window, CancellationToken.None)));
        }

        Assert.Equal(
            ["TrendyolGo a: ok", "TrendyolGo b: ok", "TrendyolGo r: 302", "Yemeksepeti a: ok", "Yemeksepeti b: ok", "Yemeksepeti r: 302"],
            outcomes);
        Assert.True(target.Requests.Count == 0, "The redirect target received:" + Environment.NewLine + string.Join(Environment.NewLine, target.Requests));
        var withCookies = provider.Requests.Where(r => r.Header("Cookie") is not null).Select(r => r.ToString()).ToArray();
        Assert.True(withCookies.Length == 0, "Requests sent cookies:" + Environment.NewLine + string.Join(Environment.NewLine, withCookies));
        Assert.Equal(8, provider.Requests.Count);
    }

    [Fact]
    public async Task PrimaryHandlerWithoutCookieAndRedirectControl_FailsTheProviderClient()
    {
        var handler = new RecordingHandler();
        await using var services = Services(new Uri("http://127.0.0.1:9/"), new Uri("http://127.0.0.1:9/"), s =>
            s.AddHttpClient(TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));
        await using var scope = services.CreateAsyncScope();

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetServices<IFoodPlatformClient>().ToArray());

        Assert.Contains($"'{TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RecordingHandler), ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>
    /// Web, Api and Worker get provider clients only from <see cref="ServiceCollectionExtensions.AddWaslaInfrastructure"/>.
    /// Starting the real hosts in a test would need their master key, Data Protection folder and log files, so this
    /// checks their source instead: a host that builds or configures HttpClient itself, or touches the provider clients,
    /// fails here. The guard above already keeps cookies and redirects off if a host reconfigures a provider client.
    /// </summary>
    [Fact]
    public void HostProjects_ComposeProviderHttpOnlyThroughTheInfrastructureRegistration()
    {
        var forbidden = new Regex(
            @"\b(AddHttpClient|ConfigurePrimaryHttpMessageHandler|ConfigureHttpClientDefaults|IHttpMessageHandlerBuilderFilter|HttpMessageHandlerBuilder|UseSocketsHttpHandler|RedactLoggedHeaders|AddProviderHttpClient|TrendyolGoFoodPlatformClient|YemeksepetiFoodPlatformClient|IHttpClientFactory)\b|new\s+(HttpClient|SocketsHttpHandler|HttpClientHandler)\s*\(",
            RegexOptions.CultureInvariant);
        var root = RepositoryRoot();
        var findings = new List<string>();

        foreach (var host in new[] { "Wasla.Web", "Wasla.Api", "Wasla.Worker" })
        {
            var directory = Path.Combine(root, "src", host);
            var sources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(Path.GetRelativePath(directory, f)))
                .ToArray();
            Assert.NotEmpty(sources);

            foreach (var file in sources)
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (forbidden.IsMatch(lines[i]))
                        findings.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }

            var program = File.ReadAllText(Path.Combine(directory, "Program.cs"));
            Assert.Contains("AddWaslaInfrastructure(builder.Configuration)", program, StringComparison.Ordinal);
        }

        Assert.True(findings.Count == 0, "Host code composes HTTP clients itself:" + Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    private static void AddHandlerOverride(IServiceCollection services, string overrideKind)
    {
        switch (overrideKind)
        {
            case "named SocketsHttpHandler":
                foreach (var name in ProviderClientNames)
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler());
                break;
            case "named HttpClientHandler":
                foreach (var name in ProviderClientNames)
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler());
                break;
            case "client defaults":
                services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler()));
                break;
            case "later filter":
                services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new PrimaryHandlerReplacingFilter());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(overrideKind), overrideKind, null);
        }
    }

    private static void AssertOnlyTrendyolRequests(LoopbackProviderOrigin origin, int expectedCount)
    {
        Assert.True(origin.Requests.Count == expectedCount, $"Expected {expectedCount} requests; received:{Environment.NewLine}{string.Join(Environment.NewLine, origin.Requests)}");
        foreach (var request in origin.Requests)
        {
            var match = Regex.Match(request.Path, "^/integrator/order/meal/suppliers/supplier-([^/]+)/packages$");
            Assert.True(match.Success, $"Not a Trendyol GO request: {request}");
            var tenant = match.Groups[1].Value;
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"api-key-{tenant}:api-secret-{tenant}"));
            Assert.Equal($"Basic {basic}", request.Header("Authorization"));
            Assert.Equal($"executor-{tenant}@wasla.test", request.Header("x-executor-user"));
            Assert.Null(request.Header("Cookie"));
        }
    }

    private static void AssertOnlyYemeksepetiRequests(LoopbackProviderOrigin origin, int expectedCount, string accessToken)
    {
        Assert.True(origin.Requests.Count == expectedCount, $"Expected {expectedCount} requests; received:{Environment.NewLine}{string.Join(Environment.NewLine, origin.Requests)}");
        foreach (var request in origin.Requests)
        {
            Assert.True(request.Path.StartsWith("/v2/", StringComparison.Ordinal), $"Not a Yemeksepeti request: {request}");
            Assert.Null(request.Header("x-executor-user"));
            Assert.Null(request.Header("x-agentname"));
            Assert.Null(request.Header("Cookie"));
            if (request.Path == "/v2/oauth/token")
                Assert.Null(request.Header("Authorization"));
            else
                Assert.Equal($"Bearer {accessToken}", request.Header("Authorization"));
        }
    }

    private static async Task<string> Outcome(string operation, Func<Task> call)
    {
        try
        {
            await call();
            return $"{operation}: ok";
        }
        catch (ProviderRequestException ex)
        {
            return $"{operation}: {(int?)ex.StatusCode}";
        }
    }

    private static TrendyolGoFoodPlatformClient Trendyol(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetServices<IFoodPlatformClient>().OfType<TrendyolGoFoodPlatformClient>().Single();

    private static YemeksepetiFoodPlatformClient Yemeksepeti(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetServices<IFoodPlatformClient>().OfType<YemeksepetiFoodPlatformClient>().Single();

    private static ServiceProvider Services(Uri trendyol, Uri yemeksepeti, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaInfrastructure(Configuration(trendyol, yemeksepeti));
        services.AddSingleton<ISecretManager, PassthroughSecrets>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(Uri trendyol, Uri yemeksepeti) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platforms:ProviderMode"] = "Real",
                ["ConnectionStrings:CentralDb"] = "Server=unused;Database=unused",
                ["Platform:TrendyolGo:BaseUrl"] = trendyol.AbsoluteUri,
                ["Platform:Yemeksepeti:BaseUrl"] = yemeksepeti.AbsoluteUri,
                ["Platform:Yemeksepeti:TokenPath"] = "/v2/oauth/token"
            })
            .Build();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.GetFiles("Wasla.sln").Any())
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Wasla.sln.");
    }

    private static bool IsBuildOutput(string relativePath)
    {
        var first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }

    private sealed class HandlerObserver(ConcurrentQueue<(string Name, HttpMessageHandler Primary)> built)
        : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            built.Enqueue((builder.Name ?? string.Empty, builder.PrimaryHandler));
        };
    }

    /// <summary>A filter a host might add later: replaces the primary handler with a default (cookies and redirects on).</summary>
    private sealed class PrimaryHandlerReplacingFilter : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = new SocketsHttpHandler();
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>A hypothetical further provider on the shared interface, with its own credential header.</summary>
    private sealed class OtherProviderClient(HttpClient httpClient) : IFoodPlatformClient
    {
        public const string KeyHeader = "x-other-provider-key";

        public FoodPlatform Platform => FoodPlatform.GetirYemek;

        public TimeSpan? MaxFetchWindow => null;

        public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/other/orders");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "other-provider-token");
            request.Headers.Add(KeyHeader, "other-provider-key-value");
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return [];
        }

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => throw new NotSupportedException();

        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => throw new NotSupportedException();

        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => throw new NotSupportedException();

        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => throw new NotSupportedException();

        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) => throw new NotSupportedException();
    }
}
