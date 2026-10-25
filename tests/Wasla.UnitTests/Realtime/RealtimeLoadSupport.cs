using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Infrastructure.Realtime;
using Wasla.UnitTests.Auth;
using Wasla.Web.Realtime;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// A lightweight live-hub client for load tests: the real .NET SignalR client over WebSockets (or long polling), sending
/// a tenant host and the session cookies of a signed-in <see cref="TenantSessionClient"/>, as a browser tab does. It
/// records every notification and when its connection closed.
/// </summary>
internal sealed class LiveHubClient : IAsyncDisposable
{
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly Channel<JsonElement> _received = Channel.CreateUnbounded<JsonElement>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _receivedCount;

    private LiveHubClient(HubConnection connection)
    {
        Connection = connection;
    }

    public HubConnection Connection { get; }

    public Task Closed => _closed.Task;

    public int ReceivedCount => Volatile.Read(ref _receivedCount);

    /// <summary>Every notification received so far, in order, without waiting.</summary>
    public List<JsonElement> Drain()
    {
        var all = new List<JsonElement>();
        while (_received.Reader.TryRead(out var message))
            all.Add(message);
        return all;
    }

    public async Task<JsonElement> NextAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        return await _received.Reader.ReadAsync(limit.Token);
    }

    /// <summary>
    /// Connects and waits for the hub's ready message (the groups are joined). Throws when the hub refuses the session
    /// or does not become ready within <see cref="ReadyTimeout"/>.
    /// </summary>
    public static async Task<LiveHubClient> ConnectAsync(
        Uri baseAddress,
        string host,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct,
        HttpTransportType transport = HttpTransportType.WebSockets,
        string? attempt = null,
        TimeSpan? keepAlive = null)
    {
        var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}"));
        var path = TenantEventsHub.Path + (attempt is null ? "" : $"?{RealtimeMetrics.Attempts.QueryParameter}={Uri.EscapeDataString(attempt)}");
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, path), transport, options =>
            {
                options.HttpMessageHandlerFactory = inner => new HostAndCookieHandler(host, cookieHeader) { InnerHandler = inner };
                options.WebSocketConfiguration = socket =>
                {
                    socket.SetRequestHeader("Host", host);
                    socket.SetRequestHeader("Cookie", cookieHeader);
                };
            })
            .Build();
        if (keepAlive is { } interval)
            connection.KeepAliveInterval = interval;
        var client = new LiveHubClient(connection);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(TenantEventsHub.ClientMethod, message =>
        {
            Interlocked.Increment(ref client._receivedCount);
            client._received.Writer.TryWrite(message);
        });
        connection.On(TenantEventsHub.ReadyMethod, () => ready.TrySetResult());
        connection.Closed += _ =>
        {
            client._closed.TrySetResult();
            ready.TrySetException(new InvalidOperationException("The hub closed the connection before it was ready."));
            return Task.CompletedTask;
        };
        try
        {
            await connection.StartAsync(ct);
            await ready.Task.WaitAsync(ReadyTimeout, ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return client;
    }

    public static Task<LiveHubClient> ConnectAsync(
        Uri baseAddress, TenantSessionClient session, CancellationToken ct, string? attempt = null, TimeSpan? keepAlive = null) =>
        ConnectAsync(baseAddress, session.Host, session.Cookies, ct, attempt: attempt, keepAlive: keepAlive);

    public ValueTask DisposeAsync() => Connection.DisposeAsync();

    private sealed class HostAndCookieHandler(string host, string cookies) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Host = host;
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// Records the <c>Wasla.Realtime</c> measurements of one host (only meters created by that host's
/// <see cref="IMeterFactory"/>, so parallel tests do not mix), plus ASP.NET Core's own SignalR connection metrics.
/// </summary>
internal sealed class RealtimeMetricsRecorder : IDisposable
{
    public const string SignalRMeterName = "Microsoft.AspNetCore.Http.Connections";

    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Measurement> _measurements = new();

    public RealtimeMetricsRecorder(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IMeterFactory>();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory)
                && (instrument.Meter.Name == RealtimeMetrics.MeterName || instrument.Meter.Name == SignalRMeterName))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    public sealed record Measurement(string Meter, string Instrument, double Value, IReadOnlyDictionary<string, string> Tags);

    public IReadOnlyList<Measurement> Measurements => _measurements.ToArray();

    /// <summary>The Wasla.Realtime measurements only.</summary>
    public IReadOnlyList<Measurement> Wasla => Measurements.Where(m => m.Meter == RealtimeMetrics.MeterName).ToList();

    /// <summary>The sum of an instrument's values whose tags include every given pair.</summary>
    public double Sum(string instrument, params (string Key, string Value)[] tags) =>
        Measurements
            .Where(m => m.Instrument == instrument && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && v == t.Value))
            .Sum(m => m.Value);

    public int Count(string instrument, params (string Key, string Value)[] tags) =>
        Measurements.Count(m => m.Instrument == instrument && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && v == t.Value));

    /// <summary>Open, admitted hub connections according to <c>wasla.realtime.connections.active</c>.</summary>
    public long ActiveConnections => (long)Sum("wasla.realtime.connections.active");

    /// <summary>Waits until <paramref name="condition"/> holds; returns false after <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitForAsync(Func<RealtimeMetricsRecorder, bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition(this))
        {
            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(50, ct);
        }

        return true;
    }

    /// <summary>A short summary for failure messages: totals per instrument and tag set.</summary>
    public string Describe() =>
        string.Join("; ", Wasla
            .GroupBy(m => m.Instrument + "{" + string.Join(",", m.Tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}")) + "}")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Sum(m => m.Value).ToString(CultureInfo.InvariantCulture)}"));

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
            copy[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? "";
        _measurements.Enqueue(new Measurement(instrument.Meter.Name, instrument.Name, value, copy));
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// Bounds of the load tests. The defaults suit every pull request and a developer machine; a heavier run raises them
/// through environment variables (docs/operations/testing.md#realtime-load-tests), never past the hard caps.
/// </summary>
internal static class RealtimeLoad
{
    public const string ConnectionsVariable = "WASLA_REALTIME_LOAD_CONNECTIONS";
    public const string EventsVariable = "WASLA_REALTIME_LOAD_EVENTS";
    public const string TenantsVariable = "WASLA_REALTIME_LOAD_TENANTS";

    /// <summary>Lightweight hub connections across all tenants (default 120, at most 2,000).</summary>
    public static int Connections => Read(ConnectionsVariable, 120, 12, 2000);

    /// <summary>Notifications per tenant in a burst (default 200, at most 5,000).</summary>
    public static int EventsPerTenant => Read(EventsVariable, 200, 10, 5000);

    /// <summary>Tenants (default 3, at most 10).</summary>
    public static int Tenants => Read(TenantsVariable, 3, 2, 10);

    private static int Read(string variable, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new InvalidOperationException($"{variable} must be a whole number between {min} and {max}.");
        return value;
    }
}
