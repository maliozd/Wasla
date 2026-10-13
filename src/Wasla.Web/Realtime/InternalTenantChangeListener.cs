using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Infrastructure.Realtime;

namespace Wasla.Web.Realtime;

/// <summary>
/// Receives tenant change notifications from Wasla.Worker and Wasla.Api (<see cref="InternalRealtimeOptions"/>) and
/// hands them to this process's hub. It is a second, separate Kestrel listener bound to 127.0.0.1 only, with one
/// route and nothing else: no tenant resolution, cookies, MVC or static files, and the public listener (the one the
/// reverse proxy forwards to) does not serve this path. A request needs the shared secret; the tenant id it names is
/// trusted only because of that, and the most it can cause is a screen reading its own state again.
/// </summary>
public sealed class InternalTenantChangeListener : IHostedService, IAsyncDisposable
{
    private const int MaxBodyBytes = 4 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InternalRealtimeOptions _options;
    private readonly ITenantChangeNotifier _notifier;
    private readonly ILogger<InternalTenantChangeListener> _logger;
    private readonly RealtimeMetrics _metrics;
    private readonly byte[] _secret;
    private WebApplication? _app;

    public InternalTenantChangeListener(
        InternalRealtimeOptions options,
        ITenantChangeNotifier notifier,
        ILogger<InternalTenantChangeListener> logger,
        RealtimeMetrics metrics)
    {
        _options = options;
        _notifier = notifier;
        _logger = logger;
        _metrics = metrics;
        _secret = Encoding.UTF8.GetBytes(options.Secret!.Trim());
    }

    /// <summary>The address it listens on, once started.</summary>
    public Uri? Address => _app?.Urls.Select(u => new Uri(u)).FirstOrDefault();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.AddFilter(_ => false);
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, _options.Port!.Value);
            kestrel.Limits.MaxRequestBodySize = MaxBodyBytes;
        });

        var app = builder.Build();
        app.Run(HandleAsync);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        _logger.LogInformation("Live notifications from Wasla.Worker and Wasla.Api are accepted on {Address}.", Address);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";

        // Defence in depth: only this loopback listener, only this route, only POST.
        if (!IPAddress.IsLoopback(http.Connection.LocalIpAddress ?? IPAddress.None)
            || http.Connection.LocalPort != _options.Port
            || !string.Equals(http.Request.Path.Value, InternalRealtimeOptions.Path, StringComparison.Ordinal))
        {
            _metrics.InternalRequestAnswered(RealtimeMetrics.InternalRequestResults.NotFound);
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpMethods.IsPost(http.Request.Method))
        {
            _metrics.InternalRequestAnswered(RealtimeMetrics.InternalRequestResults.MethodNotAllowed);
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (!HasSecret(http.Request.Headers[InternalRealtimeOptions.SecretHeaderName].ToString()))
        {
            _metrics.InternalRequestAnswered(RealtimeMetrics.InternalRequestResults.Unauthorized);
            _logger.LogWarning("Internal live notification refused: missing or wrong secret.");
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        InternalTenantChangeMessage? message;
        try
        {
            message = await JsonSerializer
                .DeserializeAsync<InternalTenantChangeMessage>(http.Request.Body, Json, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            message = null;
        }

        if (message is null || !message.TryRead(out var tenantId, out var change) || change is null)
        {
            _metrics.InternalRequestAnswered(RealtimeMetrics.InternalRequestResults.Invalid);
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // The hub send is measured by the notifier (channel "hub"); this counts the request itself.
        await _notifier.NotifyAsync(tenantId, change, http.RequestAborted).ConfigureAwait(false);
        _metrics.InternalRequestAnswered(RealtimeMetrics.InternalRequestResults.Accepted);
        http.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    private bool HasSecret(string presented)
    {
        var bytes = Encoding.UTF8.GetBytes(presented);
        return bytes.Length == _secret.Length && CryptographicOperations.FixedTimeEquals(bytes, _secret);
    }
}
