using System.Runtime.CompilerServices;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Realtime;

namespace Wasla.Web.Realtime;

/// <summary>
/// A tenant cookie is checked against the user's row on every HTTP request, but a live connection is one long request.
/// This repeats the same check (<see cref="ITenantSessionValidator"/>, the validator of the cookie scheme) for every
/// open hub connection once per <see cref="RealtimeOptions.SessionRevalidationInterval"/>, and closes the connection
/// when the user was deactivated or deleted, their password, role or security stamp changed, their role no longer admits
/// the streams it joined, the connection's host no longer resolves to its tenant (for example the tenant was deactivated; the same
/// resolver and cache HTTP requests use), or a database cannot be read (fail closed). A closed browser reconnects through negotiate, where
/// the cookie scheme decides again; a session that is no longer valid gets no new connection.
/// </summary>
public sealed class RealtimeSessionRevalidator
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RealtimeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RealtimeSessionRevalidator> _logger;
    private readonly RealtimeMetrics _metrics;

    // Why this check closed a connection, until the hub's OnDisconnectedAsync takes it for the closure metric. Weak, so
    // a reason recorded just after a connection ended on its own goes away with the connection.
    private readonly ConditionalWeakTable<HubCallerContext, string> _closeReasons = new();

    public RealtimeSessionRevalidator(
        IServiceScopeFactory scopes,
        RealtimeOptions options,
        TimeProvider time,
        ILogger<RealtimeSessionRevalidator> logger,
        RealtimeMetrics metrics)
    {
        _scopes = scopes;
        _options = options;
        _time = time;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// The reason (<see cref="RealtimeMetrics.CloseReasons"/>) this check closed the connection, or null when it did not;
    /// removes it. Called once per connection when it ends.
    /// </summary>
    public string? TakeCloseReason(HubCallerContext connection) =>
        _closeReasons.TryGetValue(connection, out var reason) && _closeReasons.Remove(connection) ? reason : null;

    /// <summary>Starts the periodic check of one connection; it ends with the connection.</summary>
    /// <param name="roles">The roles that admit every stream the connection joined.</param>
    public void Watch(HubCallerContext connection, Guid tenantId, IReadOnlySet<UserRole> roles)
    {
        var host = connection.GetHttpContext()?.Request.Host.Host;
        // One loop per connection, bound to the connection's lifetime; it catches everything and ends with it.
        _ = Task.Run(() => WatchAsync(connection, tenantId, host, roles), CancellationToken.None);
    }

    private async Task WatchAsync(HubCallerContext connection, Guid tenantId, string? host, IReadOnlySet<UserRole> roles)
    {
        var closed = connection.ConnectionAborted;
        try
        {
            while (!closed.IsCancellationRequested)
            {
                await Task.Delay(_options.SessionRevalidationInterval, _time, closed).ConfigureAwait(false);
                var started = Stopwatch.GetTimestamp();
                string? refusal;
                try
                {
                    refusal = await CheckAsync(connection, tenantId, host, roles, closed).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !closed.IsCancellationRequested)
                {
                    _metrics.SessionChecked(RealtimeMetrics.CloseReasons.CheckFailed, RealtimeMetrics.Since(started));
                    _logger.LogWarning("Live connection check failed ({ExceptionType}); the connection is closed.", ex.GetType().Name);
                    Close(connection, RealtimeMetrics.CloseReasons.CheckFailed);
                    return;
                }

                _metrics.SessionChecked(refusal ?? RealtimeMetrics.CloseReasons.Valid, RealtimeMetrics.Since(started));
                if (refusal is not null)
                {
                    Close(connection, refusal);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested)
        {
        }
    }

    private void Close(HubCallerContext connection, string reason)
    {
        _closeReasons.AddOrUpdate(connection, reason);
        connection.Abort();
    }

    /// <summary>Null when the connection may stay open; otherwise the reason it is closed (<see cref="RealtimeMetrics.CloseReasons"/>).</summary>
    private async Task<string?> CheckAsync(
        HubCallerContext connection,
        Guid tenantId,
        string? host,
        IReadOnlySet<UserRole> roles,
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();

        // The tenant must still be the active tenant of the host the connection came in on.
        var resolved = string.IsNullOrWhiteSpace(host)
            ? null
            : await scope.ServiceProvider.GetRequiredService<ITenantResolver>().ResolveByHostAsync(host, ct).ConfigureAwait(false);
        if (resolved is null || resolved.Id != tenantId)
        {
            _logger.LogInformation("Live connection closed: the host no longer resolves to the tenant. TenantId={TenantId}", tenantId);
            return RealtimeMetrics.CloseReasons.TenantUnresolved;
        }

        TenantSessionState state;
        try
        {
            var validator = scope.ServiceProvider.GetRequiredService<ITenantSessionValidator>();
            state = await validator.ValidateAsync(tenantId, connection.User, ct).ConfigureAwait(false);
        }
        catch (TenantSessionUnavailableException)
        {
            _logger.LogWarning("Live connection closed: the tenant session could not be checked. TenantId={TenantId}", tenantId);
            return RealtimeMetrics.CloseReasons.CheckUnavailable;
        }

        if (state != TenantSessionState.Valid)
        {
            _logger.LogInformation("Live connection closed: tenant session {State}. TenantId={TenantId}", state, tenantId);
            return RealtimeMetrics.CloseReasons.SessionInvalid;
        }

        if (connection.User is null || !TenantSessionClaims.IsInRole(connection.User, tenantId, roles))
        {
            _logger.LogInformation("Live connection closed: the role no longer admits it. TenantId={TenantId}", tenantId);
            return RealtimeMetrics.CloseReasons.RoleChanged;
        }

        return null;
    }
}

/// <summary>Section <c>Realtime</c> of Wasla.Web.</summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>How often an open live connection's session is checked again (default 60 seconds, 1 to 600).</summary>
    public double SessionRevalidationSeconds { get; set; } = 60;

    public TimeSpan SessionRevalidationInterval => TimeSpan.FromSeconds(SessionRevalidationSeconds);

    public static RealtimeOptions Read(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<RealtimeOptions>() ?? new RealtimeOptions();
        if (options.SessionRevalidationSeconds is < 1 or > 600)
            throw new InvalidOperationException($"{SectionName}:SessionRevalidationSeconds must be between 1 and 600.");
        return options;
    }
}
