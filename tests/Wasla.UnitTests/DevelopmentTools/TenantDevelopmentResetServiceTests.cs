using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.DevelopmentTools;

/// <summary>
/// The temporary Development tenant reset: exactly the current tenant goes back to its post-provisioning
/// state, identity and people are kept, and a failure changes nothing.
/// </summary>
public sealed class TenantDevelopmentResetServiceTests : IDisposable
{
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Guid _kitchen = Guid.NewGuid();
    private readonly Guid _branch = Guid.NewGuid();
    private readonly Guid _ownerB = Guid.NewGuid();
    private readonly Clock _clock = new();
    private readonly TenantSqlite _tenants = new();
    private readonly SqliteConnection _centralConnection = new("Data Source=:memory:");

    public TenantDevelopmentResetServiceTests()
    {
        _centralConnection.Open();
        using var central = NewCentral();
        central.Database.EnsureCreated();
    }

    [Fact]
    public async Task Reset_RemovesTheTenantsOperationalAndOnboardingData()
    {
        await SeedTenantAsync(_tenantA, _owner, _manager, _kitchen);
        await SeedCentralAsync();

        var result = await Service().ResetAsync(_tenantA, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Orders);
        Assert.Equal(2, result.PrintJobs);
        Assert.Equal(2, result.PlatformConnections);
        Assert.Equal(2, result.PrintBridgeDevices);
        Assert.Equal(3, result.GuidedSetupStates);
        Assert.Equal(1, result.PracticeOrders);

        await using var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(0, await db.Orders.CountAsync(ct));
        Assert.Equal(0, await db.OrderItems.CountAsync(ct));
        Assert.Equal(0, await db.OrderItemOptions.CountAsync(ct));
        Assert.Equal(0, await db.PrintJobs.CountAsync(ct));
        Assert.Equal(0, await db.SyncLogs.CountAsync(ct));
        Assert.Equal(0, await db.IntegrationErrors.CountAsync(ct));
        Assert.Equal(0, await db.PlatformConnections.CountAsync(ct));
        Assert.Equal(0, await db.GuidedDemoSessions.CountAsync(ct));
        Assert.Equal(0, await db.UserGuidedSetupStates.CountAsync(ct));
        Assert.Equal(0, await db.UserProductTourCompletions.CountAsync(ct));
        Assert.Equal(0, await db.UserNotificationSettings.CountAsync(ct));

        // The settings row is replaced by the one provisioning writes: default settings, back in Setup.
        var settings = await db.TenantOperationalSettings.SingleAsync(ct);
        Assert.Equal(TenantOperationalMode.Setup, settings.OperationalMode);
        Assert.True(settings.OrderSyncEnabled);
        Assert.False(settings.AutoApproveNewOrders);
        Assert.False(settings.AutoPrintReceiptOnAutoApprove);
        Assert.Equal(1, settings.ReceiptPrintCopyCount);
        Assert.Null(settings.ReceiptTemplateSettingsJson);
        Assert.Null(settings.SetupGuidanceCompletedAtUtc);
    }

    [Fact]
    public async Task Reset_ReturnsOnlyThisLiveTenantToSetup_AndTheNextFinishedJourneyTakesItLiveAgain()
    {
        await SeedTenantAsync(_tenantA, _owner, _manager, _kitchen); // a live, production-style test tenant
        await SeedTenantAsync(_tenantB, _ownerB);
        await SeedCentralAsync();
        var modes = new TenantOperationalModeService(_tenants);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(TenantOperationalMode.Live, (await modes.GetAutomationStatusAsync(_tenantA, ct)).Mode);

        Assert.True((await Service().ResetAsync(_tenantA, CancellationToken.None)).Succeeded);

        // This explicit full tenant reset is the one way back to the first-login experience.
        Assert.Equal(TenantOperationalMode.Setup, (await modes.GetAutomationStatusAsync(_tenantA, ct)).Mode);
        Assert.Equal(TenantOperationalMode.Live, (await modes.GetAutomationStatusAsync(_tenantB, ct)).Mode);

        await new GuidedSetupService(_tenants, _clock).SkipAsync(_tenantA, _manager, ct);
        Assert.Equal(TenantOperationalMode.Live, (await modes.GetAutomationStatusAsync(_tenantA, ct)).Mode);
    }

    [Fact]
    public async Task Reset_KeepsIdentityUsersRolesPasswordsBranchesAndSecurityTokens()
    {
        await SeedTenantAsync(_tenantA, _owner, _manager, _kitchen);
        await SeedCentralAsync();
        var usersBefore = await UsersAsync(_tenantA);

        Assert.True((await Service().ResetAsync(_tenantA, CancellationToken.None)).Succeeded);

        Assert.Equal(usersBefore, await UsersAsync(_tenantA));
        await using var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(_branch, (await db.Branches.SingleAsync(ct)).Id);
        Assert.Equal(1, await db.PasswordResetTokens.CountAsync(ct));

        await using var central = NewCentral();
        var tenant = await central.Tenants.SingleAsync(t => t.Id == _tenantA, ct);
        Assert.Equal("reset-a", tenant.Slug);
        Assert.Equal("reset-a.wasla.local", tenant.PrimaryDomain);
        Assert.True(tenant.IsActive);
        var membership = await central.TenantMemberships.SingleAsync(m => m.TenantId == _tenantA, ct);
        Assert.Equal("Growth", membership.PlanCode);
        Assert.Equal("Kadıköy", membership.City);
    }

    [Fact]
    public async Task Reset_RemovesPrintBridgeDevicesAndStopsTheirTokens_AndFailsUnfinishedPairing()
    {
        await SeedTenantAsync(_tenantA, _owner);
        var seeded = await SeedCentralAsync();

        Assert.True((await Service().ResetAsync(_tenantA, CancellationToken.None)).Succeeded);

        await using var central = NewCentral();
        var ct = TestContext.Current.CancellationToken;
        var devices = await central.PrintBridgeDevices.Where(d => d.TenantId == _tenantA).ToListAsync(ct);
        Assert.All(devices, device =>
        {
            Assert.Equal(_clock.Now.UtcDateTime, device.RemovedAtUtc);
            Assert.False(device.IsActive);
            Assert.DoesNotContain(device.TokenHash, seeded.TokenHashesA);
        });
        // Manageable devices are the ones the setup checklist and device list read: none are left.
        Assert.Equal(0, await central.PrintBridgeDevices.CountAsync(d => d.TenantId == _tenantA && d.RemovedAtUtc == null, ct));

        var pending = await central.PrintBridgeSetupSessions.SingleAsync(s => s.Id == seeded.PendingSessionA, ct);
        Assert.Equal(TenantDevelopmentResetService.SetupSessionFailureReason, pending.FailureReason);
        Assert.NotNull(pending.FailedAtUtc);
        var completed = await central.PrintBridgeSetupSessions.SingleAsync(s => s.Id == seeded.CompletedSessionA, ct);
        Assert.Null(completed.FailedAtUtc);
    }

    [Fact]
    public async Task Reset_NeverTouchesAnotherTenant()
    {
        await SeedTenantAsync(_tenantA, _owner, _manager);
        await SeedTenantAsync(_tenantB, _ownerB);
        var seeded = await SeedCentralAsync();
        var before = await SnapshotAsync(_tenantB);

        Assert.True((await Service().ResetAsync(_tenantA, CancellationToken.None)).Succeeded);

        Assert.Equal(before, await SnapshotAsync(_tenantB));
        await using var central = NewCentral();
        var ct = TestContext.Current.CancellationToken;
        var deviceB = await central.PrintBridgeDevices.SingleAsync(d => d.TenantId == _tenantB, ct);
        Assert.Null(deviceB.RemovedAtUtc);
        Assert.True(deviceB.IsActive);
        Assert.Equal(seeded.TokenHashB, deviceB.TokenHash);
        var pendingB = await central.PrintBridgeSetupSessions.SingleAsync(s => s.TenantId == _tenantB, ct);
        Assert.Null(pendingB.FailedAtUtc);
    }

    [Fact]
    public async Task AfterReset_SettingsAreTheCanonicalDefaults_AndEveryUserStartsFresh()
    {
        await SeedTenantAsync(_tenantA, _owner, _manager, _kitchen);
        await SeedCentralAsync();

        Assert.True((await Service().ResetAsync(_tenantA, CancellationToken.None)).Succeeded);
        var ct = TestContext.Current.CancellationToken;

        // The same readers the app uses; a missing row is what provisioning leaves.
        Assert.True((await new OrderSyncSettingsService(_tenants).GetAsync(_tenantA, ct)).OrderSyncEnabled);
        var orderSettings = await new TenantOrderSettingsService(_tenants, null!, NullLogger<TenantOrderSettingsService>.Instance).GetAsync(_tenantA, ct);
        Assert.False(orderSettings.AutoApproveNewOrders);
        Assert.False(orderSettings.AutoPrintReceiptOnAutoApprove);
        Assert.Equal(1, orderSettings.ReceiptPrintCopyCount);
        var notifications = await new UserNotificationSettingsService(_tenants, null!).GetAsync(_tenantA, _owner, ct);
        Assert.True(notifications.NewOrderSoundEnabled);
        Assert.False(notifications.ShowBrowserNotification);

        var guidedSetup = new GuidedSetupService(_tenants, _clock);
        foreach (var user in new[] { _owner, _manager, _kitchen })
            Assert.Equal(GuidedSetupStatus.NotStarted, (await guidedSetup.GetAsync(_tenantA, user, ct)).Status);

        // A fresh journey and a fresh practice order can start normally, with no stale practice card.
        var demos = new GuidedDemoService(_tenants, new NoSubtypes(), _clock);
        Assert.Null(await demos.GetForLiveScreenAsync(_tenantA, _owner, ct));
        Assert.Null(await demos.GetLatestAsync(_tenantA, _owner, ct));
        var started = await guidedSetup.StartAsync(_tenantA, _owner, new GuidedSetupPosition(GuidedSetupSections.PlatformConnections, GuidedTrainingSteps.Intro), ct);
        Assert.Equal(GuidedSetupOutcome.Applied, started.Outcome);
        var practice = await demos.StartAsync(_tenantA, _owner, ct);
        Assert.Equal(OrderStatus.New, practice.Status);
    }

    [Fact]
    public async Task Reset_IsIdempotent()
    {
        await SeedTenantAsync(_tenantA, _owner);
        await SeedCentralAsync();

        Assert.True((await Service().ResetAsync(_tenantA, CancellationToken.None)).Succeeded);
        var again = await Service().ResetAsync(_tenantA, CancellationToken.None);

        Assert.True(again.Succeeded);
        Assert.Equal(0, again.Orders);
        Assert.Equal(0, again.PrintBridgeDevices);
    }

    [Fact]
    public async Task FailureInTheTenantDatabase_RollsEverythingBack()
    {
        await SeedTenantAsync(_tenantA, _owner, _manager);
        var seeded = await SeedCentralAsync();
        var before = await SnapshotAsync(_tenantA);
        // A late step fails after orders, connections and practice orders were already deleted in the transaction.
        _tenants.FailOn(_tenantA, "DELETE FROM \"UserGuidedSetupStates\"");

        var result = await Service().ResetAsync(_tenantA, CancellationToken.None);

        Assert.False(result.Succeeded);
        _tenants.FailOn(_tenantA, null);
        Assert.Equal(before, await SnapshotAsync(_tenantA));
        await AssertCentralUntouchedAsync(seeded);
    }

    [Fact]
    public async Task FailureInTheCentralDatabase_RollsTheTenantDatabaseBack()
    {
        await SeedTenantAsync(_tenantA, _owner, _manager);
        var seeded = await SeedCentralAsync();
        var before = await SnapshotAsync(_tenantA);

        var result = await Service(failCentralSave: true).ResetAsync(_tenantA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(before, await SnapshotAsync(_tenantA));
        await AssertCentralUntouchedAsync(seeded);
    }

    [Fact]
    public async Task TenantCommitFailingAfterTheCentralCommit_IsReportedAsPartial_AndARerunCompletes()
    {
        await SeedTenantAsync(_tenantA, _owner, _manager);
        await SeedCentralAsync();
        var before = await SnapshotAsync(_tenantA);
        _tenants.FailCommit(_tenantA, true);

        var result = await Service().ResetAsync(_tenantA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.PartiallyApplied);
        Assert.Equal(before, await SnapshotAsync(_tenantA));
        await using (var central = NewCentral())
        {
            Assert.Equal(0, await central.PrintBridgeDevices.CountAsync(
                d => d.TenantId == _tenantA && d.RemovedAtUtc == null, TestContext.Current.CancellationToken));
        }

        _tenants.FailCommit(_tenantA, false);
        var rerun = await Service().ResetAsync(_tenantA, CancellationToken.None);
        Assert.True(rerun.Succeeded);
        Assert.False(rerun.PartiallyApplied);
        await using var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None);
        Assert.Equal(0, await db.Orders.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Reset_NeverReadsOrWritesIdentitySubscriptionCheckoutOrProvisioningRecords()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Wasla.Infrastructure", "Services", "TenantDevelopmentResetService.cs"));

        foreach (var forbidden in new[] { ".Tenants", "TenantMemberships", "PendingRegistration", ".AppUsers", ".Branches", "PasswordResetTokens", "EnsureDeleted", "Migrate" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------

    private TenantDevelopmentResetService Service(bool failCentralSave = false)
    {
        var central = failCentralSave ? new FailingCentralDbContext(CentralOptions()) : NewCentral();
        return new TenantDevelopmentResetService(
            _tenants,
            central,
            new SqlServerPrintBridgeSetupTenantLock(central),
            _clock,
            NullLogger<TenantDevelopmentResetService>.Instance);
    }

    private CentralDbContext NewCentral() => new(CentralOptions());

    private DbContextOptions<CentralDbContext> CentralOptions() =>
        new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_centralConnection).Options;

    private async Task SeedTenantAsync(Guid tenantId, params Guid[] userIds)
    {
        var now = _clock.Now.UtcDateTime;
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        if (tenantId == _tenantA)
            db.Branches.Add(new Branch { Id = _branch, Name = "Merkez", Address = "Kadıköy" });

        var roles = new[] { UserRole.Owner, UserRole.Manager, UserRole.Kitchen };
        for (var i = 0; i < userIds.Length; i++)
        {
            db.AppUsers.Add(new AppUser
            {
                Id = userIds[i],
                Email = $"{userIds[i]:N}@example.test",
                PasswordHash = "hash-" + i,
                FullName = "User " + i,
                Role = roles[i],
                IsActive = true,
                BranchId = tenantId == _tenantA ? _branch : null
            });
            db.UserGuidedSetupStates.Add(new UserGuidedSetupState
            {
                UserId = userIds[i],
                Status = i == 0 ? GuidedSetupStatus.Completed : GuidedSetupStatus.InProgress,
                CurrentSectionKey = GuidedSetupSections.LiveScreenDemo,
                CurrentStepKey = GuidedTrainingSteps.PracticeReady
            });
            db.UserNotificationSettings.Add(new UserNotificationSettings { UserId = userIds[i], NewOrderSoundEnabled = false, ShowBrowserNotification = true });
            db.UserProductTourCompletions.Add(new UserProductTourCompletion { UserId = userIds[i], TourKey = "dashboard-intro", CompletedAtUtc = now });
        }

        db.PasswordResetTokens.Add(new PasswordResetToken { UserId = userIds[0], TokenHash = "reset-hash-" + tenantId.ToString("N"), ExpiresAtUtc = now.AddHours(1) });
        db.GuidedDemoSessions.Add(new GuidedDemoSession
        {
            UserId = userIds[0],
            ScenarioCode = "lokanta",
            Status = OrderStatus.OnTheWay,
            CustomerNameKey = "Demo.CustomerName",
            ReceivedAtUtc = now,
            ExpiresAtUtc = now.AddHours(2)
        });
        db.TenantOperationalSettings.Add(new TenantOperationalSettings
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            OrderSyncEnabled = false,
            AutoApproveNewOrders = true,
            AutoPrintReceiptOnAutoApprove = true,
            ReceiptPrintCopyCount = 3,
            SetupGuidanceCompletedAtUtc = now
        });

        foreach (var platform in new[] { FoodPlatform.TrendyolYemek, FoodPlatform.Yemeksepeti })
        {
            var connection = new PlatformConnection
            {
                Platform = platform,
                StoreId = "store-" + platform,
                EncryptedApiKey = "ciphertext-key",
                EncryptedApiSecret = "ciphertext-secret",
                LastSuccessfulSync = now,
                ConsecutiveFailures = 2
            };
            db.PlatformConnections.Add(connection);
            db.SyncLogs.Add(new SyncLog { PlatformConnectionId = connection.Id, StartedAt = now, Status = SyncStatus.Success });
            db.IntegrationErrors.Add(new IntegrationError { Platform = platform, PlatformConnectionId = connection.Id, ErrorType = "Timeout", ErrorMessage = "test" });

            var order = new Order
            {
                Platform = platform,
                ExternalOrderId = "ext-" + platform,
                ExternalOrderCode = "C-" + platform,
                IdempotencyKey = "idem-" + platform + tenantId.ToString("N"),
                InternalStatus = OrderStatus.Preparing,
                CustomerName = "Müşteri",
                ReceivedAt = now,
                CreatedAtPlatform = now,
                RawPayloadJson = "{}",
                TotalAmount = 100m
            };
            var item = new OrderItem { ProductName = "Köfte", Quantity = 1, UnitPrice = 100m, TotalPrice = 100m };
            item.Options.Add(new OrderItemOption { Name = "Acılı", Price = 0m });
            order.Items.Add(item);
            db.Orders.Add(order);
            db.PrintJobs.Add(new PrintJob { OrderId = order.Id, Type = PrintJobType.Receipt, PayloadJson = "{}" });
        }

        await db.SaveChangesAsync();
    }

    private sealed record CentralSeed(string[] TokenHashesA, string TokenHashB, Guid PendingSessionA, Guid CompletedSessionA);

    private async Task<CentralSeed> SeedCentralAsync()
    {
        var now = _clock.Now.UtcDateTime;
        await using var central = NewCentral();
        foreach (var (id, slug) in new[] { (_tenantA, "reset-a"), (_tenantB, "reset-b") })
        {
            central.Tenants.Add(new Tenant
            {
                Id = id,
                Name = slug,
                Slug = slug,
                PrimaryDomain = slug + ".wasla.local",
                DatabaseName = "Wasla_" + slug,
                EncryptedConnectionString = "encrypted",
                EncryptionKeyVersion = 1,
                SchemaVersion = "1.0.0",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        central.TenantMemberships.Add(new TenantMembership { TenantId = _tenantA, PlanCode = "Growth", StartedAt = now, City = "Kadıköy", Country = "TR" });

        string NewHash() => PrintBridgeTokenHasher.HashToken(PrintBridgeTokenHasher.GenerateRawToken());
        var hashesA = new[] { NewHash(), NewHash() };
        var hashB = NewHash();
        foreach (var hash in hashesA)
            central.PrintBridgeDevices.Add(new PrintBridgeDevice { TenantId = _tenantA, Name = "Kitchen", TokenHash = hash, LastSeenAt = now });
        central.PrintBridgeDevices.Add(new PrintBridgeDevice { TenantId = _tenantB, Name = "Counter", TokenHash = hashB, LastSeenAt = now });

        var pendingA = new PrintBridgeSetupSession { TenantId = _tenantA, SetupMode = "NewDevice", CodeHash = "code-a1", ServerUrl = "https://a", ExpiresAtUtc = now.AddMinutes(5) };
        var completedA = new PrintBridgeSetupSession { TenantId = _tenantA, SetupMode = "NewDevice", CodeHash = "code-a2", ServerUrl = "https://a", ExpiresAtUtc = now.AddMinutes(5), CompletedAtUtc = now };
        central.PrintBridgeSetupSessions.AddRange(pendingA, completedA,
            new PrintBridgeSetupSession { TenantId = _tenantB, SetupMode = "NewDevice", CodeHash = "code-b1", ServerUrl = "https://b", ExpiresAtUtc = now.AddMinutes(5) });

        await central.SaveChangesAsync();
        return new CentralSeed(hashesA, hashB, pendingA.Id, completedA.Id);
    }

    private async Task AssertCentralUntouchedAsync(CentralSeed seeded)
    {
        await using var central = NewCentral();
        var ct = TestContext.Current.CancellationToken;
        var devicesA = await central.PrintBridgeDevices.Where(d => d.TenantId == _tenantA).ToListAsync(ct);
        Assert.All(devicesA, device => Assert.Null(device.RemovedAtUtc));
        Assert.Equal(seeded.TokenHashesA.OrderBy(h => h), devicesA.Select(d => d.TokenHash).OrderBy(h => h));
        Assert.Null((await central.PrintBridgeSetupSessions.SingleAsync(s => s.Id == seeded.PendingSessionA, ct)).FailedAtUtc);
    }

    private async Task<string> UsersAsync(Guid tenantId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        var users = await db.AppUsers.OrderBy(u => u.Email)
            .Select(u => new { u.Id, u.Email, u.PasswordHash, u.FullName, u.Role, u.IsActive, u.BranchId })
            .ToListAsync();
        return string.Join("|", users);
    }

    /// <summary>Row counts of every tenant table plus the user list: enough to see any change.</summary>
    private async Task<string> SnapshotAsync(Guid tenantId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        var counts = new[]
        {
            await db.Orders.CountAsync(), await db.OrderItems.CountAsync(), await db.OrderItemOptions.CountAsync(),
            await db.PrintJobs.CountAsync(), await db.SyncLogs.CountAsync(), await db.IntegrationErrors.CountAsync(),
            await db.PlatformConnections.CountAsync(), await db.GuidedDemoSessions.CountAsync(),
            await db.UserGuidedSetupStates.CountAsync(), await db.UserProductTourCompletions.CountAsync(),
            await db.UserNotificationSettings.CountAsync(), await db.TenantOperationalSettings.CountAsync(),
            await db.Branches.CountAsync(), await db.PasswordResetTokens.CountAsync()
        };
        return string.Join(",", counts) + "|" + await UsersAsync(tenantId);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    public void Dispose()
    {
        _tenants.Dispose();
        _centralConnection.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoSubtypes : ITenantBusinessSubtypeReader
    {
        public Task<IReadOnlyList<string>?> GetSubtypeCodesAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(null);
    }

    private sealed class FailingCentralDbContext(DbContextOptions<CentralDbContext> options) : CentralDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("simulated central failure");
    }

    /// <summary>One in-memory SQLite database per tenant, with the real tenant model.</summary>
    private sealed class TenantSqlite : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();
        private readonly Dictionary<Guid, FailOnCommand> _faults = new();

        public void FailOn(Guid tenantId, string? sqlStart) => Fault(tenantId).SqlStart = sqlStart;

        public void FailCommit(Guid tenantId, bool fail) => Fault(tenantId).FailCommit = fail;

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();
                _connections.Add(customerId, connection);
                using var setup = new SqliteTenantDbContext(Options(connection, Fault(customerId)));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new SqliteTenantDbContext(Options(connection, Fault(customerId))));
        }

        private FailOnCommand Fault(Guid tenantId)
        {
            if (!_faults.TryGetValue(tenantId, out var fault))
                _faults[tenantId] = fault = new FailOnCommand();
            return fault;
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection, FailOnCommand fault) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).AddInterceptors(fault, new FailOnCommit(fault)).Options;
    }

    /// <summary>Simulates the tenant commit failing (after the Central commit already succeeded).</summary>
    private sealed class FailOnCommit(FailOnCommand fault) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (fault.FailCommit)
                throw new InvalidOperationException("simulated tenant commit failure");
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    /// <summary>Simulates a failing statement: throws when a command starts with the configured SQL.</summary>
    private sealed class FailOnCommand : DbCommandInterceptor
    {
        public string? SqlStart { get; set; }

        public bool FailCommit { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (SqlStart is not null && command.CommandText.TrimStart().StartsWith(SqlStart, StringComparison.Ordinal))
                throw new InvalidOperationException("simulated tenant database failure");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>The production tenant model; only SQL Server's nvarchar(max) becomes SQLite TEXT.</summary>
    private sealed class SqliteTenantDbContext(DbContextOptions<TenantDbContext> options) : TenantDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>().Property(order => order.RawPayloadJson).HasColumnType("TEXT");
            modelBuilder.Entity<TenantOperationalSettings>().Property(settings => settings.ReceiptTemplateSettingsJson).HasColumnType("TEXT");
        }
    }
}
