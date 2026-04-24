using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Domain.Entities.Central;

namespace OrderHub.Api.Tenant;

public sealed class CurrentCustomerService : ICurrentCustomerService
{
    private const string ItemKey = "CurrentCustomer";
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentCustomerService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Customer? CurrentCustomer
    {
        get
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null) return null;

            return ctx.Items.TryGetValue(ItemKey, out var value) ? value as Customer : null;
        }
    }
}

