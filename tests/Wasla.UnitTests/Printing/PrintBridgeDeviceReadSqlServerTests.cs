using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Billing;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// WAS-205: the Print Bridge device reads outside setups and moves, on a real SQL Server with a realistic number of
/// restaurants and devices (<see cref="PrintBridgeDeviceReadDatabase"/>), so the optimizer picks the secondary indexes it
/// picks in production. Each read runs against a writer of one device row that locks the row first and then changes an
/// indexed column, in the lock order of a real token revocation or device removal (WAS-203's two-step writer). A read
/// that held an index key while it looked up that row deadlocked here; the reads now take their ids from an index alone
/// and the rows by key, so they wait for the writer instead. A deadlock fails the test: nothing retries.
/// </summary>
public sealed class PrintBridgeDeviceReadSqlServerTests(PrintBridgeDeviceReadDatabase fixture) : IClassFixture<PrintBridgeDeviceReadDatabase>
{
    private const int Siblings = 2;
    private readonly PrintBridgeSqlServerRace _race = new(fixture.Database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_WhileTheTokenIsRevokedOrTheDeviceRemoved_WaitsAndRejectsTheOldToken(bool removed)
    {
        var device = await fixture.DeviceAsync(Siblings);

        var result = await _race.ReadDuringTwoStepDeviceWriteAsync(
            device.Id,
            db => ChangeAsync(db, device.Id, removed),
            readFrom: null,
            db => new PrintBridgeAuthService(db, NullLogger<PrintBridgeAuthService>.Instance)
                .AuthenticateDetailedAsync(device.Token, new PrintBridgeClientInfo("QA-PC", "1.0.0.0", null, null), CancellationToken.None));

        Assert.False(result.Succeeded);
        Assert.Equal(PrintBridgeAuthFailureCode.InvalidToken, result.FailureCode);
    }

    [Fact]
    public async Task Authentication_StillAcceptsTheToken_AndRecordsThePoll()
    {
        var device = await fixture.DeviceAsync(Siblings);

        await using (var db = fixture.Database.Open("Poll"))
        {
            var result = await new PrintBridgeAuthService(db, NullLogger<PrintBridgeAuthService>.Instance)
                .AuthenticateDetailedAsync(device.Token, new PrintBridgeClientInfo("QA-PC", "1.0.0.0", "XP-80C", "10.0.0.5", device.Installation), Ct);
            Assert.True(result.Succeeded);
            Assert.Equal(device.Tenant, result.Context!.TenantId);
            Assert.Equal(device.Id, result.Context.DeviceId);
        }

        await using var check = fixture.Database.Open("Inspector");
        var row = await check.PrintBridgeDevices.AsNoTracking().SingleAsync(d => d.Id == device.Id, Ct);
        Assert.Equal("QA-PC", row.MachineName);
        Assert.Equal("XP-80C", row.PrinterName);
        Assert.NotNull(row.LastSeenAt);
    }

    [Fact]
    public async Task TheDevicesPageList_WhileADeviceIsRemoved_WaitsAndListsTheOthers()
    {
        var device = await fixture.DeviceAsync(Siblings);

        var list = await _race.ReadDuringTwoStepDeviceWriteAsync(
            device.Id,
            db => ChangeAsync(db, device.Id, removed: true),
            readFrom: null,
            db => Manage(db).ListDevicesAsync(device.Tenant, CancellationToken.None));

        Assert.Equal(Siblings, list.Count);
        Assert.DoesNotContain(list, d => d.Id == device.Id);
    }

    [Fact]
    public async Task TheSetupStatus_WhileADeviceIsRemoved_WaitsAndReadsThePrintingFacts()
    {
        // The setup status turns a failed read into "printing unknown" and logs it, so a lost deadlock shows in the log.
        var device = await fixture.DeviceAsync(Siblings);
        var log = new RecordingLogger<TenantSetupStatusService>();

        await _race.ReadDuringTwoStepDeviceWriteAsync(
            device.Id,
            db => ChangeAsync(db, device.Id, removed: true),
            readFrom: null,
            db => new TenantSetupStatusService(db, NoTenantDatabase.Instance, TimeProvider.System, log)
                .GetAsync(device.Tenant, Guid.NewGuid(), CancellationToken.None));

        Assert.DoesNotContain(log.Messages, m => m.Contains("Printing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCentralAdminTenantDetail_WhileADeviceIsRemoved_WaitsAndShowsTheOthers()
    {
        var device = await fixture.DeviceAsync(Siblings);

        var detail = await _race.ReadDuringTwoStepDeviceWriteAsync(
            device.Id,
            db => ChangeAsync(db, device.Id, removed: true),
            readFrom: null,
            db => new CentralAdminTenantOperationsService(db, TimeProvider.System).GetTenantDetailAsync(device.Tenant, 1, CancellationToken.None));

        Assert.NotNull(detail);
        Assert.Equal(Siblings, detail.PrintBridgeDevices.Items.Count);
        Assert.Equal(Siblings, detail.PrintBridgeDeviceCounts.Total);
        Assert.Equal(Siblings, detail.PrintBridgeDeviceCounts.Active);
    }

    [Fact]
    public async Task TheCentralAdminTenantDetail_ReturnsOnePageAndOneRowOfCounts_FromTheClusteredIndexAlone()
    {
        // 120 devices of one restaurant among the 3,900 of the others. The server cuts the page and aggregates the counts:
        // the client receives at most one page of rows and one row of counts and sends no list of device ids. Neither
        // statement reads a secondary index of the device table, so neither can hold an index key while it waits for a
        // device row (WAS-203, WAS-205); the race above shows it waiting for a removal instead.
        var device = await fixture.DeviceAsync(siblings: 119);
        var recorder = new SqlStatementRecorder();
        TenantOperationsDetail? detail;
        await using (var db = fixture.Database.Open("Admin", recorder))
            detail = await new CentralAdminTenantOperationsService(db, TimeProvider.System).GetTenantDetailAsync(device.Tenant, 2, Ct);

        var devices = detail!.PrintBridgeDevices;
        Assert.Equal((2, 3, 120, 50), (devices.Page, devices.TotalPages, devices.TotalCount, devices.Items.Count));
        Assert.Equal((120, 120), (detail.PrintBridgeDeviceCounts.Total, detail.PrintBridgeDeviceCounts.Active));

        var deviceStatements = recorder.Statements.Where(s => s.ReadsDevices).ToList();
        Assert.Equal(
            new[] { CentralAdminTenantOperationsService.DeviceCountsTag, CentralAdminTenantOperationsService.DevicePageTag },
            deviceStatements.Select(s => s.Tag));
        Assert.InRange(deviceStatements[0].Reads, 1, 2);
        Assert.InRange(deviceStatements[1].Reads, TenantPrintBridgeDevicePage.PageSize, TenantPrintBridgeDevicePage.PageSize + 1);
        Assert.All(deviceStatements, s =>
        {
            Assert.DoesNotContain("OPENJSON", s.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(s.ParameterCount, 1, 4);
            Assert.InRange(s.ParameterBytes, 1, 200);
        });

        await using var inspector = fixture.Database.Open("Inspector");
        var plans = new List<System.Xml.Linq.XDocument>();
        foreach (var statement in deviceStatements)
            plans.AddRange(await CachedPlansAsync(inspector, statement.Tag!));
        Assert.All(plans, plan =>
        {
            var indexes = plan.Descendants(Showplan + "Object")
                .Where(o => (string?)o.Attribute("Table") == "[PrintBridgeDevices]")
                .Select(o => (string?)o.Attribute("Index"))
                .Distinct();
            Assert.Equal(new[] { "[PK_PrintBridgeDevices]" }, indexes);
            Assert.DoesNotContain(plan.Descendants(Showplan + "IndexScan"), scan => (string?)scan.Attribute("Lookup") == "1");
        });
    }

    [Fact]
    public async Task TheCentralAdminTenantList_WhileADeviceIsRemoved_WaitsAndCountsTheOthers()
    {
        var device = await fixture.DeviceAsync(Siblings);

        var page = await _race.ReadDuringTwoStepDeviceWriteAsync(
            device.Id,
            db => ChangeAsync(db, device.Id, removed: true),
            readFrom: null,
            db => new CentralAdminTenantOperationsService(db, TimeProvider.System)
                .GetTenantListAsync(new TenantListQuery { Search = device.Slug }, CancellationToken.None));

        var item = Assert.Single(page.Items);
        Assert.Equal(device.Tenant, item.Id);
        Assert.Equal(Siblings, item.ActivePrintBridgeDevices);
    }

    // The second step of a token revocation (the TokenHash index key) or of a removal (also RemovedAtUtc).
    private static Task ChangeAsync(CentralDbContext db, Guid deviceId, bool removed)
    {
        var replacement = PrintBridgeTokenHasher.HashToken(PrintBridgeTokenHasher.GenerateRawToken());
        return removed
            ? db.Database.ExecuteSqlAsync($"UPDATE [PrintBridgeDevices] SET [RemovedAtUtc] = SYSUTCDATETIME(), [IsActive] = 0, [TokenHash] = {replacement} WHERE [Id] = {deviceId}", Ct)
            : db.Database.ExecuteSqlAsync($"UPDATE [PrintBridgeDevices] SET [TokenHash] = {replacement} WHERE [Id] = {deviceId}", Ct);
    }

    private static readonly System.Xml.Linq.XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    // The cached plans of the statement carrying this query tag, compiled in this test database.
    private static async Task<IReadOnlyList<System.Xml.Linq.XDocument>> CachedPlansAsync(CentralDbContext db, string tag)
    {
        var pattern = "%-- " + tag + "%";
        var plans = await db.Database
            .SqlQuery<string>($"""
                SELECT CAST(p.query_plan AS nvarchar(max)) AS [Value]
                FROM sys.dm_exec_query_stats s
                CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) t
                CROSS APPLY sys.dm_exec_query_plan(s.plan_handle) p
                CROSS APPLY (SELECT CAST(a.value AS int) AS dbid FROM sys.dm_exec_plan_attributes(s.plan_handle) a WHERE a.attribute = 'dbid') d
                WHERE d.dbid = DB_ID() AND t.text LIKE {pattern} AND t.text NOT LIKE '%dm_exec_query_stats%'
                """)
            .ToListAsync(Ct);
        Assert.NotEmpty(plans);
        return plans.Distinct().Select(System.Xml.Linq.XDocument.Parse).ToList();
    }

    private static PrintBridgeDeviceManagementService Manage(CentralDbContext db) =>
        new(db, NullLogger<PrintBridgeDeviceManagementService>.Instance, NoActivePrinting.Instance, new SqlServerPrintBridgeSetupTenantLock(db));

    private sealed class NoActivePrinting : IPrintBridgeActivePrintJobChecker
    {
        public static NoActivePrinting Instance { get; } = new();

        public Task<bool> HasActivePrintingJobAsync(Guid tenantId, Guid? installationId, string? legacyLockedBy, CancellationToken ct) =>
            Task.FromResult(false);
    }

    // The setup status reads the tenant database too; these tests are about the CentralDb device read only.
    private sealed class NoTenantDatabase : ITenantDbContextFactory
    {
        public static NoTenantDatabase Instance { get; } = new();

        public Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct) =>
            throw new InvalidOperationException("No tenant database in this test.");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }
}

/// <summary>
/// A throwaway CentralDb with a realistic spread of restaurants and devices (WAS-205): 2,000 restaurants, most with one
/// Print Bridge, some with two or three, a few with up to six, and a quarter with one to three retired devices from earlier
/// computers or moves (about 3,900 devices). With only a few rows SQL Server scans the table instead of using the secondary
/// indexes, and the key-lookup deadlocks these tests guard against cannot happen.
/// </summary>
public sealed class PrintBridgeDeviceReadDatabase : IAsyncLifetime
{
    public const int Restaurants = 2000;

    public SqlServerCentralDatabase Database { get; } = new();

    public sealed record Device(Guid Tenant, string Slug, Guid Id, string Token, Guid Installation);

    public async ValueTask InitializeAsync()
    {
        await Database.InitializeAsync();
        var random = new Random(205);
        for (var batch = 0; batch < Restaurants; batch += 500)
        {
            await using var db = Database.Open("Seed");
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var t = batch; t < Math.Min(Restaurants, batch + 500); t++)
            {
                var (tenant, _) = AddTenant(db);
                var roll = random.NextDouble();
                var active = roll < 0.70 ? 1 : roll < 0.90 ? 2 : roll < 0.97 ? 3 : random.Next(4, 7);
                var retired = random.NextDouble() < 0.25 ? random.Next(1, 4) : 0;
                for (var d = 0; d < active + retired; d++)
                {
                    var isRetired = d >= active;
                    db.PrintBridgeDevices.Add(new PrintBridgeDevice
                    {
                        TenantId = tenant,
                        Name = "Kasa " + d,
                        InstallationId = Guid.NewGuid(),
                        TokenHash = PrintBridgeTokenHasher.HashToken(PrintBridgeTokenHasher.GenerateRawToken()),
                        IsActive = !isRetired,
                        RemovedAtUtc = isRetired ? DateTime.UtcNow.AddDays(-random.Next(1, 400)) : null,
                        LastSeenAt = DateTime.UtcNow.AddSeconds(-random.Next(0, 600)),
                        MachineName = "PC-" + d,
                        AppVersion = "1.4.2.0"
                    });
                }
            }

            await db.SaveChangesAsync();
        }

        await using var stats = Database.Open("Seed");
        await stats.Database.ExecuteSqlRawAsync("UPDATE STATISTICS [PrintBridgeDevices] WITH FULLSCAN; UPDATE STATISTICS [Tenants] WITH FULLSCAN;");
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();

    /// <summary>A new restaurant with <paramref name="siblings"/> other active devices; returns the first device.</summary>
    public async Task<Device> DeviceAsync(int siblings)
    {
        await using var db = Database.Open("Seed");
        var (tenant, slug) = AddTenant(db);
        Device? first = null;
        for (var i = 0; i <= siblings; i++)
        {
            var token = PrintBridgeTokenHasher.GenerateRawToken();
            var installation = Guid.NewGuid();
            var device = new PrintBridgeDevice
            {
                TenantId = tenant,
                Name = "QA device " + i,
                InstallationId = installation,
                TokenHash = PrintBridgeTokenHasher.HashToken(token),
                IsActive = true,
                LastSeenAt = DateTime.UtcNow
            };
            db.PrintBridgeDevices.Add(device);
            first ??= new Device(tenant, slug, device.Id, token, installation);
        }

        await db.SaveChangesAsync();
        return first!;
    }

    private static (Guid Id, string Slug) AddTenant(CentralDbContext db)
    {
        var slug = "kasa" + Guid.NewGuid().ToString("N")[..12];
        var tenant = new Tenant
        {
            Name = "Restoran " + slug,
            Slug = slug,
            PrimaryDomain = $"{slug}.app.wasla.tr",
            DatabaseName = $"Wasla_Tenant_{slug}",
            EncryptedConnectionString = "encrypted-not-real",
            EncryptionKeyVersion = 1,
            SchemaVersion = "1.0",
            IsActive = true
        };
        db.Tenants.Add(tenant);
        return (tenant.Id, slug);
    }
}
