using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.Web.Security;
using Wasla.Web.Tenant;

namespace Wasla.Web.Realtime;

/// <summary>
/// Server-to-browser change notifications for tenant screens. It has no method a client can call: a connection cannot
/// choose a tenant or a group. The tenant comes from TenantResolutionMiddleware (the request's host) and the session
/// from the tenant cookie, revalidated by the cookie scheme on the negotiate and connect requests and then
/// periodically by <see cref="RealtimeSessionRevalidator"/>. Today it carries only receipt print status (WAS-23), so
/// only users who may print manually can connect.
/// </summary>
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManualPrint)]
public sealed class TenantEventsHub : Hub
{
    public const string Path = "/hubs/tenant-events";

    /// <summary>The client method every notification is sent to; its payload names its <c>type</c>.</summary>
    public const string ClientMethod = "tenantChange";

    internal static readonly IReadOnlySet<UserRole> PrintStatusRoles =
        WaslaTenantPolicies.AllowedRoles[WaslaTenantPolicies.CanManualPrint].ToHashSet();

    private readonly RealtimeSessionRevalidator _revalidator;

    public TenantEventsHub(RealtimeSessionRevalidator revalidator)
    {
        _revalidator = revalidator;
    }

    public override async Task OnConnectedAsync()
    {
        // The endpoint policy admitted this session for the tenant resolved from the host. The same check runs again
        // against that tenant before a group is joined, so the only group a connection can ever join is its own.
        var tenant = CurrentTenantService.From(Context.GetHttpContext());
        if (tenant is null || Context.User is null || !TenantSessionClaims.IsInRole(Context.User, tenant.Id, PrintStatusRoles))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, TenantEventGroups.PrintStatus(tenant.Id), Context.ConnectionAborted);
        _revalidator.Watch(Context, tenant.Id);
        await base.OnConnectedAsync();
    }
}

/// <summary>Group names, built only from a server-side tenant id.</summary>
public static class TenantEventGroups
{
    /// <summary>Connections of one tenant's users who may print manually; receives <c>PrintJobChanged</c>.</summary>
    public static string PrintStatus(Guid tenantId) => $"tenant:{tenantId:N}:print-status";
}
