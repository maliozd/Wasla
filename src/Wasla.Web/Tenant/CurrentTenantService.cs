using Wasla.Application.Abstractions.Tenant;

namespace Wasla.Web.Tenant;

public sealed class CurrentTenantService : ICurrentTenantService
{
    private const string ItemKey = "CurrentTenant";
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentTenantService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public ResolvedTenantDto? CurrentTenant => From(_httpContextAccessor.HttpContext);

    /// <summary>
    /// The tenant that TenantResolutionMiddleware resolved from the request's host, or null. For code that holds the
    /// request directly, such as a SignalR hub reading its connection's request.
    /// </summary>
    public static ResolvedTenantDto? From(HttpContext? ctx)
    {
        if (ctx is null) return null;
        return ctx.Items.TryGetValue(ItemKey, out var value) ? value as ResolvedTenantDto : null;
    }
}
