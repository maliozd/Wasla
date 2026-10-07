using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Setup;

/// <summary>
/// One SQLite file per tenant with the production tenant model (only SQL Server's nvarchar(max) becomes TEXT), and a
/// new connection per context, so parallel requests race on the database like separate web requests do. SQLite's
/// locking is not SQL Server's: these tests show the logic is atomic and idempotent, not SQL Server lock behaviour.
/// </summary>
internal sealed class OperationalModeTestDatabases : ITenantDbContextFactory, IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "wasla-operational-mode-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly HashSet<Guid> _created = new();
    private readonly object _gate = new();
    private readonly FailingStatement _failure = new();
    private readonly OwnedSqlitePools _pools = new();

    /// <summary>When set, the first statement containing this SQL fails (once), as a database error would.</summary>
    public string? FailNextStatementContaining
    {
        get => _failure.SqlStart;
        set => _failure.SqlStart = value;
    }

    /** Every SQL statement run against any tenant database so far (reads and writes). */
    public int StatementCount => _failure.Count;

    public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
    {
        var connectionString = _pools.Own(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, customerId.ToString("N") + ".db"),
            DefaultTimeout = 30
        }.ToString());

        lock (_gate)
        {
            if (_created.Add(customerId))
            {
                using var setup = new ProductionModelSqliteContext(Options(connectionString));
                setup.Database.EnsureCreated();
                setup.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            }
        }

        return Task.FromResult<TenantDbContext>(new ProductionModelSqliteContext(Options(connectionString)));
    }

    /// <summary>A tenant with a settings row in <paramref name="mode"/> and the given automation settings.</summary>
    public async Task SeedSettingsAsync(Guid tenantId, TenantOperationalMode mode, bool autoApprove = false, bool autoPrint = false)
    {
        await using var db = await CreateAsync(tenantId, CancellationToken.None);
        var now = DateTime.UtcNow;
        db.TenantOperationalSettings.Add(new TenantOperationalSettings
        {
            Id = TenantOperationalModes.SettingsId,
            OperationalMode = mode,
            OrderSyncEnabled = true,
            AutoApproveNewOrders = autoApprove,
            AutoPrintReceiptOnAutoApprove = autoPrint,
            ReceiptPrintCopyCount = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    public async Task SeedUserAsync(Guid tenantId, Guid userId, UserRole role = UserRole.Owner)
    {
        await using var db = await CreateAsync(tenantId, CancellationToken.None);
        db.AppUsers.Add(new AppUser
        {
            Id = userId,
            Email = userId.ToString("N") + "@example.test",
            PasswordHash = "hash",
            FullName = "Test User",
            Role = role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task<TenantOperationalMode> ModeAsync(Guid tenantId)
    {
        await using var db = await CreateAsync(tenantId, CancellationToken.None);
        return (await TenantOperationalModes.ReadAutomationStatusAsync(db, CancellationToken.None)).Mode;
    }

    public void Dispose()
    {
        _pools.Clear();
        Directory.Delete(_directory, recursive: true);
    }

    private DbContextOptions<TenantDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connectionString).AddInterceptors(_failure).Options;

    private sealed class FailingStatement : DbCommandInterceptor
    {
        private string? _sqlStart;
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public string? SqlStart
        {
            get => Volatile.Read(ref _sqlStart);
            set => Volatile.Write(ref _sqlStart, value);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            Interlocked.Increment(ref _count);
            var start = SqlStart;
            if (start is null || !command.CommandText.Contains(start, StringComparison.Ordinal))
                return;
            if (Interlocked.CompareExchange(ref _sqlStart, null, start) == start)
                throw new InvalidOperationException("simulated tenant database failure");
        }
    }

    private sealed class ProductionModelSqliteContext(DbContextOptions<TenantDbContext> options) : TenantDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>().Property(order => order.RawPayloadJson).HasColumnType("TEXT");
            modelBuilder.Entity<TenantOperationalSettings>().Property(settings => settings.ReceiptTemplateSettingsJson).HasColumnType("TEXT");
        }
    }
}

/// <summary>A clock the test moves.</summary>
internal sealed class OperationalModeTestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public DateTime UtcNow => Now.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => Now;
}
