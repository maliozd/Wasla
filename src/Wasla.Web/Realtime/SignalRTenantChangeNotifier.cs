using Microsoft.AspNetCore.SignalR;
using Wasla.Application.Abstractions.Realtime;

namespace Wasla.Web.Realtime;

/// <summary>
/// Wasla.Web's tenant change notifier: sends the notification to the tenant's group on <see cref="TenantEventsHub"/>.
/// It waits at most <see cref="SendTimeout"/> and never throws, so a notification can never fail the work it reports.
/// Connections live in this process's memory: with more than one Wasla.Web instance a notification reaches only the
/// instance it was sent on; there is no backplane (one Web instance runs today).
/// </summary>
public sealed class SignalRTenantChangeNotifier : ITenantChangeNotifier
{
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    private readonly IHubContext<TenantEventsHub> _hub;
    private readonly ILogger<SignalRTenantChangeNotifier> _logger;

    public SignalRTenantChangeNotifier(IHubContext<TenantEventsHub> hub, ILogger<SignalRTenantChangeNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct)
    {
        try
        {
            if (change is not PrintJobChanged printJob)
                return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            await _hub.Clients
                .Group(TenantEventGroups.PrintStatus(tenantId))
                .SendAsync(TenantEventsHub.ClientMethod, TenantChangeClientMessage.From(printJob), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Live tenant notification could not be sent ({ExceptionType}). TenantId={TenantId}",
                ex.GetType().Name,
                tenantId);
        }
    }
}

/// <summary>
/// What a browser receives: the change type, identifiers, the status name and the order-scoped version. No order
/// content, customer data, receipt payload, device or token.
/// </summary>
public sealed record TenantChangeClientMessage(string Type, Guid OrderId, Guid PrintJobId, string Status, long Version)
{
    public static TenantChangeClientMessage From(PrintJobChanged change) =>
        new(PrintJobChanged.TypeName, change.OrderId, change.PrintJobId, change.Status.ToString(), change.Version);
}
