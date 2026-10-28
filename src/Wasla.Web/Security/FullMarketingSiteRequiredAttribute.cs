using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Wasla.Web.Routing;

namespace Wasla.Web.Security;

/// <summary>
/// Closes a page of the full marketing site while <c>Wasla:MarketingSite:FullSiteEnabled</c> is false: the page answers
/// 404 as if it did not exist, and only the coming-soon page is published.
/// </summary>
/// <remarks>
/// A resource filter, like <see cref="PublicSignupRequiredAttribute"/>: it runs after the antiforgery check and before
/// model binding, the contact rate limit and the action, so a closed request binds no form, takes no rate-limit permit,
/// and stores or sends nothing.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class FullMarketingSiteRequiredAttribute : Attribute, IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<MarketingSiteOptions>>().Value;
        if (options.FullSiteEnabled)
            return next();

        context.Result = new NotFoundResult();
        return Task.CompletedTask;
    }
}
