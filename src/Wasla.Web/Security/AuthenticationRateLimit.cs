using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Security.RateLimiting;

namespace Wasla.Web.Security;

public static class WebAuthenticationRateLimiting
{
    /// <summary>Set in ViewData when a login form is shown again because its attempt was rate limited.</summary>
    public const string RateLimitedViewDataKey = "Wasla.AuthenticationRateLimited";

    /// <summary>The tenant and Central Admin login limits (<see cref="AuthenticationRateLimitAttribute"/>).</summary>
    public static IServiceCollection AddWaslaWebAuthenticationRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddWaslaAuthenticationRateLimitOptions(configuration);
        services.TryAddSingleton<WebAuthenticationRateLimiter>();
        return services;
    }
}

/// <summary>One partitioned limiter per Web login policy, for the life of the process.</summary>
public sealed class WebAuthenticationRateLimiter : IDisposable
{
    private readonly AuthenticationRateLimitOptions _options;
    private readonly Dictionary<string, PartitionedRateLimiter<HttpContext>> _limiters;

    public WebAuthenticationRateLimiter(IOptions<AuthenticationRateLimitOptions> options)
    {
        _options = options.Value;
        _limiters = new Dictionary<string, PartitionedRateLimiter<HttpContext>>(StringComparer.Ordinal)
        {
            [AuthenticationRateLimitPolicies.TenantLogin] = Create(AuthenticationRateLimitPolicies.TenantLogin),
            [AuthenticationRateLimitPolicies.CentralAdminLogin] = Create(AuthenticationRateLimitPolicies.CentralAdminLogin),
            [AuthenticationRateLimitPolicies.SignupSubmission] = Create(AuthenticationRateLimitPolicies.SignupSubmission),
            [AuthenticationRateLimitPolicies.SignupCheckout] = Create(AuthenticationRateLimitPolicies.SignupCheckout)
        };
    }

    public AuthenticationRateLimitRule Rule(string policy) => _options.For(policy);

    /// <summary>Takes one permit from the request's partition, or a failed lease when the window is used up.</summary>
    public RateLimitLease AttemptAcquire(string policy, HttpContext context) => _limiters[policy].AttemptAcquire(context);

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
            limiter.Dispose();
    }

    private PartitionedRateLimiter<HttpContext> Create(string policy)
    {
        var rule = _options.For(policy);
        return PartitionedRateLimiter.Create<HttpContext, string>(
            context => AuthenticationRateLimiting.Partition(policy, rule, context));
    }
}

/// <summary>
/// Limits a login, signup or checkout POST action. It runs after antiforgery validation and model binding and before the
/// action, so an attempt over the limit never reaches the action's work (no credential check, no password hash, no
/// database write). By default that attempt gets the action's own view with status 429, <c>Retry-After</c> and the
/// rate-limited flag in ViewData, the submitted form kept and the password not echoed; a controller that implements
/// <see cref="IAuthenticationRateLimitResponder"/> builds the page itself. The answer is the same for every email, so
/// it does not reveal whether an account exists.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuthenticationRateLimitAttribute : TypeFilterAttribute
{
    public AuthenticationRateLimitAttribute(string policy)
        : base(typeof(AuthenticationRateLimitFilter))
    {
        Policy = policy;
        Arguments = [policy];
    }

    public string Policy { get; }
}

internal sealed class AuthenticationRateLimitFilter(string policy, WebAuthenticationRateLimiter limiter) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        using var lease = limiter.AttemptAcquire(policy, context.HttpContext);
        if (lease.IsAcquired)
        {
            await next();
            return;
        }

        if (context.Controller is not Controller controller)
            throw new InvalidOperationException($"{nameof(AuthenticationRateLimitAttribute)} applies to MVC view controllers only.");

        var retryAfter = AuthenticationRateLimiting.RetryAfter(lease, limiter.Rule(policy));
        AuthenticationRateLimiting.ApplyRejection(context.HttpContext.Response, retryAfter);
        AuthenticationRateLimiting.LogRejection(context.HttpContext, policy, retryAfter);

        controller.ViewData[WebAuthenticationRateLimiting.RateLimitedViewDataKey] = true;
        if (controller is IAuthenticationRateLimitResponder responder)
        {
            context.Result = await responder.RateLimitedAsync(context);
            return;
        }

        var model = context.ActionArguments.Values.FirstOrDefault(value => value is not CancellationToken);
        var result = controller.View(model);
        result.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Result = result;
    }
}

/// <summary>
/// Implemented by a controller whose rate-limited actions need more than their own view with the bound model, for
/// example a form that needs its option lists, or a POST action without a view. The response status is already 429 and
/// <c>Retry-After</c> is set; the result must do no work the limit is meant to prevent. Implement it explicitly, so
/// that MVC does not treat it as an action.
/// </summary>
public interface IAuthenticationRateLimitResponder
{
    Task<IActionResult> RateLimitedAsync(ActionExecutingContext context);
}
