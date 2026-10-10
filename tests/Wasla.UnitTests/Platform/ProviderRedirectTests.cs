using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Platform.Dtos;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.Yemeksepeti;
using static Wasla.UnitTests.Platform.ProviderCookieIsolationTests;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// WAS-95: provider clients never follow redirects. A followed redirect re-sends the request to the URL the response
/// names: a 307 or 308 repeats the Yemeksepeti token request body (client id and secret), and every redirect repeats
/// the Trendyol GO identifying headers. Any 3xx is therefore a failed provider request. These tests resolve the
/// clients from the production Real-mode registration and point them at a fake provider on 127.0.0.1 that redirects
/// either to another path of its own origin or to a second local origin. Every credential is fake and no request
/// leaves the machine.
/// </summary>
public sealed class ProviderRedirectTests
{
    private static readonly OrderFetchWindow Window = new(DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow);

    public static TheoryData<int, bool> Redirects()
    {
        var data = new TheoryData<int, bool>();
        foreach (var status in new[] { 301, 302, 303, 307, 308 })
        {
            data.Add(status, false);
            data.Add(status, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Redirects))]
    public async Task EveryProviderRequest_FailsOnARedirect_AndTheRedirectTargetReceivesNothing(int status, bool crossOrigin)
    {
        // Both origins answer every request like the provider would, so a followed redirect would succeed.
        await using var target = await LocalOrigin.StartAsync();
        await using var provider = await LocalOrigin.StartAsync();
        var targetBase = crossOrigin ? target.BaseAddress : new Uri(provider.BaseAddress, "/moved/");
        provider.Redirect = request =>
            // A same-origin target is answered normally. Tenant B's token request succeeds so its orders request can
            // be redirected.
            request.Path.StartsWith("/moved/", StringComparison.Ordinal)
            || (request.Path == "/v2/oauth/token" && request.Body.Contains("client_id=client-b", StringComparison.Ordinal))
                ? null
                : (status, new Uri(targetBase, request.Path.TrimStart('/')));
        await using var services = ProductionServices(provider.BaseAddress);
        await using var scope = services.CreateAsyncScope();
        var clients = scope.ServiceProvider.GetServices<IFoodPlatformClient>().ToArray();
        var trendyol = clients.OfType<TrendyolGoFoodPlatformClient>().Single();
        var yemeksepeti = clients.OfType<YemeksepetiFoodPlatformClient>().Single();

        var outcomes = new[]
        {
            await Outcome("TrendyolGo FetchOrders", () => trendyol.FetchOrdersAsync(TrendyolConnection("a"), Window, CancellationToken.None)),
            await Outcome("TrendyolGo AcceptOrder", () => trendyol.AcceptOrderAsync(TrendyolConnection("a"), "package-1", 15, CancellationToken.None)),
            await Outcome("Yemeksepeti Token", () => yemeksepeti.FetchOrdersAsync(YemeksepetiConnection("a"), Window, CancellationToken.None)),
            await Outcome("Yemeksepeti FetchOrders", () => yemeksepeti.FetchOrdersAsync(YemeksepetiConnection("b"), Window, CancellationToken.None))
        };

        var redirected = (crossOrigin ? target.Requests : provider.Requests.Where(r => r.Path.StartsWith("/moved/", StringComparison.Ordinal)))
            .Select(r => r.ToString())
            .ToArray();
        Assert.True(redirected.Length == 0, "The redirect target received:" + Environment.NewLine + string.Join(Environment.NewLine, redirected));

        // Every operation failed with the provider's own redirect status; nothing was retried or followed.
        Assert.Equal(
            [
                $"TrendyolGo FetchOrders: {status}",
                $"TrendyolGo AcceptOrder: {status}",
                $"Yemeksepeti Token: {status}",
                $"Yemeksepeti FetchOrders: {status}"
            ],
            outcomes);
        Assert.Equal(
            [
                "GET /integrator/order/meal/suppliers/supplier-a/packages",
                "PUT /integrator/order/meal/suppliers/supplier-a/packages/picked",
                "POST /v2/oauth/token",
                "POST /v2/oauth/token",
                "GET /v2/chains/chain-b/vendors/vendor-b/orders"
            ],
            provider.Requests.Select(r => $"{r.Method} {r.Path}"));

        // Every redirect response set cookies; none was sent with a later request.
        Assert.All(provider.Requests, r => Assert.Null(r.Cookie));
    }

    private static async Task<string> Outcome(string operation, Func<Task> call)
    {
        try
        {
            await call();
            return $"{operation}: succeeded";
        }
        catch (ProviderRequestException ex)
        {
            return $"{operation}: {(int?)ex.StatusCode}";
        }
    }

    private sealed record ReceivedRequest(string Method, string Path, string Headers, string Body, string? Cookie)
    {
        public override string ToString() => $"{Method} {Path} [{Headers}] body=[{Body}]";
    }

    /// <summary>
    /// One origin on 127.0.0.1. It answers token, orders, packages and lifecycle requests with success bodies, or
    /// with the redirect <see cref="Redirect"/> returns. Redirect responses set a cookie.
    /// </summary>
    private sealed class LocalOrigin : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<ReceivedRequest> _requests = new();

        private LocalOrigin(WebApplication app) => _app = app;

        public Uri BaseAddress { get; private set; } = null!;

        public IReadOnlyList<ReceivedRequest> Requests => _requests.ToArray();

        public Func<ReceivedRequest, (int Status, Uri Location)?> Redirect { get; set; } = _ => null;

        public static async Task<LocalOrigin> StartAsync()
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var origin = new LocalOrigin(app);
            app.Run(origin.HandleAsync);
            await app.StartAsync();
            origin.BaseAddress = new Uri(app.Urls.Single());
            return origin;
        }

        private async Task HandleAsync(HttpContext context)
        {
            var request = context.Request;
            using var reader = new StreamReader(request.Body);
            var received = new ReceivedRequest(
                request.Method,
                request.Path.Value ?? string.Empty,
                string.Join("; ", request.Headers.Select(h => $"{h.Key}: {h.Value}")),
                await reader.ReadToEndAsync(context.RequestAborted),
                request.Headers.Cookie.Count == 0 ? null : request.Headers.Cookie.ToString());
            _requests.Enqueue(received);

            if (Redirect(received) is { } redirect)
            {
                context.Response.Headers.Append("Set-Cookie", "wasla_fake_redirect=seeded; Path=/");
                context.Response.Headers.Location = redirect.Location.AbsoluteUri;
                context.Response.StatusCode = redirect.Status;
                return;
            }

            var path = received.Path;
            var body = path.EndsWith("/oauth/token", StringComparison.Ordinal)
                ? """{"access_token":"token-from-local-origin","expires_in":7200}"""
                : path.EndsWith("/orders", StringComparison.Ordinal)
                    ? """{"data":[],"page":0,"total_pages":1,"total_count":0}"""
                    : path.EndsWith("/packages", StringComparison.Ordinal)
                        ? """{"page":0,"size":50,"totalPages":1,"totalCount":0,"content":[]}"""
                        : "{}";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body, context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
