using OrderHub.Application.Abstractions.Tenant;

namespace OrderHub.Web.Tenant;

public sealed class CurrentTenantService : ICurrentTenantService
{
    private const string ItemKey = "CurrentTenant";
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentTenantService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public ResolvedTenantDto? CurrentTenant
    {
        get
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null) return null;
            return ctx.Items.TryGetValue(ItemKey, out var value) ? value as ResolvedTenantDto : null;
        }
    }
}
