using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Realtime;

/// <summary>
/// The internal channel that carries tenant change notifications from Wasla.Worker and Wasla.Api to Wasla.Web, which
/// owns the browsers' live connections. Section <c>Realtime:Internal</c>, the same in every process:
/// <list type="bullet">
/// <item><c>Port</c>: Wasla.Web listens on <c>127.0.0.1:Port</c> only, on a listener separate from its public one;
/// the other processes post there. Empty means the channel is off.</item>
/// <item><c>Secret</c>: shared by the processes, at least <see cref="MinimumSecretLength"/> characters, sent in
/// <see cref="SecretHeaderName"/>. Never committed; set it as an environment variable.</item>
/// <item><c>TimeoutSeconds</c>: how long a sender waits for Wasla.Web (default 3, longer than Web's own 2-second bound
/// on handing the notification to the hub, so a slow hub send is not mistaken for an unreachable Web).</item>
/// </list>
/// </summary>
public sealed class InternalRealtimeOptions
{
    public const string SectionName = "Realtime:Internal";
    public const string SecretHeaderName = "X-Wasla-Realtime-Secret";
    public const string Path = "/internal/tenant-changes";
    public const int MinimumSecretLength = 32;

    public int? Port { get; set; }
    public string? Secret { get; set; }
    public double TimeoutSeconds { get; set; } = 3;

    public bool IsEnabled => Port is not null;

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    public Uri Endpoint => new($"http://{IPAddress.Loopback}:{Port}{Path}");

    public static InternalRealtimeOptions Read(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<InternalRealtimeOptions>() ?? new InternalRealtimeOptions();

    /// <summary>
    /// The problems that stop a process from starting with this configuration. The messages name keys, never values.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        if (!IsEnabled)
            return [];

        var errors = new List<string>();
        if (Port is < 1 or > 65535)
            errors.Add($"{SectionName}:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(Secret) || Secret.Trim().Length < MinimumSecretLength)
            errors.Add($"{SectionName}:Secret must be set to at least {MinimumSecretLength} characters when {SectionName}:Port is set.");
        else if (Secret.Trim().Any(c => c is < '!' or > '~'))
            errors.Add($"{SectionName}:Secret must contain only printable ASCII characters without spaces (it is sent as an HTTP header).");
        if (TimeoutSeconds is < 0.1 or > 10)
            errors.Add($"{SectionName}:TimeoutSeconds must be between 0.1 and 10.");
        return errors;
    }

    public void ThrowIfInvalid()
    {
        var errors = Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
    }
}

/// <summary>
/// The internal wire format. Only identifiers, a status name and the version travel; the tenant id is set by the
/// sending service from its own server-side context. <c>PrintJobId</c> and <c>Status</c> belong to
/// <see cref="PrintJobChanged"/> only and are absent for <see cref="OrderChanged"/>.
/// </summary>
public sealed record InternalTenantChangeMessage(
    Guid TenantId,
    string Type,
    Guid OrderId,
    Guid? PrintJobId,
    string? Status,
    long Version)
{
    /// <summary>Whether this channel carries the change; anything else is dropped by the sender.</summary>
    public static bool CanCarry(TenantChange change) => change is PrintJobChanged or OrderChanged;

    public static InternalTenantChangeMessage From(Guid tenantId, TenantChange change) => change switch
    {
        PrintJobChanged p => new(tenantId, PrintJobChanged.TypeName, p.OrderId, p.PrintJobId, p.Status.ToString(), p.Version),
        OrderChanged o => new(tenantId, OrderChanged.TypeName, o.OrderId, null, null, o.Version),
        _ => throw new ArgumentOutOfRangeException(nameof(change), change.GetType().Name, "Unknown tenant change.")
    };

    /// <summary>Accepts only a complete, known message; anything else is refused without a partial result.</summary>
    public bool TryRead(out Guid tenantId, out TenantChange? change)
    {
        tenantId = TenantId;
        change = null;
        if (TenantId == Guid.Empty || OrderId == Guid.Empty || Version < 1)
            return false;

        if (string.Equals(Type, OrderChanged.TypeName, StringComparison.Ordinal))
        {
            if (PrintJobId is not null || Status is not null)
                return false;
            change = new OrderChanged(OrderId, Version);
            return true;
        }

        if (!string.Equals(Type, PrintJobChanged.TypeName, StringComparison.Ordinal))
            return false;
        if (PrintJobId is not { } printJobId || printJobId == Guid.Empty)
            return false;
        if (!Enum.TryParse<PrintJobStatus>(Status, ignoreCase: false, out var status)
            || !Enum.IsDefined(status)
            || int.TryParse(Status, out _))
            return false;

        change = new PrintJobChanged(OrderId, printJobId, status, Version);
        return true;
    }
}

/// <summary>
/// Sends tenant change notifications to Wasla.Web over <see cref="InternalRealtimeOptions"/>. It waits for at most
/// the configured timeout and never throws. After a failure it stops trying for <see cref="SuspendAfterFailure"/>, so
/// an unreachable Wasla.Web cannot slow order synchronization or Print Bridge reports down. What is not delivered
/// reaches a screen only through its read on reconnect or its fallback while disconnected (docs/architecture/realtime.md).
/// </summary>
public sealed class WebTenantChangeNotifier : ITenantChangeNotifier
{
    public const string HttpClientName = "WaslaRealtimeInternal";
    public static readonly TimeSpan SuspendAfterFailure = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClients;
    private readonly InternalRealtimeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<WebTenantChangeNotifier> _logger;
    private readonly RealtimeMetrics _metrics;
    private readonly object _gate = new();
    private DateTimeOffset _suspendedUntil = DateTimeOffset.MinValue;
    private bool _suspended;

    public WebTenantChangeNotifier(
        IHttpClientFactory httpClients,
        InternalRealtimeOptions options,
        TimeProvider time,
        ILogger<WebTenantChangeNotifier> logger,
        RealtimeMetrics metrics)
    {
        _httpClients = httpClients;
        _options = options;
        _time = time;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_suspended && _time.GetUtcNow() < _suspendedUntil)
            {
                _metrics.NotificationHandled(RealtimeMetrics.Channels.Internal, change, RealtimeMetrics.NotificationResults.Paused);
                return;
            }
        }

        // A change this channel cannot carry is dropped without pausing the channel for the others.
        if (!InternalTenantChangeMessage.CanCarry(change))
        {
            _metrics.NotificationHandled(RealtimeMetrics.Channels.Internal, change, RealtimeMetrics.NotificationResults.Unsupported);
            return;
        }

        var message = InternalTenantChangeMessage.From(tenantId, change);
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            timeout.CancelAfter(_options.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = JsonContent.Create(message)
            };
            request.Headers.Add(InternalRealtimeOptions.SecretHeaderName, _options.Secret!.Trim());

            using var response = await _httpClients.CreateClient(HttpClientName)
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Record(change, RealtimeMetrics.NotificationResults.Rejected, started);
                Suspend(RealtimeMetrics.NotificationResults.Rejected, $"HTTP {(int)response.StatusCode}");
                return;
            }

            Record(change, RealtimeMetrics.NotificationResults.Sent, started);
            lock (_gate)
            {
                if (_suspended)
                {
                    _suspended = false;
                    _logger.LogInformation("Live notifications to Wasla.Web are delivered again.");
                }
            }
        }
        catch (Exception ex)
        {
            var result = ex switch
            {
                OperationCanceledException when ct.IsCancellationRequested => RealtimeMetrics.NotificationResults.Cancelled,
                OperationCanceledException when timeout.IsCancellationRequested => RealtimeMetrics.NotificationResults.Timeout,
                HttpRequestException => RealtimeMetrics.NotificationResults.Unreachable,
                _ => RealtimeMetrics.NotificationResults.Failed
            };
            Record(change, result, started);
            Suspend(result, ex.GetType().Name);
        }
    }

    private void Record(TenantChange change, string result, long started) =>
        _metrics.NotificationHandled(RealtimeMetrics.Channels.Internal, change, result, RealtimeMetrics.Since(started));

    /// <param name="category">The bounded metric reason (<see cref="RealtimeMetrics.NotificationResults"/>).</param>
    /// <param name="detail">An HTTP status or exception type for the log; never a message, URL or secret.</param>
    private void Suspend(string category, string detail)
    {
        lock (_gate)
        {
            var wasSuspended = _suspended;
            _suspended = true;
            _suspendedUntil = _time.GetUtcNow() + SuspendAfterFailure;
            if (!wasSuspended)
            {
                _metrics.InternalChannelPaused(category);
                _logger.LogWarning(
                    "Live notification to Wasla.Web failed ({Reason}); notifications pause for {Seconds} seconds and open screens fall back to refreshing.",
                    detail,
                    SuspendAfterFailure.TotalSeconds);
            }
        }
    }
}

public static class InternalRealtimeServiceCollectionExtensions
{
    /// <summary>
    /// For Wasla.Worker and Wasla.Api: sends this process's tenant change notifications to Wasla.Web when
    /// <c>Realtime:Internal</c> is configured, and keeps the default (drop them) otherwise. An invalid configuration
    /// stops the process at startup.
    /// </summary>
    public static IServiceCollection AddWaslaWebTenantChangeNotifier(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = InternalRealtimeOptions.Read(configuration);
        options.ThrowIfInvalid();
        if (!options.IsEnabled)
            return services;

        services.AddSingleton(options);
        services.AddWaslaRealtimeMetrics();
        services.AddHttpClient(WebTenantChangeNotifier.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A loopback call with a shared secret: no cookies, redirects or proxies.
                UseCookies = false,
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = options.Timeout
            });
        services.RemoveAll<ITenantChangeNotifier>();
        services.AddSingleton<ITenantChangeNotifier, WebTenantChangeNotifier>();
        return services;
    }

    /// <summary>The <see cref="RealtimeMetrics"/> of this process (meter <c>Wasla.Realtime</c>); safe to call more than once.</summary>
    public static IServiceCollection AddWaslaRealtimeMetrics(this IServiceCollection services)
    {
        services.AddMetrics();
        services.TryAddSingleton<RealtimeMetrics>();
        return services;
    }

    /// <summary>
    /// Logs at startup whether this process's changes reach Wasla.Web, so a missing <c>Realtime:Internal</c> never
    /// silently turns off live Live Screen and print status updates for them.
    /// </summary>
    public static void LogWebTenantChangeChannel(this IServiceProvider services, IConfiguration configuration, string process)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Wasla.Realtime");
        if (InternalRealtimeOptions.Read(configuration).IsEnabled)
        {
            logger.LogInformation("{Process} sends live change notifications to Wasla.Web over the loopback channel.", process);
            return;
        }

        logger.LogWarning(
            "{Process}: {Section} is not configured, so changes made here (orders, automatic approvals, receipts, Print Bridge reports) are not pushed to open screens; they show at the screens' next poll. See docs/operations/deployment.md#live-notifications.",
            process,
            InternalRealtimeOptions.SectionName);
    }
}
