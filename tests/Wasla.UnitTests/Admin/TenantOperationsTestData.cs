using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Recognisable values planted in every secret-bearing column. A test that finds one of them in output has found a leak.
/// </summary>
internal static class SecretMarkers
{
    public const string ConnectionString = "Server=tcp:secret-sql.example;Password=CONNSTR-SECRET-7f3a";
    public const string EncryptedConnectionString = "ENC-CONNSTR-SECRET-7f3a";
    public const string TokenHash = "TOKENHASH-SECRET-91bc";
    public const string AdminPasswordHash = "ADMIN-PWHASH-SECRET-4d2e";
    public const string RegistrationPasswordHash = "REG-PWHASH-SECRET-5e1f";
    public const string TenantUserPasswordHash = "USER-PWHASH-SECRET-6a0b";
    public const string ApiKey = "APIKEY-SECRET-0c9d";
    public const string ApiSecret = "APISECRET-SECRET-8e7f";
    public const string SupplierId = "SUPPLIER-SECRET-2b3c";
    public const string ExecutorEmail = "executor-secret@example.test";
    public const string MigrationFailureText = "Failed: Login failed for user 'sa'. Password=MIGRATION-SECRET-1a2b";
    public const string SyncErrorText = "Unauthorized token=SYNCERR-SECRET-3c4d";
    public const string PrintJobPayload = "PAYLOAD-SECRET-9f8e";
    public const string DeviceIp = "203.0.113.77";

    public static IReadOnlyList<string> All { get; } =
    [
        ConnectionString,
        "CONNSTR-SECRET-7f3a",
        EncryptedConnectionString,
        TokenHash,
        AdminPasswordHash,
        RegistrationPasswordHash,
        TenantUserPasswordHash,
        ApiKey,
        ApiSecret,
        SupplierId,
        ExecutorEmail,
        "MIGRATION-SECRET-1a2b",
        "Login failed for user",
        "SYNCERR-SECRET-3c4d",
        PrintJobPayload,
        DeviceIp
    ];
}

/// <summary>Counts and records every SQL command a context sends.</summary>
internal sealed class SqlCommandCounter : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public IReadOnlyList<string> Commands => _commands.ToArray();

    public int Count => _commands.Count;

    public void Reset() => _commands.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        _commands.Enqueue(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        _commands.Enqueue(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        _commands.Enqueue(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return ValueTask.FromResult(result);
    }
}

/// <summary>A file-backed SQLite CentralDb for one test.</summary>
internal sealed class CentralTestDatabase : IDisposable
{
    private readonly string _path;

    public CentralTestDatabase()
    {
        _path = Path.Combine(Path.GetTempPath(), $"wasla-central-{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={_path};Pooling=False";
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public string ConnectionString { get; }

    public SqlCommandCounter Counter { get; } = new();

    public CentralDbContext CreateContext(bool counted = false)
    {
        var builder = new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(ConnectionString);
        if (counted)
            builder.AddInterceptors(Counter);
        return new CentralDbContext(builder.Options);
    }

    public Tenant AddTenant(
        string slug,
        string? name = null,
        bool isActive = true,
        DateTime? createdAt = null,
        DateTime? updatedAt = null,
        string? lastMigrationResult = "Success",
        string encryptedConnectionString = SecretMarkers.EncryptedConnectionString,
        string? planCode = null,
        MembershipStatus membershipStatus = MembershipStatus.Active)
    {
        using var db = CreateContext();
        var created = createdAt ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"Restaurant {slug}",
            Slug = slug,
            PrimaryDomain = $"{slug}.wasla.local",
            DatabaseName = $"Wasla_Tenant_{slug}",
            EncryptedConnectionString = encryptedConnectionString,
            IsActive = isActive,
            LastMigrationAt = lastMigrationResult is null ? null : created,
            LastMigrationResult = lastMigrationResult,
            CreatedAt = created,
            UpdatedAt = updatedAt ?? created
        };
        db.Tenants.Add(tenant);
        if (planCode is not null)
        {
            db.TenantMemberships.Add(new TenantMembership
            {
                TenantId = tenant.Id,
                PlanCode = planCode,
                Status = membershipStatus,
                BillingPeriod = "Monthly",
                StartedAt = created,
                OwnerEmail = "owner-pii@example.test",
                BusinessPhone = "5551112233"
            });
        }

        db.SaveChanges();
        return tenant;
    }

    public void AddDevice(Guid tenantId, string name, bool isActive, DateTime? lastSeenAt, DateTime? removedAt = null)
    {
        using var db = CreateContext();
        db.PrintBridgeDevices.Add(new PrintBridgeDevice
        {
            TenantId = tenantId,
            Name = name,
            TokenHash = SecretMarkers.TokenHash + Guid.NewGuid().ToString("N"),
            IsActive = isActive,
            LastSeenAt = lastSeenAt,
            RemovedAtUtc = removedAt,
            AppVersion = "1.4.2",
            MachineName = "KITCHEN-PC",
            LastIpAddress = SecretMarkers.DeviceIp,
            InstallationId = Guid.NewGuid()
        });
        db.SaveChanges();
    }

    public PendingRegistration AddRegistration(
        string slug,
        PendingRegistrationStatus status,
        Guid? tenantId = null,
        DateTime? createdAt = null,
        DateTime? paymentSucceededAt = null)
    {
        using var db = CreateContext();
        var registration = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            PlanCode = "Pro",
            BillingPeriod = "Monthly",
            BusinessName = $"Registration {slug}",
            Slug = slug,
            PrimaryDomain = $"{slug}.wasla.local",
            DatabaseName = $"Wasla_Tenant_{slug}",
            BusinessPhone = "5551112233",
            Country = "TR",
            City = "Istanbul",
            District = "Kadikoy",
            OwnerFullName = "Owner Person",
            OwnerEmail = $"{slug}@example.test",
            PasswordHash = SecretMarkers.RegistrationPasswordHash,
            Status = status,
            CreatedAtUtc = createdAt ?? new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            PaymentSucceededAtUtc = paymentSucceededAt,
            TenantId = tenantId,
            ProvisionedAtUtc = status == PendingRegistrationStatus.Provisioned ? new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc) : null
        };
        db.PendingRegistrations.Add(registration);
        db.SaveChanges();
        return registration;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}

/// <summary>SQLite needs TEXT for the SQL Server max-length columns when the schema is created.</summary>
internal sealed class SqliteSchemaTenantDbContext(DbContextOptions<TenantDbContext> options) : TenantDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Order>().Property(order => order.RawPayloadJson).HasColumnType("TEXT");
        modelBuilder.Entity<TenantOperationalSettings>().Property(settings => settings.ReceiptTemplateSettingsJson).HasColumnType("TEXT");
        modelBuilder.Entity<PrintJob>().Property(job => job.PayloadJson).HasColumnType("TEXT");
    }
}

/// <summary>
/// One file-backed SQLite database per tenant, opened through the same <see cref="ITenantDbContextFactory"/> seam the
/// application uses. It records which tenants were opened so tests can prove there is no fan-out.
/// </summary>
internal sealed class RecordingTenantDbFactory : ITenantDbContextFactory, IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wasla-tenants-").FullName;
    private readonly ConcurrentQueue<Guid> _opened = new();
    private readonly ConcurrentDictionary<Guid, Func<CancellationToken, Task<TenantDbContext>>> _overrides = new();

    public IReadOnlyList<Guid> Opened => _opened.ToArray();

    public SqlCommandCounter Counter { get; } = new();

    public string ConnectionStringFor(Guid tenantId) =>
        $"Data Source={Path.Combine(_directory, $"{tenantId:N}.db")};Pooling=False";

    public void Override(Guid tenantId, Func<CancellationToken, Task<TenantDbContext>> create) => _overrides[tenantId] = create;

    public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
    {
        _opened.Enqueue(customerId);
        if (_overrides.TryGetValue(customerId, out var create))
            return create(ct);

        return Task.FromResult(new TenantDbContext(Options(ConnectionStringFor(customerId))));
    }

    /// <summary>Creates the tenant schema and records the given migrations as applied (all of this build's by default).</summary>
    public void CreateTenantDatabase(Guid tenantId, Func<IReadOnlyList<string>, IEnumerable<string>>? appliedMigrations = null)
    {
        var connectionString = ConnectionStringFor(tenantId);
        using (var schema = new SqliteSchemaTenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connectionString).Options))
            schema.Database.EnsureCreated();

        using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connectionString).Options);
        var expected = db.Database.GetMigrations().ToList();
        var applied = appliedMigrations is null ? expected : appliedMigrations(expected).ToList();
        db.Database.ExecuteSqlRaw("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);");
        foreach (var id in applied)
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" VALUES ({0}, '8.0.10');", id);
    }

    public void Seed(Guid tenantId, Action<TenantDbContext> seed)
    {
        using var db = new SqliteSchemaTenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(ConnectionStringFor(tenantId)).Options);
        seed(db);
        db.SaveChanges();
    }

    public void Execute(Guid tenantId, string sql)
    {
        using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(ConnectionStringFor(tenantId)).Options);
        db.Database.ExecuteSqlRaw(sql);
    }

    private DbContextOptions<TenantDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connectionString).AddInterceptors(Counter).Options;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class TenantSeed
{
    public static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    public static void HealthyTenant(TenantDbContext db, DateTime now, TenantOperationalMode mode = TenantOperationalMode.Live)
    {
        db.TenantOperationalSettings.Add(new TenantOperationalSettings
        {
            Id = TenantOperationalModes.SettingsId,
            OperationalMode = mode,
            OrderSyncEnabled = true,
            AutoApproveNewOrders = true,
            AutoPrintReceiptOnAutoApprove = true
        });
        var owner = new AppUser { Email = "owner@tenant.test", PasswordHash = SecretMarkers.TenantUserPasswordHash, FullName = "Owner", Role = UserRole.Owner, IsActive = true };
        db.AppUsers.Add(owner);
        db.AppUsers.Add(new AppUser { Email = "kitchen@tenant.test", PasswordHash = SecretMarkers.TenantUserPasswordHash, FullName = "Kitchen", Role = UserRole.Kitchen, IsActive = true });
        db.UserGuidedSetupStates.Add(new UserGuidedSetupState { UserId = owner.Id, Status = GuidedSetupStatus.Completed });

        var connection = new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = "STORE-1001",
            SupplierId = SecretMarkers.SupplierId,
            ExecutorEmail = SecretMarkers.ExecutorEmail,
            EncryptedApiKey = SecretMarkers.ApiKey,
            EncryptedApiSecret = SecretMarkers.ApiSecret,
            IsActive = true,
            LastSyncAttempt = now.AddSeconds(-20),
            LastSuccessfulSync = now.AddSeconds(-20)
        };
        db.PlatformConnections.Add(connection);
        db.SyncLogs.Add(new SyncLog
        {
            PlatformConnectionId = connection.Id,
            StartedAt = now.AddHours(-2),
            Status = SyncStatus.Failed,
            ErrorMessage = SecretMarkers.SyncErrorText
        });
        db.IntegrationErrors.Add(new IntegrationError
        {
            Platform = FoodPlatform.TrendyolYemek,
            PlatformConnectionId = connection.Id,
            ErrorType = "HttpRequestException",
            ErrorMessage = SecretMarkers.SyncErrorText
        });

        var recent = Order(now.AddHours(-1), OrderStatus.New, "A-1");
        db.Orders.Add(recent);
        db.Orders.Add(Order(now.AddDays(-3), OrderStatus.Delivered, "A-2"));
        db.Orders.Add(Order(now.AddDays(-20), OrderStatus.Delivered, "A-3"));
        db.PrintJobs.Add(new PrintJob { OrderId = recent.Id, Status = PrintJobStatus.Pending, PayloadJson = SecretMarkers.PrintJobPayload });
        db.PrintJobs.Add(new PrintJob { OrderId = recent.Id, Status = PrintJobStatus.Failed, PayloadJson = SecretMarkers.PrintJobPayload, UpdatedAt = now.AddHours(-1) });
    }

    public static Order Order(DateTime receivedAt, OrderStatus status, string externalId) => new()
    {
        Platform = FoodPlatform.TrendyolYemek,
        ExternalOrderId = externalId,
        ExternalOrderCode = externalId,
        IdempotencyKey = $"key-{externalId}",
        InternalStatus = status,
        PlatformStatus = "Created",
        CustomerName = "Order Customer",
        CustomerPhone = "5550000000",
        CustomerAddress = "Street 1",
        CreatedAtPlatform = receivedAt,
        ReceivedAt = receivedAt,
        RawPayloadJson = "{}"
    };
}
