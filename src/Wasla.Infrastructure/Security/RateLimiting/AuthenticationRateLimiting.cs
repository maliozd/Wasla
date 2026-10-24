using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Tenant;

namespace Wasla.Infrastructure.Security.RateLimiting;

/// <summary>
/// The parts of credential-entry rate limiting that Wasla.Web and Wasla.Api share: configuration, partitions, the
/// <c>Retry-After</c> value and the rejection log. Wasla.Api applies the policy with the ASP.NET Core rate-limiting
/// middleware; Wasla.Web applies it in an MVC filter so that the login page can be shown again.
/// </summary>
/// <remarks>
/// A partition is the policy, the tenant resolved from the host (tenant-scoped policies only) and the client network
/// (<see cref="ClientNetworkPartition"/>). Nothing from the request body, a query string or a forwarded header that did
/// not come through a trusted proxy is part of it. Counters are in process memory: they are not shared between
/// processes and start empty after a restart.
/// </remarks>
public static class AuthenticationRateLimiting
{
    public const string LoggerCategory = "Wasla.Security.AuthenticationRateLimiting";

    private const string NoTenant = "no-tenant";
    private const string RejectionLogCachePrefix = "wasla:auth-rate-limit-rejected:";

    // Per-process key: log lines from one process can be correlated by client, but the tag cannot be reversed into an
    // address, and tags differ between processes and restarts.
    private static readonly byte[] ClientTagKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Binds <see cref="AuthenticationRateLimitOptions"/> and validates them when the host starts.</summary>
    public static IServiceCollection AddWaslaAuthenticationRateLimitOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AuthenticationRateLimitOptions>()
            .Bind(configuration.GetSection(AuthenticationRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AuthenticationRateLimitOptions>, AuthenticationRateLimitOptionsValidator>());
        return services;
    }

    /// <summary>The partition a request counts against for <paramref name="policy"/>. Never sent to the client.</summary>
    public static string PartitionKey(string policy, HttpContext context)
    {
        var client = ClientNetworkPartition.For(context.Connection.RemoteIpAddress);
        if (!AuthenticationRateLimitPolicies.IsTenantScoped(policy))
            return client;

        var tenant = context.RequestServices.GetRequiredService<ICurrentTenantService>().CurrentTenant;
        return (tenant is null ? NoTenant : tenant.Id.ToString("N")) + "|" + client;
    }

    /// <summary>A fixed window per partition, without a queue: an attempt over the limit is rejected at once.</summary>
    public static RateLimitPartition<string> Partition(string policy, AuthenticationRateLimitRule rule, HttpContext context) =>
        RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(policy, context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rule.PermitLimit,
                Window = rule.Window,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });

    /// <summary>
    /// The time until the partition's window resets, as the limiter reports it; at most the window length. Falls back
    /// to the window length when the limiter gives no value.
    /// </summary>
    public static TimeSpan RetryAfter(RateLimitLease lease, AuthenticationRateLimitRule rule) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) && retryAfter > TimeSpan.Zero
            ? retryAfter
            : rule.Window;

    /// <summary>Whole seconds, rounded up, never below 1.</summary>
    public static string RetryAfterHeaderValue(TimeSpan retryAfter) =>
        Math.Max(1L, (long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Status 429, <c>Retry-After</c> and <c>Cache-Control: no-store</c>. The body is left to the caller.</summary>
    public static void ApplyRejection(HttpResponse response, TimeSpan retryAfter)
    {
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = RetryAfterHeaderValue(retryAfter);
        response.Headers.CacheControl = "no-store";
    }

    /// <summary>
    /// Logs a rejection: a warning for the first rejection of a partition in its window, Debug for the rest, so that an
    /// attack does not flood the log. Logs the policy, the resolved tenant id, an opaque client tag and the retry delay;
    /// never the address, the partition key, the submitted email or password, cookies or headers.
    /// </summary>
    public static void LogRejection(HttpContext context, string policy, TimeSpan retryAfter)
    {
        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        var cache = services.GetRequiredService<IMemoryCache>();
        var partitionKey = PartitionKey(policy, context);
        var tenantId = AuthenticationRateLimitPolicies.IsTenantScoped(policy)
            ? services.GetRequiredService<ICurrentTenantService>().CurrentTenant?.Id
            : null;
        var clientTag = ClientTag(ClientNetworkPartition.For(context.Connection.RemoteIpAddress));
        var retryAfterSeconds = RetryAfterHeaderValue(retryAfter);

        var cacheKey = RejectionLogCachePrefix + policy + "|" + partitionKey;
        if (cache.TryGetValue(cacheKey, out RejectionCount? count) && count is not null)
        {
            var total = count.Increment();
            logger.LogDebug(
                "Authentication rate limit rejected another request. Policy={Policy} TenantId={TenantId} ClientTag={ClientTag} RetryAfterSeconds={RetryAfterSeconds} RejectedInWindow={RejectedInWindow}",
                policy, tenantId, clientTag, retryAfterSeconds, total);
            return;
        }

        cache.Set(cacheKey, new RejectionCount(), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = retryAfter,
            Size = 1
        });
        logger.LogWarning(
            "Authentication rate limit reached; rejecting requests until the window resets. Policy={Policy} TenantId={TenantId} ClientTag={ClientTag} RetryAfterSeconds={RetryAfterSeconds}",
            policy, tenantId, clientTag, retryAfterSeconds);
    }

    internal static string ClientTag(string clientPartition)
    {
        var hash = HMACSHA256.HashData(ClientTagKey, Encoding.UTF8.GetBytes(clientPartition));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private sealed class RejectionCount
    {
        private int _count = 1;

        public int Increment() => Interlocked.Increment(ref _count);
    }
}
