using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Options;

namespace Wasla.Web.Security;

/// <summary>
/// Closes a signup route that only serves creating a new registration while
/// <c>Wasla:TenantOnboarding:PublicSignupEnabled</c> is false (WAS-76). Without a payment provider a public signup can
/// never be completed, so the signup page explains that online registration is not open yet instead of offering a form.
/// </summary>
/// <remarks>
/// A resource filter: it runs after the antiforgery check (an authorization filter), so a POST without a valid token is
/// rejected exactly as before, and before model binding, the signup rate limit and the action, so a closed request binds
/// no form, takes no rate-limit permit, hashes nothing and writes nothing. The status page of an existing registration
/// and the checkout pages do not carry it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PublicSignupRequiredAttribute : Attribute, IAsyncResourceFilter
{
    public const string UnavailableViewName = "Unavailable";

    public PublicSignupRequiredAttribute(bool showUnavailablePage = false)
    {
        ShowUnavailablePage = showUnavailablePage;
    }

    /// <summary>True for the signup page itself; the form's JSON lookups answer 404.</summary>
    public bool ShowUnavailablePage { get; }

    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<TenantOnboardingOptions>>().Value;
        if (options.PublicSignupEnabled)
            return next();

        var method = context.HttpContext.Request.Method;
        context.Result = ShowUnavailablePage
            ? new ViewResult
            {
                ViewName = UnavailableViewName,
                // A submitted form gets the same explanation with 404, so no client can take the submission as accepted.
                StatusCode = HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status404NotFound
            }
            : new NotFoundResult();
        return Task.CompletedTask;
    }
}
