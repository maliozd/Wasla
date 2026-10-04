using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mock;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

public sealed class TenantOperationalHealthReaderTests : IDisposable
{
    private readonly RecordingTenantDbFactory _tenants = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly CapturingLogger _logger = new();

    public void Dispose() => _tenants.Dispose();

    [Fact]
    public async Task HealthyTenant_ReportsEverySection_FromItsOwnDatabaseOnly()
    {
        var now = DateTime.UtcNow;
        _tenants.CreateTenantDatabase(_tenantId);
        _tenants.Seed(_tenantId, db => TenantSeed.HealthyTenant(db, now));
        var other = Guid.NewGuid();
        _tenants.CreateTenantDatabase(other);
        _tenants.Seed(other, db => db.Orders.Add(TenantSeed.Order(now, OrderStatus.New, "OTHER-1")));

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantDatabaseState.Reachable, health.Database);
        Assert.True(health.IsComplete);
        Assert.Equal(TenantMigrationState.Current, health.Migrations!.State);
        Assert.Equal(TenantOperationalMode.Live, health.Automation!.Mode);
        Assert.Equal(AutomationState.Active, health.Automation.AutoReceipt);
        Assert.Equal(new TenantGuidedSetupSummary(0, 1, 0), health.GuidedSetup);
        Assert.Equal(new TenantUserSummary(2, 1), health.Users);
        Assert.Equal(TenantProviderMode.Mock, health.ProviderMode);

        var connection = Assert.Single(health.Connections!);
        Assert.Equal(FoodPlatform.TrendyolYemek, connection.Platform);
        Assert.Equal("STORE-1001", connection.StoreId);
        Assert.Equal(ProviderConnectionHealthState.Healthy, connection.State);
        Assert.Equal(ProviderClientKind.Mock, connection.ClientKind);
        Assert.NotNull(connection.LastFailedSyncUtc);

        Assert.Equal(1, health.Orders!.ReceivedLast24Hours);
        Assert.Equal(2, health.Orders.ReceivedLast7Days);
        Assert.Equal(1, health.Orders.OpenOrders);
        Assert.Equal(new TenantPrintJobSummary(1, 1), health.PrintJobs);

        // Only the selected tenant's database was opened.
        Assert.Equal([_tenantId], _tenants.Opened);
    }

    [Fact]
    public async Task HealthRead_IsReadOnly()
    {
        _tenants.CreateTenantDatabase(_tenantId);
        _tenants.Seed(_tenantId, db => TenantSeed.HealthyTenant(db, DateTime.UtcNow));
        _tenants.Counter.Reset();

        await Reader().ReadAsync(_tenantId, CancellationToken.None);

        // A fixed, bounded set of aggregates for one tenant: migration history (existence check + read), settings,
        // guided setup, users, connections, last failures, four order counts and two print-job counts.
        Assert.Equal(13, _tenants.Counter.Count);
        Assert.All(_tenants.Counter.Commands, sql =>
        {
            var statement = sql.TrimStart().ToUpperInvariant();
            Assert.True(statement.StartsWith("SELECT", StringComparison.Ordinal), sql);
        });
        Assert.All(_tenants.Counter.Commands, sql =>
        {
            Assert.DoesNotContain("EncryptedApi", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("PasswordHash", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("PayloadJson", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("ErrorMessage", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("SupplierId", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("ExecutorEmail", sql, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task HealthResult_NeverContainsCredentialsOrErrorText()
    {
        _tenants.CreateTenantDatabase(_tenantId);
        _tenants.Seed(_tenantId, db => TenantSeed.HealthyTenant(db, DateTime.UtcNow));

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);
        var json = JsonSerializer.Serialize(health);

        foreach (var secret in SecretMarkers.All)
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(TenantPlatformConnectionHealth).GetProperties(), p =>
            p.Name.Contains("Key", StringComparison.Ordinal)
            || p.Name.Contains("Secret", StringComparison.Ordinal)
            || p.Name.Contains("Supplier", StringComparison.Ordinal)
            || p.Name.Contains("Executor", StringComparison.Ordinal)
            || p.Name.Contains("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigrationBehind_IsReportedAsPending_WithTheMissingCount()
    {
        _tenants.CreateTenantDatabase(_tenantId, expected => expected.Take(expected.Count - 2));

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantDatabaseState.Reachable, health.Database);
        Assert.Equal(TenantMigrationState.Pending, health.Migrations!.State);
        Assert.Equal(2, health.Migrations.PendingCount);
        Assert.NotEqual(health.Migrations.LatestExpectedMigration, health.Migrations.LatestAppliedMigration);
    }

    [Fact]
    public async Task MigrationFromANewerBuild_IsReportedAsDatabaseAhead()
    {
        _tenants.CreateTenantDatabase(_tenantId, expected => [.. expected, "29991231000000_FromTheFuture"]);

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantMigrationState.DatabaseAhead, health.Migrations!.State);
    }

    [Theory]
    [InlineData(TenantOperationalMode.Setup, AutomationState.PendingSetup)]
    [InlineData(TenantOperationalMode.Live, AutomationState.Active)]
    public async Task OperationalMode_IsReadWithTheEffectiveAutomation(TenantOperationalMode mode, AutomationState expectedAutoApprove)
    {
        _tenants.CreateTenantDatabase(_tenantId);
        _tenants.Seed(_tenantId, db => TenantSeed.HealthyTenant(db, DateTime.UtcNow, mode));

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(mode, health.Automation!.Mode);
        Assert.Equal(expectedAutoApprove, health.Automation.AutoApprove);
        Assert.Equal(AutomationState.Active, health.Automation.OrderSync);
    }

    [Fact]
    public async Task MissingSettingsRow_ReadsAsLive_LikeTheRestOfTheApplication()
    {
        _tenants.CreateTenantDatabase(_tenantId);

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantAutomationStatus.WithoutSettings, health.Automation);
        Assert.Empty(health.Connections!);
        Assert.Null(health.Orders!.LatestReceivedAtUtc);
    }

    [Fact]
    public async Task TenantSyncOff_MarksConnectionsSyncOff()
    {
        _tenants.CreateTenantDatabase(_tenantId);
        _tenants.Seed(_tenantId, db =>
        {
            TenantSeed.HealthyTenant(db, DateTime.UtcNow);
            db.TenantOperationalSettings.Local.Single().OrderSyncEnabled = false;
        });

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(ProviderConnectionHealthState.SyncOff, health.Connections!.Single().State);
    }

    [Fact]
    public async Task UnreachableDatabase_IsReturnedAsAState_NotThrown()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"wasla-missing-{Guid.NewGuid():N}", "nope.db");
        _tenants.Override(_tenantId, _ => Task.FromResult(new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite($"Data Source={missing};Mode=ReadOnly;Pooling=False").Options)));

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantDatabaseState.Unreachable, health.Database);
        Assert.False(health.IsReachable);
        Assert.Null(health.Migrations);
        Assert.Null(health.Connections);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains(missing, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnreadableConnectionDetails_AreReportedSeparately()
    {
        _tenants.Override(_tenantId, _ => throw new CryptographicException("bad key " + SecretMarkers.ConnectionString));

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantDatabaseState.ConfigurationUnreadable, health.Database);
        Assert.All(_logger.Messages, m => Assert.DoesNotContain("CONNSTR-SECRET", m, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedFailure_IsSanitized()
    {
        _tenants.Override(_tenantId, _ => throw new InvalidOperationException(SecretMarkers.ConnectionString));

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantDatabaseState.Failed, health.Database);
        Assert.DoesNotContain("CONNSTR-SECRET", JsonSerializer.Serialize(health), StringComparison.Ordinal);
        Assert.All(_logger.Messages, m => Assert.DoesNotContain("CONNSTR-SECRET", m, StringComparison.Ordinal));
        Assert.Contains(_logger.Messages, m => m.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowDatabase_TimesOut_WithinTheConfiguredBound()
    {
        _tenants.Override(_tenantId, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var started = System.Diagnostics.Stopwatch.StartNew();

        var health = await Reader(TimeSpan.FromMilliseconds(300)).ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantDatabaseState.TimedOut, health.Database);
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DriverIgnoringCancellation_StillTimesOut()
    {
        var release = new TaskCompletionSource();
        _tenants.Override(_tenantId, async _ =>
        {
            await release.Task;
            throw new InvalidOperationException("late");
        });

        try
        {
            var health = await Reader(TimeSpan.FromMilliseconds(300)).ReadAsync(_tenantId, CancellationToken.None);
            Assert.Equal(TenantDatabaseState.TimedOut, health.Database);
        }
        finally
        {
            release.SetResult();
        }
    }

    [Fact]
    public async Task CallerCancellation_IsHonoured()
    {
        var gate = new TaskCompletionSource();
        _tenants.Override(_tenantId, async ct =>
        {
            await gate.Task.WaitAsync(ct);
            throw new InvalidOperationException("never");
        });
        using var cts = new CancellationTokenSource();
        var reader = Reader(TimeSpan.FromSeconds(30));

        var read = reader.ReadAsync(_tenantId, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        gate.SetResult();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(_tenantId, cancelled.Token));
    }

    [Fact]
    public async Task ConcurrentReadsOfOneTenant_ShareASingleDatabaseRead()
    {
        _tenants.CreateTenantDatabase(_tenantId);
        var gate = new TaskCompletionSource();
        var real = _tenants.ConnectionStringFor(_tenantId);
        _tenants.Override(_tenantId, async ct =>
        {
            await gate.Task.WaitAsync(ct);
            return new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(real).Options);
        });
        var reader = Reader();

        var first = reader.ReadAsync(_tenantId, CancellationToken.None);
        var second = reader.ReadAsync(_tenantId, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(_tenants.Opened);
        Assert.Same(results[0], results[1]);

        // Nothing is cached once the read has finished.
        await reader.ReadAsync(_tenantId, CancellationToken.None);
        Assert.Equal(2, _tenants.Opened.Count);
    }

    [Fact]
    public async Task OneCallerCancelling_DoesNotCancelAnotherCallersSharedRead()
    {
        _tenants.CreateTenantDatabase(_tenantId);
        var gate = new TaskCompletionSource();
        var real = _tenants.ConnectionStringFor(_tenantId);
        _tenants.Override(_tenantId, async ct =>
        {
            await gate.Task.WaitAsync(ct);
            return new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(real).Options);
        });
        var reader = Reader();
        using var cts = new CancellationTokenSource();

        var cancelled = reader.ReadAsync(_tenantId, cts.Token);
        var kept = reader.ReadAsync(_tenantId, CancellationToken.None);
        await cts.CancelAsync();
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(TenantDatabaseState.Reachable, (await kept).Database);
    }

    [Fact]
    public async Task ASectionThatCannotBeRead_IsUnknown_WhileTheRestIsShown()
    {
        _tenants.CreateTenantDatabase(_tenantId);
        _tenants.Seed(_tenantId, db => TenantSeed.HealthyTenant(db, DateTime.UtcNow));
        _tenants.Execute(_tenantId, "DROP TABLE \"PrintJobs\";");

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantDatabaseState.Reachable, health.Database);
        Assert.Null(health.PrintJobs);
        Assert.NotNull(health.Orders);
        Assert.NotNull(health.Connections);
        Assert.False(health.IsComplete);
        Assert.Contains(_logger.Messages, m => m.Contains("print-jobs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderClientKinds_ComeFromTheRegisteredClients()
    {
        _tenants.CreateTenantDatabase(_tenantId);
        _tenants.Seed(_tenantId, db =>
        {
            TenantSeed.HealthyTenant(db, DateTime.UtcNow);
            db.PlatformConnections.Add(new PlatformConnection { Platform = FoodPlatform.Yemeksepeti, StoreId = "Y1", IsActive = false });
            db.PlatformConnections.Add(new PlatformConnection { Platform = FoodPlatform.GetirYemek, StoreId = "G1", IsActive = true });
        });
        var services = new ServiceCollection();
        services.AddSingleton<ISecretManager, UnusedSecretManager>();
        services.AddSingleton<IFoodPlatformClient>(new FakeRealClient(FoodPlatform.TrendyolYemek));
        services.AddSingleton<IFoodPlatformClient, MockGetirYemekFoodPlatformClient>();

        var health = await Reader(configuration: ProviderMode("Real"), services: services).ReadAsync(_tenantId, CancellationToken.None);
        var byPlatform = health.Connections!.ToDictionary(c => c.Platform);

        Assert.Equal(TenantProviderMode.Real, health.ProviderMode);
        Assert.Equal(ProviderClientKind.Real, byPlatform[FoodPlatform.TrendyolYemek].ClientKind);
        Assert.Equal(ProviderClientKind.Mock, byPlatform[FoodPlatform.GetirYemek].ClientKind);
        Assert.Equal(ProviderClientKind.NotRegistered, byPlatform[FoodPlatform.Yemeksepeti].ClientKind);
        Assert.Equal(ProviderConnectionHealthState.Disabled, byPlatform[FoodPlatform.Yemeksepeti].State);
        Assert.Equal(ProviderConnectionHealthState.NeverSynced, byPlatform[FoodPlatform.GetirYemek].State);
    }

    // Probe-only SQL Server connection -------------------------------------------------------------------

    [Fact]
    public void ProbeConnection_DisablesRetriesAndShortensConnect_ForTheProbeContextOnly()
    {
        const string stored = "Server=(localdb)\\Fixture;Database=Wasla_Tenant_x;Integrated Security=True;TrustServerCertificate=True;Connect Retry Count=5;Connect Timeout=30";
        var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(stored).Options;
        using var probe = new TenantDbContext(options);

        TenantOperationalHealthReader.PrepareProbeConnection(probe, TimeSpan.FromSeconds(5));

        var derived = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(probe.Database.GetConnectionString());
        Assert.Equal(0, derived.ConnectRetryCount);
        Assert.Equal(4, derived.ConnectTimeout);
        Assert.Equal("(localdb)\\Fixture", derived.DataSource);
        Assert.Equal("Wasla_Tenant_x", derived.InitialCatalog);
        Assert.True(derived.IntegratedSecurity);

        // The shared options (and so every normal application context) keep the stored settings.
        using var normal = new TenantDbContext(options);
        var untouched = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(normal.Database.GetConnectionString());
        Assert.Equal(5, untouched.ConnectRetryCount);
        Assert.Equal(30, untouched.ConnectTimeout);
    }

    [Theory]
    [InlineData(5000, 4)]
    [InlineData(2500, 1)]
    [InlineData(300, 1)]
    [InlineData(60000, 15)]
    public void ProbeConnectTimeout_StaysUnderTheReadDeadline(int readTimeoutMs, int expectedSeconds) =>
        Assert.Equal(expectedSeconds, TenantOperationalHealthReader.ProbeConnectTimeoutSeconds(TimeSpan.FromMilliseconds(readTimeoutMs)));

    [Fact]
    public void ProbeConnection_LeavesNonSqlServerContextsAlone()
    {
        var connectionString = _tenants.ConnectionStringFor(_tenantId);
        using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connectionString).Options);

        TenantOperationalHealthReader.PrepareProbeConnection(db, TimeSpan.FromSeconds(5));

        Assert.Equal(connectionString, db.Database.GetConnectionString());
    }

    [Fact]
    public async Task MalformedStoredConnectionString_IsConfigurationUnreadable_WithoutLeakingIt()
    {
        _tenants.Override(_tenantId, _ => Task.FromResult(new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer("Serverx=nowhere;Passwordy=" + SecretMarkers.ApiSecret).Options)));

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantDatabaseState.ConfigurationUnreadable, health.Database);
        Assert.All(_logger.Messages, m => Assert.DoesNotContain(SecretMarkers.ApiSecret, m, StringComparison.Ordinal));
    }

    [Fact]
    public void Classification_LooksThroughTheExecutionStrategyWrapper()
    {
        var database = new Microsoft.Data.Sqlite.SqliteException("cannot open database", 14);

        // EF Core's SQL Server execution strategy wraps transient errors such as 4060 like this.
        Assert.Equal(TenantDatabaseState.Unreachable, TenantOperationalHealthReader.Classify(new InvalidOperationException("transient", database)));
        Assert.Equal(TenantDatabaseState.Unreachable, TenantOperationalHealthReader.Classify(database));
        Assert.Equal(TenantDatabaseState.TimedOut, TenantOperationalHealthReader.Classify(new TimeoutException()));
        Assert.Equal(TenantDatabaseState.ConfigurationUnreadable, TenantOperationalHealthReader.Classify(new CryptographicException()));
        Assert.Equal(TenantDatabaseState.ConfigurationUnreadable, TenantOperationalHealthReader.Classify(new ArgumentException("Keyword not supported")));
        Assert.Equal(TenantDatabaseState.Failed, TenantOperationalHealthReader.Classify(new InvalidOperationException("Customer not found")));
    }

    [Fact]
    public async Task WrappedTransientDatabaseError_IsUnreachable_NotFailed()
    {
        _tenants.Override(_tenantId, _ => throw new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.",
            new Microsoft.Data.Sqlite.SqliteException("Cannot open database " + SecretMarkers.ConnectionString, 14)));

        var health = await Reader().ReadAsync(_tenantId, CancellationToken.None);

        Assert.Equal(TenantDatabaseState.Unreachable, health.Database);
        Assert.All(_logger.Messages, m => Assert.DoesNotContain("CONNSTR-SECRET", m, StringComparison.Ordinal));
    }

    // Helpers ----------------------------------------------------------------------------------------

    private TenantOperationalHealthReader Reader(
        TimeSpan? timeout = null,
        IConfiguration? configuration = null,
        IServiceCollection? services = null)
    {
        if (services is null)
        {
            services = new ServiceCollection();
            services.AddSingleton<ISecretManager, UnusedSecretManager>();
            services.AddSingleton<IFoodPlatformClient, MockTrendyolYemekFoodPlatformClient>();
            services.AddSingleton<IFoodPlatformClient, MockGetirYemekFoodPlatformClient>();
        }

        return new TenantOperationalHealthReader(
            _tenants,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            configuration ?? ProviderMode("Mock"),
            TimeProvider.System,
            Options.Create(new TenantOperationalHealthOptions { Timeout = timeout ?? TimeSpan.FromSeconds(10) }),
            _logger);
    }

    private static IConfiguration ProviderMode(string mode) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platforms:ProviderMode"] = mode }).Build();

    private sealed class CapturingLogger : ILogger<TenantOperationalHealthReader>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " EXCEPTION:" + exception));
        }
    }

    private sealed class UnusedSecretManager : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A client outside the mock namespace, standing in for a real HTTP client.</summary>
    private sealed class FakeRealClient(FoodPlatform platform) : IFoodPlatformClient
    {
        public FoodPlatform Platform => platform;
        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, CancellationToken ct) => throw new NotSupportedException();
        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => throw new NotSupportedException();
        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) => throw new NotSupportedException();
    }
}
