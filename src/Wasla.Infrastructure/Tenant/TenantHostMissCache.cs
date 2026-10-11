using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Tenant;

/// <summary>
/// Settings for <see cref="TenantHostMissCache"/>, bound from <c>TenantResolution:MissCache</c>.
/// </summary>
public sealed class TenantHostMissCacheOptions
{
    public const string SectionName = "TenantResolution:MissCache";

    /// <summary>How long a host without a tenant is answered from memory. A tenant created for that host (or a
    /// pending registration that changes) is seen at the latest when this expires.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The most hosts kept at once. When full, a further miss is not cached until entries expire.</summary>
    public int MaxEntries { get; set; } = 10_000;
}

public sealed class TenantHostMissCacheOptionsValidator : IValidateOptions<TenantHostMissCacheOptions>
{
    public static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(10);
    public const int MaxMaxEntries = 1_000_000;

    public ValidateOptionsResult Validate(string? name, TenantHostMissCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.Ttl < MinTtl || options.Ttl > MaxTtl)
            failures.Add($"{TenantHostMissCacheOptions.SectionName}:Ttl must be between {MinTtl:c} and {MaxTtl:c}.");
        if (options.MaxEntries is < 1 or > MaxMaxEntries)
            failures.Add($"{TenantHostMissCacheOptions.SectionName}:MaxEntries must be between 1 and {MaxMaxEntries}.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// What tenant resolution found for a host: a tenant, a pending registration for that host (Wasla.Web only), or
/// nothing.
/// </summary>
public sealed record TenantHostLookup(
    ResolvedTenantDto? Tenant,
    Guid? PendingRegistrationId = null,
    PendingRegistrationStatus? PendingRegistrationStatus = null)
{
    public static readonly TenantHostLookup NotFound = new((ResolvedTenantDto?)null);

    public bool IsMiss => Tenant is null;
}

/// <summary>
/// Remembers hosts that did not resolve to a tenant, so that requests for an unknown host (any name an attacker sends in
/// the <c>Host</c> header) cost at most one CentralDb lookup per host per <see cref="TenantHostMissCacheOptions.Ttl"/>.
/// Requests for the same host that arrive while its lookup runs wait for that lookup instead of starting their own.
/// Tenants are not kept here; they stay in the resolver's own cache.
/// </summary>
/// <remarks>
/// The size is bounded (<see cref="TenantHostMissCacheOptions.MaxEntries"/>): a flood of random hosts cannot grow
/// memory beyond it, and once it is full further misses are looked up and not cached until entries expire. The key is
/// the host exactly as the resolver looks it up (<see cref="Key"/>), so an entry can only ever answer for the host
/// that produced it: a different spelling (a trailing dot, another subdomain) is a different key and a different
/// lookup, and the port and forwarded headers are never part of it. Process-local, like the resolver's cache.
/// </remarks>
public sealed class TenantHostMissCache : IDisposable
{
    private readonly MemoryCache _misses;
    private readonly ConcurrentDictionary<string, Task<TenantHostLookup>> _inFlight = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public TenantHostMissCache(IOptions<TenantHostMissCacheOptions> options)
        : this(options, clock: null)
    {
    }

    /// <summary>For tests: a controllable clock for expiry.</summary>
    internal TenantHostMissCache(IOptions<TenantHostMissCacheOptions> options, ISystemClock? clock)
    {
        var settings = options.Value;
        _ttl = settings.Ttl;
        MaxEntries = settings.MaxEntries;
        _misses = new MemoryCache(new MemoryCacheOptions { SizeLimit = settings.MaxEntries, Clock = clock });
    }

    public int MaxEntries { get; }

    /// <summary>Hosts currently remembered.</summary>
    public int Count => _misses.Count;

    /// <summary>
    /// The cache key for a <c>Host</c> header host (without port): trimmed and lower-cased, the same normalization the
    /// resolver applies before its query. Null for an empty host.
    /// </summary>
    public static string? Key(string? host) =>
        string.IsNullOrWhiteSpace(host) ? null : host.Trim().ToLowerInvariant();

    /// <summary>
    /// A remembered miss for <paramref name="key"/>, or the result of <paramref name="lookup"/>. Only one lookup per key
    /// runs at a time; concurrent callers share its result (or its exception, which is not cached). A miss is
    /// remembered; a tenant is not.
    /// </summary>
    public async Task<TenantHostLookup> GetOrLookupAsync(string key, Func<Task<TenantHostLookup>> lookup, CancellationToken ct)
    {
        if (TryGetMiss(key, out var cached))
            return cached;

        var mine = new TaskCompletionSource<TenantHostLookup>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = _inFlight.GetOrAdd(key, mine.Task);
        if (ReferenceEquals(shared, mine.Task))
        {
            try
            {
                // A lookup for this key may have finished between the first check and joining.
                var result = TryGetMiss(key, out cached) ? cached : await lookup();
                if (result.IsMiss)
                    Remember(key, result);
                mine.SetResult(result);
            }
            catch (Exception ex)
            {
                mine.SetException(ex);
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, Task<TenantHostLookup>>(key, mine.Task));
            }
        }

        // The caller that ran the lookup has its result already; the others stop waiting when their own request ends.
        return await shared.WaitAsync(ct);
    }

    public void Dispose() => _misses.Dispose();

    private bool TryGetMiss(string key, out TenantHostLookup miss)
    {
        if (_misses.TryGetValue(key, out TenantHostLookup? value) && value is not null)
        {
            miss = value;
            return true;
        }

        miss = TenantHostLookup.NotFound;
        return false;
    }

    // When the cache is full the entry is not stored (MemoryCache refuses an entry over SizeLimit and compacts).
    private void Remember(string key, TenantHostLookup miss) =>
        _misses.Set(key, miss, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _ttl, Size = 1 });
}
