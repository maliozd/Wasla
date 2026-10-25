using System.Diagnostics;
using System.Diagnostics.Metrics;
using Wasla.Application.Abstractions.Realtime;

namespace Wasla.Infrastructure.Realtime;

/// <summary>
/// Metrics of the live tenant notification channel (meter <see cref="MeterName"/>), recorded by Wasla.Web (hub,
/// connections, session checks, the loopback listener) and by Wasla.Worker and Wasla.Api (the internal channel sender).
/// Every tag has a fixed, small set of values (the constants below): never a tenant, user, order, job or connection id,
/// a host name, a URL, an exception message or a secret. Read them with <c>dotnet-counters</c>
/// (docs/operations/observability.md#live-notification-metrics).
/// </summary>
public sealed class RealtimeMetrics
{
    public const string MeterName = "Wasla.Realtime";

    public const string ChannelTag = "wasla.realtime.channel";
    public const string StreamTag = "wasla.realtime.stream";
    public const string StreamsTag = "wasla.realtime.streams";
    public const string ResultTag = "wasla.realtime.result";
    public const string ReasonTag = "wasla.realtime.reason";
    public const string AttemptTag = "wasla.realtime.attempt";

    /// <summary>Where a notification is handed on: to the hub inside Wasla.Web, or to Wasla.Web over the internal channel.</summary>
    public static class Channels
    {
        public const string Hub = "hub";
        public const string Internal = "internal";
    }

    /// <summary>The stream a notification belongs to (<see cref="StreamTag"/>).</summary>
    public static class Streams
    {
        public const string PrintStatus = "print_status";
        public const string LiveScreen = "live_screen";
        public const string Other = "other";

        /// <summary>A connection that joined both streams (<see cref="StreamsTag"/>).</summary>
        public const string LiveScreenAndPrintStatus = "live_screen+print_status";

        public static string Of(TenantChange change) => change switch
        {
            PrintJobChanged => PrintStatus,
            OrderChanged => LiveScreen,
            _ => Other
        };
    }

    /// <summary>The outcome of handing on one notification (<see cref="ResultTag"/> of the notification instruments).</summary>
    public static class NotificationResults
    {
        /// <summary>The hub accepted the group send, or Wasla.Web acknowledged the internal request.</summary>
        public const string Sent = "sent";
        /// <summary>Not sent within the bound (2 seconds for the hub, <c>Realtime:Internal:TimeoutSeconds</c> internally).</summary>
        public const string Timeout = "timeout";
        /// <summary>The caller gave up first (the internal request that carried it was aborted).</summary>
        public const string Cancelled = "cancelled";
        /// <summary>Wasla.Web answered the internal request with an error status (for example a wrong secret).</summary>
        public const string Rejected = "rejected";
        /// <summary>Wasla.Web could not be reached over the internal channel.</summary>
        public const string Unreachable = "unreachable";
        /// <summary>Any other exception; only its type is logged.</summary>
        public const string Failed = "failed";
        /// <summary>Dropped without an attempt: the internal channel is paused after a failure.</summary>
        public const string Paused = "paused";
        /// <summary>Dropped without an attempt: a change type the channel does not carry.</summary>
        public const string Unsupported = "unsupported";
    }

    /// <summary>Answers of Wasla.Web's loopback listener (<see cref="ResultTag"/> of <c>wasla.realtime.internal.requests</c>).</summary>
    public static class InternalRequestResults
    {
        public const string Accepted = "accepted";
        public const string Unauthorized = "unauthorized";
        public const string Invalid = "invalid";
        public const string NotFound = "not_found";
        public const string MethodNotAllowed = "method_not_allowed";
    }

    /// <summary>The outcome of a hub connection reaching <c>OnConnectedAsync</c>.</summary>
    public static class ConnectionResults
    {
        /// <summary>Its groups were joined.</summary>
        public const string Admitted = "admitted";
        /// <summary>Its role admits no stream for the resolved tenant; it was closed.</summary>
        public const string Refused = "refused";
        /// <summary>Joining its groups failed; it was closed.</summary>
        public const string Failed = "failed";
    }

    /// <summary>
    /// What the browser client says the connection is (<see cref="AttemptTag"/>): its first connection, or a reconnect
    /// after a drop, a failed attempt or a back/forward-cache restore. Declared by the client in the hub URL, so it is a
    /// diagnostic label only; anything else is <see cref="Unknown"/>. It never affects tenant, groups or authorization.
    /// </summary>
    public static class Attempts
    {
        public const string Initial = "initial";
        public const string Reconnect = "reconnect";
        public const string Unknown = "unknown";

        /// <summary>The name of the hub URL's query parameter that carries it.</summary>
        public const string QueryParameter = "attempt";

        public static string Parse(string? declared) => declared switch
        {
            Initial => Initial,
            Reconnect => Reconnect,
            _ => Unknown
        };
    }

    /// <summary>Why an admitted connection ended (<see cref="ReasonTag"/>), and the results of a periodic session check.</summary>
    public static class CloseReasons
    {
        /// <summary>
        /// The connection ended without an exception reported to the hub: the client stopped or the page closed, the
        /// cookie expired, or the server's client timeout closed a client that went silent (measured in WAS-25: SignalR
        /// reports no exception for that).
        /// </summary>
        public const string Closed = "closed";
        /// <summary>The connection ended with an exception reported to the hub (for example a transport failure).</summary>
        public const string Error = "error";
        /// <summary>A session check passed (session check result only).</summary>
        public const string Valid = "valid";
        /// <summary>The user was deactivated or deleted, or their password, role or security stamp changed.</summary>
        public const string SessionInvalid = "session_invalid";
        /// <summary>The role no longer admits every stream the connection joined.</summary>
        public const string RoleChanged = "role_changed";
        /// <summary>The connection's host no longer resolves to its tenant (for example the tenant was deactivated).</summary>
        public const string TenantUnresolved = "tenant_unresolved";
        /// <summary>The tenant session could not be read; closed to fail safe.</summary>
        public const string CheckUnavailable = "check_unavailable";
        /// <summary>The check itself failed with an exception; closed to fail safe.</summary>
        public const string CheckFailed = "check_failed";
    }

    private readonly Counter<long> _notifications;
    private readonly Histogram<double> _notificationDuration;
    private readonly Counter<long> _internalPauses;
    private readonly Counter<long> _internalRequests;
    private readonly UpDownCounter<long> _activeConnections;
    private readonly Counter<long> _connectionAttempts;
    private readonly Counter<long> _connectionClosures;
    private readonly Counter<long> _sessionChecks;
    private readonly Histogram<double> _sessionCheckDuration;
    private readonly Counter<long> _originRefusals;

    public RealtimeMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _notifications = meter.CreateCounter<long>(
            "wasla.realtime.notifications", "{notification}",
            "Change notifications handed on after a commit, by channel, stream and result.");
        _notificationDuration = meter.CreateHistogram<double>(
            "wasla.realtime.notification.duration", "s",
            "Time to hand on one notification: the hub's group send in Wasla.Web, or the internal request until Wasla.Web acknowledged it. Not the time until a browser shows the change.");
        _internalPauses = meter.CreateCounter<long>(
            "wasla.realtime.internal.pauses", "{pause}",
            "Times the internal channel sender stopped sending for 30 seconds after a failure, by reason.");
        _internalRequests = meter.CreateCounter<long>(
            "wasla.realtime.internal.requests", "{request}",
            "Requests to Wasla.Web's loopback listener, by result.");
        _activeConnections = meter.CreateUpDownCounter<long>(
            "wasla.realtime.connections.active", "{connection}",
            "Open hub connections whose groups are joined, by the streams they joined.");
        _connectionAttempts = meter.CreateCounter<long>(
            "wasla.realtime.connection.attempts", "{connection}",
            "Authenticated hub connections that reached the hub, by result and by what the client says it is (initial or reconnect).");
        _connectionClosures = meter.CreateCounter<long>(
            "wasla.realtime.connection.closures", "{connection}",
            "Admitted hub connections that ended, by reason.");
        _sessionChecks = meter.CreateCounter<long>(
            "wasla.realtime.session_checks", "{check}",
            "Periodic session checks of open hub connections, by result.");
        _sessionCheckDuration = meter.CreateHistogram<double>(
            "wasla.realtime.session_check.duration", "s",
            "Time of one periodic session check of an open hub connection, by result.");
        _originRefusals = meter.CreateCounter<long>(
            "wasla.realtime.origin_refusals", "{request}",
            "Hub requests refused because their Origin is another host.");
    }

    public void NotificationHandled(string channel, TenantChange change, string result, TimeSpan? duration = null)
    {
        var tags = new TagList
        {
            { ChannelTag, channel },
            { StreamTag, Streams.Of(change) },
            { ResultTag, result }
        };
        _notifications.Add(1, tags);
        if (duration is { } elapsed)
            _notificationDuration.Record(elapsed.TotalSeconds, tags);
    }

    public void InternalChannelPaused(string reason) => _internalPauses.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason));

    public void InternalRequestAnswered(string result) => _internalRequests.Add(1, new KeyValuePair<string, object?>(ResultTag, result));

    public void ConnectionAdmitted(string streams, string attempt)
    {
        _connectionAttempts.Add(1, new TagList { { ResultTag, ConnectionResults.Admitted }, { AttemptTag, attempt } });
        _activeConnections.Add(1, new KeyValuePair<string, object?>(StreamsTag, streams));
    }

    /// <summary>A connection that reached the hub but joined no group (<see cref="ConnectionResults"/>).</summary>
    public void ConnectionNotAdmitted(string result, string attempt) =>
        _connectionAttempts.Add(1, new TagList { { ResultTag, result }, { AttemptTag, attempt } });

    public void ConnectionClosed(string streams, string reason)
    {
        _activeConnections.Add(-1, new KeyValuePair<string, object?>(StreamsTag, streams));
        _connectionClosures.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason));
    }

    public void SessionChecked(string result, TimeSpan duration)
    {
        var tag = new KeyValuePair<string, object?>(ResultTag, result);
        _sessionChecks.Add(1, tag);
        _sessionCheckDuration.Record(duration.TotalSeconds, tag);
    }

    public void OriginRefused() => _originRefusals.Add(1);

    /// <summary>Elapsed time since a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    public static TimeSpan Since(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);
}
