using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Wasla.Infrastructure.Diagnostics;

public static class TenantDiagnosticContext
{
    public static void Apply(HttpContext httpContext, Guid tenantId)
    {
        Activity.Current?.SetTag("tenant.id", tenantId.ToString("D"));
        if (httpContext.Items.TryGetValue(RequestLogState.ItemKey, out var value) && value is RequestLogState state)
            state.SetTenantId(tenantId);
    }
}
