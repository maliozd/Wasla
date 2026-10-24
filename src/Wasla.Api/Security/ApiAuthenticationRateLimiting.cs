using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Security.RateLimiting;

namespace Wasla.Api.Security;

/// <summary>
/// Rate limiting for the Api's anonymous credential check, <c>POST /api/auth/validate</c>, with the ASP.NET Core
/// rate-limiting middleware. The middleware runs after tenant resolution and before authentication
/// (<see cref="WaslaApiPipeline"/>), so a rejected request reaches no controller, no credential validation and no tenant
/// database, not even for a session cookie it carries.
/// </summary>
public static class ApiAuthenticationRateLimiting
{
    /// <summary>The JSON error of a rejected request: <c>{"error":"too_many_requests"}</c>.</summary>
    public const string ErrorCode = "too_many_requests";

    public static IServiceCollection AddWaslaApiAuthenticationRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddWaslaAuthenticationRateLimitOptions(configuration);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthenticationRateLimitPolicies.ApiCredentialValidation, context =>
                AuthenticationRateLimiting.Partition(
                    AuthenticationRateLimitPolicies.ApiCredentialValidation,
                    Rule(context, AuthenticationRateLimitPolicies.ApiCredentialValidation),
                    context));
            options.OnRejected = OnRejectedAsync;
        });
        return services;
    }

    /// <summary>
    /// 429 with <c>Retry-After</c>, <c>Cache-Control: no-store</c> and a fixed JSON error, like the Api's other status
    /// answers: no redirect, no HTML, nothing about the tenant, the account or the partition.
    /// </summary>
    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
            ?? AuthenticationRateLimitPolicies.ApiCredentialValidation;
        var retryAfter = AuthenticationRateLimiting.RetryAfter(context.Lease, Rule(http, policy));

        AuthenticationRateLimiting.ApplyRejection(http.Response, retryAfter);
        AuthenticationRateLimiting.LogRejection(http, policy, retryAfter);
        await http.Response.WriteAsJsonAsync(new { error = ErrorCode }, cancellationToken);
    }

    private static AuthenticationRateLimitRule Rule(HttpContext context, string policy) =>
        context.RequestServices.GetRequiredService<IOptions<AuthenticationRateLimitOptions>>().Value.For(policy);
}
