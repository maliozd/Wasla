using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Admin;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform;
using Wasla.Infrastructure.Platform.Mock;

namespace Wasla.Infrastructure.Services;

public sealed class TenantOperationalHealthOptions
{
    /// <summary>Upper bound for one tenant health read, connection included. Kept short on purpose.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Reads one tenant database for the Admin tenant detail page, and only when that page asks for it.
/// <list type="bullet">
/// <item>The tenant is opened through <see cref="ITenantDbContextFactory"/>, which resolves it from the CentralDb
/// registry by id; no connection detail comes from the caller.</item>
/// <item>Read-only: no-tracking queries and aggregates only; it never saves, migrates or repairs.</item>
/// <item>Bounded: the whole read has <see cref="TenantOperationalHealthOptions.Timeout"/>; each command the same.</item>
/// <item>Concurrent requests for the same tenant share one in-flight read; nothing is cached after it finishes.</item>
/// <item>Failures become a <see cref="TenantDatabaseState"/>. Exception messages are never returned or logged,
/// because database errors can carry server, login or database names.</item>
/// </list>
/// </summary>
public sealed class TenantOperationalHealthReader : ITenantOperationalHealthReader
{
    private static readonly OrderStatus[] OpenOrderStatuses =
    [
        OrderStatus.New,
        OrderStatus.Accepted,
        OrderStatus.Preparing,
        OrderStatus.ReadyForPickup,
        OrderStatus.OnTheWay
    ];

    private static readonly string? MockClientNamespace = typeof(MockGetirYemekFoodPlatformClient).Namespace;

    private readonly ConcurrentDictionary<Guid, Lazy<Task<TenantOperationalHealth>>> _inFlight = new();
    private readonly ITenantDbContextFactory _tenantDbs;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly ILogger<TenantOperationalHealthReader> _logger;

    public TenantOperationalHealthReader(
        ITenantDbContextFactory tenantDbs,
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        TimeProvider time,
        IOptions<TenantOperationalHealthOptions> options,
        ILogger<TenantOperationalHealthReader> logger)
    {
        _tenantDbs = tenantDbs;
        _scopes = scopes;
        _configuration = configuration;
        _time = time;
        _timeout = options.Value.Timeout > TimeSpan.Zero ? options.Value.Timeout : TimeSpan.FromSeconds(5);
        _logger = logger;
    }

    public async Task<TenantOperationalHealth> ReadAsync(Guid tenantId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Lazy<Task<TenantOperationalHealth>>? candidate = null;
        candidate = new Lazy<Task<TenantOperationalHealth>>(
            () => RunSharedReadAsync(tenantId, candidate!),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = _inFlight.GetOrAdd(tenantId, candidate);

        // The shared read has its own timeout; one caller cancelling stops only that caller's wait.
        return await shared.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<TenantOperationalHealth> RunSharedReadAsync(Guid tenantId, Lazy<Task<TenantOperationalHealth>> self)
    {
        try
        {
            await Task.Yield();
            return await ReadWithTimeoutAsync(tenantId).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<Guid, Lazy<Task<TenantOperationalHealth>>>(tenantId, self));
        }
    }

    private async Task<TenantOperationalHealth> ReadWithTimeoutAsync(Guid tenantId)
    {
        var providerMode = ResolveProviderMode();
        using var timeoutCts = new CancellationTokenSource(_timeout, _time);
        var read = ReadCoreAsync(tenantId, providerMode, timeoutCts.Token);

        try
        {
            // Hard upper bound even if a driver ignores the token while connecting.
            return await read.WaitAsync(_timeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLateFailure(read);
            return Unavailable(tenantId, TenantDatabaseState.TimedOut, providerMode, exception: null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return Unavailable(tenantId, TenantDatabaseState.TimedOut, providerMode, exception: null);
        }
        catch (Exception ex)
        {
            return Unavailable(tenantId, Classify(ex), providerMode, ex);
        }
    }

    private async Task<TenantOperationalHealth> ReadCoreAsync(Guid tenantId, TenantProviderMode providerMode, CancellationToken ct)
    {
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        PrepareProbeConnection(db, _timeout);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.Database.SetCommandTimeout((int)Math.Max(1, Math.Ceiling(_timeout.TotalSeconds)));

        // The first round trip proves the database exists and accepts this login. It must be explicit: EF's migration
        // history check treats a missing database as "no migrations applied" instead of failing.
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var applied = await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false);
        var migrations = TenantMigrationStatuses.Compare(applied, db.Database.GetMigrations());

        var now = _time.GetUtcNow().UtcDateTime;

        var automation = await SectionAsync(tenantId, "automation", ct,
            () => TenantOperationalModes.ReadAutomationStatusAsync(db, ct)).ConfigureAwait(false);

        var guidedSetup = await SectionAsync(tenantId, "guided-setup", ct, async () =>
        {
            var groups = await db.UserGuidedSetupStates
                .GroupBy(s => s.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            int Count(GuidedSetupStatus status) => groups.Where(g => g.Status == status).Sum(g => g.Count);
            return new TenantGuidedSetupSummary(
                Count(GuidedSetupStatus.InProgress),
                Count(GuidedSetupStatus.Completed),
                Count(GuidedSetupStatus.Skipped));
        }).ConfigureAwait(false);

        var users = await SectionAsync(tenantId, "users", ct, async () =>
        {
            var counts = (await db.AppUsers
                .Where(u => u.IsActive)
                .GroupBy(_ => 1)
                .Select(g => new { Active = g.Count(), Owners = g.Count(u => u.Role == UserRole.Owner) })
                .ToListAsync(ct)
                .ConfigureAwait(false))
                .SingleOrDefault();
            return new TenantUserSummary(counts?.Active ?? 0, counts?.Owners ?? 0);
        }).ConfigureAwait(false);

        var connections = await SectionAsync(tenantId, "connections", ct,
            () => ReadConnectionsAsync(db, automation?.OrderSyncConfigured ?? true, now, ct)).ConfigureAwait(false);

        var orders = await SectionAsync(tenantId, "orders", ct, async () =>
        {
            var since24Hours = now.AddHours(-24);
            var since7Days = now.AddDays(-7);
            var last24Hours = await db.Orders.CountAsync(o => o.ReceivedAt >= since24Hours, ct).ConfigureAwait(false);
            var last7Days = await db.Orders.CountAsync(o => o.ReceivedAt >= since7Days, ct).ConfigureAwait(false);
            var open = await db.Orders.CountAsync(o => OpenOrderStatuses.Contains(o.InternalStatus), ct).ConfigureAwait(false);
            var latest = await db.Orders.MaxAsync(o => (DateTime?)o.ReceivedAt, ct).ConfigureAwait(false);
            return new TenantOrderActivity(last24Hours, last7Days, open, latest);
        }).ConfigureAwait(false);

        var printJobs = await SectionAsync(tenantId, "print-jobs", ct, async () =>
        {
            var since24Hours = now.AddHours(-24);
            var queued = await db.PrintJobs
                .CountAsync(j => j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Printing, ct)
                .ConfigureAwait(false);
            var failed = await db.PrintJobs
                .CountAsync(j => j.Status == PrintJobStatus.Failed && j.UpdatedAt >= since24Hours, ct)
                .ConfigureAwait(false);
            return new TenantPrintJobSummary(queued, failed);
        }).ConfigureAwait(false);

        return new TenantOperationalHealth(
            TenantDatabaseState.Reachable,
            migrations,
            automation,
            guidedSetup,
            users,
            providerMode,
            connections,
            orders,
            printJobs,
            now);
    }

    private async Task<IReadOnlyList<TenantPlatformConnectionHealth>> ReadConnectionsAsync(
        TenantDbContext db,
        bool tenantOrderSyncEnabled,
        DateTime now,
        CancellationToken ct)
    {
        // Explicit projection: the encrypted API key and secret, supplier id and executor e-mail are never selected.
        var rows = await db.PlatformConnections
            .OrderBy(c => c.Platform)
            .Select(c => new
            {
                c.Id,
                c.Platform,
                c.StoreId,
                c.IsActive,
                c.LastSyncAttempt,
                c.LastSuccessfulSync,
                c.ConsecutiveFailures,
                c.CircuitOpenUntil
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return [];

        var since7Days = now.AddDays(-7);
        var lastFailures = await db.SyncLogs
            .Where(s => s.Status == SyncStatus.Failed && s.StartedAt >= since7Days)
            .GroupBy(s => s.PlatformConnectionId)
            .Select(g => new { ConnectionId = g.Key, LastFailedAt = g.Max(s => s.StartedAt) })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var lastFailureByConnection = lastFailures.ToDictionary(f => f.ConnectionId, f => f.LastFailedAt);

        var clientKinds = ResolveClientKinds();

        return rows
            .Select(c => new TenantPlatformConnectionHealth(
                c.Platform,
                string.IsNullOrWhiteSpace(c.StoreId) ? null : c.StoreId,
                c.IsActive,
                clientKinds.TryGetValue(c.Platform, out var kind) ? kind : ProviderClientKind.NotRegistered,
                c.LastSyncAttempt,
                c.LastSuccessfulSync,
                lastFailureByConnection.TryGetValue(c.Id, out var failedAt) ? failedAt : null,
                c.ConsecutiveFailures,
                c.CircuitOpenUntil,
                ProviderConnectionHealthRules.Classify(
                    c.IsActive,
                    tenantOrderSyncEnabled,
                    c.LastSuccessfulSync,
                    c.ConsecutiveFailures,
                    c.CircuitOpenUntil,
                    now)))
            .ToList();
    }

    /// <summary>
    /// Gives this health-probe context, and only it, a derived SQL Server connection string: no connection retries and
    /// a connect timeout below the read deadline. SqlClient treats "cannot open database" (error 4060) as transient and
    /// retries it after ConnectRetryInterval (10 seconds by default), so with the stored settings a missing database
    /// outlasted the deadline and was reported as TimedOut. The stored connection string, the factory's cached options
    /// and every other application connection keep their own settings.
    /// </summary>
    internal static void PrepareProbeConnection(TenantDbContext db, TimeSpan readTimeout)
    {
        if (!db.Database.IsSqlServer())
            return;

        var stored = db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(stored))
            return;

        var probe = new SqlConnectionStringBuilder(stored)
        {
            ConnectRetryCount = 0,
            ConnectTimeout = ProbeConnectTimeoutSeconds(readTimeout)
        };
        db.Database.SetConnectionString(probe.ConnectionString);
    }

    /// <summary>One second under the read deadline (at least 1, at most 15), so an unreachable server is reported as such.</summary>
    internal static int ProbeConnectTimeoutSeconds(TimeSpan readTimeout) =>
        Math.Clamp((int)Math.Floor(readTimeout.TotalSeconds) - 1, 1, 15);

    /// <summary>One section's failure (for example a table a pending migration has not created) leaves that section unknown.</summary>
    private async Task<T?> SectionAsync<T>(Guid tenantId, string section, CancellationToken ct, Func<Task<T>> read)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Tenant health section {Section} could not be read for tenant {TenantId} ({ExceptionType}, SQL error {SqlErrorNumber}).",
                section,
                tenantId,
                ex.GetType().Name,
                SqlErrorNumber(ex));
            return null;
        }
    }

    private IReadOnlyDictionary<FoodPlatform, ProviderClientKind> ResolveClientKinds()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            return scope.ServiceProvider
                .GetServices<IFoodPlatformClient>()
                .GroupBy(client => client.Platform)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().GetType().Namespace == MockClientNamespace
                        ? ProviderClientKind.Mock
                        : ProviderClientKind.Real);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Provider client registrations could not be inspected ({ExceptionType}).", ex.GetType().Name);
            return new Dictionary<FoodPlatform, ProviderClientKind>();
        }
    }

    private TenantProviderMode ResolveProviderMode()
    {
        try
        {
            return ProviderModeResolver.Resolve(_configuration).IsMock ? TenantProviderMode.Mock : TenantProviderMode.Real;
        }
        catch (InvalidOperationException)
        {
            return TenantProviderMode.Unknown;
        }
    }

    private TenantOperationalHealth Unavailable(
        Guid tenantId,
        TenantDatabaseState state,
        TenantProviderMode providerMode,
        Exception? exception)
    {
        _logger.LogWarning(
            "Tenant health read for tenant {TenantId} ended as {DatabaseState} ({ExceptionType}, SQL error {SqlErrorNumber}).",
            tenantId,
            state,
            exception?.GetType().Name ?? "none",
            SqlErrorNumber(exception));
        return TenantOperationalHealth.Unavailable(state, providerMode, _time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Classifies by the innermost meaningful exception. EF Core's default SQL Server execution strategy wraps
    /// transient SqlExceptions (4060 "cannot open database" included) in an InvalidOperationException, so the inner
    /// chain is inspected before falling back to Failed.
    /// </summary>
    internal static TenantDatabaseState Classify(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                // Decryption failures, and a decrypted value that is not a valid connection string.
                case CryptographicException or FormatException or ArgumentException:
                    return TenantDatabaseState.ConfigurationUnreadable;
                case DbException:
                    return TenantDatabaseState.Unreachable;
                case TimeoutException:
                    return TenantDatabaseState.TimedOut;
            }
        }

        return TenantDatabaseState.Failed;
    }

    private static int? SqlErrorNumber(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
            if (current is SqlException sql)
                return sql.Number;
        return null;
    }

    private static void ObserveLateFailure(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
