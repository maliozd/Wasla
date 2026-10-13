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
/// periodically by <see cref="RealtimeSessionRevalidator"/>.
/// <para>
/// Each stream has its own group, joined on connect only when the user's role admits it: receipt print status
/// (WAS-23, <see cref="WaslaTenantPolicies.CanManualPrint"/>) and Live Screen invalidation (WAS-24,
/// <see cref="WaslaTenantPolicies.CanViewLiveScreen"/>). <see cref="ConnectPolicy"/> admits a user who may receive at
/// least one of them.
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = ConnectPolicy)]
public sealed class TenantEventsHub : Hub
{
    public const string Path = "/hubs/tenant-events";

    /// <summary>The client method every notification is sent to; its payload names its <c>type</c>.</summary>
    public const string ClientMethod = "tenantChange";

    /// <summary>
    /// Sent to the caller once its groups are joined. The client's start completes at the handshake, before
    /// <see cref="OnConnectedAsync"/> has joined the groups, so a screen treats itself as connected (and reads its state)
    /// only after this, never missing a change between its read and the join.
    /// </summary>
    public const string ReadyMethod = "tenantEventsReady";

    /// <summary>The roles of <see cref="Streams"/> together; registered by <c>AddWaslaRealtime</c>.</summary>
    public const string ConnectPolicy = "CanReceiveLiveTenantUpdates";

    internal static readonly IReadOnlySet<UserRole> PrintStatusRoles =
        WaslaTenantPolicies.AllowedRoles[WaslaTenantPolicies.CanManualPrint].ToHashSet();

    internal static readonly IReadOnlySet<UserRole> LiveScreenRoles =
        WaslaTenantPolicies.AllowedRoles[WaslaTenantPolicies.CanViewLiveScreen].ToHashSet();

    /// <summary>Every stream: its group for a tenant and the roles that may receive it.</summary>
    internal static readonly IReadOnlyList<(Func<Guid, string> Group, IReadOnlySet<UserRole> Roles)> Streams =
    [
        (TenantEventGroups.PrintStatus, PrintStatusRoles),
        (TenantEventGroups.LiveScreen, LiveScreenRoles)
    ];

    internal static IEnumerable<UserRole> AnyStreamRoles => Streams.SelectMany(s => s.Roles).Distinct();

    private readonly RealtimeSessionRevalidator _revalidator;

    public TenantEventsHub(RealtimeSessionRevalidator revalidator)
    {
        _revalidator = revalidator;
    }

    public override async Task OnConnectedAsync()
    {
        // The endpoint policy admitted this session for the tenant resolved from the host. Each stream's role check runs
        // again against that tenant before its group is joined, so a connection only ever joins its own tenant's groups,
        // and only those its role admits.
        var tenant = CurrentTenantService.From(Context.GetHttpContext());
        var user = Context.User;
        var streams = tenant is null || user is null
            ? []
            : Streams.Where(s => TenantSessionClaims.IsInRole(user, tenant.Id, s.Roles)).ToList();
        if (streams.Count == 0)
        {
            Context.Abort();
            return;
        }

        foreach (var stream in streams)
            await Groups.AddToGroupAsync(Context.ConnectionId, stream.Group(tenant!.Id), Context.ConnectionAborted);

        // Later checks require the role to still admit every stream joined here.
        var required = streams.SelectMany(s => s.Roles).ToHashSet();
        foreach (var stream in streams)
            required.IntersectWith(stream.Roles);
        _revalidator.Watch(Context, tenant!.Id, required);
        await base.OnConnectedAsync();
        await Clients.Caller.SendAsync(ReadyMethod, Context.ConnectionAborted);
    }
}

/// <summary>Group names, built only from a server-side tenant id.</summary>
public static class TenantEventGroups
{
    /// <summary>Connections of one tenant's users who may print manually; receives <c>PrintJobChanged</c>.</summary>
    public static string PrintStatus(Guid tenantId) => $"tenant:{tenantId:N}:print-status";

    /// <summary>Connections of one tenant's users who may view the Live Screen; receives <c>OrderChanged</c>.</summary>
    public static string LiveScreen(Guid tenantId) => $"tenant:{tenantId:N}:live-screen";
}
