using Microsoft.AspNetCore.SignalR;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;

namespace Wasla.Web.Realtime;

/// <summary>
/// A tenant cookie is checked against the user's row on every HTTP request, but a live connection is one long request.
/// This repeats the same check (<see cref="ITenantSessionValidator"/>, the validator of the cookie scheme) for every
/// open hub connection once per <see cref="RealtimeOptions.SessionRevalidationInterval"/>, and closes the connection
/// when the user was deactivated or deleted, their password, role or security stamp changed, they can no longer print
/// manually, the connection's host no longer resolves to its tenant (for example the tenant was deactivated; the same
/// resolver and cache HTTP requests use), or a database cannot be read (fail closed). A closed browser reconnects through negotiate, where
/// the cookie scheme decides again; a session that is no longer valid gets no new connection.
/// </summary>
public sealed class RealtimeSessionRevalidator
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RealtimeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RealtimeSessionRevalidator> _logger;

    public RealtimeSessionRevalidator(
        IServiceScopeFactory scopes,
        RealtimeOptions options,
        TimeProvider time,
        ILogger<RealtimeSessionRevalidator> logger)
    {
        _scopes = scopes;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Starts the periodic check of one connection; it ends with the connection.</summary>
    public void Watch(HubCallerContext connection, Guid tenantId)
    {
        var host = connection.GetHttpContext()?.Request.Host.Host;
        // One loop per connection, bound to the connection's lifetime; it catches everything and ends with it.
        _ = Task.Run(() => WatchAsync(connection, tenantId, host), CancellationToken.None);
    }

    private async Task WatchAsync(HubCallerContext connection, Guid tenantId, string? host)
    {
        var closed = connection.ConnectionAborted;
        try
        {
            while (!closed.IsCancellationRequested)
            {
                await Task.Delay(_options.SessionRevalidationInterval, _time, closed).ConfigureAwait(false);
                if (!await IsStillAllowedAsync(connection, tenantId, host, closed).ConfigureAwait(false))
                {
                    connection.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Live connection check failed ({ExceptionType}); the connection is closed.", ex.GetType().Name);
            connection.Abort();
        }
    }

    private async Task<bool> IsStillAllowedAsync(HubCallerContext connection, Guid tenantId, string? host, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();

        // The tenant must still be the active tenant of the host the connection came in on.
        var resolved = string.IsNullOrWhiteSpace(host)
            ? null
            : await scope.ServiceProvider.GetRequiredService<ITenantResolver>().ResolveByHostAsync(host, ct).ConfigureAwait(false);
        if (resolved is null || resolved.Id != tenantId)
        {
            _logger.LogInformation("Live connection closed: the host no longer resolves to the tenant. TenantId={TenantId}", tenantId);
            return false;
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
            return false;
        }

        if (state != TenantSessionState.Valid)
        {
            _logger.LogInformation("Live connection closed: tenant session {State}. TenantId={TenantId}", state, tenantId);
            return false;
        }

        if (connection.User is null || !TenantSessionClaims.IsInRole(connection.User, tenantId, TenantEventsHub.PrintStatusRoles))
        {
            _logger.LogInformation("Live connection closed: the role no longer admits it. TenantId={TenantId}", tenantId);
            return false;
        }

        return true;
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
