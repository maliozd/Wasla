using OrderHub.Application.Abstractions.Tenant;

namespace OrderHub.Web.Tenant;

public sealed class CurrentCustomerService : ICurrentCustomerService
{
    private const string ItemKey = "CurrentCustomer";
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentCustomerService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public ResolvedCustomerDto? CurrentCustomer
    {
        get
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null) return null;
            return ctx.Items.TryGetValue(ItemKey, out var value) ? value as ResolvedCustomerDto : null;
        }
    }
}

