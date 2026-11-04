using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Wasla.Application.Abstractions.Auth;
using Wasla.Web.Models.Auth;
using Wasla.Web.Security;

namespace Wasla.Web.Middleware;

/// <summary>
/// Answers a tenant logout request (<see cref="TenantLogoutAttribute"/>) that cannot complete because the tenant database
/// failed (<see cref="TenantSessionUnavailableException"/>), WAS-186. It runs before authentication, so it covers a
/// session that could not be checked before the action as well as a revocation the action could not store. The answer is
/// HTTP 503 with the logout failure page: the logout could not be completed, the session may still be open, and a link to
/// the logout page to try again. Nothing is signed out, and the cookie is kept as it was: the logout never requests
/// sliding renewal (<see cref="TenantCookieEvents"/>), because clearing the response here would not cancel a renewal the
/// cookie handler writes when this page starts. A storage exception does not prove that the revocation was not stored,
/// so the page claims neither outcome; a retry whose session is no longer valid is signed out and sent to the login page
/// by the cookie scheme. The page is rendered here, without running the pipeline again, so it
/// needs neither the tenant database nor the session check that just failed. The registry or the validator has logged
/// the exception type; nothing about the failure reaches the response. Every other request passes through untouched.
/// </summary>
public sealed class TenantLogoutFailureMiddleware
{
    public const string ViewPath = "~/Areas/Tenant/Views/Auth/Logout.cshtml";

    private readonly RequestDelegate _next;

    public TenantLogoutFailureMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<TenantLogoutAttribute>() is null)
        {
            await _next(context);
            return;
        }

        try
        {
            await _next(context);
        }
        catch (TenantSessionUnavailableException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.Headers.CacheControl = "no-store";
            var page = new ViewResult
            {
                ViewName = ViewPath,
                StatusCode = StatusCodes.Status503ServiceUnavailable,
                ViewData = new ViewDataDictionary<LogoutViewModel>(
                    context.RequestServices.GetRequiredService<IModelMetadataProvider>(),
                    new ModelStateDictionary())
                {
                    Model = new LogoutViewModel { Failed = true }
                }
            };
            await page.ExecuteResultAsync(new ActionContext(context, context.GetRouteData(), new ActionDescriptor()));
        }
    }
}
