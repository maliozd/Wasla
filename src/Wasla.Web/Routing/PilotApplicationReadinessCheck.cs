using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Contact;

namespace Wasla.Web.Routing;

/// <summary>
/// Logs, from startup on, why the pilot application form is closed and which stored notifications never finished
/// (WAS-217). It changes nothing: it reads the city catalog through <see cref="PilotApplicationGate"/> once the form's
/// configuration allows it, and every <see cref="ContactNotificationRetry.StuckAfter"/> counts the contact messages and
/// pilot applications whose notification started but never recorded an outcome (the process stopped while sending).
/// Those are listed in Central Admin under "Notification needs attention" and can be sent again there. A failed read is
/// logged and retried at the next interval; it never stops the host.
/// </summary>
public sealed class PilotApplicationReadinessCheck : BackgroundService
{
    private readonly PilotApplicationGate _gate;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<PilotApplicationReadinessCheck> _logger;

    public PilotApplicationReadinessCheck(
        PilotApplicationGate gate,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<PilotApplicationReadinessCheck> logger)
    {
        _gate = gate;
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the first CentralDb read.
        await Task.Yield();

        if (_gate.ConfigurationAllowsForm)
            await _gate.GetCityCatalogStateAsync(stoppingToken).ConfigureAwait(false);

        var lastStuck = 0;
        using var timer = new PeriodicTimer(ContactNotificationRetry.StuckAfter, _time);
        do
        {
            lastStuck = await LogStuckNotificationsAsync(lastStuck, stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task<int> LogStuckNotificationsAsync(int lastStuck, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var stuck = await scope.ServiceProvider.GetRequiredService<IContactMessageService>().CountStuckNotificationsAsync(ct).ConfigureAwait(false);
            if (stuck > 0 && stuck != lastStuck)
            {
                _logger.LogWarning(
                    "{StuckNotifications} contact messages or pilot applications have a team notification that started more than {StuckAfterMinutes} minutes ago and never finished. Send them again from Central Admin, Contact messages, \"Notification needs attention\".",
                    stuck,
                    ContactNotificationRetry.StuckAfter.TotalMinutes);
            }

            return stuck;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Stored notifications could not be checked for unfinished attempts. ExceptionType={ExceptionType}", ex.GetType().Name);
            return lastStuck;
        }
    }
}
