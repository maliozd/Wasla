using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Wasla.Web.Routing;

namespace Wasla.Web.Security;

/// <summary>
/// Closes the pilot application pages while <see cref="PilotApplicationGate.IsOpen"/> is false: they answer 404 as if
/// they did not exist. Independent of <c>Wasla:MarketingSite:FullSiteEnabled</c> and of public signup (WAS-217).
/// </summary>
/// <remarks>
/// A resource filter, like <see cref="FullMarketingSiteRequiredAttribute"/>: it runs after the antiforgery check and
/// before model binding, the rate limit and the action, so a closed request binds no form, takes no rate-limit permit,
/// and stores or sends nothing.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class PilotApplicationRequiredAttribute : Attribute, IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (context.HttpContext.RequestServices.GetRequiredService<PilotApplicationGate>().IsOpen)
            return next();

        context.Result = new NotFoundResult();
        return Task.CompletedTask;
    }
}
