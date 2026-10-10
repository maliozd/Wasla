using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Wasla.UnitTests.Platform;

internal sealed record LoopbackRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public override string ToString() =>
        $"{Method} {Path} [{string.Join("; ", Headers.Select(h => $"{h.Key}: {h.Value}"))}] body=[{Body}]";
}

/// <summary>
/// A Trendyol GO and Yemeksepeti stand-in on 127.0.0.1 that records every request, headers and body included. It answers
/// token, orders, packages and any other path with provider-like success bodies, and every response sets a cookie, so a
/// stored cookie would show up on a later request. Credentials are fake and nothing leaves the machine.
/// </summary>
internal sealed class LoopbackProviderOrigin : IAsyncDisposable
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(30);

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<LoopbackRequest> _requests = new();
    private Hold? _hold;

    private LoopbackProviderOrigin(WebApplication app) => _app = app;

    public Uri BaseAddress { get; private set; } = null!;

    public IReadOnlyList<LoopbackRequest> Requests => _requests.ToArray();

    /// <summary>The access token a token request receives.</summary>
    public string AccessToken { get; set; } = "token-from-loopback";

    /// <summary>The value of the cookie every response sets.</summary>
    public string CookieValue { get; set; } = "seeded";

    /// <summary>Extra response headers, set on every response.</summary>
    public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A redirect for the request, or null to answer it normally.</summary>
    public Func<LoopbackRequest, (int Status, Uri Location)?> Redirect { get; set; } = _ => null;

    /// <summary>A failure status and body for the request, or null to answer it normally.</summary>
    public Func<LoopbackRequest, (int Status, string Body)?> Fail { get; set; } = _ => null;

    /// <summary>Holds the next <paramref name="count"/> requests until all of them have arrived.</summary>
    public void HoldUntil(int count) => Volatile.Write(ref _hold, new Hold(count));

    public static async Task<LoopbackProviderOrigin> StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var origin = new LoopbackProviderOrigin(app);
        app.Run(origin.HandleAsync);
        await app.StartAsync();
        origin.BaseAddress = new Uri(app.Urls.Single());
        return origin;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        using var reader = new StreamReader(request.Body);
        var received = new LoopbackRequest(
            request.Method,
            request.Path.Value ?? string.Empty,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            await reader.ReadToEndAsync(context.RequestAborted));
        _requests.Enqueue(received);

        if (Volatile.Read(ref _hold) is { } hold)
            await hold.ArriveAndWaitAsync(context.RequestAborted);

        var response = context.Response;
        response.Headers.Append("Set-Cookie", $"wasla_fake_session={CookieValue}; Path=/");
        foreach (var (name, value) in ResponseHeaders)
            response.Headers.Append(name, value);

        if (Redirect(received) is { } redirect)
        {
            response.Headers.Location = redirect.Location.AbsoluteUri;
            response.StatusCode = redirect.Status;
            return;
        }

        if (Fail(received) is { } failure)
        {
            response.StatusCode = failure.Status;
            await response.WriteAsync(failure.Body, context.RequestAborted);
            return;
        }

        var segments = received.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var body = segments switch
        {
            [.., "oauth", "token"] => $$"""{"access_token":"{{AccessToken}}","token_type":"bearer","expires_in":7200}""",
            ["v2", "chains", _, "vendors", var vendor, "orders"] =>
                $$"""{"data":[{"id":"order-{{vendor}}","status":"NEW"}],"page":0,"total_pages":1,"total_count":1}""",
            ["integrator", "order", "meal", "suppliers", var supplier, "packages"] =>
                $$"""{"page":0,"size":50,"totalPages":1,"totalCount":1,"content":[{"id":"package-{{supplier}}","packageStatus":"Created","packageCreationDate":1700000000000,"totalPrice":10}]}""",
            _ => "{}"
        };
        response.ContentType = "application/json";
        await response.WriteAsync(body, context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class Hold(int count)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = count;

        public async Task ArriveAndWaitAsync(CancellationToken ct)
        {
            var left = Interlocked.Decrement(ref _remaining);
            if (left == 0)
                _allArrived.TrySetResult();
            if (left >= 0)
                await _allArrived.Task.WaitAsync(HoldTimeout, ct);
        }
    }
}
