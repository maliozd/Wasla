using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Wasla.Web.Routing;

namespace Wasla.Web.Security;

/// <summary>
/// Closes the pilot application pages while <see cref="PilotApplicationGate.IsOpenAsync"/> is false: they answer 404 as
/// if they did not exist. Independent of <c>Wasla:MarketingSite:FullSiteEnabled</c> and of public signup (WAS-217). The
/// pages exist on the marketing host only; <c>TenantResolutionMiddleware</c> answers 404 for them on the application
/// entry and tenant hosts before this filter runs.
/// </summary>
/// <remarks>
/// A resource filter, like <see cref="FullMarketingSiteRequiredAttribute"/>: it runs after the antiforgery check and
/// before model binding, the rate limit and the action, so a closed request binds no form, takes no rate-limit permit,
/// and stores or sends nothing.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class PilotApplicationRequiredAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var gate = context.HttpContext.RequestServices.GetRequiredService<PilotApplicationGate>();
        if (await gate.IsOpenAsync(context.HttpContext.RequestAborted))
        {
            await next();
            return;
        }

        context.Result = new NotFoundResult();
    }
}
